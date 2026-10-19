#define FUSE_USE_VERSION 31
#define _GNU_SOURCE
#include <fuse.h>
#include <errno.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* The shim keeps platform-dependent stat/fuse_file_info layouts out of C#.
 * Callback results follow libfuse's negative-errno convention. */
typedef int (*getattr_cb)(const char *, uint64_t, int64_t *, int *);
typedef int (*open_cb)(const char *, int, uint64_t *);
typedef int (*read_cb)(uint64_t, int64_t, void *, int);
typedef void (*release_cb)(uint64_t);
typedef int (*opendir_cb)(const char *, uint64_t *);
typedef int (*readdir_cb)(uint64_t, int64_t, void *, void *);
static getattr_cb managed_getattr;
static open_cb managed_open;
static read_cb managed_read;
static release_cb managed_release;
static opendir_cb managed_opendir;
static readdir_cb managed_readdir;
static release_cb managed_releasedir;

static int fs_getattr(const char *p, struct stat *s, struct fuse_file_info *fi) {
    int64_t size = 0; int directory = 0;
    int rc = managed_getattr(p, fi ? fi->fh : 0, &size, &directory);
    if (rc < 0) return rc;
    memset(s, 0, sizeof(*s));
    s->st_mode = directory ? (S_IFDIR | 0555) : (S_IFREG | 0444);
    s->st_nlink = directory ? 2 : 1;
    s->st_uid = getuid(); s->st_gid = getgid();
    s->st_size = size; s->st_blksize = 4096;
    return 0;
}
static int fs_open(const char *p, struct fuse_file_info *fi) {
    int rc = managed_open(p, fi->flags, &fi->fh);
    fi->direct_io = 1; /* No stale kernel content cache across refreshed opens. */
    fi->keep_cache = 0;
    return rc;
}
static int fs_read(const char *p, char *b, size_t n, off_t o, struct fuse_file_info *fi) {
    (void)p;
    if (n > INT32_MAX) return -EINVAL;
    return managed_read(fi->fh, o, b, (int)n);
}
static int fs_release(const char *p, struct fuse_file_info *fi) {
    (void)p; managed_release(fi->fh); return 0;
}
static int fs_opendir(const char *p, struct fuse_file_info *fi) { return managed_opendir(p, &fi->fh); }
static int fs_readdir(const char *p, void *b, fuse_fill_dir_t f, off_t o,
                      struct fuse_file_info *fi, enum fuse_readdir_flags flags) {
    (void)p; (void)flags; return managed_readdir(fi->fh, o, b, (void *)f);
}
static int fs_releasedir(const char *p, struct fuse_file_info *fi) {
    (void)p; managed_releasedir(fi->fh); return 0;
}
static void *fs_init(struct fuse_conn_info *conn, struct fuse_config *cfg) {
    (void)conn; cfg->entry_timeout = 1; cfg->attr_timeout = 1; cfg->negative_timeout = 0;
    return NULL;
}
static int fs_statfs(const char *path, struct statvfs *s) {
    (void)path; memset(s, 0, sizeof(*s));
    s->f_bsize = 4096; s->f_frsize = 4096; s->f_namemax = 255; s->f_flag = ST_RDONLY;
    return 0; /* Remote capacity is unknown; this mount accepts no writes. */
}
int slskfs_add_entry(void *buffer, void *filler, const char *name, int64_t next) {
    return ((fuse_fill_dir_t)filler)(buffer, name, NULL, next, 0);
}
int slskfs_mount(int argc, char **argv, getattr_cb ga, open_cb op, read_cb rd, release_cb rel,
                 opendir_cb od, readdir_cb dr, release_cb rdir) {
    managed_getattr=ga; managed_open=op; managed_read=rd; managed_release=rel;
    managed_opendir=od; managed_readdir=dr; managed_releasedir=rdir;
    struct fuse_operations ops = { .init=fs_init, .getattr=fs_getattr, .open=fs_open,
        .read=fs_read, .release=fs_release, .opendir=fs_opendir, .readdir=fs_readdir, .releasedir=fs_releasedir, .statfs=fs_statfs };
    return fuse_main(argc, argv, &ops, NULL);
}
/* Resolve only regular files underneath the configured directory. All components
 * use O_NOFOLLOW, so neither remote event paths nor local symlinks escape it. */
int slskfs_open_regular(const char *root, const char *relative) {
    if (!relative || !*relative || *relative == '/') return -1;
    int fd = open(root, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) return -1;
    char *path = strdup(relative);
    if (!path) { close(fd); return -1; }
    char *cursor = path;
    while (cursor) {
        char *part = strsep(&cursor, "/");
        if (!*part || !strcmp(part, ".") || !strcmp(part, "..")) { close(fd); free(path); return -1; }
        int flags = O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK;
        if (cursor) flags |= O_DIRECTORY;
        int next = openat(fd, part, flags);
        close(fd);
        if (next < 0) { free(path); return -1; }
        fd = next;
    }
    free(path);
    struct stat st;
    if (fstat(fd, &st) < 0 || !S_ISREG(st.st_mode)) { close(fd); return -1; }
    return fd;
}

/* Version 0.2: errno-preserving safe traversal and inode-checked eviction.
 * Root and its ancestors must be trusted, stable local directories. */
static int cache_parent(const char *root, const char *relative, char **storage, char **leaf) {
    if (!relative || !*relative || *relative == '/') return -EINVAL;
    *storage = strdup(relative);
    if (!*storage) return -ENOMEM;
    int fd = open(root, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) { int e = errno; free(*storage); return -e; }
    char *cursor = *storage;
    while (cursor) {
        char *part = strsep(&cursor, "/");
        if (!*part || !strcmp(part, ".") || !strcmp(part, "..")) {
            close(fd); free(*storage); return -EINVAL;
        }
        if (!cursor) { *leaf = part; return fd; }
        int next = openat(fd, part, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
        int e = errno; close(fd);
        if (next < 0) { free(*storage); return -e; }
        fd = next;
    }
    close(fd); free(*storage); return -EINVAL;
}
int slskfs_open_cached(const char *root, const char *relative, uint64_t *device, uint64_t *inode) {
    char *storage = NULL, *leaf = NULL;
    int parent = cache_parent(root, relative, &storage, &leaf);
    if (parent < 0) return parent;
    int fd = openat(parent, leaf, O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK);
    int e = errno; close(parent); free(storage);
    if (fd < 0) return -e;
    struct stat st;
    if (fstat(fd, &st) < 0) { e = errno; close(fd); return -e; }
    if (!S_ISREG(st.st_mode)) { close(fd); return -EINVAL; }
    *device = st.st_dev; *inode = st.st_ino;
    return fd;
}
int slskfs_unlink_cached(const char *root, const char *relative, uint64_t device, uint64_t inode, int64_t size) {
    char *storage = NULL, *leaf = NULL;
    int parent = cache_parent(root, relative, &storage, &leaf);
    if (parent < 0) return parent;
    struct stat st;
    int rc = 0;
    if (fstatat(parent, leaf, &st, AT_SYMLINK_NOFOLLOW) < 0) rc = -errno;
    else if (!S_ISREG(st.st_mode) || (uint64_t)st.st_dev != device || (uint64_t)st.st_ino != inode || st.st_size != size) rc = -ESTALE;
    else if (unlinkat(parent, leaf, 0) < 0) rc = -errno;
    close(parent); free(storage);
    return rc;
}
