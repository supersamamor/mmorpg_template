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
            {
                // "audio" holds the sound tables (defaults, events) and "fallbacks" the client's stand-ins by entity kind; neither is an asset key.
                if (p.Name is "audio" or "fallbacks") continue;
                info[p.Name] = new AssetInfo(Text(p.Value, "rig"), Text(p.Value, "attach"), Text(p.Value, "variant_of"));
            }
            return new AssetKeySet(ResolveVariants(info));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{manifestPath}: invalid JSON: {e.Message}", e);
        }
    }

    /// <summary>
    /// The keys the client can resolve for <paramref name="theme"/>: the theme's own manifest, then <c>default</c> (ARCHITECTURE §7.7), so a partial theme may point at a key only <c>default</c> has.
    /// A key in both takes the theme's metadata. The theme's own manifest must exist; a missing <c>default</c> one adds nothing.
    /// </summary>
    public static IReadOnlySet<string> LoadChainKeys(string themesDir, string theme)
    {
        var own = (AssetKeySet)LoadKeys(PathFor(themesDir, theme));
        var defaultPath = PathFor(themesDir, DefaultTheme);
        if (theme == DefaultTheme || !File.Exists(defaultPath)) return own;
        var fallback = (AssetKeySet)LoadKeys(defaultPath);
        var info = new Dictionary<string, AssetInfo>(StringComparer.Ordinal);
        foreach (var key in fallback) info[key] = fallback.InfoOf(key)!;
        foreach (var key in own) info[key] = own.InfoOf(key)!;
        return new AssetKeySet(ResolveVariants(info));
    }

    private const string DefaultTheme = "default";

    /// <summary>
    /// A variant (T-224, <c>variant_of</c>) draws its base's model, so it takes the base's rig and attach point (followed through variants of variants, at most
    /// <c>Themes.ThemeContract.MaxVariantDepth</c>). A base that is missing or a loop leaves the variant's own metadata; <c>ThemeCheck</c> reports it.
    /// </summary>
    private static Dictionary<string, AssetInfo> ResolveVariants(Dictionary<string, AssetInfo> info)
    {
        foreach (var key in info.Keys.ToList())
        {
            var entry = info[key];
            if (entry.VariantOf is null) continue;
            var at = entry;
            for (var depth = 0; depth < Themes.ThemeContract.MaxVariantDepth && at.VariantOf is { } next && info.TryGetValue(next, out var under); depth++) at = under;
            if (at.VariantOf is null) info[key] = entry with { Rig = at.Rig, Attach = at.Attach };
        }
        return info;
    }

    private static string? Text(JsonElement entry, string property) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
