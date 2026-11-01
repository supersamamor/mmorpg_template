using System.Text.Json;

namespace Game.Content.Validation;

/// <summary>Reads the asset keys out of a theme's <c>asset-manifest.json</c> (top-level object, one property per key).</summary>
public static class AssetManifest
{
    /// <summary>Path of the manifest for <paramref name="theme"/>, given the directory that holds the theme folders.</summary>
    public static string PathFor(string themesDir, string theme) => Path.Combine(themesDir, theme, "asset-manifest.json");

    /// <summary>Loads the key set. The result is an <see cref="AssetKeySet"/>, which also carries each key's <c>rig</c> and <c>attach</c>. Throws <see cref="InvalidDataException"/> with a readable message when missing or malformed.</summary>
    public static IReadOnlySet<string> LoadKeys(string manifestPath)
    {
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"Theme manifest not found: {manifestPath}");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{manifestPath}: expected a JSON object at the top level.");
            var info = new Dictionary<string, AssetInfo>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                info[p.Name] = new AssetInfo(Text(p.Value, "rig"), Text(p.Value, "attach"));
            return new AssetKeySet(info);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{manifestPath}: invalid JSON: {e.Message}", e);
        }
    }

    private static string? Text(JsonElement entry, string property) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
