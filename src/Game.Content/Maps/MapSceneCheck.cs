using System.Text.Json;
using Game.Content.Loading;
using Game.Content.Registry;
using Game.Content.Validation;

namespace Game.Content.Maps;

/// <summary>
/// <c>validate</c>'s check that every grid was exported from the current version of its scene (ARCHITECTURE §5.1, §5.3). It reads text files only, so it needs no Godot.
/// It is not part of <see cref="ContentBoot"/>: the server reads grids, never scenes, and a server host need not hold the map art.
/// </summary>
public static class MapSceneCheck
{
    private const string DefaultTheme = "default";
    private const string HashKey = "source_scene_hash";

    /// <summary>The findings for every map of <paramref name="database"/>; <paramref name="themesDir"/> holds the theme folders.</summary>
    public static IReadOnlyList<ValidationIssue> Check(ContentDatabase database, string contentDir, string themesDir, string theme)
    {
        var issues = new List<ValidationIssue>();
        foreach (var map in database.Maps.All)
        {
            if (!database.Grids.TryGetValue(map.Id, out var grid)) continue; // the map-grid rule's error
            var gridFile = ContentLayout.GridPath(map.GridFile);
            var line = LineOfHash(Path.Combine(contentDir, ContentLayout.MapsFolder, map.GridFile));
            void Fail(string code, string message) =>
                issues.Add(new ValidationIssue(ValidationSeverity.Error, gridFile, line, line > 0 ? 1 : 0, HashKey, code, message));

            var scene = FindScene(themesDir, theme, map.SceneAssetKey);
            if (scene is null)
            {
                Fail(ValidationCodes.GridSceneMissing, $"{map.Id}: no theme lists a scene file for '{map.SceneAssetKey}', so the grid cannot be checked");
                continue;
            }
            if (!scene.EndsWith(".tscn", StringComparison.Ordinal))
            {
                Fail(ValidationCodes.GridSceneMissing, $"{map.Id}: '{scene}' is not a text scene. Map scenes must be .tscn, because validate reads them without Godot");
                continue;
            }

            var authoring = MapSceneHash.AuthoringPathOf(scene);
            var full = Path.Combine(themesDir, authoring.Replace('/', Path.DirectorySeparatorChar));
            var shown = "themes/" + authoring;
            if (!File.Exists(full))
            {
                Fail(ValidationCodes.GridSceneMissing, $"{map.Id}: the authoring scene {shown} does not exist");
                continue;
            }

            MapSceneHashResult hash;
            try
            {
                hash = MapSceneHash.Compute(File.ReadAllText(full), resource => ReadResource(themesDir, resource));
            }
            catch (FormatException e)
            {
                Fail(ValidationCodes.GridSceneMissing, $"{map.Id}: {shown} cannot be read: {e.Message}");
                continue;
            }
            foreach (var problem in hash.Problems)
                Fail(ValidationCodes.GridSceneMissing, $"{map.Id}: {shown}: {problem}");
            if (hash.Problems.Count > 0) continue;

            if (grid.SourceSceneHash != hash.Hash)
            {
                var had = grid.SourceSceneHash.Length == 0 ? "none" : Short(grid.SourceSceneHash);
                Fail(ValidationCodes.GridSceneStale, $"{gridFile} is stale: {shown} changed after the last export (grid has {had}, scene is {Short(hash.Hash)}). Export it again: client/export-maps.ps1 or the Map dock in the editor");
            }
        }
        return issues;
    }

    private static string Short(string hash) => hash.Length > 12 ? hash.Substring(0, 12) : hash;

    private static byte[]? ReadResource(string themesDir, string resource)
    {
        const string root = "res://themes/";
        if (!resource.StartsWith(root, StringComparison.Ordinal)) return null;
        var full = Path.Combine(themesDir, resource.Substring(root.Length).Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    // <theme>/<file> of a scene key, looking in the theme, then in the default theme (the client's fallback order, §7.7).
    private static string? FindScene(string themesDir, string theme, string key)
    {
        foreach (var candidate in theme == DefaultTheme ? new[] { theme } : new[] { theme, DefaultTheme })
        {
            var manifest = AssetManifest.PathFor(themesDir, candidate);
            if (!File.Exists(manifest)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.TryGetProperty(key, out var entry) && entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String)
                return candidate + "/" + file.GetString();
        }
        return null;
    }

    private static int LineOfHash(string gridPath)
    {
        if (!File.Exists(gridPath)) return 0;
        var lines = File.ReadAllLines(gridPath);
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains('"' + HashKey + '"', StringComparison.Ordinal)) return i + 1;
        return 0;
    }
}
