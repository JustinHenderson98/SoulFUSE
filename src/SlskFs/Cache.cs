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
    // Separate format: never reinterpret or delete prototype 0.1's private copies.
    private string Index => Path.Combine(config.StateDirectory, "downloads-cache.json");
    public Cache(Config config, ISlskd api, CancellationToken shutdown)
    {
        if (!config.DedicatedDownloadsCache) throw new ArgumentException("A dedicatedDownloadsCache is required; see MIGRATION.md");
        this.config = config; this.api = api; this.shutdown = shutdown;
        if (!Directory.Exists(config.LocalDownloadsRoot)) throw new DirectoryNotFoundException("Create/mount the dedicated slskd downloads directory first");
        items = File.Exists(Index) ? Json.Load<Dictionary<string, CacheItem>>(Index) : new();
        foreach (var (key, item) in items)
        {
            if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c)) || key != item.Key || item.Size < 0)
                throw new InvalidDataException("Invalid cache index");
            ValidateRelative(item.RelativePath);
            if (item.Transfer == null || string.IsNullOrWhiteSpace(item.Transfer.Id) ||
                item.Transfer.Size != item.Size ||
                new Entry("", item.Transfer.Username, item.Transfer.Filename, item.Transfer.Size).Key != key)
                throw new InvalidDataException("Invalid cached transfer identity");
        }
        if (items.Values.Where(i => !i.Evicted).Select(i => i.RelativePath).Distinct().Count() != items.Values.Count(i => !i.Evicted))
            throw new InvalidDataException("Duplicate cache destination");
        Save();
    }
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\0') ||
            path.Split('/').Any(p => p is "" or "." or "..")) throw new IOException("Unsafe cache relative path");
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
    // ENOENT alone means missing. Permission errors and symlinks must not trigger destructive recovery.
    private FileStream? Open(string relative, long size, out ulong device, out ulong inode)
    {
        ValidateRelative(relative);
        var fd = OpenRegular(config.LocalDownloadsRoot, relative, out device, out inode);
        if (fd == -2) return null;
        if (fd < 0) throw new IOException($"Cannot open completed file safely (errno {-fd}); check volume mapping, permissions, and symlinks");
        var stream = new FileStream(new SafeFileHandle((IntPtr)fd, true), FileAccess.Read);
        if (stream.Length != size) { stream.Dispose(); throw new IOException("Completed file size differs from catalog"); }
        return stream;
    }
    private FileStream? OpenItem(CacheItem item)
    {
        var stream = Open(item.RelativePath, item.Size, out var dev, out var ino);
        if (stream != null && (dev != item.Device || ino != item.Inode))
        { stream.Dispose(); throw new IOException("Cached download was replaced externally; refusing to read or delete the replacement"); }
        return stream;
    }
    public async Task EnsureAsync(Entry entry, CancellationToken ct)
    {
        Task task;
        lock (gate)
        {
            if (items.TryGetValue(entry.Key, out var item))
            {
                using var existing = OpenItem(item);
                if (existing != null) { items[entry.Key] = item with { LastAccess = DateTime.UtcNow, Evicted = false }; Save(); return; }
            }
            if (!pending.TryGetValue(entry.Key, out task!))
            {
                MakeRoom(entry.Size, entry.Key);
                reservations[entry.Key] = entry.Size;
                task = Task.Run(async () =>
                {
                    try { await DownloadAsync(entry); }
                    finally { lock (gate) { reservations.Remove(entry.Key); pending.Remove(entry.Key); } }
                }, CancellationToken.None);
                pending[entry.Key] = task;
                _ = task.ContinueWith(t => Console.Error.WriteLine($"Transfer failed: {t.Exception!.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
        await task.WaitAsync(ct);
    }
    private long Used(string? exclude = null) => items.Values.Where(i => !i.Evicted && i.Key != exclude && !reservations.ContainsKey(i.Key)).Sum(i => i.Size) + reservations.Values.Sum();
    private void DeleteItem(CacheItem item)
    {
        // Compare inode/device/size and resolve parent directories with O_NOFOLLOW.
        var rc = UnlinkRegular(config.LocalDownloadsRoot, item.RelativePath, item.Device, item.Inode, item.Size);
        if (rc != 0 && rc != -2) throw new IOException($"Cannot evict completed download safely (errno {-rc}); file may have changed externally");
        items[item.Key] = item with { Evicted = true }; Save();
    }
    private void MakeRoom(long needed, string key)
    {
        if (needed > config.CacheBytes) throw new IOException("File exceeds the configured cache budget");
        foreach (var victim in items.Values.Where(i => !i.Evicted && i.Key != key && !i.Pinned && !readers.ContainsKey(i.Key) && !pending.ContainsKey(i.Key)).OrderBy(i => i.LastAccess).ToArray())
        {
            if (Used(key) + needed <= config.CacheBytes) break;
            DeleteItem(victim);
        }
        if (Used(key) + needed > config.CacheBytes) throw new IOException("Cache is full of pinned, open, or pending files");
    }
    private async Task DownloadAsync(Entry entry)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TransferTimeoutSeconds));
        var ct = timeout.Token;
        var restarted = false;
        CacheItem? previous;
        lock (gate) previous = items.GetValueOrDefault(entry.Key);
        if (previous != null)
        {
            // The persisted mapping survives eviction and event-history pruning.
            // Only remove its exact successful transfer, after verifying the file is absent.
            using var existing = OpenItem(previous);
            if (existing == null)
            {
                var stale = (await api.TransfersAsync(entry.Username, ct)).SingleOrDefault(t =>
                    t.Id == previous.Transfer.Id && t.Username == entry.Username &&
                    t.Filename == entry.RemoteFilename && t.Size == entry.Size && SlskdClient.Succeeded(t.State));
                if (stale != null) await api.RemoveTransferAsync(stale, ct);
            }
        }
        while (true)
        {
            var matches = (await api.TransfersAsync(entry.Username, ct)).Where(t =>
                t.Username == entry.Username && t.Filename == entry.RemoteFilename && t.Size == entry.Size).ToArray();
            var transfer = matches.LastOrDefault(t => !SlskdClient.TerminalFailure(t.State));
            if (transfer == null)
            {
                // Remove only matching terminal records, never a live transfer for another entry.
                foreach (var failed in matches) await api.RemoveTransferAsync(failed, ct);
                await api.EnqueueAsync(entry, ct);
                transfer = (await api.TransfersAsync(entry.Username, ct)).LastOrDefault(t =>
                    t.Username == entry.Username && t.Filename == entry.RemoteFilename && t.Size == entry.Size && !SlskdClient.TerminalFailure(t.State));
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
                        if (Adopt(entry, transfer, completed)) return;
                        // The completion event proves the final move happened, but its file is now missing.
                        if (restarted) throw new IOException("Completed download is missing again; check volume mapping and slskd cleanup scripts");
                        foreach (var old in (await api.TransfersAsync(entry.Username, ct)).Where(t =>
                            t.Username == entry.Username && t.Filename == entry.RemoteFilename && t.Size == entry.Size && SlskdClient.Succeeded(t.State)))
                            await api.RemoveTransferAsync(old, ct);
                        restarted = true;
                        break;
                    }
                }
                await Task.Delay(config.PollMilliseconds, ct);
                transfer = (await api.TransfersAsync(entry.Username, ct)).SingleOrDefault(t => t.Id == id)
                    ?? throw new IOException("slskd transfer record disappeared; disable aggressive transfer retention");
                if (transfer.Filename != entry.RemoteFilename || transfer.Username != entry.Username || transfer.Size != entry.Size)
                    throw new IOException("Transfer identity or size changed");
            }
        }
    }
    private bool Adopt(Entry entry, Transfer transfer, Completion completion)
    {
        var root = Path.GetFullPath(config.RemoteDownloadsRoot).TrimEnd('/') + "/";
        var source = Path.GetFullPath(completion.LocalFilename);
        if (!source.StartsWith(root, StringComparison.Ordinal)) throw new IOException("Completion path is outside the configured slskd downloads root");
        var relative = source[root.Length..];
        using var stream = Open(relative, entry.Size, out var dev, out var ino);
        if (stream == null) return false;
        lock (gate)
        {
            if (items.Values.Any(i => !i.Evicted && i.Key != entry.Key && (i.RelativePath == relative || (i.Device == dev && i.Inode == ino))))
                throw new IOException("Completed download is already owned by another cache entry; configure slskd destination conflicts to rename");
            var pinned = items.GetValueOrDefault(entry.Key)?.Pinned ?? false;
            items[entry.Key] = new(entry.Key, entry.Size, pinned, DateTime.UtcNow, relative, dev, ino, transfer);
            Save();
        }
        return true;
    }
    [DllImport("slskfs_native", EntryPoint = "slskfs_open_cached", CallingConvention = CallingConvention.Cdecl)]
    private static extern int OpenRegular([MarshalAs(UnmanagedType.LPUTF8Str)] string root, [MarshalAs(UnmanagedType.LPUTF8Str)] string relative, out ulong device, out ulong inode);
    [DllImport("slskfs_native", EntryPoint = "slskfs_unlink_cached", CallingConvention = CallingConvention.Cdecl)]
    private static extern int UnlinkRegular([MarshalAs(UnmanagedType.LPUTF8Str)] string root, [MarshalAs(UnmanagedType.LPUTF8Str)] string relative, ulong device, ulong inode, long size);
    public FileStream OpenCompleted(Entry entry)
    {
        lock (gate)
        {
            if (!items.TryGetValue(entry.Key, out var item)) throw new IOException("Content has not been materialized");
            return OpenItem(item) ?? throw new IOException("Cached download disappeared; retry the read");
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
            if (!items.TryGetValue(entry.Key, out var item) || item.Evicted) return;
            if (item.Pinned) throw new IOException("Unpin the file first");
            DeleteItem(item);
        }
    }
    public object Status() { lock (gate) return new { mode = "slskd-completed-downloads", bytes = Used() - reservations.Values.Sum(), budget = config.CacheBytes,
        reservedBytes = reservations.Values.Sum(), pending = pending.Count, files = items.Values.Where(i => !i.Evicted).ToArray() }; }
}
