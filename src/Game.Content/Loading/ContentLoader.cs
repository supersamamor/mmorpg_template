using System.Text.RegularExpressions;
using Game.Content.Registry;
using Game.Shared.Definitions;
using Game.Shared.World;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Game.Content.Loading;

/// <summary>
/// Loads a content root (<c>content/</c>) into a <see cref="ContentDatabase"/>.
/// <para>
/// Layout: see <see cref="ContentLayout"/>. The folder decides the definition type; every <c>*.yaml</c> file in it is
/// loaded, and every YAML document in a file (separated by <c>---</c>) is its own definition.
/// Only parse/shape errors, id prefix/format errors and duplicate ids are reported here; ids that point at other
/// definitions and asset keys are checked by the cross-reference validator (T-102).
/// </para>
/// <para>Loading never stops at the first error. At most one error is reported per YAML document that fails to
/// deserialize (the parser cannot continue inside a broken document); other documents and files are still checked.</para>
/// </summary>
public static class ContentLoader
{
    private static readonly IDeserializer Deserializer = YamlSetup.BuildDeserializer();
    private static readonly Regex IdTail = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>Loads every YAML file under <paramref name="root"/>. Deterministic: files are processed in ordinal path order.</summary>
    public static LoadResult Load(string root)
    {
        var errors = new List<ContentError>();
        if (!Directory.Exists(root))
        {
            errors.Add(new ContentError(root, 0, 0, "", ContentErrorCodes.RootMissing, $"Content root '{root}' does not exist."));
            return new LoadResult(null, errors);
        }

        var ctx = new Context(Path.GetFullPath(root), errors);

        var jobs = ctx.LoadFolder<JobDef>();
        var skills = ctx.LoadFolder<SkillDef>();
        var items = ctx.LoadFolder<ItemDef>();
        var monsters = ctx.LoadFolder<MonsterDef>();
        var statuses = ctx.LoadFolder<StatusDef>();
        var quests = ctx.LoadFolder<QuestDef>();
        var npcs = ctx.LoadFolder<NpcDef>();
        var dialogue = ctx.LoadFolder<DialogueDef>();
        var drops = ctx.LoadFolder<DropTableDef>();
        var maps = ctx.LoadFolder<MapDef>();
        var expTables = ctx.LoadRootDefs<ExpTableDef>(ContentLayout.ExpTablesFile);
        var formulas = ctx.LoadSingleton<FormulaSetDef>(ContentLayout.FormulasFile);
        var elements = ctx.LoadSingleton<ElementTableDef>(ContentLayout.ElementsFile);
        var balance = ctx.LoadSingleton<BalanceDef>(ContentLayout.BalanceFile);

        ctx.ReportUnexpectedFiles();

        if (errors.Count > 0)
        {
            return new LoadResult(null, errors
                .OrderBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line).ThenBy(e => e.Column).ToList());
        }

