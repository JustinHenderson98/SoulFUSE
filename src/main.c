#define FUSE_USE_VERSION 31

#include <curl/curl.h>
#include <errno.h>
#include <fcntl.h>
#include <fuse3/fuse.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>

struct snapshot {
    const char *path;
    const char *endpoint;
    char *contents;
    size_t size;
};

static struct snapshot snapshots[] = {
    { "/shares.json", "/api/v0/shares/contents", NULL, 0 },
    { "/downloads.json", "/api/v0/transfers/downloads", NULL, 0 },
};
static const char *api_url;
static const char *api_key;

static size_t append_response(char *contents, size_t size, size_t count, void *user_data)
{
    struct snapshot *snapshot = user_data;
    size_t bytes = size * count;
    char *resized = realloc(snapshot->contents, snapshot->size + bytes + 1);
    if (resized == NULL)
        return 0;

    snapshot->contents = resized;
    memcpy(snapshot->contents + snapshot->size, contents, bytes);
    snapshot->size += bytes;
    snapshot->contents[snapshot->size] = '\0';
    return bytes;
}

static int fetch_snapshot(struct snapshot *snapshot)
{
    CURL *curl = curl_easy_init();
    if (curl == NULL)
        return -EIO;

    size_t url_size = strlen(api_url) + strlen(snapshot->endpoint) + 1;
    char *url = malloc(url_size);
    if (url == NULL) {
        curl_easy_cleanup(curl);
        return -ENOMEM;
    }
    snprintf(url, url_size, "%s%s", api_url, snapshot->endpoint);

    struct curl_slist *headers = NULL;
    if (api_key != NULL && api_key[0] != '\0') {
        size_t header_size = strlen(api_key) + sizeof("X-API-Key: ");
        char *header = malloc(header_size);
        if (header == NULL) {
            free(url);
            curl_easy_cleanup(curl);
            return -ENOMEM;
        }
        snprintf(header, header_size, "X-API-Key: %s", api_key);
        headers = curl_slist_append(headers, header);
        free(header);
        if (headers == NULL) {
            free(url);
            curl_easy_cleanup(curl);
            return -ENOMEM;
        }
    }

    snapshot->contents = malloc(1);
    if (snapshot->contents == NULL) {
        curl_slist_free_all(headers);
        free(url);
        curl_easy_cleanup(curl);
        return -ENOMEM;
    }
    snapshot->contents[0] = '\0';
    snapshot->size = 0;

    curl_easy_setopt(curl, CURLOPT_URL, url);
    curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, append_response);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, snapshot);
    curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 0L);
    curl_easy_setopt(curl, CURLOPT_FAILONERROR, 1L);
    curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT, 10L);
    curl_easy_setopt(curl, CURLOPT_TIMEOUT, 30L);

    CURLcode result = curl_easy_perform(curl);
    curl_slist_free_all(headers);
    free(url);
    curl_easy_cleanup(curl);
    if (result != CURLE_OK) {
        fprintf(stderr, "slskd request failed for %s: %s\n",
                snapshot->endpoint, curl_easy_strerror(result));
        free(snapshot->contents);
        snapshot->contents = NULL;
        snapshot->size = 0;
        return -EIO;
    }

    return 0;
}

static struct snapshot *find_snapshot(const char *path)
{
    for (size_t i = 0; i < sizeof(snapshots) / sizeof(snapshots[0]); i++) {
        if (strcmp(path, snapshots[i].path) == 0)
            return &snapshots[i];
    }
    return NULL;
}

static int filesystem_getattr(const char *path, struct stat *attributes,
                              struct fuse_file_info *file_info)
{
    (void)file_info;
    memset(attributes, 0, sizeof(*attributes));

    if (strcmp(path, "/") == 0) {
        attributes->st_mode = S_IFDIR | 0555;
        attributes->st_nlink = 2;
        return 0;
    }

    struct snapshot *snapshot = find_snapshot(path);
    if (snapshot == NULL)
        return -ENOENT;

    attributes->st_mode = S_IFREG | 0444;
    attributes->st_nlink = 1;
    attributes->st_size = (off_t)snapshot->size;
    return 0;
}

static int filesystem_readdir(const char *path, void *buffer,
                              fuse_fill_dir_t filler, off_t offset,
                              struct fuse_file_info *file_info,
                              enum fuse_readdir_flags flags)
{
    (void)offset;
    (void)file_info;
    (void)flags;
    if (strcmp(path, "/") != 0)
        return -ENOENT;

    filler(buffer, ".", NULL, 0, 0);
    filler(buffer, "..", NULL, 0, 0);
    for (size_t i = 0; i < sizeof(snapshots) / sizeof(snapshots[0]); i++)
        filler(buffer, snapshots[i].path + 1, NULL, 0, 0);
    return 0;
}

static int filesystem_open(const char *path, struct fuse_file_info *file_info)
{
    struct snapshot *snapshot = find_snapshot(path);
    if (snapshot == NULL)
        return -ENOENT;
    if ((file_info->flags & O_ACCMODE) != O_RDONLY)
        return -EROFS;

    file_info->fh = (uint64_t)(uintptr_t)snapshot;
    return 0;
}

static int filesystem_read(const char *path, char *buffer, size_t size,
                           off_t offset, struct fuse_file_info *file_info)
{
    (void)path;
    struct snapshot *snapshot = (struct snapshot *)(uintptr_t)file_info->fh;
    if (offset < 0)
        return -EINVAL;
    if ((size_t)offset >= snapshot->size)
        return 0;

    if (size > snapshot->size - (size_t)offset)
        size = snapshot->size - (size_t)offset;
    memcpy(buffer, snapshot->contents + offset, size);
    return (int)size;
}

static const struct fuse_operations operations = {
    .getattr = filesystem_getattr,
    .readdir = filesystem_readdir,
    .open = filesystem_open,
    .read = filesystem_read,
};

int main(int argc, char *argv[])
{
    api_url = getenv("SLSKD_URL");
    api_key = getenv("SLSKD_API_KEY");
    if (api_url == NULL || api_url[0] == '\0') {
        fprintf(stderr, "Set SLSKD_URL to the slskd base URL (for example http://localhost:5030).\n");
        return EXIT_FAILURE;
    }

    size_t url_length = strlen(api_url);
    while (url_length > 0 && api_url[url_length - 1] == '/')
        url_length--;
    char *normalized_url = strndup(api_url, url_length);
    if (normalized_url == NULL) {
        fprintf(stderr, "Unable to allocate memory for slskd URL.\n");
        return EXIT_FAILURE;
    }
    api_url = normalized_url;

    if (curl_global_init(CURL_GLOBAL_DEFAULT) != CURLE_OK) {
        fprintf(stderr, "Unable to initialize libcurl.\n");
        free(normalized_url);
        return EXIT_FAILURE;
    }

    for (size_t i = 0; i < sizeof(snapshots) / sizeof(snapshots[0]); i++) {
        int result = fetch_snapshot(&snapshots[i]);
        if (result != 0) {
            for (size_t j = 0; j < i; j++)
                free(snapshots[j].contents);
            curl_global_cleanup();
            free(normalized_url);
            return EXIT_FAILURE;
        }
    }

    int result = fuse_main(argc, argv, &operations, NULL);
    for (size_t i = 0; i < sizeof(snapshots) / sizeof(snapshots[0]); i++)
        free(snapshots[i].contents);
    curl_global_cleanup();
    free(normalized_url);
    return result;
}
