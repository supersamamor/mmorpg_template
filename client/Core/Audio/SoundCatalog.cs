using System;
using System.Collections.Generic;

namespace Game.Client.Core.Audio;

/// <summary>A sound ready to play: the entry's mix and the project-relative paths of the files that exist (<c>themes/&lt;theme&gt;/audio/x.ogg</c>).</summary>
public sealed class ResolvedSound
{
    internal ResolvedSound(SoundEntry entry, string[] paths)
    {
        Key = entry.Key;
        Paths = paths;
        VolumeDb = entry.VolumeDb;
        PitchMin = entry.PitchMin;
        PitchMax = entry.PitchMax;
        Bus = entry.Bus;
        Loop = entry.Loop;
    }

    /// <summary>The asset key.</summary>
    public string Key { get; }

    /// <summary>Project-relative file paths; at least one.</summary>
    public string[] Paths { get; }

    /// <summary>Gain in decibels.</summary>
    public double VolumeDb { get; }

    /// <summary>Lowest pitch scale.</summary>
    public double PitchMin { get; }

    /// <summary>Highest pitch scale.</summary>
    public double PitchMax { get; }

    /// <summary>The bus it plays on.</summary>
    public string Bus { get; }

    /// <summary>True when it repeats.</summary>
    public bool Loop { get; }
}

/// <summary>
/// Turns sound keys into files through the theme chain (ARCHITECTURE §7.7, §7.8): the active theme, then <c>default</c>. A key is found when a theme lists it and
/// at least one of its files exists; a theme's entry replaces the default theme's whole entry. A key nobody has is silence with one warning, never a crash.
/// Cached, so asking again allocates nothing.
/// </summary>
public sealed class SoundCatalog
{
    private readonly IReadOnlyList<SoundTable> _layers;
    private readonly Func<string, bool> _exists;
    private readonly Action<string> _warn;
    private readonly Dictionary<string, ResolvedSound?> _cache = new Dictionary<string, ResolvedSound?>(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Creates a catalog over <paramref name="layers"/> (active theme first).</summary>
    /// <param name="layers">The tables to search, in order.</param>
    /// <param name="exists">True when the file at a project-relative path exists.</param>
    /// <param name="warn">Gets one line per problem key, once.</param>
    public SoundCatalog(IReadOnlyList<SoundTable> layers, Func<string, bool> exists, Action<string> warn)
    {
        _layers = layers;
        _exists = exists;
        _warn = warn;
        Defaults = Merge(true);
        Events = Merge(false);
    }

    /// <summary>The category defaults of all layers; the active theme wins.</summary>
    public IReadOnlyDictionary<string, string> Defaults { get; }

    /// <summary>The event sounds of all layers; the active theme wins.</summary>
    public IReadOnlyDictionary<string, string> Events { get; }

    /// <summary>How many distinct problem keys were warned about.</summary>
    public int WarningCount => _warned.Count;

    /// <summary>Every sound key any layer lists (for preloading).</summary>
    public IEnumerable<string> AllKeys()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < _layers.Count; i++)
            foreach (var key in _layers[i].Keys)
                if (seen.Add(key)) yield return key;
    }

    /// <summary>Finds a sound. False (silence) when no theme has the entry with a file on disk; one warning per key.</summary>
    public bool TryResolve(string key, out ResolvedSound sound)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            sound = cached!;
            return cached is not null;
        }
        var found = Find(key);
        _cache[key] = found;
        sound = found!;
        return found is not null;
    }

    private ResolvedSound? Find(string key)
    {
        var listed = false;
        for (var i = 0; i < _layers.Count; i++)
        {
            if (!_layers[i].TryGet(key, out var entry)) continue;
            listed = true;
            var paths = new List<string>(entry.Files.Length);
            foreach (var file in entry.Files)
            {
                var path = "themes/" + _layers[i].Theme + "/" + file;
                if (_exists(path)) paths.Add(path);
            }
            if (paths.Count > 0) return new ResolvedSound(entry, paths.ToArray());
        }
        if (_warned.Add(key)) _warn("[audio] sound '" + key + "': " + (listed ? "its files are missing on disk" : "no theme lists it") + "; playing silence");
        return null;
    }

    private Dictionary<string, string> Merge(bool defaults)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = _layers.Count - 1; i >= 0; i--)
            foreach (var pair in defaults ? _layers[i].Defaults : _layers[i].Events)
                merged[pair.Key] = pair.Value;
        return merged;
    }
}
