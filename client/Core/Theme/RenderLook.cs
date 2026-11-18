using System;
using System.Collections.Generic;
using Game.Shared.Protocol;

namespace Game.Client.Core.Theme;

/// <summary>
/// The render roles a theme fills (VOCABULARY "Render roles", T-222): a manifest entry says which with a <c>"render"</c> field, so which material, outline and
/// environment draw the world is theme data and no asset key is written in code (CLAUDE.md rule 3).
/// </summary>
public static class RenderRoles
{
    /// <summary>The cel-shaded material (a <c>ShaderMaterial</c> resource): characters, monsters, equipment, props and map meshes take it in place of their own.</summary>
    public const string Toon = "toon";

    /// <summary>The outline material, drawn as the overlay of nearby characters, monsters and interactable objects.</summary>
    public const string Outline = "outline";

    /// <summary>The world's environment: a scene with the sky, ambient, haze and key light.</summary>
    public const string Environment = "environment";

    /// <summary>Every role, in the order a warning lists them.</summary>
    public static readonly string[] All = { Toon, Outline, Environment };
}

/// <summary>
/// What the active theme gives for the storybook toon look (ADR-003, T-222), resolved once through the theme chain: the file for each render role, or null. A
/// theme (and the default theme behind it) that leaves a role out falls back to the look from before T-222 for that part, with <b>one</b> warning naming everything
/// missing. Pure, so the fallback is tested without the engine.
/// </summary>
public sealed class RenderPlan
{
    private RenderPlan(string? toon, string? outline, string? environment, string? warning)
    {
        Toon = toon;
        Outline = outline;
        Environment = environment;
        Warning = warning;
    }

    /// <summary>The toon material's file, or null: materials stay as the models bring them.</summary>
    public string? Toon { get; }

    /// <summary>The outline material's file, or null: no outlines.</summary>
    public string? Outline { get; }

    /// <summary>The environment scene's file, or null: the client's built-in light and background.</summary>
    public string? Environment { get; }

    /// <summary>The one warning about missing roles, or null when the theme has them all (or the look is switched off).</summary>
    public string? Warning { get; }

    /// <summary>True when the toon material is used.</summary>
    public bool ToonShading => Toon is not null;

    /// <summary>The plan with nothing from the theme: the look from before T-222 (the <c>toon_shading</c> setting off).</summary>
    public static RenderPlan Off { get; } = new RenderPlan(null, null, null, null);

    /// <summary>Resolves every role through <paramref name="catalog"/>; the warning, if any, goes out once through the catalog.</summary>
    public static RenderPlan Resolve(ThemeCatalog catalog)
    {
        var found = new string?[RenderRoles.All.Length];
        List<string>? missing = null;
        for (var i = 0; i < found.Length; i++)
        {
            if (catalog.ResolveRender(RenderRoles.All[i], out var path)) found[i] = path;
            else (missing ??= new List<string>()).Add(RenderRoles.All[i]);
        }
        string? warning = null;
        if (missing is not null)
        {
            warning = "[theme] no usable render entry for " + string.Join(", ", missing) + " in any theme; drawing the look from before T-222 for " + (missing.Count == 1 ? "it" : "them");
            catalog.WarnOnce("\u0000render", warning);
        }
        return new RenderPlan(found[0], found[1], found[2], warning);
    }
}

/// <summary>
/// Which entities get an outline this frame (ADR-003 point 2, T-222): characters, monsters, NPCs and ground items (the things a player taps), alive, within
/// <c>distanceCells</c> of the camera target, and of those the nearest <c>maxCount</c> (ties to the lower id), so a crowd never pushes the draw calls past §9: each
/// outline is one more draw call. Terrain and props are never entities, so never outlined. Reuses its arrays: planning allocates nothing once warm.
/// </summary>
public sealed class OutlinePlanner
{
    private readonly double _limitSquared;
    private readonly int _maxCount;
    private double[] _distance = new double[64];
    private long[] _id = new long[64];
    private bool[] _eligible = new bool[64];
    private bool[] _outlined = new bool[64];
    private int[] _order = new int[64];
    private int _count;

    /// <summary>Creates a planner. A distance of 0 or a count of 0 outlines nothing.</summary>
    public OutlinePlanner(double distanceCells, int maxCount)
    {
        _limitSquared = distanceCells <= 0 ? -1 : distanceCells * distanceCells;
        _maxCount = Math.Max(0, maxCount);
    }

    /// <summary>True for the entity kinds that may have an outline (<see cref="EntityKinds"/>).</summary>
    public static bool KindWanted(string kind) =>
        kind == EntityKinds.Player || kind == EntityKinds.Monster || kind == EntityKinds.Npc || kind == EntityKinds.Item;

    /// <summary>Starts a round.</summary>
    public void Begin() => _count = 0;

    /// <summary>Adds an entity at squared distance <paramref name="distanceSquared"/> (cells) from the camera target; returns its slot.</summary>
    public int Add(long id, string kind, bool dead, double distanceSquared)
    {
        if (_count == _distance.Length)
        {
            var size = _count * 2;
            Array.Resize(ref _distance, size);
            Array.Resize(ref _id, size);
            Array.Resize(ref _eligible, size);
            Array.Resize(ref _outlined, size);
            Array.Resize(ref _order, size);
        }
        _distance[_count] = distanceSquared;
        _id[_count] = id;
        _eligible[_count] = !dead && KindWanted(kind) && distanceSquared <= _limitSquared;
        _outlined[_count] = false;
        return _count++;
    }

    /// <summary>Decides: the eligible entities, nearest first, up to the count.</summary>
    public void Plan()
    {
        var n = 0;
        for (var i = 0; i < _count; i++)
        {
            if (!_eligible[i]) continue;
            var at = n++;
            while (at > 0 && Before(i, _order[at - 1]))
            {
                _order[at] = _order[at - 1];
                at--;
            }
            _order[at] = i;
        }
        for (var rank = 0; rank < n && rank < _maxCount; rank++) _outlined[_order[rank]] = true;
    }

    /// <summary>True when the entity in <paramref name="slot"/> gets an outline.</summary>
    public bool Outlined(int slot) => _outlined[slot];

    /// <summary>How many got one in the last plan (for measuring).</summary>
    public int OutlinedCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < _count; i++) if (_outlined[i]) n++;
            return n;
        }
    }

    private bool Before(int a, int b) => _distance[a] < _distance[b] || (_distance[a] == _distance[b] && _id[a] < _id[b]);
}

/// <summary>
/// How often a worn piece that failed to load may be tried again (ADR-002 row 225, T-222): a failed load is not "present", so the preload asks again, up to
/// <c>attempts</c> tries in all; after that it is drawn as the missing model for good. Pure.
/// </summary>
public sealed class RetryBudget
{
    private readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly int _attempts;

    /// <summary>Allows <paramref name="attempts"/> tries in all (at least one).</summary>
    public RetryBudget(int attempts) => _attempts = Math.Max(1, attempts);

    /// <summary>Records a failed try of <paramref name="path"/>.</summary>
    public void Failed(string path) => _failures[path] = Failures(path) + 1;

    /// <summary>Failed tries of <paramref name="path"/> so far.</summary>
    public int Failures(string path) => _failures.TryGetValue(path, out var n) ? n : 0;

    /// <summary>True when <paramref name="path"/> has failed and may be tried again.</summary>
    public bool MayRetry(string path) => _failures.TryGetValue(path, out var n) && n < _attempts;

    /// <summary>True when <paramref name="path"/> has used every try.</summary>
    public bool GaveUp(string path) => Failures(path) >= _attempts;
}
