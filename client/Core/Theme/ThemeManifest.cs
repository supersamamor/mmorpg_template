using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Game.Client.Core.Theme;

/// <summary>One asset key's manifest entry (ARCHITECTURE §8). Only the fields the client reads so far; the rest is left to the task that needs it.</summary>
public sealed class ThemeAsset
{
    internal ThemeAsset(string key, string? file, string? rig, string? anims, string? attach, string? skinned, IReadOnlyDictionary<string, string> clips, double? impactDelay, string? effect,
        string? render = null, string? variantOf = null, ThemeColor? tint = null, double scale = 1, bool outlineOff = false, ThemeColor? outlineColor = null)
    {
        VariantOf = variantOf;
        Tint = tint;
        Scale = scale;
        OutlineOff = outlineOff;
        OutlineColor = outlineColor;
        Key = key;
        File = file;
        Rig = rig;
        Anims = anims;
        Attach = attach;
        Skinned = skinned;
        Clips = clips;
        ImpactDelay = impactDelay;
        Effect = effect;
        Render = render;
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

    /// <summary>
    /// For a variant (T-224, docs/THEMES.md "Variants"): the key of the entry whose model this one draws, resolved through the theme chain; null for an ordinary entry.
    /// A resolved variant (<see cref="ThemeCatalog.TryFind"/>) has the base's file, rig, animations and clips, and keeps this key.
    /// </summary>
    public string? VariantOf { get; }

    /// <summary>A colour the model's materials are multiplied by (a variant's <c>tint</c>), or null.</summary>
    public ThemeColor? Tint { get; }

    /// <summary>A uniform scale on the whole model (a variant's <c>scale</c>); 1 when the entry says nothing.</summary>
    public double Scale { get; }

    /// <summary>True when the entry turns the outline off for this model (<c>"outline": false</c>).</summary>
    public bool OutlineOff { get; }

    /// <summary>The outline colour for this model (<c>"outline": "#rrggbb"</c>), or null for the theme's outline.</summary>
    public ThemeColor? OutlineColor { get; }

    /// <summary>True when the entry changes how its model is drawn (a tint, a scale other than 1, or an outline override).</summary>
    public bool Restyled => Tint is not null || Scale != 1 || OutlineOff || OutlineColor is not null;

    /// <summary>The render role this entry fills (a <see cref="RenderRoles"/> term such as <c>toon</c>), or null: how a theme names its toon material, outline and environment (T-222).</summary>
    public string? Render { get; }
}

/// <summary>
/// A parsed <c>asset-manifest.json</c>: asset key to entry, plus the <c>fallbacks</c> table (entity kind or <c>missing</c> to an asset key). The
/// <c>audio</c> table is read by the audio task. Parsed with <see cref="JsonDocument"/>, so it survives trimming and AOT.
/// </summary>
public sealed class ThemeManifest
{
    private readonly Dictionary<string, ThemeAsset> _assets = new Dictionary<string, ThemeAsset>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _effects = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _renders = new Dictionary<string, string>(StringComparer.Ordinal);
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

    /// <summary>The asset key of the entry that fills a render role (<see cref="RenderRoles"/>), or false.</summary>
    public bool TryGetRender(string role, out string key) => _renders.TryGetValue(role, out key!);

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
                if (asset.Render is not null) result._renders[asset.Render] = property.Name;
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
        var outlineOff = entry.TryGetProperty("outline", out var outline) && outline.ValueKind == JsonValueKind.False;
        return new ThemeAsset(key, Text(entry, "file"), Text(entry, "rig"), Text(entry, "anims"), Text(entry, "attach"), Text(entry, "skinned"), clips, Number(entry, "impact_delay"), Text(entry, "effect"),
            Text(entry, "render"), Text(entry, "variant_of"), Color(entry, "tint"), Number(entry, "scale") is { } scale && scale > 0 ? scale : 1, outlineOff, Color(entry, "outline"));
    }

    private static ThemeColor? Color(JsonElement entry, string name) => Text(entry, name) is { } text && ThemeColor.TryParse(text, out var color) ? color : (ThemeColor?)null;

    private static double? Number(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>An sRGB colour from a manifest (<c>"#rrggbb"</c>), each channel 0 to 1. Pure, so the Godot layer turns it into its own colour type.</summary>
public readonly struct ThemeColor : IEquatable<ThemeColor>
{
    /// <summary>A colour from channels 0 to 1.</summary>
    public ThemeColor(float r, float g, float b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>Red, 0 to 1.</summary>
    public float R { get; }

    /// <summary>Green, 0 to 1.</summary>
    public float G { get; }

    /// <summary>Blue, 0 to 1.</summary>
    public float B { get; }

    /// <summary>White: multiplying by it changes nothing.</summary>
    public static ThemeColor White => new ThemeColor(1, 1, 1);

    /// <summary>Channel by channel product: a tint on a tint.</summary>
    public ThemeColor Times(ThemeColor other) => new ThemeColor(R * other.R, G * other.G, B * other.B);

    /// <summary>Reads <c>#rrggbb</c> (case-insensitive). False for anything else.</summary>
    public static bool TryParse(string text, out ThemeColor color)
    {
        color = default;
        if (text.Length != 7 || text[0] != '#') return false;
        var channels = new float[3];
        for (var i = 0; i < 3; i++)
        {
            var hi = Hex(text[1 + 2 * i]);
            var lo = Hex(text[2 + 2 * i]);
            if (hi < 0 || lo < 0) return false;
            channels[i] = (hi * 16 + lo) / 255f;
        }
        color = new ThemeColor(channels[0], channels[1], channels[2]);
        return true;
    }

    private static int Hex(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    /// <summary><c>#rrggbb</c>.</summary>
    public override string ToString()
    {
        static int B8(float v) => (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
        return "#" + B8(R).ToString("x2") + B8(G).ToString("x2") + B8(B).ToString("x2");
    }

    /// <inheritdoc />
    public bool Equals(ThemeColor other) => R == other.R && G == other.G && B == other.B;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ThemeColor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (R, G, B).GetHashCode();
}
