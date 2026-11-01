using System.Text.Json;

namespace Game.Content.Validation;

/// <summary>Reads the asset keys out of a theme's <c>asset-manifest.json</c> (top-level object, one property per key).</summary>
public static class AssetManifest
{
    /// <summary>Path of the manifest for <paramref name="theme"/>, given the directory that holds the theme folders.</summary>
    public static string PathFor(string themesDir, string theme) => Path.Combine(themesDir, theme, "asset-manifest.json");

    /// <summary>Loads the key set. Throws <see cref="InvalidDataException"/> with a readable message when missing or malformed.</summary>
    public static IReadOnlySet<string> LoadKeys(string manifestPath)
    {
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"Theme manifest not found: {manifestPath}");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{manifestPath}: expected a JSON object at the top level.");
            // "audio" holds the sound tables (defaults, events), not an asset.
            return doc.RootElement.EnumerateObject().Select(p => p.Name).Where(n => n != "audio").ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{manifestPath}: invalid JSON: {e.Message}", e);
        }
    }
}
