using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Game.Client.Core.Theme;

/// <summary>One asset key's manifest entry (ARCHITECTURE §8). Only the fields the client reads so far; the rest is left to the task that needs it.</summary>
public sealed class ThemeAsset
{
    internal ThemeAsset(string key, string? file, string? rig, string? anims, string? attach, string? skinned, IReadOnlyDictionary<string, string> clips, double? impactDelay, string? effect)
    {
        Key = key;
        File = file;
        Rig = rig;
        Anims = anims;
        Attach = attach;
        Skinned = skinned;
        Clips = clips;
        ImpactDelay = impactDelay;
        Effect = effect;
    }

    /// <summary>The asset key.</summary>
    public string Key { get; }

    /// <summary>The file, relative to the theme folder; null for an entry that lists several files (<c>files</c>, sounds).</summary>
    public string? File { get; }

    /// <summary>The rig name, or null (rig names are theme-internal).</summary>
    public string? Rig { get; }

    /// <summary>The animation library's asset key, or null.</summary>
    public string? Anims { get; }

    /// <summary>The attach point name, or null.</summary>
    public string? Attach { get; }

    /// <summary>The rig a garment is skinned to, or null.</summary>
    public string? Skinned { get; }

    /// <summary>For an animation library: animation id to the clip's name in the file.</summary>
    public IReadOnlyDictionary<string, string> Clips { get; }

    /// <summary>
    /// Seconds from the start of the attack clip to the moment the blow lands (T-303), on a model or on a rig's animation library; null when the entry says nothing.
    /// A model's own value wins over its library's, which wins over the client default.
    /// </summary>
    public double? ImpactDelay { get; }

    /// <summary>The combat effect this entry plays (an <c>EffectNames</c> term such as <c>effect.hit</c>), or null: how a theme says which scene shows a hit, a crit, a death or a level-up.</summary>
    public string? Effect { get; }
}

/// <summary>
/// A parsed <c>asset-manifest.json</c>: asset key to entry, plus the <c>fallbacks</c> table (entity kind or <c>missing</c> to an asset key). The
/// <c>audio</c> table is read by the audio task. Parsed with <see cref="JsonDocument"/>, so it survives trimming and AOT.
/// </summary>
public sealed class ThemeManifest
{
    private readonly Dictionary<string, ThemeAsset> _assets = new Dictionary<string, ThemeAsset>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _effects = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fallbacks = new Dictionary<string, string>(StringComparer.Ordinal);

    private ThemeManifest(string theme) => Theme = theme;

    /// <summary>The theme folder name.</summary>
    public string Theme { get; }

    /// <summary>Number of asset keys.</summary>
    public int Count => _assets.Count;

    /// <summary>All asset keys.</summary>
    public IEnumerable<string> Keys => _assets.Keys;

    /// <summary>One entry, or false.</summary>
    public bool TryGet(string key, out ThemeAsset asset) => _assets.TryGetValue(key, out asset!);

    /// <summary>The asset key of the entry that plays a combat effect (<c>EffectNames</c>), or false.</summary>
    public bool TryGetEffect(string name, out string key) => _effects.TryGetValue(name, out key!);

    /// <summary>The asset key a <c>fallbacks</c> name (an entity kind, or <c>missing</c>) points to.</summary>
    public bool TryGetFallback(string name, out string key) => _fallbacks.TryGetValue(name, out key!);

    /// <summary>Parses a manifest. False with a readable <paramref name="error"/> when it is not a JSON object of objects.</summary>
    public static bool TryParse(string theme, string json, out ThemeManifest? manifest, out string error)
    {
        manifest = null;
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "the manifest must be a JSON object";
                return false;
            }
            var result = new ThemeManifest(theme);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "audio") continue;
                if (property.Name == "fallbacks")
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        error = "'fallbacks' must be an object";
                        return false;
                    }
                    foreach (var fallback in property.Value.EnumerateObject())
                        if (fallback.Value.ValueKind == JsonValueKind.String) result._fallbacks[fallback.Name] = fallback.Value.GetString()!;
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    error = "entry '" + property.Name + "' must be an object";
                    return false;
                }
                var asset = ReadAsset(property.Name, property.Value);
                result._assets[property.Name] = asset;
                if (asset.Effect is not null) result._effects[asset.Effect] = property.Name;
            }
            manifest = result;
            return true;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }
    }

    private static ThemeAsset ReadAsset(string key, JsonElement entry)
    {
        var clips = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry.TryGetProperty("clips", out var clipList) && clipList.ValueKind == JsonValueKind.Object)
            foreach (var clip in clipList.EnumerateObject())
                if (clip.Value.ValueKind == JsonValueKind.String) clips[clip.Name] = clip.Value.GetString()!;
        return new ThemeAsset(key, Text(entry, "file"), Text(entry, "rig"), Text(entry, "anims"), Text(entry, "attach"), Text(entry, "skinned"), clips, Number(entry, "impact_delay"), Text(entry, "effect"));
    }

    private static double? Number(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
