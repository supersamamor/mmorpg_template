using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Game.Content.Loading;

/// <summary>
/// Turns a source position into a document path such as <c>effects[1].formula</c> by walking the parsed node tree.
/// A position is attributed to the last child that starts at or before it (block collections report unreliable end marks).
/// </summary>
internal static class YamlPathFinder
{
    /// <summary>The path to the innermost entry at <paramref name="mark"/>, and the node found there (for a mapping key: the mapping that owns the key).</summary>
    /// <remarks>An error on the first key of a block mapping shares its position with the mapping itself; the key wins.</remarks>
    public static (string Path, YamlNode Node) Find(YamlNode root, Mark mark) => Walk(root, mark.Index, "");

    private static (string Path, YamlNode Node) Walk(YamlNode node, long index, string path)
    {
        switch (node)
        {
            case YamlMappingNode map:
            {
                KeyValuePair<YamlNode, YamlNode>? hit = null;
                foreach (var pair in map.Children)
                {
                    if (pair.Key.Start.Index <= index) hit = pair;
                    else break;
                }
                if (hit is null) return (path, node);
                var name = (hit.Value.Key as YamlScalarNode)?.Value ?? "?";
                var child = path.Length == 0 ? name : path + "." + name;
                if (hit.Value.Key.Start.Index == index) return (child, map);
                return Walk(hit.Value.Value, index, child);
            }
            case YamlSequenceNode seq:
            {
                var hit = -1;
                for (var i = 0; i < seq.Children.Count; i++)
                {
                    if (seq.Children[i].Start.Index <= index) hit = i;
                    else break;
                }
                return hit < 0 ? (path, node) : Walk(seq.Children[hit], index, $"{path}[{hit}]");
            }
            default:
                return (path, node);
        }
    }
}
