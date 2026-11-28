using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Game.Tools.Editor;

/// <summary>How a save rewrote a YAML file.</summary>
public enum YamlWriteMode
{
    /// <summary>Only scalar values changed: each was replaced where it stood, so comments, key order, quoting and layout are all kept.</summary>
    InPlace,

    /// <summary>Keys or list items were added or removed: the document was written again. Key order is kept (new keys go after the old ones); comments are lost.</summary>
    Rewritten,
}

/// <summary>
/// Turns one content YAML document into JSON for the editor's forms and writes the forms' JSON back (T-801). YamlDotNet's representation model drops comments, so a
/// save that only changes values edits the file's text at each changed scalar (its start and end marks) and keeps everything else byte for byte. A save that changes
/// the shape (a key or list item added or removed, a value that becomes a list) writes the whole document again in the old key order, and says that comments were
/// lost; the <c>yaml-language-server</c> schema line at the top is kept either way. Plain scalars are typed as YAML's core schema types them (numbers, booleans, null);
/// quoted ones are strings.
/// </summary>
public static class YamlDocumentEdit
{
    private static readonly Regex Integer = new(@"^[-+]?[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Float = new(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex PlainSafe = new(@"^[A-Za-z_][A-Za-z0-9 _.,'!?()/+\-]*$", RegexOptions.CultureInvariant);

    /// <summary>Reads the single document of <paramref name="yaml"/> as JSON. Throws <see cref="YamlException"/> or <see cref="InvalidDataException"/> when it is not one mapping.</summary>
    public static JsonObject ToJson(string yaml)
    {
        var root = Root(yaml);
        return (JsonObject)Convert(root)!;
    }

    /// <summary>
    /// The text of <paramref name="yaml"/> with its document replaced by <paramref name="document"/>, and how it was written. The result is not validated here: the caller
    /// validates the whole content set with it before writing anything.
    /// </summary>
    public static (string Text, YamlWriteMode Mode) Apply(string yaml, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = Root(yaml);
        var edits = new List<(int Start, int End, string Text)>();
        if (Collect(root, document, edits, yaml))
        {
            var text = new StringBuilder(yaml);
            foreach (var (start, end, replacement) in edits.OrderByDescending(e => e.Start))
            {
                text.Remove(start, end - start);
                text.Insert(start, replacement);
            }
            return (text.ToString(), YamlWriteMode.InPlace);
        }
        return (Rewrite(yaml, root, document), YamlWriteMode.Rewritten);
    }

    private static YamlMappingNode Root(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1) throw new InvalidDataException($"the file holds {stream.Documents.Count} documents; the editor edits files of one");
        return stream.Documents[0].RootNode as YamlMappingNode ?? throw new InvalidDataException("the document is not a mapping");
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(p => KeyValuePair.Create(((YamlScalarNode)p.Key).Value ?? "", Convert(p.Value)))),
        YamlSequenceNode list => new JsonArray(list.Children.Select(Convert).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => throw new InvalidDataException($"unsupported YAML node at {node.Start}"),
    };

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(value);
        if (value is "" or "~" or "null" or "Null" or "NULL") return null;
        if (value is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (Integer.IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return JsonValue.Create(n);
        if (Float.IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(value);
    }

    /// <summary>Collects in-place scalar replacements; false when the shapes differ (a rewrite is needed).</summary>
    private static bool Collect(YamlNode node, JsonNode? json, List<(int, int, string)> edits, string text)
    {
        switch (node)
        {
            case YamlMappingNode map:
                if (json is not JsonObject obj) return false;
                var keys = map.Children.Keys.Select(k => ((YamlScalarNode)k).Value ?? "").ToList();
                if (keys.Count != obj.Count || keys.Any(k => !obj.ContainsKey(k))) return false;
                foreach (var (key, value) in map.Children)
                    if (!Collect(value, obj[((YamlScalarNode)key).Value ?? ""], edits, text)) return false;
                return true;
            case YamlSequenceNode list:
                if (json is not JsonArray array || array.Count != list.Children.Count) return false;
                for (var i = 0; i < array.Count; i++)
                    if (!Collect(list.Children[i], array[i], edits, text)) return false;
                return true;
            case YamlScalarNode scalar:
                if (json is JsonObject or JsonArray) return false;
                if (Same(Scalar(scalar), json)) return true;
                if (json is null && scalar.Style == ScalarStyle.Plain) return false;   // a value that becomes empty: the key goes, which is a shape change
                var start = (int)scalar.Start.Index;
                var end = (int)scalar.End.Index;
                if (start < 0 || end > text.Length || end <= start) return false;
                edits.Add((start, end, Render(json, scalar.Style is ScalarStyle.DoubleQuoted or ScalarStyle.SingleQuoted)));
                return true;
            default:
                return false;
        }
    }

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    /// <summary>One scalar as YAML: a number or boolean plainly, a string plainly when that reads back as the same string, else double-quoted.</summary>
    private static string Render(JsonNode? value, bool quoted = false)
    {
        if (value is null) return "null";
        switch (value.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.True: return "true";
            case System.Text.Json.JsonValueKind.False: return "false";
            case System.Text.Json.JsonValueKind.Number: return value.ToJsonString();
        }
        var s = value.GetValue<string>();
        if (!quoted && PlainSafe.IsMatch(s) && !s.EndsWith(' ') && Scalar(new YamlScalarNode(s)) is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String && v.GetValue<string>() == s)
            return s;
        return value.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });   // JSON's escapes are YAML's double-quoted ones
    }

    private static string Rewrite(string yaml, YamlMappingNode root, JsonObject document)
    {
        var text = new StringBuilder();
        var nl = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var first = yaml.Split('\n')[0].TrimEnd('\r');
        if (first.StartsWith("# yaml-language-server:", StringComparison.Ordinal)) text.Append(first).Append(nl);
        WriteMapping(text, Ordered(root, document), 0, nl);
        return text.ToString();
    }

    /// <summary>The document's keys in the old file's order, then the new ones in the order the form sent them; empty values left out.</summary>
    private static IEnumerable<KeyValuePair<string, JsonNode?>> Ordered(YamlMappingNode? old, JsonObject document)
    {
        var order = old?.Children.Keys.Select(k => ((YamlScalarNode)k).Value ?? "").ToList() ?? [];
        return document.Where(p => p.Value is not null).OrderBy(p => order.IndexOf(p.Key) is var i && i >= 0 ? i : int.MaxValue).ToList();
    }

    private static void WriteMapping(StringBuilder text, IEnumerable<KeyValuePair<string, JsonNode?>> pairs, int indent, string nl)
    {
        foreach (var (key, value) in pairs)
        {
            text.Append(' ', indent).Append(key).Append(':');
            WriteValue(text, value, indent, nl);
        }
    }

    private static void WriteValue(StringBuilder text, JsonNode? value, int indent, string nl)
    {
        switch (value)
        {
            case JsonObject obj when obj.Count == 0:
                text.Append(" {}").Append(nl);
                break;
            case JsonObject obj:
                text.Append(nl);
                WriteMapping(text, Ordered(null, obj), indent + 2, nl);
                break;
            case JsonArray array when array.Count == 0:
                text.Append(" []").Append(nl);
                break;
            case JsonArray array:
                text.Append(nl);
                foreach (var item in array)
                {
                    text.Append(' ', indent + 2).Append('-');
                    if (item is JsonObject o && o.Count > 0)
                    {
                        // The first key on the dash's line, the rest under it.
                        var pairs = Ordered(null, o).ToList();
                        text.Append(' ').Append(pairs[0].Key).Append(':');
                        WriteValue(text, pairs[0].Value, indent + 4, nl);
                        WriteMapping(text, pairs.Skip(1), indent + 4, nl);
                    }
                    else WriteValue(text, item, indent + 2, nl);
                }
                break;
            default:
                text.Append(' ').Append(Render(value)).Append(nl);
                break;
        }
    }
}
