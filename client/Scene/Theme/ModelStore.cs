using Game.Client.Core.Animation;
using Game.Client.Core.Theme;
using Godot;

namespace Game.Client.Scene;

/// <summary>An animation library plus the clip name each <see cref="AnimClip"/> has in it.</summary>
public sealed class AnimSet
{
    internal AnimSet(AnimationLibrary library, string?[] names)
    {
        Library = library;
        Names = names;
    }

    /// <summary>The shared library; many players hold it, none changes it.</summary>
    public AnimationLibrary Library { get; }

    /// <summary>Clip name by <see cref="AnimClip"/>; null where the library has no such clip.</summary>
    public string?[] Names { get; }
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
    private readonly Action<string> _log;
    private long _clock;

    /// <summary>Creates a store.</summary>
    public ModelStore(int cacheLimit, Action<string> log)
    {
        _limit = Math.Max(1, cacheLimit);
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
        if (!_requested.Add(path)) return;
        var error = ResourceLoader.LoadThreadedRequest("res://" + path, "PackedScene");
        if (error != Error.Ok)
        {
            _log($"[theme] could not start loading {path}: {error}");
            _scenes[path] = new Entry { Failed = true };
        }
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
        instance.Free();
        _scenes.Remove(path); // only the library is needed from now on
        _requested.Remove(path);
        _animations[path] = set = new AnimSet(shared, names);
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
