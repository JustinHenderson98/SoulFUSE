using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SlskFs;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("SoulFUSE — read-only Soulseek filesystem\nUsage: soulfuse CONFIG.json COMMAND [PATH]\nCommands: mount MOUNTPOINT [--allow-other], serve, refresh, list [VIRTUAL_PATH], fetch PATH, pin PATH, unpin PATH, evict PATH, status, friends list|add|remove [USERNAME]\nPaths for commands are virtual paths such as /alice/Music/song.flac, not mountpoint-prefixed paths.\nAPI key is read from SLSKD_API_KEY (or apiKeyEnv). Linux only.\nserve starts the same engine/control socket without mounting (diagnostics/testing).");
            return args.Length == 0 || args[0] is "--help" or "-h" ? 0 : 2;
        }
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux is required; use Samba for Windows access");
            var config = Json.Load<Config>(args[0]);
            if (args[1] == "friends")
            {
                if (args.Length < 3 || args.Length > 4) throw new ArgumentException("Usage: soulfuse CONFIG.json friends list|add|remove [USERNAME]");
                var response = FriendsManager.Execute(config, args[2], args.Length == 4 ? args[3] : null);
                Console.WriteLine(JsonSerializer.Serialize(response, Json.Options));
                return response.Ok ? 0 : 1;
            }
            config.Validate();
            Directory.CreateDirectory(config.StateDirectory);
            File.SetUnixFileMode(config.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var socketPath = Path.Combine(config.StateDirectory, "control.sock");
            if (Encoding.UTF8.GetByteCount(socketPath) > 100) throw new ArgumentException("State path is too long for a Unix socket");
            using var shutdown = new CancellationTokenSource();
            // libfuse owns signal handling while mounted. Headless commands use .NET cancellation.
            if (args[1] != "mount") Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            var request = new Request(args[1], args.Length > 2 ? args[2] : null);
            if (request.Command is not ("mount" or "serve"))
            {
                var remote = await TryRemote(socketPath, request, shutdown.Token);
                if (remote != null) { Console.WriteLine(JsonSerializer.Serialize(remote, Json.Options)); return remote.Ok ? 0 : 1; }
            }
            // One engine per state directory. CLI commands route to it when mounted/serving.
            using var processLock = new FileStream(Path.Combine(config.StateDirectory, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var api = new SlskdClient(config);
            using var engine = new Engine(config, api, shutdown.Token);
            if (request.Command is not ("mount" or "serve"))
            {
                var response = await engine.ExecuteAsync(request, shutdown.Token);
                Console.WriteLine(JsonSerializer.Serialize(response, Json.Options)); return response.Ok ? 0 : 1;
            }
            if (request.Command == "mount")
            {
                if (request.Path == null || !Directory.Exists(request.Path)) throw new ArgumentException("Create the mount directory first");
                if (!File.Exists("/dev/fuse")) throw new IOException("/dev/fuse is unavailable; enable FUSE on this Linux host");
                if (Directory.EnumerateFileSystemEntries(request.Path).Any()) throw new IOException("Mountpoint must be empty");
            }
            if (File.Exists(socketPath)) File.Delete(socketPath); // Exclusive lock proves no other engine owns it.
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(16);
            var serving = Serve(listener, engine, shutdown.Token);
            var refreshing = RefreshLoop(engine, shutdown.Token);
            Console.Error.WriteLine("SoulFUSE ready; cached listings available while background refresh runs");
            var result = 0;
            try
            {
                if (request.Command == "mount") result = NativeFs.Mount(engine, Path.GetFullPath(request.Path!), args.Contains("--allow-other"));
                else await Task.Delay(Timeout.Infinite, shutdown.Token);
            }
            catch (OperationCanceledException) when(shutdown.IsCancellationRequested) { }
            finally
            {
                shutdown.Cancel(); listener.Close();
                try { await Task.WhenAll(serving, refreshing); } catch (OperationCanceledException) { }
                File.Delete(socketPath);
            }
            return result;
        }
        catch(Exception ex) { Console.Error.WriteLine($"SoulFUSE: {ex.GetType().Name}: {ex.Message}"); return 1; }
    }
    private static async Task RefreshLoop(Engine engine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var error in await engine.Catalog.RefreshAsync(ct)) Console.Error.WriteLine(error);
            await Task.Delay(TimeSpan.FromSeconds(engine.Config.RefreshSeconds), ct);
        }
    }
    private static async Task<Response?> TryRemote(string path, Request request, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct); }
        catch(SocketException ex) when(ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable || ex.ErrorCode == 2) { return null; }
        using var stream = new NetworkStream(socket, false);
        using var reader = new StreamReader(stream, leaveOpen:true);
        using var writer = new StreamWriter(stream, leaveOpen:true) { AutoFlush=true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        var line = await reader.ReadLineAsync(ct) ?? throw new IOException("Control connection closed");
        return JsonSerializer.Deserialize<Response>(line, Json.Options) ?? throw new IOException("Invalid control response");
    }
    private static async Task Serve(Socket listener, Engine engine, CancellationToken ct)
    {
        var clients = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(ct);
                clients.RemoveAll(t => t.IsCompleted);
                if (clients.Count >= 32) { socket.Dispose(); continue; }
                clients.Add(Handle(socket, engine, ct));
            }
        }
        catch(Exception ex) when(ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally { await Task.WhenAll(clients); }
    }
    private static async Task Handle(Socket socket, Engine engine, CancellationToken ct)
    {
        using (socket)
        using (var stream = new NetworkStream(socket, false))
        using (var reader = new StreamReader(stream, leaveOpen:true))
        using (var writer = new StreamWriter(stream, leaveOpen:true) { AutoFlush=true })
        {
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await reader.ReadLineAsync(handshake.Token);
                if (line == null || line.Length > 16384) return;
                var request = JsonSerializer.Deserialize<Request>(line, Json.Options) ?? throw new InvalidDataException();
                var result = await engine.ExecuteAsync(request, ct);
                await writer.WriteLineAsync(JsonSerializer.Serialize(result));
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
        }
    }
}
