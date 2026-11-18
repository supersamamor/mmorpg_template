using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Game.ThemeGen;

/// <summary>
/// The theme's shared palette (ADR-003 point 4), read from <c>themes/&lt;theme&gt;/palette.json</c>: named sRGB colours, the lists creatures, gear and hair pick from,
/// and per-model entries (two identity colours, a trim colour and a look) for the models that have an identity, such as the jobs. Nothing here names game content:
/// the model keys are theme data. Colours come out <b>linear</b>, ready for glTF vertex colours.
/// </summary>
internal sealed class Palette
{
    private readonly Dictionary<string, Vector4> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelPalette> _models = new(StringComparer.Ordinal);
    private readonly List<string> _creatures = new();
    private readonly List<string> _gear = new();
    private readonly List<string> _hair = new();

    /// <summary>The file name inside a theme folder.</summary>
    public const string FileName = "palette.json";

    /// <summary>An empty palette: every lookup falls back to the key colour (for a theme without a palette file).</summary>
    public static Palette Empty { get; } = new();

    /// <summary>Reads <paramref name="path"/>, or <see cref="Empty"/> when there is no such file.</summary>
    public static Palette Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var palette = new Palette();
        foreach (var (name, value) in root["colors"]!.AsObject()) palette._colors[name] = Hex(value!.GetValue<string>(), name);
        Names(root, "creatures", palette._creatures, palette);
        Names(root, "gear", palette._gear, palette);
        Names(root, "hair", palette._hair, palette);
        if (root["models"] is JsonObject models)
        {
            foreach (var (key, node) in models)
            {
                var entry = node!.AsObject();
                var colors = entry["colors"]!.AsArray().Select(c => palette.Named(c!.GetValue<string>())).ToArray();
                if (colors.Length != 2) throw new InvalidDataException($"{path}: {key}: 'colors' must name two colours");
                var trim = entry["trim"] is { } t ? palette.Named(t.GetValue<string>()) : colors[1];
                palette._models[key] = new ModelPalette(colors[0], colors[1], trim, entry["look"]?.GetValue<string>() ?? Looks.Plain);
            }
        }
        return palette;
    }

    private static void Names(JsonObject root, string list, List<string> into, Palette palette)
    {
        if (root[list] is not JsonArray names) return;
        foreach (var name in names)
        {
            var text = name!.GetValue<string>();
            palette.Named(text); // must exist
            into.Add(text);
        }
    }

    /// <summary>True when the theme has a palette file.</summary>
    public bool Any => _colors.Count > 0;

    /// <summary>A named colour, linear. Throws on an unknown name, so a typo in the palette stops the generator.</summary>
    public Vector4 Named(string name) =>
        _colors.TryGetValue(name, out var c) ? c : throw new InvalidDataException($"{FileName}: no colour named '{name}'");

    /// <summary>A named colour if the palette has it, else <paramref name="fallback"/>.</summary>
    public Vector4 NamedOr(string name, Vector4 fallback) => _colors.TryGetValue(name, out var c) ? c : fallback;

    /// <summary>The identity of a model the palette names, else null.</summary>
    public ModelPalette? Model(string key) => _models.GetValueOrDefault(key);

    /// <summary>Two different colours from the creature list, picked by <paramref name="hash"/> (monsters and anything else alive without an identity).</summary>
    public (Vector4 Primary, Vector4 Secondary) Creature(uint hash) => Pair(_creatures, hash);

    /// <summary>Two different colours from the gear list, picked by <paramref name="hash"/>.</summary>
    public (Vector4 Primary, Vector4 Secondary) Gear(uint hash) => Pair(_gear, hash);

    /// <summary>A hair colour picked by <paramref name="hash"/>.</summary>
    public Vector4 Hair(uint hash) => _hair.Count == 0 ? NamedOr("leather", new Vector4(0.25f, 0.12f, 0.05f, 1)) : Named(_hair[(int)(hash % (uint)_hair.Count)]);

    private (Vector4, Vector4) Pair(List<string> list, uint hash)
    {
        if (list.Count < 2) throw new InvalidDataException($"{FileName}: a pick list needs at least two colours");
        var n = (uint)list.Count;
        var first = (int)(hash % n);
        var second = (first + 1 + (int)((hash / n) % (n - 1))) % (int)n;
        return (Named(list[first]), Named(list[second]));
    }

    /// <summary>sRGB hex (#rrggbb) to linear RGBA.</summary>
    public static Vector4 Hex(string hex, string name = "")
    {
        if (hex.Length != 7 || hex[0] != '#') throw new InvalidDataException($"{FileName}: '{name}' must be #rrggbb, not '{hex}'");
        float Channel(int at) => ToLinear(int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f);
        return new Vector4(Channel(1), Channel(3), Channel(5), 1);
    }

    /// <summary>The sRGB transfer function, inverted.</summary>
    public static float ToLinear(float s) => s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);

    /// <summary>The sRGB transfer function.</summary>
    public static float ToSrgb(float l) => l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;

    /// <summary>A linear colour with its hue turned by <paramref name="turn"/> (0 to 1), for the test theme's other colours.</summary>
    public static Vector4 Shift(Vector4 linear, float turn)
    {
        if (turn == 0) return linear;
        var r = ToSrgb(linear.X);
        var g = ToSrgb(linear.Y);
        var b = ToSrgb(linear.Z);
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var d = max - min;
        var h = d == 0 ? 0 : max == r ? ((g - b) / d % 6) / 6f : max == g ? ((b - r) / d + 2) / 6f : ((r - g) / d + 4) / 6f;
        var s = max == 0 ? 0 : d / max;
        h = ((h + turn) % 1f + 1f) % 1f;
        var i = (int)MathF.Floor(h * 6f);
        var f = h * 6f - i;
        var p = max * (1 - s);
        var q = max * (1 - f * s);
        var t = max * (1 - (1 - f) * s);
        var (nr, ng, nb) = (i % 6) switch
        {
            0 => (max, t, p),
            1 => (q, max, p),
            2 => (p, max, t),
            3 => (p, q, max),
            4 => (t, p, max),
            _ => (max, p, q),
        };
        return new Vector4(ToLinear(nr), ToLinear(ng), ToLinear(nb), linear.W);
    }

    /// <summary>A linear colour scaled toward black (<paramref name="k"/> below 1) in sRGB terms, so "a bit darker" looks the same on every colour.</summary>
    public static Vector4 Shade(Vector4 linear, float k) =>
        new(ToLinear(ToSrgb(linear.X) * k), ToLinear(ToSrgb(linear.Y) * k), ToLinear(ToSrgb(linear.Z) * k), linear.W);
}

/// <summary>A model's identity colours (linear) and its look (<see cref="Looks"/>).</summary>
internal sealed record ModelPalette(Vector4 Primary, Vector4 Secondary, Vector4 Trim, string Look);

/// <summary>The placeholder humanoid's silhouettes: told apart by headgear and shoulders (ADR-003 point 3).</summary>
internal static class Looks
{
    public const string Plain = "plain";   // hair only
    public const string Hood = "hood";     // hair, a hood fallen back, a short cape and a satchel
    public const string Helm = "helm";     // a crested helm, big pauldrons and a tabard
    public const string Hat = "hat";       // a pointed hat with a glowing gem, long hair and a robe

    public static readonly string[] All = { Plain, Hood, Helm, Hat };
}