        var grids = new Dictionary<string, MapGrid>(StringComparer.Ordinal);
        foreach (var map in maps)
        {
            var grid = GridLoader.Load(ctx.Root, map, ctx.Sources[map.Id], errors);
            if (grid != null) grids.Add(map.Id, grid);
        }
        if (errors.Count > 0)
        {
            return new LoadResult(null, errors
                .OrderBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line).ThenBy(e => e.Column).ToList());
        }

        var db = new ContentDatabase(
            new Registry<JobDef>(jobs), new Registry<SkillDef>(skills), new Registry<ItemDef>(items),
            new Registry<MonsterDef>(monsters), new Registry<StatusDef>(statuses), new Registry<QuestDef>(quests),
            new Registry<NpcDef>(npcs), new Registry<DialogueDef>(dialogue), new Registry<DropTableDef>(drops),
            new Registry<MapDef>(maps), new Registry<ExpTableDef>(expTables), formulas!, elements!, balance!, ctx.Sources, grids);
        return new LoadResult(db, errors);
    }

    private sealed class Context
    {
        private readonly string _root;
        private readonly List<ContentError> _errors;
        private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

        public Context(string root, List<ContentError> errors)
        {
            _root = root;
            _errors = errors;
        }

        public string Root => _root;

        public Dictionary<string, SourceLocation> Sources { get; } = new(StringComparer.Ordinal);

        private string Rel(string fullPath) => Path.GetRelativePath(_root, fullPath).Replace('\\', '/');

        private void Add(string file, long line, long col, string path, string code, string message) =>
            _errors.Add(new ContentError(file, (int)line, (int)col, path, code, message));

        /// <summary>Loads every <c>*.yaml</c> under the folder that holds <typeparamref name="T"/> (see <see cref="ContentLayout.Folders"/>).</summary>
        public List<T> LoadFolder<T>() where T : class, IDefinition
        {
            var folder = ContentLayout.Folders.First(kv => kv.Value == typeof(T)).Key;
            var dir = Path.Combine(_root, folder);
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.yaml", SearchOption.AllDirectories)
                : Array.Empty<string>();
            var defs = new List<(T Def, SourceLocation At)>();
            foreach (var file in files.OrderBy(f => Rel(f), StringComparer.Ordinal))
            {
                _consumed.Add(file);
                defs.AddRange(LoadDocuments<T>(file));
            }
            return CheckIds(defs);
        }

        /// <summary>Loads a root file in which each document is an id-keyed definition.</summary>
        public List<T> LoadRootDefs<T>(string fileName) where T : class, IDefinition
        {
            var file = Path.Combine(_root, fileName);
            if (!RequireFile(file)) return new List<T>();
            _consumed.Add(file);
            return CheckIds(LoadDocuments<T>(file));
        }

        /// <summary>Loads a root file that holds exactly one id-less document.</summary>
        public T? LoadSingleton<T>(string fileName) where T : class
        {
            var file = Path.Combine(_root, fileName);
            if (!RequireFile(file)) return null;
            _consumed.Add(file);
            var docs = LoadDocuments<T>(file);
            for (var i = 1; i < docs.Count; i++)
                Add(Rel(file), docs[i].At.Line, docs[i].At.Column, "", ContentErrorCodes.ExtraDocument, $"{fileName} must hold a single document.");
            return docs.Count > 0 ? docs[0].Def : null;
        }

        public void ReportUnexpectedFiles()
        {
            foreach (var file in Directory.GetFiles(_root, "*.yaml", SearchOption.AllDirectories)
                         .Where(f => !_consumed.Contains(f)).OrderBy(f => Rel(f), StringComparer.Ordinal))
            {
                Add(Rel(file), 0, 0, "", ContentErrorCodes.FileUnexpected,
                    "YAML file is outside the content layout (" + string.Join("/, ", ContentLayout.Folders.Keys) + "/, or the root files " +
                    $"{ContentLayout.FormulasFile}, {ContentLayout.ElementsFile}, {ContentLayout.BalanceFile}, {ContentLayout.ExpTablesFile}) and was not loaded.");
            }
        }

        private bool RequireFile(string file)
        {
            if (File.Exists(file)) return true;
            Add(Rel(file), 0, 0, "", ContentErrorCodes.FileMissing, $"Required file '{Rel(file)}' is missing.");
            return false;
        }

        private List<T> CheckIds<T>(List<(T Def, SourceLocation At)> defs) where T : class, IDefinition
        {
            var prefix = DefinitionIdPrefixes.ByType[typeof(T)];
            var seen = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);
            var result = new List<T>();
            foreach (var (def, at) in defs)
            {
                var id = def.Id;
                if (string.IsNullOrEmpty(id))
                {
                    Add(at.File, at.Line, at.Column, "id", ContentErrorCodes.IdMissing, $"{typeof(T).Name} has no id (expected '{prefix}<name>').");
                    continue;
                }
                if (!id.StartsWith(prefix, StringComparison.Ordinal))
                {
                    Add(at.File, at.Line, at.Column, "id", ContentErrorCodes.IdWrongPrefix, $"Id '{id}' must start with '{prefix}' for a {typeof(T).Name}.");
                    continue;
                }
                if (!IdTail.IsMatch(id.Substring(prefix.Length)))
                {
                    Add(at.File, at.Line, at.Column, "id", ContentErrorCodes.IdInvalid, $"Id '{id}' must be '{prefix}' followed by lowercase letters, digits and '_'.");
                    continue;
                }
                if (seen.TryGetValue(id, out var first))
                {
                    Add(at.File, at.Line, at.Column, "id", ContentErrorCodes.IdDuplicate, $"Duplicate id '{id}' (first defined at {first}).");
                    continue;
                }
                seen.Add(id, at);
                Sources[id] = at;
                result.Add(def);
            }
            return result;
        }

        /// <summary>Deserializes every document of a file as <typeparamref name="T"/>, reporting errors and returning what parsed.</summary>
        private List<(T Def, SourceLocation At)> LoadDocuments<T>(string fullPath) where T : class
        {
            var rel = Rel(fullPath);
            var result = new List<(T, SourceLocation)>();

            string text;
            try { text = File.ReadAllText(fullPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Add(rel, 0, 0, "", ContentErrorCodes.FileUnreadable, e.Message);
                return result;
            }

            // Pass 1: syntax, and the node tree used to turn error positions into paths.
            var stream = new YamlStream();
            try { stream.Load(new StringReader(text)); }
            catch (YamlException e)
            {
                Add(rel, e.Start.Line, e.Start.Column, "", ContentErrorCodes.YamlSyntax, Innermost(e).Message);
                return result;
            }
            catch (InvalidOperationException e)
            {
                // YamlDotNet's scanner throws this (without a position) on some malformed scalars.
                Add(rel, 0, 0, "", ContentErrorCodes.YamlSyntax, "Malformed YAML: " + e.Message);
                return result;
            }

            // Pass 2: typed deserialization, one document at a time.
            var parser = new Parser(new StringReader(text));
            var index = 0;
            try
            {
                parser.Consume<StreamStart>();
                while (parser.Accept<DocumentStart>(out _))
                {
                    var node = stream.Documents[index++].RootNode;
                    try
                    {
                        var value = Deserializer.Deserialize(parser, typeof(T));
                        if (value is T def)
                        {
                            // Report the line where the content starts rather than the '---' marker.
                            result.Add((def, new SourceLocation(rel, (int)node.Start.Line, (int)node.Start.Column)));
                        }
                        else
                        {
                            Add(rel, node.Start.Line, node.Start.Column, "", ContentErrorCodes.EmptyDocument, "Document is empty.");
                        }
                    }
                    catch (YamlException e)
                    {
                        Report(rel, node, e);
                        SkipToDocumentEnd(parser);
                    }
                }
            }
            catch (YamlException e)
            {
                // The parser itself failed while moving between documents; nothing further can be read.
                Add(rel, e.Start.Line, e.Start.Column, "", ContentErrorCodes.YamlSyntax, Innermost(e).Message);
            }
            return result;
        }

        private static void SkipToDocumentEnd(IParser parser)
        {
            while (!parser.Accept<DocumentEnd>(out _) && !parser.Accept<StreamEnd>(out _))
            {
                if (!parser.MoveNext()) return;
            }
            parser.TryConsume<DocumentEnd>(out _);
        }

        private void Report(string file, YamlNode root, YamlException e)
        {
            var inner = Innermost(e);
            var (path, node) = YamlPathFinder.Find(root, e.Start);
            var message = inner.Message;
            string code;
            var unknownKey = Regex.Match(message, @"Property '([^']+)' not found on type '([^']+)'");
            var abstractType = Regex.Match(message, @"instance of type '([^']+)'.*abstract class");
            if (unknownKey.Success)
            {
                code = ContentErrorCodes.UnknownKey;
                var typeName = unknownKey.Groups[2].Value.Split('.', '+').Last();
                message = $"Unknown key '{unknownKey.Groups[1].Value}' on {typeName}.";
            }
            else if (abstractType.Success)
            {
                // Polymorphic object whose 'type' is absent or not in the discriminator table. The position is the
                // object's first key, so drop that key from the path to point at the object itself.
                var baseType = Discriminators.ByBaseType.First(kv => kv.Key.FullName == abstractType.Groups[1].Value);
                var valid = string.Join(", ", baseType.Value.Keys.OrderBy(k => k, StringComparer.Ordinal));
                var typeNode = (node as YamlMappingNode)?.Children
                    .FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == Discriminators.PropertyName).Value;
                if (path.EndsWith(']') == false && path.Contains('.')) path = path.Substring(0, path.LastIndexOf('.'));
                if (typeNode is YamlScalarNode given)
                {
                    code = ContentErrorCodes.UnknownType;
                    message = $"Unknown {baseType.Key.Name} type '{given.Value}'. Valid types: {valid}.";
                }
                else
                {
                    code = ContentErrorCodes.MissingType;
                    message = $"Missing '{Discriminators.PropertyName}' on {baseType.Key.Name}. Valid types: {valid}.";
                }
            }
            else
            {
                code = ContentErrorCodes.InvalidValue;
            }
            Add(file, e.Start.Line, e.Start.Column, path, code, message);
        }

        private static Exception Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}
