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
        Rebuild();
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
        foreach (var entry in users.Values.SelectMany(x => x))
        {
            if (!config.Users.Contains(entry.Username) || entry.Path != Paths.MountPath(entry.Username, entry.RemoteFilename) || entry.Size < 0)
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
