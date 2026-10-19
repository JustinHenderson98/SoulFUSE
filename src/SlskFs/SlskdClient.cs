using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SlskFs;

public sealed class SlskdClient : ISlskd, IDisposable
{
    private readonly HttpClient http;
    private readonly Config config;
    public SlskdClient(Config config)
    {
        this.config = config;
        var key = Environment.GetEnvironmentVariable(config.ApiKeyEnv);
        if (string.IsNullOrEmpty(key)) throw new ArgumentException($"Set {config.ApiKeyEnv} to a slskd API key");
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(config.ApiUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(config.ApiTimeoutSeconds) };
        http.DefaultRequestHeaders.Add("X-API-Key", key);
    }
    private async Task<JsonElement> GetAsync(string path, CancellationToken ct, bool missingAllowed = false)
    {
        using var r = await http.GetAsync(path, ct);
        if (missingAllowed && r.StatusCode == HttpStatusCode.NotFound) return default;
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }
    private static string S(JsonElement e, string field) => e.GetProperty(field).GetString() ?? "";
    private static Transfer ParseTransfer(JsonElement e) => new(S(e, "id"), S(e, "username"), S(e, "filename"),
        e.GetProperty("size").GetInt64(), S(e, "state"));
    public async Task<Entry[]> BrowseAsync(string username, CancellationToken ct)
    {
        var root = await GetAsync($"users/{Uri.EscapeDataString(username)}/browse", ct);
        var directories = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("directories");
        var entries = new List<Entry>();
        foreach (var dir in directories.EnumerateArray())
        {
            var name = S(dir, "name");
            var separator = name.Contains('\\') || !name.Contains('/') ? "\\" : "/";
            foreach (var file in dir.GetProperty("files").EnumerateArray())
            {
                var filename = S(file, "filename");
                if (filename.Contains('/') || filename.Contains('\\'))
                    throw new InvalidDataException("Expected a basename in browse directory file list");
                var remote = name.TrimEnd('\\', '/') + separator + filename;
                var size = file.GetProperty("size").GetInt64();
                if (size < 0) throw new InvalidDataException("Negative file size");
                entries.Add(new(Paths.MountPath(username, remote), username, remote, size));
                if (entries.Count > config.MaxCatalogEntries) throw new InvalidDataException("Catalog limit exceeded");
            }
        }
        return entries.ToArray(); // Locked directories intentionally excluded.
    }
    public async Task<Transfer[]> TransfersAsync(string username, CancellationToken ct)
    {
        var root = await GetAsync($"transfers/downloads/{Uri.EscapeDataString(username)}", ct, true);
        if (root.ValueKind == JsonValueKind.Undefined) return [];
        return root.GetProperty("directories").EnumerateArray()
            .SelectMany(d => d.GetProperty("files").EnumerateArray()).Select(ParseTransfer).ToArray();
    }
    public async Task RemoveTransferAsync(Transfer transfer, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"transfers/downloads/{Uri.EscapeDataString(transfer.Username)}/{Uri.EscapeDataString(transfer.Id)}?remove=true", ct);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }
    public async Task EnqueueAsync(Entry entry, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"transfers/downloads/{Uri.EscapeDataString(entry.Username)}",
            new[] { new { filename = entry.RemoteFilename, size = entry.Size } }, ct);
        response.EnsureSuccessStatusCode();
        // Some versions return 201 even when all requests fail. The coordinator re-queries transfers.
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("failed", out var failed) && failed.GetArrayLength() > 0)
        {
            var active = await TransfersAsync(entry.Username, ct);
            if (!active.Any(t => t.Filename == entry.RemoteFilename && t.Size == entry.Size && !TerminalFailure(t.State)))
                throw new IOException("slskd rejected the download request; inspect its transfer queue");
        }
    }
    public static bool Succeeded(string state) => state.Split(',', StringSplitOptions.TrimEntries).Contains("Succeeded");
    public static bool TerminalFailure(string state) => state.Split(',', StringSplitOptions.TrimEntries)
        .Any(s => s is "Errored" or "Cancelled" or "Rejected" or "TimedOut");
    public async Task<Completion?> CompletionAsync(Transfer transfer, CancellationToken ct)
    {
        const int pageSize = 100;
        for (var page = 0; page < config.EventPageLimit; page++)
        {
            var events = await GetAsync($"events?offset={page * pageSize}&limit={pageSize}", ct);
            foreach (var record in events.EnumerateArray())
            {
                if (S(record, "type") != "DownloadFileComplete") continue;
                var data = record.GetProperty("data");
                using var doc = JsonDocument.Parse(data.ValueKind == JsonValueKind.String ? data.GetString()! : data.GetRawText());
                var e = doc.RootElement;
                var t = ParseTransfer(e.GetProperty("transfer"));
                if (t.Id == transfer.Id && t.Username == transfer.Username && t.Filename == transfer.Filename &&
                    t.Size == transfer.Size && S(e, "remoteFilename") == transfer.Filename)
                    return new(t.Id, t.Username, t.Filename, t.Size, S(e, "localFilename"));
            }
            if (events.GetArrayLength() < pageSize) break;
        }
        return null;
    }
    public void Dispose() => http.Dispose();
}
