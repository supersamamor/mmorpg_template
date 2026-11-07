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

    /// <summary>The animation library for a model: its <c>anims</c> key resolved. Null when the model has none or the library is missing (one warning).</summary>
    public ThemeAsset? ResolveAnimations(ThemeAsset model, out string path)
    {
        path = string.Empty;
        if (model.Anims is null) return null;
        if (TryFind(model.Anims, out path, out var library)) return library;
        Warn(model.Anims, Why(model.Anims));
        return null;
    }

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
