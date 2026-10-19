using YamlDotNet.RepresentationModel;

namespace SlskFs;

// New bindings use an identifiable container namespace. Existing hand-written
// bindings are listed but never removed implicitly.
public static class SharesManager
{
    private static YamlScalarNode Scalar(string value) => new(value);
    private static YamlMappingNode Map(YamlMappingNode parent, string key) =>
        parent.Children.TryGetValue(Scalar(key), out var node) && node is YamlMappingNode map ? map : throw new InvalidDataException("Missing mapping: " + key);
    private static YamlSequenceNode Seq(YamlMappingNode parent, string key) =>
        parent.Children.TryGetValue(Scalar(key), out var node) && node is YamlSequenceNode seq ? seq : throw new InvalidDataException("Missing list: " + key);
    private static (YamlStream, YamlMappingNode) Read(string text)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch { throw new InvalidDataException("Could not parse configuration YAML"); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map) throw new InvalidDataException("Expected one configuration mapping");
        return (stream, map);
    }
    private static string Render(YamlStream stream) { using var writer = new StringWriter(); stream.Save(writer, false); return writer.ToString(); }
    public static void Execute(string root, Config config, string[] args)
    {
        var yamlPath = config.SlskdConfigPath ?? throw new ArgumentException("slskdConfigPath is required");
        var composePath = Path.Combine(root, "compose.yaml");
        using var gate = new FileStream(Path.Combine(root, "shares.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var originalYaml = File.ReadAllText(yamlPath); var originalCompose = File.ReadAllText(composePath);
        var (yaml, y) = Read(originalYaml); var (compose, c) = Read(originalCompose);
        var dirs = Seq(Map(y, "shares"), "directories");
        var volumes = Seq(Map(Map(c, "services"), "slskd"), "volumes");
        if (args.Length == 1 && args[0] == "list") { foreach (var dir in dirs.Children) Console.WriteLine(dir.ToString()); return; }
        if (args.Length < 2 || args[0] is not ("add" or "remove") || args.Length != (args[0] == "add" ? 3 : 2)) throw new ArgumentException("shares list | add NAME ABSOLUTE_FOLDER | remove NAME");
        var name = args[1]; Paths.Segment(name);
        if (name.Any(char.IsControl) || name.IndexOfAny(['[', ']', '\\']) >= 0) throw new ArgumentException("Invalid share name");
        var target = "/soulfuse-shares/" + name;
        var share = "[" + name + "]" + target;
        var matches = dirs.Children.Where(d => d is YamlScalarNode s && (s.Value ?? "").StartsWith("[" + name + "]", StringComparison.Ordinal)).ToArray();
        var bindings = volumes.Children.OfType<YamlMappingNode>().Where(v => v.Children.TryGetValue(Scalar("target"), out var t) && t.ToString() == target).ToArray();
        if (args[0] == "add")
        {
            var source = args[2];
            if (!Path.IsPathFullyQualified(source) || source.Any(char.IsControl) || !Directory.Exists(source)) throw new ArgumentException("Shared folder must be an existing absolute directory");
            if (matches.Length != 0 || bindings.Length != 0) throw new IOException("Share name or binding already exists");
            dirs.Add(Scalar(share));
            volumes.Add(new YamlMappingNode { { "type", "bind" }, { "source", source.Replace("$", "$$") }, { "target", target }, { "read_only", "true" } });
        }
        else
        {
            if (matches.Length != 1 || matches[0].ToString() != share || bindings.Length != 1) throw new IOException("Only shares added with this command can be removed; edit existing shares in Compose and slskd YAML");
            dirs.Children.Remove(matches[0]); volumes.Children.Remove(bindings[0]);
        }
        var nextYaml = Render(yaml); var nextCompose = Render(compose);
        if (File.ReadAllText(yamlPath) != originalYaml || File.ReadAllText(composePath) != originalCompose) throw new IOException("Configuration changed concurrently; retry");
        Installation.WritePrivate(yamlPath + ".before-shares", originalYaml);
        Installation.WritePrivate(composePath + ".before-shares", originalCompose);
        Installation.WritePrivate(composePath, nextCompose);
        try { Installation.WritePrivate(yamlPath, nextYaml); }
        catch { Installation.WritePrivate(composePath, originalCompose); throw; }
        Console.WriteLine("Share saved. Restart with stop then start to recreate the slskd container. Original files backed up as .before-shares. YAML comments may be reformatted.");
    }
}
