using System;
using System.Collections.Generic;

namespace Game.Client.Core.Theme;

/// <summary>A linear colour, the space vertex colours are kept in (T-706). Pure, so the merge's colour work is tested without the engine.</summary>
public readonly struct LinearColor
{
    /// <summary>A colour from linear channels 0 to 1.</summary>
    public LinearColor(float r, float g, float b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>Red.</summary>
    public float R { get; }

    /// <summary>Green.</summary>
    public float G { get; }

    /// <summary>Blue.</summary>
    public float B { get; }

    /// <summary>The linear colour of an sRGB one.</summary>
    public static LinearColor From(ThemeColor c) => new LinearColor(ToLinear(c.R), ToLinear(c.G), ToLinear(c.B));

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055) / 1.055, 2.4);
}

/// <summary>
/// Replaces painted vertex colours with a character's own (T-706, docs/THEMES.md 1.7): the skin, hair and cloth of a body or face by the colours their models are
/// painted with, or a hairstyle as a whole. A colour near none of the painted ones stays as it is. Immutable; applied on a worker while a merge runs.
/// </summary>
public sealed class Recolor
{
    /// <summary>How far a vertex colour may be from a painted one, per linear channel: the engine keeps 8 bits a channel, so one step and rounding.</summary>
    public const float Tolerance = 1.5f / 255f;

    /// <summary>Changes nothing.</summary>
    public static readonly Recolor None = new Recolor(Array.Empty<Rule>(), null);

    private readonly Rule[] _rules;
    private readonly LinearColor? _whole;

    private Recolor(Rule[] rules, LinearColor? whole)
    {
        _rules = rules;
        _whole = whole;
    }

    private readonly struct Rule
    {
        public Rule(LinearColor paint, LinearColor result)
        {
            Paint = paint;
            Result = result;
        }

        public readonly LinearColor Paint;
        public readonly LinearColor Result;
    }

    /// <summary>True when it changes nothing.</summary>
    public bool IsNone => _rules.Length == 0 && _whole is null;

    /// <summary>True when it colours a whole mesh (<see cref="WholeMesh"/>), so <see cref="Apply"/> needs the mesh's dominant colour.</summary>
    public bool NeedsDominant => _whole is not null;

    /// <summary>Every vertex takes <paramref name="color"/>, keeping its relation to the mesh's most common colour (<see cref="Dominant"/>): a lighter strand stays lighter.</summary>
    public static Recolor WholeMesh(ThemeColor color) => new Recolor(Array.Empty<Rule>(), LinearColor.From(color));

    /// <summary>Collects rules for painted colours.</summary>
    public sealed class Builder
    {
        private readonly List<Rule> _rules = new List<Rule>();

        /// <summary>Families of painted colours: in each, the first becomes <paramref name="color"/> and the others keep their relation to the first (a cheek, a shadow).</summary>
        public Builder Add(IReadOnlyList<IReadOnlyList<ThemeColor>> families, ThemeColor color)
        {
            var result = LinearColor.From(color);
            foreach (var family in families)
            {
                if (family.Count == 0) continue;
                var first = LinearColor.From(family[0]);
                foreach (var shade in family)
                {
                    var from = LinearColor.From(shade);
                    _rules.Add(new Rule(from, Scale(from, first, result)));
                }
            }
            return this;
        }

        /// <summary>The finished rules.</summary>
        public Recolor Build() => _rules.Count == 0 ? None : new Recolor(_rules.ToArray(), null);
    }

    /// <summary>
    /// The colour a vertex takes. <paramref name="dominant"/> is the mesh's most common colour (<see cref="Dominant"/>); only a <see cref="WholeMesh"/> reads it.
    /// </summary>
    public LinearColor Apply(LinearColor vertex, LinearColor dominant)
    {
        if (_whole is { } whole) return Scale(vertex, dominant, whole);
        for (var i = 0; i < _rules.Length; i++)
        {
            var paint = _rules[i].Paint;
            if (Math.Abs(vertex.R - paint.R) <= Tolerance && Math.Abs(vertex.G - paint.G) <= Tolerance && Math.Abs(vertex.B - paint.B) <= Tolerance) return _rules[i].Result;
        }
        return vertex;
    }

    /// <summary>The most common colour of a mesh: what a hairstyle is painted with, whatever that is. White for a mesh without vertices.</summary>
    public static LinearColor Dominant(IEnumerable<LinearColor> vertices)
    {
        var counts = new Dictionary<int, (int Count, LinearColor First)>();
        var best = int.MaxValue;
        var most = 0;
        var color = new LinearColor(1, 1, 1);
        foreach (var v in vertices)
        {
            var key = Byte(v.R) << 16 | Byte(v.G) << 8 | Byte(v.B);
            var known = counts.TryGetValue(key, out var seen) ? seen : (Count: 0, First: v);
            counts[key] = (known.Count + 1, known.First);
            if (known.Count + 1 < most || (known.Count + 1 == most && key >= best)) continue;
            (best, most, color) = (key, known.Count + 1, known.First);
        }
        return color;
    }

    private static int Byte(float c) => (int)Math.Round(Math.Max(0, Math.Min(1, c)) * 255);

    private static LinearColor Scale(LinearColor color, LinearColor from, LinearColor to) =>
        new LinearColor(Channel(color.R, from.R, to.R), Channel(color.G, from.G, to.G), Channel(color.B, from.B, to.B));

    /// <summary>A channel as it stands to <paramref name="from"/>, moved to <paramref name="to"/>, never above 1. A painted channel of almost nothing carries no relation: the result is <paramref name="to"/>.</summary>
    private static float Channel(float color, float from, float to) => from < 2f / 255f ? to : Math.Min(1f, to * color / from);
}

/// <summary>
/// A manifest entry's <c>recolor</c> (T-706): the painted colours of a colour role (VOCABULARY "Appearance colour roles") on that model. A role the entry does not
/// name uses the contract's colours (<c>AppearancePaint</c>).
/// </summary>
public sealed class RecolorPaint
{
    /// <summary>An entry without <c>recolor</c>.</summary>
    public static readonly RecolorPaint Contract = new RecolorPaint(new Dictionary<string, IReadOnlyList<ThemeColor>>());

    private readonly IReadOnlyDictionary<string, IReadOnlyList<ThemeColor>> _roles;

    internal RecolorPaint(IReadOnlyDictionary<string, IReadOnlyList<ThemeColor>> roles) => _roles = roles;

    /// <summary>True when the entry names the role itself (an empty list: nothing on the model takes that colour).</summary>
    public bool Names(string role) => _roles.ContainsKey(role);

    /// <summary>The families painted for a role: the entry's own list as one family, else <paramref name="contract"/>.</summary>
    public IReadOnlyList<IReadOnlyList<ThemeColor>> Families(string role, IReadOnlyList<IReadOnlyList<ThemeColor>> contract) =>
        _roles.TryGetValue(role, out var own) ? new[] { own } : contract;

    /// <summary>Reads <c>#rrggbb</c> colours, leaving out what is not one (validate reports those).</summary>
    public static List<ThemeColor> Parse(IEnumerable<string> colors)
    {
        var list = new List<ThemeColor>();
        foreach (var text in colors)
            if (ThemeColor.TryParse(text, out var color)) list.Add(color);
        return list;
    }
}
