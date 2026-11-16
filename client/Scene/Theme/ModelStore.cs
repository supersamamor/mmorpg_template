using Game.Client.Core.Animation;
using Game.Client.Core.Theme;
using Godot;

namespace Game.Client.Scene;

/// <summary>An animation library plus the clip name each <see cref="AnimClip"/> has in it, generic and per weapon type (T-403).</summary>
public sealed class AnimSet
{
    private readonly IReadOnlyDictionary<string, string> _clips;
    private readonly Dictionary<string, string?[]> _byWeapon = new(StringComparer.Ordinal);
    private readonly Func<string, bool> _has;

    internal AnimSet(AnimationLibrary library, string?[] names, IReadOnlyDictionary<string, string> clips)
    {
        Library = library;
        Names = names;
        _clips = clips;
        _has = name => library.HasAnimation(name);
    }

    /// <summary>The shared library; many players hold it, none changes it.</summary>
    public AnimationLibrary Library { get; }

    /// <summary>Clip name by <see cref="AnimClip"/>; null where the library has no such clip.</summary>
    public string?[] Names { get; }

    /// <summary>
    /// Clip names by <see cref="AnimClip"/> for a weapon type (§7.6): the library's <c>attack.&lt;weapon type&gt;</c>-style clip where it has one, else the
    /// generic clip (<see cref="WeaponClips"/>). Built once per weapon type and shared by every character holding it.
    /// </summary>
    public string?[] NamesFor(string? weaponType)
    {
        if (weaponType is null) return Names;
        if (_byWeapon.TryGetValue(weaponType, out var names)) return names;
        names = new string?[AnimClips.Count];
        for (var i = 0; i < names.Length; i++) names[i] = WeaponClips.NameFor(_clips, AnimClips.IdOf((AnimClip)i), weaponType, _has);
        return _byWeapon[weaponType] = names;
    }
}

/// <summary>
/// Loaded theme resources, loaded on a worker thread and kept in memory only as long as the budget allows: the packed scene of each model, and the animation
/// library of each rig, which is shared by every instance. Memory is bounded two ways: at most <c>ModelCacheLimit</c> scenes stay loaded, and when a scene is
/// dropped (least recently used first, never one with an instance in use) so are its idle pooled instances.
/// </summary>
public sealed class ModelStore
{
    private sealed class Entry
    {
        public PackedScene? Scene;
        public bool Failed;
        public long LastUsed;
        public int InUse;
    }

    private readonly Dictionary<string, Entry> _scenes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimSet?> _animations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
    private readonly int _limit;
    private readonly bool _threaded;
    private readonly Action<string> _log;
    private long _clock;

    /// <summary>Creates a store. Without <paramref name="threaded"/> every load happens at once on the calling thread (<see cref="ModelLoading"/>).</summary>
    public ModelStore(int cacheLimit, bool threaded, Action<string> log)
    {
        _limit = Math.Max(1, cacheLimit);
        _threaded = threaded;
        _log = log;
    }

    /// <summary>Scenes currently held.</summary>
    public int LoadedScenes => _scenes.Count(e => e.Value.Scene is not null);

    /// <summary>Animation libraries currently held.</summary>
    public int LoadedLibraries => _animations.Count(e => e.Value is not null);

    /// <summary>Starts loading a project-relative path in the background if nothing has.</summary>
    public void Request(string path)
    {
        if (_scenes.TryGetValue(path, out var entry) && (entry.Scene is not null || entry.Failed)) return;
        if (!_threaded)
        {
            LoadNow(path);
            return;
        }
        if (!_requested.Add(path)) return;
        var error = ResourceLoader.LoadThreadedRequest("res://" + path, "PackedScene");
        if (error != Error.Ok)
        {
            _log($"[theme] could not start loading {path}: {error}");
            _scenes[path] = new Entry { Failed = true };
        }
    }

    private void LoadNow(string path)
    {
        if (ResourceLoader.Load("res://" + path, "PackedScene") is PackedScene loaded)
        {
            _scenes[path] = new Entry { Scene = loaded, LastUsed = ++_clock };
            return;
        }
        _log($"[theme] {path} could not be loaded; drawing a fallback");
        _scenes[path] = new Entry { Failed = true };
    }

