using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SlskFs;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("Empty JSON document");
    public static void Save<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, true);
    }
}

public sealed record Config
{
    public string? SlskdConfigPath { get; init; }
    public string ApiUrl { get; init; } = "http://127.0.0.1:5030/api/v0/";
    public string ApiKeyEnv { get; init; } = "SLSKD_API_KEY";
    public string[] Users { get; init; } = [];
    public string StateDirectory { get; init; } = "/var/lib/slskfs";
    public string RemoteDownloadsRoot { get; init; } = "/app/downloads";
    public string LocalDownloadsRoot { get; init; } = "/srv/slskd/downloads";
    public bool DedicatedDownloadsCache { get; init; }
    public long CacheBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public int ReadTimeoutSeconds { get; init; } = 30;
    public int TransferTimeoutSeconds { get; init; } = 86400;
    public int ApiTimeoutSeconds { get; init; } = 60;
    public int PollMilliseconds { get; init; } = 1000;
    public int RefreshSeconds { get; init; } = 900;
    public int EventPageLimit { get; init; } = 50;
    public int MaxCatalogEntries { get; init; } = 200000;
    public void Validate()
    {
        if (!DedicatedDownloadsCache) throw new ArgumentException("Set dedicatedDownloadsCache=true only for a dedicated slskd download directory; eviction deletes completed downloads. See MIGRATION.md.");
        var uri = new Uri(ApiUrl);
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("apiUrl must be HTTP(S), without embedded credentials");
        if (Users.Length == 0 || Users.Any(string.IsNullOrWhiteSpace) || Users.Distinct().Count() != Users.Length)
            throw new ArgumentException("Configure a nonempty, unique list of Soulseek users");
        foreach (var path in new[] { StateDirectory, RemoteDownloadsRoot, LocalDownloadsRoot })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("All storage paths must be absolute");
        if (CacheBytes <= 0 || ReadTimeoutSeconds < 1 || TransferTimeoutSeconds < ReadTimeoutSeconds ||
            ApiTimeoutSeconds < 1 || PollMilliseconds < 20 || RefreshSeconds < 1 || EventPageLimit < 1 || MaxCatalogEntries < 1)
            throw new ArgumentException("Invalid numeric configuration limit");
        var local = Path.GetFullPath(LocalDownloadsRoot).TrimEnd('/') + "/";
        var state = Path.GetFullPath(StateDirectory).TrimEnd('/') + "/";
        if (local.StartsWith(state, StringComparison.Ordinal) || state.StartsWith(local, StringComparison.Ordinal))
            throw new ArgumentException("State and slskd download directories must be separate trees");
    }
}

public sealed record Entry(string Path, string Username, string RemoteFilename, long Size)
{
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new object[] { Username, RemoteFilename, Size })))).ToLowerInvariant();
}
public sealed record CacheItem(string Key, long Size, bool Pinned, DateTime LastAccess,
    string RelativePath, ulong Device, ulong Inode, Transfer Transfer, bool Evicted = false);
public sealed record SavedCatalog(Dictionary<string, Entry[]> Users);
public sealed record Transfer(string Id, string Username, string Filename, long Size, string State);
public sealed record Completion(string Id, string Username, string Filename, long Size, string LocalFilename);
public sealed record Request(string Command, string? Path = null);
public sealed record Response(bool Ok, string Message, object? Data = null);

public interface ISlskd
{
    Task<Entry[]> BrowseAsync(string username, CancellationToken ct);
    Task<Transfer[]> TransfersAsync(string username, CancellationToken ct);
    Task RemoveTransferAsync(Transfer transfer, CancellationToken ct);
    Task EnqueueAsync(Entry entry, CancellationToken ct);
    Task<Completion?> CompletionAsync(Transfer transfer, CancellationToken ct);
}

public static class Paths
{
    // Mount names are filesystem names, not URLs. HTTP escaping belongs in SlskdClient.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static string Segment(string value)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." || value.Contains('\0') || value.Contains('/'))
            throw new InvalidDataException("Unsafe remote path component");
        if (StrictUtf8.GetByteCount(value) > 255) throw new InvalidDataException("Path component exceeds 255 UTF-8 bytes");
        return value;
    }
    public static string MountPath(string username, string remote)
    {
        var parts = remote.Replace('\\', '/').Split('/');
        return "/" + Segment(username) + "/" + string.Join('/', parts.Select(Segment));
    }
    public static string Normalize(string path)
    {
        if (!path.StartsWith('/') || path.Contains('\0') || path.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("Use a virtual absolute path such as /alice/Music/file.flac");
        return path == "/" ? path : path.TrimEnd('/');
    }
}
