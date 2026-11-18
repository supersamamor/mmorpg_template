using System.Text;
using System.Text.Json;
using Game.Content.Registry;
using Game.Shared.Vocabulary;

namespace Game.Content.Validation;

/// <summary>
/// <c>validate</c>'s check of a theme's manifest entries for worn equipment and weapon clips (T-405, ARCHITECTURE §7.5–§7.7). It reads text and glTF headers only, so it
/// needs no Godot. Errors: a <c>model.equip.*</c> entry without exactly one of <c>attach</c> or <c>skinned</c>; an <c>attach</c> bone that a character rig lacks; a
/// <c>skinned</c> rig no model declares; a clip suffix (<c>&lt;animation id&gt;.&lt;weapon type&gt;</c>) that is no weapon type; a theme file that is a Git LFS pointer.
/// Warning: an item's worn visual key whose file no theme in the chain has, so the client draws the theme's missing model instead.
/// </summary>
public static class ThemeCheck
{
    private const string DefaultTheme = "default";

    /// <summary>The findings for <paramref name="theme"/> and, when it is another theme, <c>default</c> behind it.</summary>
    public static IReadOnlyList<ValidationIssue> Check(ContentDatabase database, string themesDir, string theme)
    {
        var issues = new List<ValidationIssue>();
        var chain = new List<Manifest>();
        foreach (var name in theme == DefaultTheme ? new[] { DefaultTheme } : new[] { theme, DefaultTheme })
        {
            var manifest = Manifest.Load(themesDir, name);
            if (manifest is not null) chain.Add(manifest);
        }
        if (chain.Count == 0) return issues; // a missing manifest is reported by the content boot

        foreach (var manifest in chain)
            foreach (var (key, _) in manifest.Entries)
                if (manifest.FileOf(key) is { } file && file.EndsWith(".glb", StringComparison.Ordinal) && File.Exists(file) && IsPointer(file))
                    issues.Add(Issue(ValidationSeverity.Error, manifest, key, key, ValidationCodes.EquipManifest, $"{key}: {LfsPointer.Message(manifest.RelativeFile(key))}"));

        var rigs = RigBones(chain, issues);
        var bodyRigs = BodyRigs(database, chain);

        foreach (var manifest in chain)
        {
            foreach (var (key, entry) in manifest.Entries)
            {
                if (key.StartsWith("model.equip.", StringComparison.Ordinal)) CheckEquip(manifest, key, entry, rigs, bodyRigs, issues);
                if (entry.TryGetProperty("clips", out var clips) && clips.ValueKind == JsonValueKind.Object) CheckClips(manifest, key, clips, issues);
            }
        }

        var missing = chain.Select(m => m.Fallback("missing")).FirstOrDefault(k => k is not null);
        foreach (var item in database.Items.All)
        {
            var key = item.Equip?.VisualAssetKey;
            if (string.IsNullOrEmpty(key)) continue;
            var first = chain.FirstOrDefault(m => m.Entries.ContainsKey(key));
            if (first is null) continue; // an unknown key is the asset-keys rule's error
            if (chain.Any(m => m.HasFile(key))) continue;
            issues.Add(Issue(ValidationSeverity.Warning, first, key, key, ValidationCodes.WarnVisualFile,
                $"{item.Id}: worn visual '{key}' has no model file in the theme chain; the client draws {(missing is null ? "the built-in missing model" : $"the missing model '{missing}'")} instead"));
        }
        return issues;
    }

    private static void CheckEquip(Manifest manifest, string key, JsonElement entry, Dictionary<string, IReadOnlySet<string>> rigs, IReadOnlyList<string> bodyRigs, List<ValidationIssue> issues)
    {
        var attach = Text(entry, "attach");
        var skinned = Text(entry, "skinned");
        if ((attach is null) == (skinned is null))
        {
            issues.Add(Issue(ValidationSeverity.Error, manifest, key, key, ValidationCodes.EquipManifest,
                $"{key}: must have exactly one of 'attach' or 'skinned', not {(attach is null ? "neither" : "both")}"));
            return;
        }
        if (attach is not null)
        {
            foreach (var rig in bodyRigs)
            {
                if (!rigs.TryGetValue(rig, out var bones)) continue; // unreadable: reported with the rig
                if (!bones.Contains(attach))
                    issues.Add(Issue(ValidationSeverity.Error, manifest, key, key + ".attach", ValidationCodes.EquipManifest,
                        $"{key}: attach bone '{attach}' does not exist in rig '{rig}' (bones: {string.Join(", ", bones.OrderBy(b => b, StringComparer.Ordinal))})"));
            }
        }
        else if (!rigs.ContainsKey(skinned!))
        {
            issues.Add(Issue(ValidationSeverity.Error, manifest, key, key + ".skinned", ValidationCodes.EquipManifest,
                $"{key}: skinned to rig '{skinned}', but no model in the theme chain declares a readable rig of that name"));
        }
    }

    private static void CheckClips(Manifest manifest, string key, JsonElement clips, List<ValidationIssue> issues)
    {
        foreach (var clip in clips.EnumerateObject())
        {
            var dot = clip.Name.IndexOf('.');
            if (dot < 0) continue;
            var suffix = clip.Name[(dot + 1)..];
            if (!WeaponTypes.All.Contains(suffix))
                issues.Add(Issue(ValidationSeverity.Error, manifest, clip.Name, key + ".clips." + clip.Name, ValidationCodes.EquipManifest,
                    $"{key}: clip '{clip.Name}' ends in '{suffix}', which is not a weapon type ({string.Join(", ", WeaponTypes.All.Ordered)})"));
        }
    }

