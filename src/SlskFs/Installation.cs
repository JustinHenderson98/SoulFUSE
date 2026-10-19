using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using YamlDotNet.Serialization;

namespace SlskFs;

// The existing adapter and slskd files remain authoritative. This record contains
// only lifecycle information, so there is no second copy of the friend list.
public sealed record InstallationSettings(string Mountpoint, string Dotnet, string Assembly);
public static class Installation
{
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    [DllImport("libc")] private static extern uint getuid();
    [DllImport("libc")] private static extern uint getgid();
    public static bool IsCommand(string value) => value is "setup" or "start" or "stop" or "status" or "friends" or "users" or "shares" or "enable" or "disable" or "run";
    public static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/soulfuse");
    public static async Task<int> Execute(string[] arguments)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Managed setup requires Linux or WSL2");
        var args = arguments.ToList();
        var root = DefaultHome;
        var home = args.IndexOf("--home");
        if (home >= 0)
        {
            if (home + 1 >= args.Count) throw new ArgumentException("--home requires a directory");
            root = Path.GetFullPath(args[home + 1]); args.RemoveRange(home, 2);
        }
        if (args.Contains("--home")) throw new ArgumentException("Duplicate --home");
        var command = args[0];
        if (command == "setup")
        {
            if (args.Count > 2 || args.Count == 2 && args[1] != "--adopt") throw new ArgumentException("Usage: soulfuse setup [--adopt] [--home DIRECTORY]");
            Setup(root, args.Contains("--adopt")); return 0;
        }
        var settings = Json.Load<InstallationSettings>(Path.Combine(root, "installation.json"));
        var configPath = Path.Combine(root, "config.json");
        var config = Json.Load<Config>(configPath);
        if (command == "friends")
        {
            if (args.Count is < 2 or > 3) throw new ArgumentException("friends list|add|remove [USERNAME]");
            var response = FriendsManager.Execute(config, args[1], args.Count == 3 ? args[2] : null);
            Console.WriteLine(JsonSerializer.Serialize(response, Json.Options)); return response.Ok ? 0 : 1;
        }
        if (command == "shares") { SharesManager.Execute(root, config, args.Skip(1).ToArray()); return 0; }
        if (command == "users")
        {
            if (args.Count == 2 && args[1] == "list") { Console.WriteLine(JsonSerializer.Serialize(config.Users, Json.Options)); return 0; }
            if (args.Count != 3 || args[1] is not ("add" or "remove")) throw new ArgumentException("users list|add|remove [USERNAME]");
            Paths.Segment(args[2]);
            var names = config.Users.ToHashSet(StringComparer.Ordinal);
            if (args[1] == "add") names.Add(args[2]); else names.Remove(args[2]);
            config = config with { Users = names.Order(StringComparer.Ordinal).ToArray() };
            config.Validate(); WritePrivate(configPath, JsonSerializer.Serialize(config, Json.Options));
            Console.WriteLine("Mounted users saved. Restart SoulFUSE to apply."); return 0;
        }
        if (args.Count != 1) throw new ArgumentException("Unexpected arguments");
        var unit = UnitName(root);
        if (command == "enable")
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/systemd/user");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, unit);
            var content = ServiceText(root, settings);
            if (File.Exists(target) && File.ReadAllText(target) != content) throw new IOException("A different service file exists; remove it manually before enabling");
            WritePrivate(target, content);
            await Run("systemctl", ["--user", "daemon-reload"]);
            await Run("systemctl", ["--user", "enable", unit]);
            Console.WriteLine("Enabled for user-session startup. Use start to launch now."); return 0;
        }
        if (command == "disable") { await Run("systemctl", ["--user", "disable", unit]); return 0; }
        if (command == "status")
        {
            var a = await Compose(root, config, ["ps"], false);
            var b = await Run("systemctl", ["--user", "status", "--no-pager", unit], false);
            return a != 0 ? a : b;
        }
        if (command == "stop")
        {
            await Run("systemctl", ["--user", "stop", unit]);
            await Compose(root, config, ["stop", "slskd"]); return 0;
        }
        if (command == "start")
        {
            await Run("systemctl", ["--user", "start", unit]); return 0;
        }
        // systemd owns this foreground process. Failed startup is retried by it.
        if (command == "run")
        {
            config.Validate();
            await Compose(root, config, ["up", "-d", "slskd"]);
            var key = ReadKey(root);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Add("X-API-Key", key);
            var ready = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { using var result = await http.GetAsync(new Uri(new Uri(config.ApiUrl), "application")); if (result.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                        throw new InvalidDataException("slskd rejected the saved API key. Check the exact uppercase SLSKD_API_KEY entry in compose.yaml and recreate the container.");
                    ready = result.IsSuccessStatusCode; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
                if (ready) break;
                await Task.Delay(1000);
            }
            if (!ready) throw new IOException("slskd API did not become ready; check Docker logs and API key");
            Environment.SetEnvironmentVariable(config.ApiKeyEnv, key);
            return await Program.Main([configPath, "mount", settings.Mountpoint]);
        }
        throw new ArgumentException("Unknown managed command");
    }
    public static string UnitName(string root) => "soulfuse-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root))))[..12].ToLowerInvariant() + ".service";
    private static string UnitQuote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$") + "\"";
    public static string ServiceText(string root, InstallationSettings settings)
    {
        foreach (var value in new[] { root, settings.Dotnet, settings.Assembly, settings.Mountpoint })
            if (!Path.IsPathFullyQualified(value) || value.Any(char.IsControl)) throw new ArgumentException("Service paths must be absolute and contain no control characters");
        return "[Unit]\nDescription=SoulFUSE filesystem and slskd startup\n\n[Service]\nType=simple\nExecStart=" +
            string.Join(' ', new[] { settings.Dotnet, settings.Assembly, "run", "--home", root }.Select(UnitQuote)) +
            "\nExecStopPost=-/usr/bin/fusermount3 -u " + UnitQuote(settings.Mountpoint) + "\nRestart=on-failure\nRestartSec=10\nTimeoutStopSec=30\nUMask=0077\n\n[Install]\nWantedBy=default.target\n";
    }
    public static void WritePrivate(string path, string text)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (File.Exists(path) && new FileInfo(path).LinkTarget != null) throw new IOException("Refusing symlink configuration");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile }))
            using (var writer = new StreamWriter(file)) writer.Write(text);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string Prompt(string label, string? fallback = null, bool secret = false)
    {
        Console.Write(label + (fallback == null ? "" : $" [{fallback}]") + ": ");
        string value;
        if (secret && !Console.IsInputRedirected)
        {
            var chars = new List<char>();
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
                else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
            }
            Console.WriteLine(); value = new string(chars.ToArray());
        }
        else value = Console.ReadLine() ?? throw new EndOfStreamException("Setup input ended");
        if (value.Length == 0 && fallback != null) return fallback;
        if (value.Length == 0 || value.Any(char.IsControl)) throw new ArgumentException("A nonempty value without control characters is required");
        return value;
    }
    private static string[] Names(string value) => value == "-" ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
    private static void Setup(string root, bool adopt)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "libslskfs_native.so"))) throw new IOException("Run build.sh before setup; native library is missing");
        if (File.Exists(Path.Combine(root, "installation.json"))) throw new IOException("Already managed; configuration was left unchanged");
        if (Directory.Exists(root) && new DirectoryInfo(root).LinkTarget != null) throw new IOException("Installation directory must not be a symlink");
        if (!adopt && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Directory is not empty; use --adopt for an existing installation");
        Config config;
        string? yaml = null, compose = null, key = null;
        if (adopt)
        {
            config = Json.Load<Config>(Path.Combine(root, "config.json"));
            config = config with { SlskdConfigPath = config.SlskdConfigPath ?? Path.Combine(root, "slskd/slskd.yml") };
            config.Validate();
            foreach (var file in new[] { "compose.yaml", "api-key" }) if (!File.Exists(Path.Combine(root, file))) throw new IOException("Adoption requires " + file);
            if (!File.Exists(config.SlskdConfigPath)) throw new IOException("slskd YAML configuration does not exist");
            _ = ReadKey(root);
            Console.WriteLine("Adopting existing Compose, slskd configuration, state and downloads in place. Stop the old FUSE mount before start. The Compose service must be named slskd.");
        }
        else
        {
            var username = Prompt("Soulseek username");
            var password = Prompt("Soulseek password", secret: true);
            var webPassword = Prompt("slskd web admin password", secret: true);
            var friends = Names(Prompt("Friends allowed to download (comma separated; - for nobody)", "-"));
            var users = Names(Prompt("Users to mount (comma separated; - for none)", "-"));
            var shares = new List<string>(); var mounts = new List<object>();
            Console.WriteLine("Add shared folders; enter - to finish.");
            while (true)
            {
                var folder = Prompt("Absolute shared folder", "-"); if (folder == "-") break;
                if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new ArgumentException("Shared folder must be an existing absolute directory");
                var alias = Prompt("Share name", Path.GetFileName(folder.TrimEnd('/')));
                Paths.Segment(alias);
                if (alias.Contains(']') || alias.Contains('[')) throw new ArgumentException("Share names cannot contain brackets");
                if (shares.Any(s => s.StartsWith("[" + alias + "]", StringComparison.Ordinal))) throw new ArgumentException("Duplicate share name");
                var target = "/shares/" + shares.Count;
                shares.Add("[" + alias + "]" + target);
                mounts.Add(new { type = "bind", source = folder.Replace("$", "$$"), target, read_only = true });
            }
            var cacheGiB = long.Parse(Prompt("Cache limit in GiB", "20"));
            config = new Config { Users = users, StateDirectory = Path.Combine(root, "state"), LocalDownloadsRoot = Path.Combine(root, "slskd/downloads"), SlskdConfigPath = Path.Combine(root, "slskd/slskd.yml"), DedicatedDownloadsCache = true, CacheBytes = checked(cacheGiB * 1024 * 1024 * 1024) };
            config.Validate();
            key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            yaml = new SerializerBuilder().Build().Serialize(new {
                soulseek = new { username, password }, web = new { authentication = new { username = "admin", password = webPassword } },
                directories = new { downloads = "/app/downloads", incomplete = "/app/incomplete" },
                shares = new { directories = shares }, transfers = new { groups = new { blacklisted = new { patterns = new[] { FriendsManager.Pattern(friends) } } } }
            });
            mounts.Insert(0, new { type = "bind", source = Path.Combine(root, "slskd").Replace("$", "$$"), target = "/app" });
            compose = JsonSerializer.Serialize(new { services = new { slskd = new {
                image = "slskd/slskd:0.26.1", user = "${SLSKFS_UID:?Missing SLSKFS_UID}:${SLSKFS_GID:?Missing SLSKFS_GID}",
                ports = new[] { "127.0.0.1:5030:5030", "50300:50300" },
                environment = new Dictionary<string, string> { ["SLSKD_API_KEY"] = "${SLSKD_API_KEY:?Missing SLSKD_API_KEY}" }, volumes = mounts, restart = "unless-stopped"
            } } }, Json.Options); // JSON is also valid Compose YAML.
        }
        var mountpoint = Path.GetFullPath(Prompt("Mount directory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "friends")));
        if (Directory.Exists(mountpoint) && Directory.EnumerateFileSystemEntries(mountpoint).Any()) throw new IOException("Unmount or empty the mount directory before setup");
        var dotnet = FindDotnet();
        var settings = new InstallationSettings(mountpoint, dotnet, Path.Combine(root, "bin/SlskFs.dll"));
        _ = ServiceText(root, settings);
        Directory.CreateDirectory(root); File.SetUnixFileMode(root, PrivateFile | UnixFileMode.UserExecute);
        using var setupLock = new FileStream(Path.Combine(root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(Path.Combine(root, "installation.json"))) throw new IOException("Already managed");
        Directory.CreateDirectory(mountpoint); Directory.CreateDirectory(Path.Combine(root, "bin"));
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            var destination = Path.Combine(root, "bin", Path.GetFileName(source));
            if (Path.GetFullPath(source) != destination) File.Copy(source, destination, true);
        }
        if (!File.Exists(Path.Combine(root, "bin/libslskfs_native.so"))) throw new IOException("Build the native library with build.sh before setup");
        if (!adopt)
        {
            Directory.CreateDirectory(Path.Combine(root, "slskd"));
            WritePrivate(config.SlskdConfigPath!, yaml!); WritePrivate(Path.Combine(root, "compose.yaml"), compose!);
            WritePrivate(Path.Combine(root, "api-key"), key! + "\n");
            WritePrivate(Path.Combine(root, ".env"), $"# Generated by SoulFUSE. API key is copied from api-key.\nSLSKD_API_KEY={key}\nSLSKFS_UID={getuid()}\nSLSKFS_GID={getgid()}\n");
        }
        else File.Copy(Path.Combine(root, "config.json"), Path.Combine(root, "config.before-setup.json"), false);
        WritePrivate(Path.Combine(root, "config.json"), JsonSerializer.Serialize(config, Json.Options));
        WritePrivate(Path.Combine(root, "soulfuse.service"), ServiceText(root, settings));
        WritePrivate(Path.Combine(root, "installation.json"), JsonSerializer.Serialize(settings, Json.Options));
        Console.WriteLine("Setup complete. Run enable then start with the same --home directory. No services have been started.");
    }
    private static string FindDotnet()
    {
        var current = Environment.ProcessPath;
        if (current != null && Path.GetFileName(current) == "dotnet") return current;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            var candidate = Path.Combine(directory, "dotnet"); if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new IOException("dotnet was not found on PATH");
    }
    private static string ReadKey(string root)
    {
        var key = File.ReadAllText(Path.Combine(root, "api-key")).Trim();
        if (key.Length == 0 || key.Any(char.IsControl)) throw new InvalidDataException("Invalid api-key file");
        return key;
    }
    private static Task<int> Compose(string root, Config config, string[] arguments, bool required = true) =>
        Run("docker", new[] { "compose", "--project-directory", root, "-f", Path.Combine(root, "compose.yaml") }.Concat(arguments).ToArray(), required,
            new Dictionary<string, string> { ["SLSKD_API_KEY"] = ReadKey(root), ["SLSKFS_UID"] = getuid().ToString(), ["SLSKFS_GID"] = getgid().ToString() });
    private static async Task<int> Run(string command, string[] args, bool required = true, Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(command) { UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (environment != null) foreach (var item in environment) info.Environment[item.Key] = item.Value;
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + command);
        await process.WaitForExitAsync();
        if (required && process.ExitCode != 0) throw new IOException(command + " failed; see output above");
        return process.ExitCode;
    }
}
