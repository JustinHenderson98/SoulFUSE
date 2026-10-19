using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SlskFs;

/// <summary>Local slskd allowlist management. Does not modify mounted users or transfer queues.</summary>
public static class FriendsManager
{
    private const string Prefix = @"\A(?!(?:";
    private const string Suffix = @")\z)[\s\S]*\z";
    private const string Empty = @"\A[\s\S]*\z";
    private const string LegacyPrefix = "^(?!(?:";
    private const string LegacySuffix = ")$).*$";
    public static string Pattern(IEnumerable<string> names)
    {
        var users = names.Order(StringComparer.Ordinal).ToArray();
        foreach (var name in users) ValidateName(name);
        return users.Length == 0 ? Empty : Prefix + string.Join('|', users.Select(Regex.Escape)) + Suffix;
    }
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl))
            throw new ArgumentException("Username must be 1–255 characters, with no control characters");
    }
    private static string[]? ParsePattern(string pattern)
    {
        if (pattern == Empty) return [];
        string body;
        if (pattern.StartsWith(Prefix, StringComparison.Ordinal) && pattern.EndsWith(Suffix, StringComparison.Ordinal))
            body = pattern[Prefix.Length..^Suffix.Length];
        else if (pattern.StartsWith(LegacyPrefix, StringComparison.Ordinal) && pattern.EndsWith(LegacySuffix, StringComparison.Ordinal))
            body = pattern[LegacyPrefix.Length..^LegacySuffix.Length];
        else return null;
        var tokens = new List<string>(); var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\') { i++; continue; }
            if (body[i] == '|') { tokens.Add(body[start..i]); start = i + 1; }
        }
        tokens.Add(body[start..]);
        var names = new List<string>();
        foreach (var token in tokens)
        {
            string name;
            try { name = Regex.Unescape(token); ValidateName(name); }
            catch (ArgumentException) { return null; }
            // Only literal usernames: never guess at the meaning of arbitrary regex.
            if (Regex.Escape(name) != token) return null;
            names.Add(name);
        }
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }
    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out var value) || value is not YamlMappingNode map)
            throw new InvalidDataException($"Expected a YAML mapping at '{key}'. See FRIENDS.md for setup.");
        return map;
    }
    private sealed record Parsed(string Text, YamlScalarNode Key, YamlSequenceNode Sequence, string[] Patterns,
        int ManagedIndex, string[] Names, bool CaseSensitive, bool NoConfigWatch);
    private static Parsed Parse(string text)
    {
        YamlStream yaml = new();
        try { yaml.Load(new StringReader(text)); }
        catch (YamlException) { throw new InvalidDataException("slskd YAML could not be parsed; file was not changed"); }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("Expected one YAML mapping document");
        // Anchors/merges can make an edit affect other settings. Reject them explicitly.
        void Check(YamlNode node)
        {
            if (!node.Anchor.IsEmpty) throw new InvalidDataException("YAML anchors are not supported by friend editing; file was not changed");
            if (node is YamlMappingNode map)
            {
                foreach (var pair in map.Children)
                {
                    if (pair.Key is not YamlScalarNode scalar || scalar.Value == "<<")
                        throw new InvalidDataException("Complex YAML keys/merges are not supported by friend editing");
                    Check(pair.Key); Check(pair.Value);
                }
            }
            if (node is YamlSequenceNode seq) foreach (var child in seq.Children) Check(child);
        }
        Check(root);
        var blacklist = Mapping(Mapping(Mapping(root, "transfers"), "groups"), "blacklisted");
        var pair = blacklist.Children.SingleOrDefault(p => p.Key is YamlScalarNode s && s.Value == "patterns");
        if (pair.Key is not YamlScalarNode key || pair.Value is not YamlSequenceNode sequence)
            throw new InvalidDataException("Configure transfers.groups.blacklisted.patterns as a YAML list; see FRIENDS.md");
        var patterns = sequence.Children.Select(n => n is YamlScalarNode s && s.Value != null ? s.Value :
            throw new InvalidDataException("Blacklist patterns must be strings")).ToArray();
        var matches = patterns.Select((p, i) => (Index:i, Names:ParsePattern(p))).Where(x => x.Names != null).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Multiple allowlist expressions found; resolve them manually before using friends commands");
        bool Flag(string name) => root.Children.TryGetValue(new YamlScalarNode("flags"), out var flags) &&
            flags is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(name), out var v) &&
            v is YamlScalarNode scalar && string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase);
        return new(text, key, sequence, patterns, matches.Length == 0 ? -1 : matches[0].Index,
            matches.Length == 0 ? [] : matches[0].Names!, Flag("case_sensitive_regex"), Flag("no_config_watch"));
    }
    // Replace only the full lines occupied by the patterns key/value, preserving the rest byte-for-byte.
    private static string Rewrite(Parsed parsed, string pattern)
    {
        var text = parsed.Text;
        var keyIndex = checked((int)parsed.Key.Start.Index);
        var lineStart = keyIndex == 0 ? 0 : text.LastIndexOf('\n', keyIndex - 1) + 1;
        var indent = text[lineStart..keyIndex];
        if (indent.Any(c => c != ' ') || !text[keyIndex..].StartsWith("patterns:", StringComparison.Ordinal))
            throw new InvalidDataException("Use a plain block-style 'patterns:' key; file was not changed");
        int lastEnd;
        if (parsed.Sequence.Children.Count == 0)
        {
            var emptyLineEnd = text.IndexOf('\n', keyIndex);
            var line = text[keyIndex..(emptyLineEnd < 0 ? text.Length : emptyLineEnd)];
            var match = Regex.Match(line, @"^patterns:[ \t]*\[\]");
            if (!match.Success) throw new InvalidDataException("Use patterns: [] to initialize an empty allowlist");
            lastEnd = keyIndex + match.Length;
        }
        else lastEnd = checked((int)parsed.Sequence.Children[^1].End.Index);
        // Flow lists are supported only as the empty initialization form [].
        if (parsed.Sequence.Style == SequenceStyle.Flow && parsed.Sequence.Children.Count != 0)
            throw new InvalidDataException("Convert patterns to a block-style YAML list before editing");
        var lineEnd = text.IndexOf('\n', lastEnd);
        if (lineEnd < 0) lineEnd = text.Length; else lineEnd++;
        var trailing = text[lastEnd..lineEnd].Trim();
        if (trailing.Length != 0 && !trailing.StartsWith('#'))
            throw new InvalidDataException("Unexpected content after patterns; file was not changed");
        var patterns = parsed.Patterns.ToList();
        if (parsed.ManagedIndex < 0) patterns.Add(pattern); else patterns[parsed.ManagedIndex] = pattern;
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = indent + "patterns:" + nl + string.Join("", patterns.Select(p =>
        {
            if (p.Any(c => c is '\r' or '\n')) throw new InvalidDataException("Multiline blacklist expressions are not supported by friend editing");
            return indent + "  - '" + p.Replace("'", "''") + "'" + nl;
        }));
        var result = text[..lineStart] + block + text[lineEnd..];
        var check = Parse(result);
        if (!check.Patterns.SequenceEqual(patterns)) throw new InvalidDataException("Edited YAML failed verification");
        return result;
    }
    public static Response Execute(Config config, string action, string? username)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Friend management requires Linux");
        if (action is not ("list" or "add" or "remove") || (action == "list" ? username != null : username == null))
            throw new ArgumentException("Usage: soulfuse CONFIG.json friends list|add|remove [USERNAME]");
        if (username != null) ValidateName(username);
        var path = config.SlskdConfigPath;
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Set slskdConfigPath to the absolute host path of slskd.yml (not its container path)");
        if (new FileInfo(path).LinkTarget != null) throw new IOException("slskdConfigPath must be a regular file, not a symlink");
        using var fileLock = new FileStream(path + ".soulfuse.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("slskd config exceeds 2 MiB editing limit");
        var text = File.ReadAllText(path);
        var parsed = Parse(text);
        var comparer = parsed.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var names = parsed.Names.ToHashSet(comparer);
        if (action == "list") return new(true, parsed.ManagedIndex < 0 ? "No managed allowlist found; access is controlled by existing slskd rules" : "Allowed users (other slskd deny rules still apply)",
            new { configured = parsed.ManagedIndex >= 0, users = names.Order(StringComparer.Ordinal).ToArray(),
                caseSensitive = parsed.CaseSensitive, otherPatternCount = parsed.Patterns.Length - (parsed.ManagedIndex >= 0 ? 1 : 0) });
        if (action == "remove" && parsed.ManagedIndex < 0)
            throw new InvalidOperationException("No managed allowlist found; refusing to guess your current access policy. Add a first friend to establish an allowlist.");
        bool changed = action == "add" ? names.Add(username!) : names.Remove(username!);
        if (!changed) return new(true, "Allowlist unchanged", names.Order(StringComparer.Ordinal).ToArray());
        var updated = Rewrite(parsed, Pattern(names));
        // Best-effort conflict detection; do not concurrently edit through the web UI.
        if (File.ReadAllText(path) != text) throw new IOException("slskd config changed during editing; retry");
        var temp = path + ".soulfuse-" + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = path + ".soulfuse.bak";
        try
        {
            void WritePrivate(string target, string content)
            {
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                using var stream = new FileStream(target, new FileStreamOptions { Mode = FileMode.CreateNew,
                    Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
                var bytes = Encoding.UTF8.GetBytes(content); stream.Write(bytes); stream.Flush(true);
            }
            WritePrivate(temp, text);
            File.Move(temp, backup, true); // One owner-only backup, including credentials; never printed.
            WritePrivate(temp, updated);
            File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            if (File.ReadAllText(path) != text) throw new IOException("slskd config changed during editing; retry");
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new(true, "Allowlist saved. " + (parsed.NoConfigWatch ? "Restart slskd: configuration watching is disabled. " : "slskd must reload the file before access changes take effect. ") +
            "Existing queued/active transfers are not cancelled; other deny rules still apply.",
            new { users = names.Order(StringComparer.Ordinal).ToArray(), blocksEveryone = names.Count == 0 });
    }
}
