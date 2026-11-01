using Game.Content.Registry;

namespace Game.Content.Validation;

/// <summary>
/// What a rule sees: the loaded content, the asset keys of the active theme, and the reporting methods.
/// Paths use YAML key naming (<c>snake_case</c>, <c>list[0]</c>, <c>map.key</c>) so they match the file.
/// </summary>
public sealed class ValidationContext
{
    private readonly List<ValidationIssue> _issues = new();
    private readonly YamlLocator _locator;

    /// <summary>Creates a context.</summary>
    public ValidationContext(ContentDatabase database, string contentRoot, IReadOnlySet<string> assetKeys)
    {
        Database = database;
        ContentRoot = contentRoot;
        AssetKeys = assetKeys;
        _locator = new YamlLocator(contentRoot);
    }

    /// <summary>The loaded content.</summary>
    public ContentDatabase Database { get; }

    /// <summary>Content root directory.</summary>
    public string ContentRoot { get; }

    /// <summary>Asset keys declared in the theme's <c>asset-manifest.json</c>. Only existence is checked, not files.</summary>
    public IReadOnlySet<string> AssetKeys { get; }

    /// <summary>Manifest metadata (<c>rig</c>, <c>attach</c>) of an asset key. Null when the key is unknown, or when the key set was built without metadata (then metadata checks are skipped).</summary>
    public AssetInfo? InfoOf(string key) => (AssetKeys as AssetKeySet)?.InfoOf(key);

    /// <summary>True when <see cref="AssetKeys"/> carries manifest metadata (it does when loaded by <see cref="AssetManifest.LoadKeys"/>).</summary>
    public bool HasAssetInfo => AssetKeys is AssetKeySet;

    /// <summary>Issues reported so far.</summary>
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>Reports an error on the definition <paramref name="ownerId"/> at <paramref name="path"/>.</summary>
    public void Error(string ownerId, string path, string code, string message) =>
        Add(ValidationSeverity.Error, ownerId, path, code, message);

    /// <summary>Reports a warning on the definition <paramref name="ownerId"/> at <paramref name="path"/>.</summary>
    public void Warn(string ownerId, string path, string code, string message) =>
        Add(ValidationSeverity.Warning, ownerId, path, code, message);

    /// <summary>Reports an error in a singleton root file (e.g. <c>elements.yaml</c>) that has no id.</summary>
    public void ErrorInFile(string file, string path, string code, string message) =>
        AddIssue(ValidationSeverity.Error, file, null, path, code, message);

    /// <summary>Reports an error that belongs to no file.</summary>
    public void ErrorGlobal(string code, string message) =>
        _issues.Add(new ValidationIssue(ValidationSeverity.Error, "", 0, 0, "", code, message));

    private void Add(ValidationSeverity severity, string ownerId, string path, string code, string message)
    {
        var src = Database.SourceOf(ownerId);
        if (src is null)
        {
            _issues.Add(new ValidationIssue(severity, "", 0, 0, path, code, $"{ownerId}: {message}"));
            return;
        }
        AddIssue(severity, src.Value.File, ownerId, path, code, message, src.Value.Line);
    }

    private void AddIssue(ValidationSeverity severity, string file, string? docId, string path, string code, string message, int fallbackLine = 0)
    {
        var (line, col) = _locator.Find(file, docId, path);
        if (line == 0) line = fallbackLine;
        _issues.Add(new ValidationIssue(severity, file, line, line > 0 && col == 0 ? 1 : col, path, code, message));
    }
}
