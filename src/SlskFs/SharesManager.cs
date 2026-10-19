using YamlDotNet.RepresentationModel;
using System.Text.Json;

namespace SlskFs;

// Remove share declarations independently of bindings. Only unambiguous, unused
// bind mounts outside application storage may be removed from Compose.
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
    private sealed record Binding(YamlNode Node, string Source, string Target, bool IsBind);
    private static string Field(YamlMappingNode map, string name) => map.Children.TryGetValue(Scalar(name), out var n) ? n.ToString() : "";
    private static Binding? ParseBinding(YamlNode node)
    {
        if (node is YamlMappingNode map)
        {
            var target = Field(map, "target");
            return target.StartsWith('/') ? new(node, Field(map, "source"), target.TrimEnd('/'), Field(map, "type") == "bind") : null;
        }
        if (node is YamlScalarNode scalar)
        {
            var parts = (scalar.Value ?? "").Split(':');
            if (parts.Length is 2 or 3 && parts[1].StartsWith('/'))
                return new(node, parts[0], parts[1].TrimEnd('/'), parts[0].StartsWith('/') || parts[0].StartsWith('.') );
        }
        return null; // Never guess at interpolated targets or ambiguous syntax.
    }
    private static (string Name, string Path) ParseShare(YamlNode node)
    {
        if (node is not YamlScalarNode scalar || string.IsNullOrEmpty(scalar.Value)) throw new InvalidDataException("Expected a share path string");
        var value = scalar.Value;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end > 1 && end + 1 < value.Length) return (value[1..end], value[(end + 1)..].TrimEnd('/'));
            throw new InvalidDataException("Invalid share alias");
        }
        return (Path.GetFileName(value.TrimEnd('/')), value.TrimEnd('/'));
    }
    private static bool ContainsPath(string parent, string child) => parent == child || child.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);
    private static string? HostPath(string root, Binding? binding, string remote)
    {
        if (binding == null || !binding.IsBind || binding.Source.Length == 0) return null;
        // Compose variable expressions remain visibly unresolved; no shell evaluation.
        var source = binding.Source.Replace("$$", "\u0001");
        if (source.Contains('$')) return binding.Source + remote[binding.Target.Length..];
        source = source.Replace('\u0001', '$');
        return Path.GetFullPath(source + remote[binding.Target.Length..], root);
    }
    public static void Execute(string root, Config config, string[] args)
    {
        var yamlPath = config.SlskdConfigPath ?? throw new ArgumentException("slskdConfigPath is required");
        var composePath = Path.Combine(root, "compose.yaml");
        using var gate = new FileStream(Path.Combine(root, "shares.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var originalYaml = File.ReadAllText(yamlPath); var originalCompose = File.ReadAllText(composePath);
        var (yaml, y) = Read(originalYaml); var (compose, c) = Read(originalCompose);
        var dirs = Seq(Map(y, "shares"), "directories");
        var volumes = Seq(Map(Map(c, "services"), "slskd"), "volumes");
        var declared = dirs.Children.Select(d => (Node: d, Share: ParseShare(d))).ToArray();
        var parsedBindings = volumes.Children.Select(ParseBinding).Where(b => b != null).Cast<Binding>().ToArray();
        if (args.Length == 1 && args[0] == "list")
        {
            var items = declared.Select(d => {
                var candidates = parsedBindings.Where(b => ContainsPath(b.Target, d.Share.Path)).OrderByDescending(b => b.Target.Length).ToArray();
                var binding = candidates.FirstOrDefault();
                if (binding != null && candidates.Count(b => b.Target == binding.Target) > 1) binding = null;
                return new { name = d.Share.Name, hostPath = HostPath(root, binding, d.Share.Path), containerPath = d.Share.Path, declaration = d.Node.ToString() };
            });
            Console.WriteLine(JsonSerializer.Serialize(items, Json.Options)); return;
        }
        if (args.Length < 2 || args[0] is not ("add" or "remove") || args.Length != (args[0] == "add" ? 3 : 2)) throw new ArgumentException("shares list | add NAME ABSOLUTE_FOLDER | remove NAME");
        var name = args[1]; Paths.Segment(name);
        if (name.Any(char.IsControl) || name.IndexOfAny(['[', ']', '\\']) >= 0) throw new ArgumentException("Invalid share name");
        var target = "/soulfuse-shares/" + name;
        var share = "[" + name + "]" + target;
        var matches = declared.Where(d => d.Share.Name == name).ToArray();
        var bindings = parsedBindings.Where(b => b.Target == target).ToArray();
        var retainedBinding = false;
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
            if (matches.Length != 1) throw new IOException("Share name was not found or is ambiguous; use shares list");
            var selected = matches[0];
            var exact = parsedBindings.Where(b => b.Target == selected.Share.Path).ToArray();
            var otherPaths = declared.Where(d => d.Node != selected.Node).Select(d => d.Share.Path).ToArray();
            var protectedPaths = new[] { "/app", config.RemoteDownloadsRoot, "/app/incomplete" };
            // Unknown volume syntax or variable share paths prevent automatic binding cleanup.
            var canRemove = exact.Length == 1 && exact[0].IsBind && parsedBindings.Length == volumes.Children.Count &&
                !declared.Any(d => !d.Share.Path.StartsWith('/') || d.Share.Path.Contains('$')) &&
                !otherPaths.Any(p => ContainsPath(exact[0].Target, p) || ContainsPath(p, exact[0].Target)) &&
                !protectedPaths.Any(p => ContainsPath(exact[0].Target, p) || ContainsPath(p, exact[0].Target));
            dirs.Children.Remove(selected.Node);
            if (canRemove) volumes.Children.Remove(exact[0].Node);
            else retainedBinding = true;
        }
        var nextYaml = Render(yaml); var nextCompose = Render(compose);
        if (File.ReadAllText(yamlPath) != originalYaml || File.ReadAllText(composePath) != originalCompose) throw new IOException("Configuration changed concurrently; retry");
        Installation.WritePrivate(yamlPath + ".before-shares", originalYaml);
        Installation.WritePrivate(composePath + ".before-shares", originalCompose);
        Installation.WritePrivate(composePath, nextCompose);
        try { Installation.WritePrivate(yamlPath, nextYaml); }
        catch { Installation.WritePrivate(composePath, originalCompose); throw; }
        if (retainedBinding) Console.WriteLine("Share declaration removed; its container binding was retained because it is shared, protected, or cannot be resolved safely. Source files were not deleted.");
        Console.WriteLine("Share saved. Restart with stop then start to recreate the slskd container. Original files backed up as .before-shares. YAML comments may be reformatted.");
    }
}
