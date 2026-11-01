using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Game.Content.Loading;
using Game.Content.Validation;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Game.Tools.Validation;

/// <summary>
/// Checks every YAML document under the content root against its generated <c>schemas/*.schema.json</c>, so the check an
/// editor runs (through the <c>yaml-language-server</c> comment) and the check <c>validate</c> runs cannot disagree.
/// Lives in the tools, not in Game.Content or Game.Shared: only the tools reference the JSON Schema library.
/// Findings carry the file, line, column and YAML path, like the other rules.
/// </summary>
public static class SchemaCheck
{
    /// <summary>Root files and the schema that describes each of their documents.</summary>
    private static readonly (string File, string Schema)[] RootFiles =
    {
        (ContentLayout.FormulasFile, "formula_set"),
        (ContentLayout.ElementsFile, "element_table"),
        (ContentLayout.BalanceFile, "balance"),
        (ContentLayout.AppearanceFile, "appearance"),
        (ContentLayout.ExpTablesFile, "exp_table"),
    };

    /// <summary>Schema file stem per content folder (<see cref="ContentLayout.Folders"/>).</summary>
    private static readonly Dictionary<string, string> FolderSchemas = new(StringComparer.Ordinal)
    {
        ["jobs"] = "job",
        ["skills"] = "skill",
        ["items"] = "item",
        ["monsters"] = "monster",
        ["statuses"] = "status",
        ["quests"] = "quest",
        ["npcs"] = "npc",
        ["dialogue"] = "dialogue",
        ["drops"] = "drop_table",
        ["maps"] = "map",
    };

    /// <summary>Checks the content under <paramref name="contentRoot"/> against the schemas in <paramref name="schemaDir"/>.</summary>
    public static IReadOnlyList<ValidationIssue> Check(string contentRoot, string schemaDir)
    {
        var issues = new List<ValidationIssue>();
        if (!Directory.Exists(schemaDir))
        {
            issues.Add(Error("", 0, 0, "", $"schema directory '{schemaDir}' does not exist; run the schema command or pass --schemas"));
            return issues;
        }
        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);

        void CheckFile(string fullPath, string schemaName)
        {
            var rel = Path.GetRelativePath(contentRoot, fullPath).Replace('\\', '/');
            if (!schemas.TryGetValue(schemaName, out var schema))
            {
                var schemaFile = Path.Combine(schemaDir, schemaName + ".schema.json");
                if (!File.Exists(schemaFile))
                {
                    issues.Add(Error(rel, 0, 0, "", $"schema '{schemaFile}' does not exist; run the schema command"));
                    return;
                }
                schemas[schemaName] = schema = JsonSchema.FromText(File.ReadAllText(schemaFile));
            }
            CheckDocuments(fullPath, rel, schema, issues);
        }

        foreach (var (folder, schemaName) in FolderSchemas.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var dir = Path.Combine(contentRoot, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.yaml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                CheckFile(file, schemaName);
        }
        foreach (var (file, schemaName) in RootFiles)
        {
            var full = Path.Combine(contentRoot, file);
            if (File.Exists(full)) CheckFile(full, schemaName);
        }
        return issues;
    }

