using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;

namespace SlskFs;

public sealed class Engine : IDisposable
{
    public Config Config { get; }
    public Catalog Catalog { get; }
    public Cache Cache { get; }
    private readonly ConcurrentDictionary<long, OpenFile> handles = new();
    private long nextHandle;
    private readonly CancellationToken shutdown;
    private sealed class OpenFile(Entry entry, IDisposable lease) : IDisposable
    {
        public Entry Entry { get; } = entry;
        public readonly object Gate = new();
        public FileStream? Stream;
        public bool Closed;
        public void Dispose() { lock (Gate) { Closed = true; Stream?.Dispose(); lease.Dispose(); } }
    }
    public Engine(Config config, ISlskd api, CancellationToken shutdown = default)
    {
        Config = config; this.shutdown = shutdown;
        Directory.CreateDirectory(config.StateDirectory);
        Catalog = new(config, api); Cache = new(config, api, shutdown);
    }
    public long Open(string path, int flags)
    {
        if ((flags & 3) != 0 || (flags & (512 | 1024 | 64)) != 0) throw new UnauthorizedAccessException("Read-only filesystem");
        var entry = Catalog.FileEntry(path);
        var id = Interlocked.Increment(ref nextHandle);
        handles[id] = new(entry, Cache.Hold(entry));
        return id;
    }
    public Entry HandleEntry(long handle) => handles.TryGetValue(handle, out var h) ? h.Entry : throw new IOException("Invalid file handle");
    public int Read(long handle, long offset, Span<byte> buffer)
    {
        if (!handles.TryGetValue(handle, out var h)) throw new IOException("Invalid file handle");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= h.Entry.Size || buffer.Length == 0) return 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromSeconds(Config.ReadTimeoutSeconds));
        lock (h.Gate)
        {
            if (h.Closed) throw new IOException("File handle was released");
            if (h.Stream == null)
            {
                Cache.EnsureAsync(h.Entry, timeout.Token).GetAwaiter().GetResult();
                h.Stream = Cache.OpenCompleted(h.Entry);
            }
            var count = (int)Math.Min(buffer.Length, h.Entry.Size - offset);
            var read = RandomAccess.Read(h.Stream.SafeFileHandle, buffer[..count], offset);
            if (read == 0 && count != 0) throw new IOException("Unexpected EOF in cached content");
            return read;
        }
    }
    public void Release(long handle) { if (handles.TryRemove(handle, out var h)) h.Dispose(); }
    public async Task<Response> ExecuteAsync(Request request, CancellationToken ct)
    {
        try
        {
            switch (request.Command)
            {
                case "refresh":
                    var errors = await Catalog.RefreshAsync(ct);
                    return new(errors.Length == 0, errors.Length == 0 ? "Catalog refreshed" : "Some listings could not be refreshed", errors);
                case "list": return new(true, "Directory entries", Catalog.List(request.Path ?? "/"));
                case "status": return new(true, "Cache status (keys are local cache IDs, not content hashes)", Cache.Status());
                case "fetch":
                case "pin":
                    var entry = Catalog.FileEntry(request.Path ?? "");
                    using (Cache.Hold(entry))
                    {
                        await Cache.EnsureAsync(entry, ct);
                        if (request.Command == "pin") Cache.Pin(entry, true);
                    }
                    return new(true, request.Command == "pin" ? "Downloaded and pinned" : "Downloaded into cache");
                case "unpin": Cache.Pin(Catalog.FileEntry(request.Path ?? ""), false); return new(true, "Unpinned; bytes retained until eviction");
                case "evict": Cache.Evict(Catalog.FileEntry(request.Path ?? "")); return new(true, "Completed download evicted; remote listing retained");
                default: return new(false, "Unknown command");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, $"{ex.GetType().Name}: {ex.Message}"); }
    }
    public void Dispose() { foreach (var id in handles.Keys) Release(id); }
}