    /// <summary>The rigs of the body models of every gender (the characters that wear gear).</summary>
    private static IReadOnlyList<string> BodyRigs(ContentDatabase database, List<Manifest> chain)
    {
        var rigs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var gender in database.Appearance.Genders.Values)
            foreach (var manifest in chain)
                if (manifest.Entries.TryGetValue(gender.BodyModelAssetKey, out var entry) && Text(entry, "rig") is { } rig) { rigs.Add(rig); break; }
        return rigs.ToList();
    }

    /// <summary>Bone names by rig name, read from the first model of each rig whose file is a glTF with a skin.</summary>
    private static Dictionary<string, IReadOnlySet<string>> RigBones(List<Manifest> chain, List<ValidationIssue> issues)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var tried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in chain)
        {
            foreach (var (key, entry) in manifest.Entries)
            {
                if (Text(entry, "rig") is not { } rig || result.ContainsKey(rig) || !key.StartsWith("model.", StringComparison.Ordinal)) continue;
                var file = chain.Select(m => m.FileOf(key)).FirstOrDefault(f => f is not null && File.Exists(f));
                if (file is null || !tried.Add(file)) continue;
                var read = GlbBones(file, out var problem);
                if (read is not null) result[rig] = read;
                else if (problem is not null && !problem.Contains("LFS pointer", StringComparison.Ordinal)) issues.Add(Issue(ValidationSeverity.Error, manifest, key, key, ValidationCodes.EquipManifest, $"{key}: {problem}"));
            }
        }
        return result;
    }

    private static bool IsPointer(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[64];
        return LfsPointer.Is(head.AsSpan(0, stream.Read(head, 0, head.Length)));
    }

    /// <summary>The joint names of the first skin of a .glb, or null (with <paramref name="problem"/> when the file is a Git LFS pointer or not a glTF).</summary>
    internal static IReadOnlySet<string>? GlbBones(string path, out string? problem)
    {
        problem = null;
        var bytes = File.ReadAllBytes(path);
        if (LfsPointer.Is(bytes)) { problem = LfsPointer.Message(path); return null; }
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != 0x46546C67u) { problem = $"{path} is not a binary glTF"; return null; }
        var length = (int)BitConverter.ToUInt32(bytes, 12);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, length));
        if (!doc.RootElement.TryGetProperty("skins", out var skins) || skins.GetArrayLength() == 0 || !doc.RootElement.TryGetProperty("nodes", out var nodes)) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var joint in skins[0].GetProperty("joints").EnumerateArray())
            if (nodes[joint.GetInt32()].TryGetProperty("name", out var name) && name.GetString() is { } text) names.Add(text);
        return names;
    }

    private static ValidationIssue Issue(ValidationSeverity severity, Manifest manifest, string token, string path, string code, string message)
    {
        var line = manifest.LineOf(token);
        return new ValidationIssue(severity, manifest.DisplayPath, line, line > 0 ? 1 : 0, path, code, message);
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class Manifest
    {
        private readonly string _themeDir;
        private readonly string _text;
        private readonly JsonDocument _doc;

        private Manifest(string themeDir, string displayPath, string text, JsonDocument doc)
        {
            _themeDir = themeDir;
            DisplayPath = displayPath;
            _text = text;
            _doc = doc;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name is not ("audio" or "fallbacks") && p.Value.ValueKind == JsonValueKind.Object) Entries[p.Name] = p.Value;
        }

        public string DisplayPath { get; }

        public Dictionary<string, JsonElement> Entries { get; } = new(StringComparer.Ordinal);

        public static Manifest? Load(string themesDir, string theme)
        {
            var path = AssetManifest.PathFor(themesDir, theme);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            try
            {
                var doc = JsonDocument.Parse(text);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? new Manifest(Path.Combine(themesDir, theme), $"themes/{theme}/asset-manifest.json", text, doc) : null;
            }
            catch (JsonException) { return null; } // the content boot reports a manifest that is not JSON
        }

        public string? Fallback(string name) =>
            _doc.RootElement.TryGetProperty("fallbacks", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public string? FileOf(string key) =>
            Entries.TryGetValue(key, out var e) && Text(e, "file") is { } file ? Path.Combine(_themeDir, file.Replace('/', Path.DirectorySeparatorChar)) : null;

        public string RelativeFile(string key) => Entries.TryGetValue(key, out var e) && Text(e, "file") is { } file ? DisplayPath[..DisplayPath.LastIndexOf('/')] + "/" + file : key;

        public bool HasFile(string key) => FileOf(key) is { } f && File.Exists(f);

        public int LineOf(string token)
        {
            var at = _text.IndexOf("\"" + token + "\"", StringComparison.Ordinal);
            return at < 0 ? 0 : _text.AsSpan(0, at).Count('\n') + 1;
        }
    }
}

/// <summary>Git LFS pointer files: what a checkout holds in place of a binary until <c>git lfs pull</c> (ADR-002 row 208).</summary>
public static class LfsPointer
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("version https://git-lfs.github.com/spec/");

    /// <summary>True when <paramref name="bytes"/> start like a pointer file.</summary>
    public static bool Is(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Prefix);

    /// <summary>The message for a file that is a pointer.</summary>
    public static string Message(string path) => $"{path} is a Git LFS pointer, not the file itself; run `git lfs pull`";
}
