namespace SlskFs;

public sealed class Catalog
{
    private readonly Config config;
    private readonly ISlskd api;
    private readonly object gate = new();
    private readonly SemaphoreSlim refresh = new(1);
    private Dictionary<string, Entry[]> users;
    private Dictionary<string, Entry> files = new(StringComparer.Ordinal);
    private Dictionary<string, string[]> directories = new(StringComparer.Ordinal);
    private string Filename => System.IO.Path.Combine(config.StateDirectory, "catalog.json");
    public Catalog(Config config, ISlskd api)
    {
        this.config = config; this.api = api;
        users = File.Exists(Filename) ? Json.Load<SavedCatalog>(Filename).Users : new();
        users = users.Where(x => config.Users.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var changed = false;
        users = users.ToDictionary(x => x.Key, x => x.Value.Select(entry =>
        {
            if (entry.Username != x.Key) throw new InvalidDataException("Catalog username mismatch");
            var path = Paths.MountPath(entry.Username, entry.RemoteFilename);
            if (entry.Path == path) return entry;
            // Upgrade only the old adapter-generated representation. Never URL-decode names:
            // a remote literal "%20" must remain different from an actual space.
            var legacy = "/" + Uri.EscapeDataString(entry.Username) + "/" +
                string.Join('/', entry.RemoteFilename.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
            if (entry.Path != legacy) throw new InvalidDataException("Invalid stored catalog path");
            changed = true;
            return entry with { Path = path };
        }).ToArray());
        Rebuild();
        if (changed) Json.Save(Filename, new SavedCatalog(users));
    }
    // Persist original readable paths; projection changes only the mounted view.
    // If roots overlap at the top level, retain this user's original hierarchy.
    // This avoids silently merging distinct shares or inventing unstable suffixes.
    private static Entry[] Project(Entry[] entries)
    {
        var candidates = entries.Select(entry =>
        {
            if (entry.Path != Paths.MountPath(entry.Username, entry.RemoteFilename))
                throw new InvalidDataException("Invalid catalog entry path");
            var parts = entry.RemoteFilename.Replace('\\', '/').Split('/');
            var strip = parts.Length > 1 && parts[0].StartsWith("@@", StringComparison.Ordinal) && parts[0].Length > 2;
            var top = strip ? parts[1] : parts[0];
            var owner = strip ? "prefix:" + parts[0] : "ordinary";
            var path = strip ? "/" + Paths.Segment(entry.Username) + "/" + string.Join('/', parts.Skip(1)) : entry.Path;
            return (Entry: entry, Top: top, Owner: owner, Path: path);
        }).ToArray();
        if (candidates.GroupBy(c => c.Top, StringComparer.Ordinal)
            .Any(g => g.Select(c => c.Owner).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            return entries;
        return candidates.Select(c => c.Entry with { Path = c.Path }).ToArray();
    }
    private void Rebuild()
    {
        var f = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var d = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal) { ["/"] = new(StringComparer.Ordinal) };
        void AddDirectory(string path)
        {
            if (d.ContainsKey(path)) return;
            if (f.ContainsKey(path)) throw new InvalidDataException("File/directory path collision");
            d[path] = new(StringComparer.Ordinal);
            var parent = System.IO.Path.GetDirectoryName(path)!;
            AddDirectory(parent);
            d[parent].Add(System.IO.Path.GetFileName(path));
        }
        foreach (var user in config.Users) AddDirectory("/" + Paths.Segment(user));
        foreach (var entry in users.Values.SelectMany(Project))
        {
            if (!config.Users.Contains(entry.Username) || entry.Size < 0)
                throw new InvalidDataException("Invalid catalog entry");
            if (d.ContainsKey(entry.Path) || !f.TryAdd(entry.Path, entry)) throw new InvalidDataException("Duplicate catalog path");
            var parent = System.IO.Path.GetDirectoryName(entry.Path)!;
            AddDirectory(parent); d[parent].Add(System.IO.Path.GetFileName(entry.Path));
            if (f.Count + d.Count > config.MaxCatalogEntries) throw new InvalidDataException("Catalog entry limit exceeded");
        }
        files = f; directories = d.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
    }
    public Entry FileEntry(string path) { lock (gate) return files.TryGetValue(Paths.Normalize(path), out var e) ? e : throw new FileNotFoundException("Not a catalog file"); }
    public bool IsDirectory(string path) { lock (gate) return directories.ContainsKey(Paths.Normalize(path)); }
    public string[] List(string path) { lock (gate) return directories.TryGetValue(Paths.Normalize(path), out var d) ? d : throw new DirectoryNotFoundException(); }
    public async Task<string[]> RefreshAsync(CancellationToken ct)
    {
        await refresh.WaitAsync(ct);
        try
        {
            var errors = new List<string>();
            foreach (var user in config.Users)
            {
                try
                {
                    var result = await api.BrowseAsync(user, ct);
                    lock (gate)
                    {
                        var old = users.GetValueOrDefault(user);
                        users[user] = result;
                        try { Rebuild(); Json.Save(Filename, new SavedCatalog(users)); }
                        catch { if (old == null) users.Remove(user); else users[user] = old; Rebuild(); throw; }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add($"{user}: {ex.GetType().Name}; kept prior listing"); }
            }
            return errors.ToArray();
        }
        finally { refresh.Release(); }
    }
}
