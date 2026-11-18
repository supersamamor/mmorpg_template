using System;
using System.Collections.Generic;

namespace Game.Client.Core.Theme;

/// <summary>How far down the fallback chain a model had to go (ARCHITECTURE §8).</summary>
public enum ResolutionLevel
{
    /// <summary>The key content named, from the active theme or the default theme.</summary>
    Exact,

    /// <summary>The theme's <c>fallbacks</c> entry for the entity's kind.</summary>
    KindFallback,

    /// <summary>The theme's <c>fallbacks.missing</c> entry: the visible "missing asset" model.</summary>
    MissingFallback,

    /// <summary>Nothing in any theme: the client draws its own built-in marker.</summary>
    BuiltIn,
}

/// <summary>Where a model comes from.</summary>
public sealed class ModelResolution
{
    internal ModelResolution(string? key, string? path, ThemeAsset? asset, ResolutionLevel level)
    {
        Key = key;
        Path = path;
        Asset = asset;
        Level = level;
    }

    /// <summary>The asset key actually used; null for <see cref="ResolutionLevel.BuiltIn"/>.</summary>
    public string? Key { get; }

    /// <summary>The file, relative to the project root (<c>themes/&lt;theme&gt;/...</c>); null for <see cref="ResolutionLevel.BuiltIn"/>.</summary>
    public string? Path { get; }

    /// <summary>Its manifest entry.</summary>
    public ThemeAsset? Asset { get; }

    /// <summary>How far down the chain it was found.</summary>
    public ResolutionLevel Level { get; }
}

/// <summary>
/// Turns asset keys into files (ARCHITECTURE §7.7, §8). The layers are searched in order: the active theme, then <c>default</c>, so a theme only
/// has to contain what it changes. A key counts as found when a layer lists it <i>and</i> its file exists. A model that cannot be found goes down the
/// chain: the key's kind fallback, then <c>missing</c>, then the built-in marker, with one warning per key. It never throws and never returns null.
/// </summary>
public sealed class ThemeCatalog
{
    /// <summary>The folder themes are mounted in, relative to the project root (§7.7).</summary>
    public const string MountPoint = "themes";

    /// <summary>The <c>fallbacks</c> name for the "missing asset" model.</summary>
    public const string MissingName = "missing";

