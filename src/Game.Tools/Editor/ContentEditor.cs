using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Game.Content.Loading;
using Game.Content.Validation;
using Game.Tools.Commands;

namespace Game.Tools.Editor;

/// <summary>One kind the editor offers: a content folder and the JSON Schema generated for its definition type.</summary>
/// <param name="Kind">The folder under <c>content/</c> (<c>items</c>).</param>
/// <param name="Schema">The schema file's stem (<c>item</c>).</param>
/// <param name="Title">The schema's title, the definition type (<c>ItemDef</c>).</param>
public sealed record EditorKind(string Kind, string Schema, string Title);

/// <summary>One document of a kind: its path under <c>content/</c>, its id and, when it has one, its name.</summary>
public sealed record EditorDocument(string Path, string Id, string? Name);

/// <summary>What a save did.</summary>
/// <param name="Saved">True when the file was written.</param>
/// <param name="Mode">How the YAML was written (when saved).</param>
/// <param name="Issues">The findings of the whole set with the change: errors (nothing was written) or warnings.</param>
/// <param name="Version">The saved file's version (when saved), for the next save.</param>
/// <param name="Problem">Why nothing was looked at (a bad path, a file changed by someone else, unreadable YAML), else null.</param>
public sealed record SaveResult(bool Saved, YamlWriteMode? Mode, IReadOnlyList<ValidationIssue> Issues, string? Version, string? Problem);

/// <summary>
/// The local content editor's model (T-801): which kinds it offers and their schemas, the documents of each, the ids that references may pick from, and saving. The kinds come from
/// the generated schemas (<c>schemas/*.schema.json</c>, matched to <see cref="ContentLayout.Folders"/> by their title), limited to <see cref="FirstKinds"/>; the forms come from
/// the schemas too, never from a C# list of fields (hard rule 2). Every save checks the whole content set with the change first, exactly as <c>validate</c> does, in a copy of
/// <c>content/</c> in a temporary folder, and writes nothing when there is an error; then it writes the one file atomically (a temporary file beside it, then a replace).
/// </summary>
public sealed class ContentEditor
{
    /// <summary>
    /// The first set of kinds, by content folder: the ones a designer changes most and whose forms are mostly plain fields: items, monsters, skills, NPCs, dialogue (lines and
    /// choices), shops (stock lists) and quests (texts and objectives). Not offered: maps (their grids come from the map scenes), jobs, statuses, drops and outfits (next), and the
    /// root files (balance, formulas and the like: one document of many sections each).
    /// </summary>
    public static readonly IReadOnlyList<string> FirstKinds = ["items", "monsters", "skills", "npcs", "dialogue", "shops", "quests"];

    private readonly string _content;
    private readonly string _schemas;
    private readonly string _themes;
    private readonly string _theme;
    private readonly object _saveGate = new();

    /// <summary>An editor over <paramref name="contentDir"/>, its schemas in <paramref name="schemasDir"/> and the themes in <paramref name="themesDir"/>.</summary>
    public ContentEditor(string contentDir, string schemasDir, string themesDir, string theme = "default")
    {
        _content = Path.GetFullPath(contentDir);
        _schemas = Path.GetFullPath(schemasDir);
        _themes = Path.GetFullPath(themesDir);
        _theme = theme;
        Kinds = LoadKinds();
    }

    /// <summary>The kinds offered, in <see cref="FirstKinds"/> order.</summary>
    public IReadOnlyList<EditorKind> Kinds { get; }

    /// <summary>The schema of <paramref name="kind"/> as JSON text, or null for a kind not offered.</summary>
    public string? Schema(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind) is { } k ? File.ReadAllText(Path.Combine(_schemas, k.Schema + ".schema.json")) : null;

    /// <summary>The documents of <paramref name="kind"/>, by path.</summary>
    public IReadOnlyList<EditorDocument> Documents(string kind)
    {
        if (Kinds.All(k => k.Kind != kind)) return [];
        var dir = Path.Combine(_content, kind);
        if (!Directory.Exists(dir)) return [];
        var list = new List<EditorDocument>();
        foreach (var file in Directory.GetFiles(dir, "*.yaml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var doc = YamlDocumentEdit.ToJson(File.ReadAllText(file));
                list.Add(new EditorDocument(Relative(file), doc["id"]?.ToString() ?? "", doc["name"]?.ToString()));
            }
            catch (Exception e) when (e is YamlDotNet.Core.YamlException or InvalidDataException)
            {
                list.Add(new EditorDocument(Relative(file), "", "(unreadable: " + e.Message + ")"));
            }
        }
        return list;
    }

