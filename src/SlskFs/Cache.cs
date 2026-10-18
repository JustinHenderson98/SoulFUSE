using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace SlskFs;

public sealed class Cache
{
    private readonly Config config;
    private readonly ISlskd api;
    private readonly CancellationToken shutdown;
    private readonly object gate = new();
    private readonly Dictionary<string, Task> pending = new();
    private readonly Dictionary<string, long> reservations = new();
    private readonly Dictionary<string, int> readers = new();
    private readonly Dictionary<string, CacheItem> items;
    private string DataDirectory => Path.Combine(config.StateDirectory, "content");
    private string Index => Path.Combine(config.StateDirectory, "cache.json");
    private string Content(string key) => Path.Combine(DataDirectory, key);
    public Cache(Config config, ISlskd api, CancellationToken shutdown)
    {
        this.config = config; this.api = api; this.shutdown = shutdown;
        Directory.CreateDirectory(DataDirectory);
        items = File.Exists(Index) ? Json.Load<Dictionary<string, CacheItem>>(Index) : new();
        foreach (var key in items.Keys.ToArray())
        {
            if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid cache key");
            if (!File.Exists(Content(key)) || new FileInfo(Content(key)).Length != items[key].Size) items.Remove(key);
        }
        // This directory is exclusively adapter-owned; remove interrupted copies and unindexed content.
        foreach (var file in Directory.EnumerateFiles(DataDirectory))
            if (!items.ContainsKey(Path.GetFileName(file))) File.Delete(file);
        Save();
    }
    private void Save() => Json.Save(Index, items);
    public IDisposable Hold(Entry entry)
    {
        lock (gate) readers[entry.Key] = readers.GetValueOrDefault(entry.Key) + 1;
        return new Lease(() => { lock (gate) { if (--readers[entry.Key] == 0) readers.Remove(entry.Key); } });
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
    public async Task EnsureAsync(Entry entry, CancellationToken ct)
    {
        Task task;
        lock (gate)
        {
            if (items.TryGetValue(entry.Key, out var item) && File.Exists(Content(entry.Key)))
            { items[entry.Key] = item with { LastAccess = DateTime.UtcNow }; return; }
            items.Remove(entry.Key);
            if (!pending.TryGetValue(entry.Key, out task!))
            {
                MakeRoom(entry.Size);
                reservations[entry.Key] = entry.Size;
                task = Task.Run(async () =>
                {
                    try { await DownloadAsync(entry); }
                    finally { lock (gate) { reservations.Remove(entry.Key); pending.Remove(entry.Key); } }
                }, CancellationToken.None);
                pending[entry.Key] = task;
                // Observe failures even if the only reader times out before the transfer ends.
                _ = task.ContinueWith(t => Console.Error.WriteLine($"Transfer failed: {t.Exception!.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
        await task.WaitAsync(ct);
    }
    private void MakeRoom(long needed)
    {
        if (needed > config.CacheBytes) throw new IOException("File exceeds the configured cache budget");
        long Used() => items.Values.Sum(i => i.Size) + reservations.Values.Sum();
        foreach (var victim in items.Values.Where(i => !i.Pinned && !readers.ContainsKey(i.Key)).OrderBy(i => i.LastAccess).ToArray())
        {
            if (Used() + needed <= config.CacheBytes) break;
            File.Delete(Content(victim.Key)); items.Remove(victim.Key);
        }
        Save();
        if (Used() + needed > config.CacheBytes) throw new IOException("Cache is full of pinned, open, or pending files");
    }
    private async Task DownloadAsync(Entry entry)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TransferTimeoutSeconds));
        var ct = timeout.Token;
        var transfer = (await api.TransfersAsync(entry.Username, ct)).LastOrDefault(t =>
            t.Filename == entry.RemoteFilename && t.Size == entry.Size && !SlskdClient.TerminalFailure(t.State));
        if (transfer == null)
        {
            await api.EnqueueAsync(entry, ct);
            transfer = (await api.TransfersAsync(entry.Username, ct)).LastOrDefault(t =>
                t.Filename == entry.RemoteFilename && t.Size == entry.Size && !SlskdClient.TerminalFailure(t.State));
            if (transfer == null) throw new IOException("No matching transfer after enqueue; check slskd compatibility");
        }
        var id = transfer.Id;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (SlskdClient.TerminalFailure(transfer.State)) throw new IOException("slskd transfer failed or was cancelled");
            if (SlskdClient.Succeeded(transfer.State))
            {
                var completed = await api.CompletionAsync(transfer, ct);
                if (completed != null)
                {
                    await ImportAsync(entry, completed, ct);
                    return;
                }
                // Succeeded can precede the final move/event. Never infer a local filename.
            }
            await Task.Delay(config.PollMilliseconds, ct);
            transfer = (await api.TransfersAsync(entry.Username, ct)).SingleOrDefault(t => t.Id == id)
                ?? throw new IOException("slskd transfer record disappeared; disable aggressive transfer retention");
            if (transfer.Filename != entry.RemoteFilename || transfer.Username != entry.Username || transfer.Size != entry.Size)
                throw new IOException("Transfer identity or size changed");
        }
    }
    private async Task ImportAsync(Entry entry, Completion completion, CancellationToken ct)
    {
        var root = Path.GetFullPath(config.RemoteDownloadsRoot).TrimEnd('/') + "/";
        var source = Path.GetFullPath(completion.LocalFilename);
        if (!source.StartsWith(root, StringComparison.Ordinal)) throw new IOException("Completion path is outside the configured slskd downloads root");
        var relative = source[root.Length..];
        var fd = OpenRegular(config.LocalDownloadsRoot, relative);
        if (fd < 0) throw new IOException("Cannot open completed file safely; check volume mapping, permissions, and symlinks");
        using var handle = new SafeFileHandle((IntPtr)fd, true);
        using var input = new FileStream(handle, FileAccess.Read);
        if (input.Length != entry.Size) throw new IOException("Completed file size differs from catalog");
        var temporary = Content(entry.Key) + ".partial";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await input.CopyToAsync(output, ct);
                if (input.Length != entry.Size || output.Length != entry.Size) throw new IOException("Source changed during cache import");
                await output.FlushAsync(ct);
            }
            File.Move(temporary, Content(entry.Key), true);
            lock (gate)
            { items[entry.Key] = new(entry.Key, entry.Size, false, DateTime.UtcNow); Save(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [DllImport("slskfs_native", EntryPoint = "slskfs_open_regular", CallingConvention = CallingConvention.Cdecl)]
    private static extern int OpenRegular([MarshalAs(UnmanagedType.LPUTF8Str)] string root, [MarshalAs(UnmanagedType.LPUTF8Str)] string relative);
    public FileStream OpenCompleted(Entry entry)
    {
        lock (gate)
        {
            if (!items.ContainsKey(entry.Key)) throw new IOException("Content has not been materialized");
            return new FileStream(Content(entry.Key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
    }
    public void Pin(Entry entry, bool value)
    {
        lock (gate)
        {
            if (!items.TryGetValue(entry.Key, out var item)) throw new IOException("File is not cached");
            items[entry.Key] = item with { Pinned = value }; Save();
        }
    }
    public void Evict(Entry entry)
    {
        lock (gate)
        {
            if (readers.ContainsKey(entry.Key) || pending.ContainsKey(entry.Key)) throw new IOException("File is open or downloading");
            if (items.TryGetValue(entry.Key, out var item) && item.Pinned) throw new IOException("Unpin the file first");
            File.Delete(Content(entry.Key)); items.Remove(entry.Key); Save();
        }
    }
    public object Status() { lock (gate) return new { bytes = items.Values.Sum(i => i.Size), budget = config.CacheBytes,
        reservedBytes = reservations.Values.Sum(), pending = pending.Count, files = items.Values.ToArray() }; }
}
