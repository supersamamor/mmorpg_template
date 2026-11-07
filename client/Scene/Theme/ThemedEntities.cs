using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Theme;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Draws entities from the active theme (ARCHITECTURE §7.7, §8, §9): <c>display_key</c> to the bundle's model asset key to the theme's file, then a pooled
/// scene instance. A model that cannot be found goes down the catalog's fallback chain with one warning, never a crash. Nothing here names content or a path.
/// <para>Loading: models load on a worker thread; with <c>PreloadModels</c> every model the bundle names starts loading at start, so the first sight of a
/// monster finds it ready. A visual whose model is not ready yet stays hidden and fills in as soon as it is. At most <c>InstantiatePerFrame</c> new instances
/// are created per frame, so a warp that brings in 40 entities spreads over a few frames instead of one long one.</para>
/// <para>Memory: at most <c>ModelCacheLimit</c> scenes stay loaded (the least recently used go first, never one in use) and at most <c>PoolIdleLimit</c> idle
/// instances per model are kept.</para>
/// </summary>
public sealed class ThemedEntities : IEntityVisualSource
{
    private readonly Node3D _parent;
    private readonly ClientConfig _config;
    private readonly DisplayBundle _bundle;
    private readonly ThemeCatalog _catalog;
    private readonly ModelStore _store;
    private readonly Dictionary<string, Stack<ModelVisual>> _pools = new(StringComparer.Ordinal);
    private readonly List<ModelVisual> _waiting = new();
    private int _version;

    /// <summary>Creates the source; its nodes go under <paramref name="parent"/>.</summary>
    public ThemedEntities(Node3D parent, ClientConfig config, DisplayBundle bundle, ThemeCatalog catalog)
    {
        _parent = parent;
        _config = config;
        _bundle = bundle;
        _catalog = catalog;
        _store = new ModelStore(config.ModelCacheLimit, message => GD.PushWarning(message));
        // Headless has no renderer to warm up, and preloading there made the process crash on exit about one run in four (T-207 hand-off), so it is skipped.
        if (config.PreloadModels && DisplayServer.GetName() != "headless") Preload();
    }

    /// <summary>The store (for measuring).</summary>
    public ModelStore Store => _store;

    /// <summary>Visuals still waiting for a model or for their turn to be created.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>Instances created so far.</summary>
    public int Created { get; private set; }

    /// <inheritdoc />
    public int Version => _version;

    /// <summary>Starts loading, in the background, every model the bundle names and the fallback models (so the first sight of one does not wait on the disk).</summary>
    public void Preload()
    {
        foreach (var key in _bundle.ModelKeys) Request(_catalog.ResolveModel(key, string.Empty));
        foreach (var kind in new[] { EntityKinds.Player, EntityKinds.Monster, EntityKinds.Npc, EntityKinds.Item }) Request(_catalog.ResolveModel(null, kind));
    }

    private void Request(ModelResolution resolution)
    {
        if (resolution.Path is null || resolution.Asset is null) return;
        _store.Request(resolution.Path);
        if (_catalog.ResolveAnimations(resolution.Asset, out var animations) is not null) _store.Request(animations);
    }

    /// <inheritdoc />
    public IEntityVisual Acquire(string kind, string? displayKey)
    {
        var resolution = _catalog.ResolveModel(_bundle.ModelKeyOf(displayKey), kind);
        var pooledKey = resolution.Path ?? string.Empty;
        ModelVisual visual;
        if (_pools.TryGetValue(pooledKey, out var pool) && pool.Count > 0)
        {
            visual = pool.Pop();
            visual.Attach();
        }
        else
        {
            visual = new ModelVisual(_store, _catalog, resolution);
            _parent.AddChild(visual.Root);
        }
        visual.SetVisible(true);
        if (!visual.Ready) _waiting.Add(visual);
        return visual;
    }

    /// <inheritdoc />
    public void Release(IEntityVisual visual)
    {
        if (visual is not ModelVisual model) return;
        _waiting.Remove(model);
        model.Detach();
        var key = model.PoolKey;
        if (!_pools.TryGetValue(key, out var pool)) _pools[key] = pool = new Stack<ModelVisual>();
        if (pool.Count >= _config.PoolIdleLimit)
        {
            model.Free();
            return;
        }
        pool.Push(model);
    }

    /// <inheritdoc />
    public void Update()
    {
        if (_waiting.Count == 0) return;
        var created = 0;
        for (var i = 0; i < _waiting.Count;)
        {
            var visual = _waiting[i];
            if (visual.Ready || (created < _config.InstantiatePerFrame && visual.TryBuild(ref created)))
            {
                _waiting.RemoveAt(i);
                _version++;
                continue;
            }
            i++;
        }
        if (created > 0)
        {
            Created += created;
            _store.Trim(DropPool);
        }
    }

    /// <summary>Frees the pooled instances and lets go of every loaded resource. For the quit path.</summary>
    public void Shutdown()
    {
        foreach (var pool in _pools.Values)
            while (pool.Count > 0) pool.Pop().Free();
        _pools.Clear();
        _waiting.Clear();
        _store.Clear();
    }

    private void DropPool(string path)
    {
        if (!_pools.Remove(path, out var pool)) return;
        while (pool.Count > 0) pool.Pop().Free();
    }
}