    private static void CheckDocuments(string fullPath, string rel, JsonSchema schema, List<ValidationIssue> issues)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StreamReader(fullPath);
            stream.Load(reader);
        }
        catch (Exception e) when (e is YamlException or IOException or InvalidOperationException)
        {
            return; // The loader reports unreadable and malformed files.
        }

        foreach (var document in stream.Documents)
        {
            var instance = ToJson(document.RootNode);
            using var json = System.Text.Json.JsonDocument.Parse(instance?.ToJsonString() ?? "null");
            var result = schema.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (result.IsValid) continue;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var detail in RelevantFailures(result))
            {
                var pointer = detail.InstanceLocation.ToString();
                foreach (var (keyword, message) in detail.Errors!)
                {
                    if (IsWrapper(keyword, message)) continue;
                    if (!seen.Add(pointer + "|" + keyword + "|" + message)) continue;
                    var (path, node) = Locate(document.RootNode, pointer);
                    issues.Add(Error(rel, (int)node.Start.Line, (int)node.Start.Column, path, $"{message} ({keyword})"));
                }
            }
            if (seen.Count == 0)
                issues.Add(Error(rel, (int)document.RootNode.Start.Line, (int)document.RootNode.Start.Column, "", "document does not match its schema"));
        }
    }

    /// <summary>
    /// Errors that only say "something below me failed" (<c>properties</c>, <c>items</c>, and the "some additional properties" summary
    /// of <c>additionalProperties</c>): the failure itself is reported where it is.
    /// </summary>
    private static bool IsWrapper(string keyword, string message) =>
        keyword is "properties" or "items" || (keyword is "additionalProperties" or "patternProperties" && message.StartsWith("Some ", StringComparison.Ordinal));

    /// <summary>
    /// The failing results worth reporting. A polymorphic value (effect, objective, reward) is an <c>anyOf</c> with one branch per
    /// type, and every branch but the right one fails on its <c>type</c> const. When a branch matches the <c>type</c>, only its
    /// own failures are kept; when none does (unknown or missing <c>type</c>), the <c>anyOf</c> error alone is reported.
    /// </summary>
    private static IEnumerable<EvaluationResults> RelevantFailures(EvaluationResults result)
    {
        var failing = (result.Details ?? new List<EvaluationResults>()).Where(d => !d.IsValid && d.Errors is { Count: > 0 }).ToList();
        var dropped = new HashSet<EvaluationResults>();

        // Failing results inside a union branch, keyed by the union's evaluation path and the branch index.
        var inBranches = failing
            .Select(d => (Detail: d, Match: BranchPath.Match(d.EvaluationPath.ToString())))
            .Where(x => x.Match.Success)
            .Select(x => (x.Detail, Union: x.Match.Groups["union"].Value, Branch: x.Match.Groups["branch"].Value))
            .ToList();
        foreach (var union in inBranches.Select(x => x.Union).Distinct().OrderBy(u => u.Length))
        {
            var branches = inBranches.Where(x => x.Union == union && !dropped.Contains(x.Detail)).GroupBy(x => x.Branch).ToList();
            if (branches.Count == 0) continue;
            var unionError = failing.FirstOrDefault(d => d.Errors!.ContainsKey(union.EndsWith("anyOf", StringComparison.Ordinal) ? "anyOf" : "oneOf")
                && d.EvaluationPath.ToString() == union.Substring(0, union.LastIndexOf('/')));
            if (unionError == null)
            {
                // One branch accepted the value, so the union passed; the failures are the other branches saying "not me".
                foreach (var x in branches.SelectMany(g => g)) dropped.Add(x.Detail);
                continue;
            }
            var typePointer = unionError.InstanceLocation + "/type";
            var matching = branches.Where(g => !g.Any(x => x.Detail.Errors!.ContainsKey("const") && x.Detail.InstanceLocation.ToString() == typePointer)).ToList();
            if (matching.Count > 0) dropped.Add(unionError);
            foreach (var g in branches.Except(matching))
                foreach (var x in g) dropped.Add(x.Detail);
        }
        return failing.Where(d => !dropped.Contains(d));
    }

    /// <summary>An evaluation path inside a union branch: <c>.../anyOf/3/properties/x</c> → union <c>.../anyOf</c>, branch <c>3</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex BranchPath =
        new(@"^(?<union>.*?/(?:anyOf|oneOf))/(?<branch>[0-9]+)(/|$)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static ValidationIssue Error(string file, int line, int column, string path, string message) =>
        new(ValidationSeverity.Error, file, line, column, path, ValidationCodes.SchemaViolation, message);

    /// <summary>Walks a JSON pointer through the YAML tree. Returns the YAML-style path and the deepest node reached.</summary>
    private static (string Path, YamlNode Node) Locate(YamlNode root, string pointer)
    {
        var node = root;
        var path = new StringBuilder();
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            YamlNode? next = null;
            if (node is YamlMappingNode map)
            {
                next = map.Children.FirstOrDefault(p => (p.Key as YamlScalarNode)?.Value == segment).Value;
                if (next != null) path.Append(path.Length > 0 ? "." : "").Append(segment);
            }
            else if (node is YamlSequenceNode seq && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < seq.Children.Count)
            {
                next = seq.Children[index];
                path.Append('[').Append(index).Append(']');
            }
            if (next is null) break;
            node = next;
        }
        return (path.ToString(), node);
    }

    /// <summary>
    /// Converts a YAML node the way an editor's YAML language server does: quoted scalars are strings, plain scalars become
    /// null, boolean, integer or number when they look like one.
    /// </summary>
    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => ToObject(map),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar => ToScalar(scalar),
        _ => null,
    };

    private static JsonObject ToObject(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map.Children)
            obj[((YamlScalarNode)key).Value ?? ""] = ToJson(value);
        return obj;
    }

    private static JsonNode? ToScalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(text);
        if (text is "" or "~" or "null" or "Null" or "NULL") return null;
        if (text is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsInfinity(number)
            && text.Any(char.IsDigit) && !text.Contains(' ')) return JsonValue.Create(number);
        return JsonValue.Create(text);
    }
}