    /// <summary>The scene, if loaded. False while loading; <paramref name="failed"/> says it never will.</summary>
    public bool TryGetScene(string path, out PackedScene? scene, out bool failed)
    {
        scene = null;
        failed = false;
        if (_scenes.TryGetValue(path, out var entry))
        {
            if (entry.Failed)
            {
                failed = true;
                return false;
            }
            if (entry.Scene is not null)
            {
                entry.LastUsed = ++_clock;
                scene = entry.Scene;
                return true;
            }
        }
        Request(path);
        if (!_threaded) return TryGetScene(path, out scene, out failed); // Request has just filled in the entry
        switch (ResourceLoader.LoadThreadedGetStatus("res://" + path))
        {
            case ResourceLoader.ThreadLoadStatus.Loaded:
                if (ResourceLoader.LoadThreadedGet("res://" + path) is PackedScene loaded)
                {
                    _scenes[path] = new Entry { Scene = loaded, LastUsed = ++_clock };
                    _requested.Remove(path);
                    scene = loaded;
                    return true;
                }
                break;
            case ResourceLoader.ThreadLoadStatus.InProgress:
                return false;
        }
        _log($"[theme] {path} could not be loaded; drawing a fallback");
        _scenes[path] = new Entry { Failed = true };
        failed = true;
        return false;
    }

    /// <summary>The animation set of a library, built once from its scene. False while it loads; null in <paramref name="set"/> if it never will (no animations).</summary>
    public bool TryGetAnimations(string path, ThemeAsset library, out AnimSet? set)
    {
        if (_animations.TryGetValue(path, out set)) return true;
        if (!TryGetScene(path, out var scene, out var failed))
        {
            if (failed) _animations[path] = null;
            set = null;
            return failed;
        }

        var instance = scene!.Instantiate();
        var player = instance.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
        if (player is null)
        {
            _log($"[theme] {path} has no animations");
            instance.Free();
            _animations[path] = set = null;
            return true;
        }
        var shared = new AnimationLibrary();
        foreach (var name in player.GetAnimationList()) shared.AddAnimation(name, player.GetAnimation(name));
        var names = new string?[AnimClips.Count];
        for (var i = 0; i < names.Length; i++)
        {
            var clip = (AnimClip)i;
            if (library.Clips.TryGetValue(AnimClips.IdOf(clip), out var name) && shared.HasAnimation(name))
            {
                names[i] = name;
                shared.GetAnimation(name).LoopMode = AnimClips.Loops(clip) ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            }
            else if (clip == AnimClip.Idle || clip == AnimClip.Walk)
            {
                _log($"[theme] {path}: no clip for '{AnimClips.IdOf(clip)}'");
            }
        }
        // Weapon-type variants (attack.staff, idle.staff, ...) loop like the clip they stand in for (T-403).
        foreach (var (id, name) in library.Clips)
        {
            var dot = id.IndexOf('.');
            if (dot <= 0 || !shared.HasAnimation(name)) continue;
            var baseId = id.Substring(0, dot);
            for (var i = 0; i < AnimClips.Count; i++)
                if (AnimClips.IdOf((AnimClip)i) == baseId)
                    shared.GetAnimation(name).LoopMode = AnimClips.Loops((AnimClip)i) ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        }
        instance.Free();
        _scenes.Remove(path); // only the library is needed from now on
        _requested.Remove(path);
        _animations[path] = set = new AnimSet(shared, names, library.Clips);
        return true;
    }

    /// <summary>Waits for loads still running on worker threads and drops them. Call before the tree goes away, so a quit never races a worker.</summary>
    public void Drain()
    {
        foreach (var path in _requested.ToList())
        {
            if (ResourceLoader.LoadThreadedGetStatus("res://" + path) == ResourceLoader.ThreadLoadStatus.InvalidResource) continue;
            ResourceLoader.LoadThreadedGet("res://" + path); // blocks until done
        }
        _requested.Clear();
    }

    /// <summary>Lets go of every loaded scene and library while the engine is still up, so nothing is released during runtime teardown.</summary>
    public void Clear()
    {
        Drain();
        _scenes.Clear();
        _animations.Clear();
    }

    /// <summary>A visual started using a scene.</summary>
    public void Use(string path)
    {
        if (_scenes.TryGetValue(path, out var entry)) entry.InUse++;
    }

    /// <summary>A visual stopped using a scene.</summary>
    public void Unuse(string path)
    {
        if (_scenes.TryGetValue(path, out var entry)) entry.InUse = Math.Max(0, entry.InUse - 1);
    }

    /// <summary>Drops the least recently used scenes nobody is using until the limit holds. <paramref name="dropped"/> hears about each path so it can free pooled instances.</summary>
    public void Trim(Action<string> dropped)
    {
        while (LoadedScenes > _limit)
        {
            string? victim = null;
            var oldest = long.MaxValue;
            foreach (var (path, entry) in _scenes)
            {
                if (entry.Scene is null || entry.InUse > 0 || entry.LastUsed >= oldest) continue;
                oldest = entry.LastUsed;
                victim = path;
            }
            if (victim is null) return; // everything loaded is in use: nothing may go
            dropped(victim);
            _scenes.Remove(victim);
            _requested.Remove(victim);
        }
    }
}
