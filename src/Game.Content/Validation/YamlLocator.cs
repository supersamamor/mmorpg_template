using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Game.Content.Validation;

/// <summary>
/// Maps (file, document id, key path) to a line and column by re-reading the YAML. Only used when an issue is
/// reported, so cost is irrelevant; files are cached per run.
/// </summary>
internal sealed class YamlLocator
{
    private readonly string _root;
    private readonly Dictionary<string, List<YamlMappingNode>> _cache = new(StringComparer.Ordinal);

    public YamlLocator(string root) => _root = root;

    /// <summary>Returns 1-based (line, column) of the value at <paramref name="path"/>, or (0, 0) if it cannot be found.</summary>
    public (int Line, int Column) Find(string file, string? docId, string path)
    {
        var docs = Load(file);
        var doc = docId is null ? docs.FirstOrDefault() : docs.FirstOrDefault(d => IdOf(d) == docId);
        if (doc is null) return (0, 0);
        YamlNode node = doc;
        foreach (var (key, index) in Split(path))
        {
            YamlNode? next = null;
            if (node is YamlMappingNode map && key is not null)
                next = map.Children.FirstOrDefault(p => (p.Key as YamlScalarNode)?.Value == key).Value;
            else if (node is YamlSequenceNode seq && index is int i && i < seq.Children.Count)
                next = seq.Children[i];
            if (next is null) break; // report the deepest node we could reach
            node = next;
        }
        return ((int)node.Start.Line, (int)node.Start.Column);
    }

    private static string? IdOf(YamlMappingNode doc) =>
        (doc.Children.FirstOrDefault(p => (p.Key as YamlScalarNode)?.Value == "id").Value as YamlScalarNode)?.Value;

    private List<YamlMappingNode> Load(string file)
    {
        if (_cache.TryGetValue(file, out var cached)) return cached;
        var docs = new List<YamlMappingNode>();
        try
        {
            using var reader = new StreamReader(Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar)));
            var stream = new YamlStream();
            stream.Load(reader);
            docs.AddRange(stream.Documents.Select(d => d.RootNode).OfType<YamlMappingNode>());
        }
        catch (Exception e) when (e is IOException or YamlException or UnauthorizedAccessException) { }
        return _cache[file] = docs;
    }

    private static IEnumerable<(string? Key, int? Index)> Split(string path)
    {
        // "steps[0].objectives[1].item_id" -> steps, [0], objectives, [1], item_id
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = part.IndexOf('[');
            if (bracket < 0) { yield return (part, null); continue; }
            if (bracket > 0) yield return (part[..bracket], null);
            foreach (var idx in part[bracket..].Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(idx, out var n)) yield return (null, n);
        }
    }
}