    /// <summary>
    /// Every id there is, by namespace prefix (<c>item</c> → <c>item.*</c>), from the content and from the theme's asset manifest (asset keys), so a reference field (a string whose
    /// schema pattern names a prefix) offers what exists. Read from the files each time, so a save shows up at once.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Ids()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var load = ContentLoader.Load(_content);
        if (load.Database is { } db)
            foreach (var registry in db.Registries)
                foreach (var def in registry.Definitions) ids.Add(def.Id);
        try
        {
            foreach (var key in AssetManifest.LoadChainKeys(_themes, _theme)) ids.Add(key);
        }
        catch (InvalidDataException)
        {
            // a broken manifest shows up as an error on save
        }
        return ids.GroupBy(id => id.Split('.')[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>A document as JSON with its version (a hash of the file's bytes), or null with why.</summary>
    public (JsonObject? Document, string? Version, string? Problem) Load(string path)
    {
        if (Resolve(path) is not { } full) return (null, null, $"'{path}' is not a document of a kind the editor offers");
        if (!File.Exists(full)) return (null, null, $"'{path}' does not exist");
        var text = File.ReadAllText(full);
        try
        {
            return (YamlDocumentEdit.ToJson(text), Version(text), null);
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or InvalidDataException)
        {
            return (null, null, $"'{path}' cannot be read as one YAML mapping: {e.Message}");
        }
    }

    /// <summary>
    /// Saves <paramref name="document"/> to <paramref name="path"/> when the whole set with it validates. <paramref name="version"/> is what <see cref="Load"/> gave: a file changed
    /// since then (by hand, by another editor window) is not overwritten. The id is the file's: changing it would remove a definition, so the editor refuses that.
    /// </summary>
    public SaveResult Save(string path, JsonObject document, string? version)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Resolve(path) is not { } full) return Refused($"'{path}' is not a document of a kind the editor offers");
        lock (_saveGate)
        {
            if (!File.Exists(full)) return Refused($"'{path}' does not exist; the editor changes existing documents");
            var before = File.ReadAllText(full);
            if (version is not null && version != Version(before)) return Refused($"'{path}' changed on disk since it was opened; open it again");
            string text;
            YamlWriteMode mode;
            try
            {
                var old = YamlDocumentEdit.ToJson(before);
                if (old["id"]?.ToString() != document["id"]?.ToString()) return Refused("the id cannot be changed here (that removes a definition and adds another); rename the file and its id by hand");
                (text, mode) = YamlDocumentEdit.Apply(before, document);
            }
            catch (Exception e) when (e is YamlDotNet.Core.YamlException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return Refused($"'{path}' cannot be edited: {e.Message}");
            }
            var issues = Check(path, text);
            if (issues.Any(i => i.Severity == ValidationSeverity.Error)) return new SaveResult(false, null, issues, null, null);
            WriteAtomically(full, text);
            return new SaveResult(true, mode, issues, Version(text), null);
        }
    }

    /// <summary>The findings of the whole content set with <paramref name="path"/> holding <paramref name="text"/>, checked in a temporary copy. Nothing in the content changes.</summary>
    public IReadOnlyList<ValidationIssue> Check(string path, string text)
    {
        var temp = Path.Combine(Path.GetTempPath(), "content-editor-" + Guid.NewGuid().ToString("N"));
        var copy = Path.Combine(temp, "content");
        try
        {
            CopyTree(_content, copy);
            File.WriteAllText(Path.Combine(copy, path.Replace('/', Path.DirectorySeparatorChar)), text, new UTF8Encoding(false));
            return ValidateCommand.Collect(copy, _schemas, _theme, _themes);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // a scanner may hold a file for a moment; it is in the temp folder
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="full"/> so that a reader sees the old file or the new one, never part of either: a temporary file in the same folder, flushed,
    /// then <see cref="File.Replace(string, string, string?)"/> (one rename on the same volume). The temporary file ends in <c>.tmp</c>, which the server's watcher ignores.
    /// </summary>
    public static void WriteAtomically(string full, string text)
    {
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(full)) File.Replace(temp, full, null);
            else File.Move(temp, full);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static SaveResult Refused(string problem) => new(false, null, [], null, problem);

    private static string Version(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>The full path of a document of an offered kind, or null for anything else (another folder, <c>..</c>, not <c>.yaml</c>).</summary>
    private string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".yaml", StringComparison.Ordinal) || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or "..")) return null;
        var kind = path.Split('/')[0];
        if (Kinds.All(k => k.Kind != kind)) return null;
        var full = Path.GetFullPath(Path.Combine(_content, path));
        return full.StartsWith(Path.Combine(_content, kind) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private string Relative(string full) => Path.GetRelativePath(_content, full).Replace('\\', '/');

    private List<EditorKind> LoadKinds()
    {
        var byTitle = new Dictionary<string, (string Stem, string Title)>(StringComparer.Ordinal);
        if (Directory.Exists(_schemas))
            foreach (var file in Directory.GetFiles(_schemas, "*.schema.json"))
            {
                if (JsonNode.Parse(File.ReadAllText(file))?["title"]?.ToString() is not { } title) continue;
                byTitle[title] = (Path.GetFileName(file)[..^".schema.json".Length], title);
            }
        var kinds = new List<EditorKind>();
        foreach (var folder in FirstKinds)
            if (ContentLayout.Folders.TryGetValue(folder, out var type) && byTitle.TryGetValue(type.Name, out var schema))
                kinds.Add(new EditorKind(folder, schema.Stem, schema.Title));
        return kinds;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            if (!file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}