    private readonly IReadOnlyList<ThemeManifest> _layers;
    private readonly Func<string, bool> _exists;
    private readonly Action<string> _warn;
    private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelResolution> _byKey = new Dictionary<string, ModelResolution>(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _effects = new Dictionary<string, string?>(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _impactDelays = new Dictionary<string, double>(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelResolution> _byKind = new Dictionary<string, ModelResolution>(StringComparer.Ordinal);

    /// <summary>Creates a catalog over <paramref name="layers"/> (active theme first).</summary>
    /// <param name="layers">The manifests to search, in order.</param>
    /// <param name="exists">True when the file at a project-relative path (<c>themes/default/models/x.glb</c>) exists.</param>
    /// <param name="warn">Gets one line per problem key, once.</param>
    public ThemeCatalog(IReadOnlyList<ThemeManifest> layers, Func<string, bool> exists, Action<string> warn)
    {
        _layers = layers;
        _exists = exists;
        _warn = warn;
    }

    /// <summary>How many distinct warnings were raised (for tests and the log).</summary>
    public int WarningCount => _warned.Count;

    /// <summary>The project-relative path of a theme file.</summary>
    public static string PathOf(string theme, string file) => MountPoint + "/" + theme + "/" + file;

    /// <summary>Finds a key in the layers. False when no layer has the entry with its file present.</summary>
    public bool TryFind(string key, out string path, out ThemeAsset asset)
    {
        for (var i = 0; i < _layers.Count; i++)
        {
            if (!_layers[i].TryGet(key, out var found) || found.File is null) continue;
            var candidate = PathOf(_layers[i].Theme, found.File);
            if (!_exists(candidate)) continue;
            path = candidate;
            asset = found;
            return true;
        }
        path = string.Empty;
        asset = null!;
        return false;
    }

    /// <summary>
    /// The model for an entity: <paramref name="key"/> is the asset key content gave it (null if content named none) and <paramref name="kind"/> an
    /// <c>EntityKinds</c> term. Cached, so asking again allocates nothing.
    /// </summary>
    public ModelResolution ResolveModel(string? key, string kind)
    {
        if (key is not null)
        {
            if (_byKey.TryGetValue(key, out var cached)) return cached;
            if (TryFind(key, out var path, out var asset))
                return _byKey[key] = new ModelResolution(key, path, asset, ResolutionLevel.Exact);
            Warn(key, Why(key));
            return _byKey[key] = FallbackFor(kind);
        }

        if (_byKind.TryGetValue(kind, out var byKind)) return byKind;
        return _byKind[kind] = FallbackFor(kind);
    }

    /// <summary>The file of a scene key (a map's visual scene). False, with one warning per key, when no theme has it: the caller shows its fallback.</summary>
    public bool ResolveScene(string key, out string path)
    {
        if (TryFind(key, out path, out _)) return true;
        Warn(key, Why(key));
        return false;
    }

    /// <summary>The animation library for a model: its <c>anims</c> key resolved. Null when the model has none or the library is missing (one warning).</summary>
    public ThemeAsset? ResolveAnimations(ThemeAsset model, out string path)
    {
        path = string.Empty;
        if (model.Anims is null) return null;
        if (TryFind(model.Anims, out path, out var library)) return library;
        Warn(model.Anims, Why(model.Anims));
        return null;
    }

    /// <summary>
    /// The scene file for an effect name (T-303, <see cref="EffectNames"/>): each theme layer's manifest entry tagged with that name (<c>"effect"</c>) gives the asset key, whose file must
    /// exist; if no layer has a usable one the built-in chain (<see cref="EffectNames.DefaultFallback"/>) moves to the next name. False when nothing in the chain is
    /// found, so the effect is simply not shown; one warning per problem key. Cached.
    /// </summary>
    public bool ResolveEffect(string name, out string path)
    {
        if (_effects.TryGetValue(name, out var cached))
        {
            path = cached ?? string.Empty;
            return cached is not null;
        }
        var current = (string?)name;
        for (var depth = 0; depth < 4 && current is not null; depth++)
        {
            for (var i = 0; i < _layers.Count; i++)
            {
                if (!_layers[i].TryGetEffect(current, out var key)) continue;
                if (TryFind(key, out path, out _))
                {
                    _effects[name] = path;
                    return true;
                }
                Warn(key, Why(key));
            }
            current = EffectNames.DefaultFallback(current);
        }
        Warn(name, "no usable scene in any theme; the effect is not shown");
        _effects[name] = null;
        path = string.Empty;
        return false;
    }

    /// <summary>
    /// The file for a render role (T-222, <see cref="RenderRoles"/>): the first theme layer whose manifest tags an entry with that role (<c>"render"</c>) and whose file
    /// exists. False when no layer has a usable one; the caller (<see cref="RenderPlan"/>) warns once for all roles. A tagged key whose file is missing is warned once.
    /// </summary>
    public bool ResolveRender(string role, out string path)
    {
        for (var i = 0; i < _layers.Count; i++)
        {
            if (!_layers[i].TryGetRender(role, out var key)) continue;
            if (TryFind(key, out path, out _)) return true;
            Warn(key, Why(key));
        }
        path = string.Empty;
        return false;
    }

    /// <summary>
    /// Seconds from the start of the attack clip to its impact for a model: the model's own <c>impact_delay</c>, else its animation library's (the rig's), else
    /// <paramref name="fallback"/>. Cached per model, so asking again allocates nothing.
    /// </summary>
    public double ImpactDelayOf(ModelResolution model, double fallback)
    {
        var cacheKey = model.Path ?? string.Empty;
        if (_impactDelays.TryGetValue(cacheKey, out var cached)) return cached;
        var value = fallback;
        if (model.Asset is { } asset)
        {
            if (asset.ImpactDelay is { } own) value = own;
            else if (asset.Anims is not null && TryFind(asset.Anims, out _, out var library) && library.ImpactDelay is { } rig) value = rig;
        }
        return _impactDelays[cacheKey] = value;
    }

    /// <summary>
    /// The theme's "missing asset" model (<c>fallbacks.missing</c>), or the built-in marker when no theme has a usable one. Worn equipment whose visual key is missing
    /// or wrong falls back to it (T-403). Cached.
    /// </summary>
    public ModelResolution ResolveMissing()
    {
        const string cacheKey = "\u0000" + MissingName; // never an entity kind
        if (_byKind.TryGetValue(cacheKey, out var cached)) return cached;
        if (TryFallback(MissingName, out var missing, ResolutionLevel.MissingFallback)) return _byKind[cacheKey] = missing;
        Warn("fallbacks." + MissingName, "no usable 'fallbacks.missing' model in any theme; drawing the built-in marker");
        return _byKind[cacheKey] = new ModelResolution(null, null, null, ResolutionLevel.BuiltIn);
    }

    /// <summary>Logs <paramref name="message"/> the first time <paramref name="key"/> has a problem, and never again for that key (T-403: equipment and bones).</summary>
    public void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _warn(message);
    }

    /// <summary>Why a key could not be found, for a warning: no theme lists it, its entry names no file, or the file is missing.</summary>
    public string WhyMissing(string key) => Why(key);

    private ModelResolution FallbackFor(string kind)
    {
        if (TryFallback(kind, out var byKind, ResolutionLevel.KindFallback)) return byKind;
        if (TryFallback(MissingName, out var missing, ResolutionLevel.MissingFallback)) return missing;
        Warn("fallbacks." + MissingName, "no usable 'fallbacks.missing' model in any theme; drawing the built-in marker");
        return new ModelResolution(null, null, null, ResolutionLevel.BuiltIn);
    }

    private bool TryFallback(string name, out ModelResolution resolution, ResolutionLevel level)
    {
        resolution = null!;
        for (var i = 0; i < _layers.Count; i++)
        {
            if (!_layers[i].TryGetFallback(name, out var key)) continue;
            if (!TryFind(key, out var path, out var asset))
            {
                Warn(key, Why(key));
                continue;
            }
            resolution = new ModelResolution(key, path, asset, level);
            return true;
        }
        return false;
    }

    private string Why(string key)
    {
        var listed = false;
        for (var i = 0; i < _layers.Count; i++)
        {
            if (!_layers[i].TryGet(key, out var found)) continue;
            listed = true;
            if (found.File is null) return "entry '" + key + "' names no file";
        }
        return listed ? "its file is missing on disk" : "no theme lists it";
    }

    private void Warn(string key, string reason)
    {
        if (_warned.Add(key)) _warn("[theme] asset '" + key + "': " + reason + "; using a fallback");
    }
}
