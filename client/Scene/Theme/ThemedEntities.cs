using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Theme;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Draws entities from the active theme (ARCHITECTURE §7.7, §8, §9): <c>display_key</c> to the bundle's model asset key to the theme's file, then a pooled
/// scene instance. A model that cannot be found goes down the catalog's fallback chain with one warning, never a crash. Nothing here names content or a path.
/// <para>Loading: models load on a worker thread (the main thread when there is no renderer, see <see cref="ModelLoading"/>); with <c>PreloadModels</c> every model the bundle names starts loading at start, so the first sight of a
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
    private readonly EquipmentCatalog _equipment;
    private readonly AppearanceCatalog _appearance;
    private readonly GearMesher _mesher;
    private readonly ToonLook? _look;
    private readonly Action<ModelVisual> _needsDressing;
    private readonly GearPreload _gearPreload;
    private readonly List<string> _gearPending = new();
    private readonly Dictionary<string, Stack<ModelVisual>> _pools = new(StringComparer.Ordinal);
    private readonly List<ModelVisual> _waiting = new();
    private readonly List<ModelVisual> _dressing = new();
    private int _version;

    /// <summary>Creates the source; its nodes go under <paramref name="parent"/>.</summary>
    public ThemedEntities(Node3D parent, ClientConfig config, DisplayBundle bundle, ThemeCatalog catalog, ToonLook? look = null)
    {
        _parent = parent;
        _config = config;
        _bundle = bundle;
        _catalog = catalog;
        _look = look;
        var threaded = ModelLoading.Threaded(DisplayServer.GetName(), config.ThreadedLoadWithoutRenderer);
        _store = new ModelStore(config.ModelCacheLimit, threaded, message => GD.PushWarning(message), config.PieceLoadAttempts);
        _equipment = new EquipmentCatalog(catalog, id => bundle.Item(id)?.Visual, id => bundle.Item(id)?.Slots);
        _appearance = new AppearanceCatalog(catalog, _equipment, bundle);
        _mesher = new GearMesher(config.LookCacheLimit, threaded, message => GD.PushWarning(message));
        _mesher.ByImage = config.MergeByImage;
        _gearPreload = new GearPreload(catalog, id => bundle.Item(id)?.Visual, config.GearPreloadLimit);
        _needsDressing = visual =>
        {
            if (!_dressing.Contains(visual)) _dressing.Add(visual);
        };
        if (config.PreloadModels) Preload();
    }

    /// <summary>Visuals whose gear changed and is not on yet (for measuring).</summary>
    public int Dressing => _dressing.Count;

    /// <summary>The toon look models are drawn with, or null.</summary>
    public ToonLook? Look => _look;

    /// <summary>Distinct looks resolved and remembered now (for measuring, T-706).</summary>
    public int LookKinds => _appearance.Count;

    /// <summary>Merged characters: cached now, and built so far (for measuring).</summary>
    public (int Cached, int Built) Merged => (_mesher.Cached, _mesher.Built);

    /// <summary>Textures read to tell which are the same image, and how many distinct images they were (T-711), for measuring.</summary>
    public (int Textures, int Images, double Ms) Images => (_mesher.Images.Read, _mesher.Images.Distinct, _mesher.Images.Milliseconds);

    /// <summary>Main-thread milliseconds spent on merges so far (T-222: the merge itself runs on a worker), for measuring.</summary>
    public double MergeMainThreadMs => _mesher.MainThreadMs;

    /// <summary>Worn pieces resolved so far, one per visual key and rig, shared by every character wearing them (for measuring).</summary>
    public int PieceKinds => _equipment.Count;

    /// <summary>Worn-visual models still loading ahead of need (for measuring).</summary>
    public int GearPending => _gearPending.Count;

    /// <summary>
    /// Loads the models of worn visuals before they are needed (T-405, row 182): <paramref name="worn"/> are the items characters on screen wear, <paramref name="bag"/> the
    /// own inventory's. Each starts loading through the shared store (a worker thread with a renderer, the main thread without one, <see cref="ModelLoading"/>), and once
    /// loaded its meshes are taken, one per frame, so equipping later finds them in the cache. Bounded by <c>GearPreloadLimit</c>; call again whenever the lists change.
    /// </summary>
    public void PreloadGear(IEnumerable<string> worn, IEnumerable<string> bag)
    {
        if (_config.GearPreloadLimit <= 0) return;
        _gearPending.Clear();
        foreach (var path in _gearPreload.Select(worn, bag))
        {
            if (_store.HasPiece(path)) continue;
            _store.Request(path);
            _gearPending.Add(path);
        }
    }

    private long _gearSignature = -1;
    private readonly List<string> _gearWorn = new();
    private readonly List<string> _gearBag = new();

    /// <summary>
    /// Once a frame: starts the preload (<see cref="PreloadGear"/>) when what characters on screen wear or what the own bag holds has changed since the last call. Costs a
    /// few additions per frame when nothing changed.
    /// </summary>
    public void WatchGear(WorldView world, Game.Client.Core.Items.InventoryView inventory)
    {
        long signature = inventory.Version;
        var entities = world.Entities;
        for (var i = 0; i < entities.Count; i++) signature = signature * 31 + entities[i].LookVersion + entities[i].Id;
        if (signature == _gearSignature) return;
        _gearSignature = signature;
        _gearWorn.Clear();
        _gearBag.Clear();
        for (var i = 0; i < entities.Count; i++) _gearWorn.AddRange(entities[i].Worn);
        for (var i = 0; i < inventory.Entries.Count; i++) _gearBag.Add(inventory.Entries[i].Item);
        PreloadGear(_gearWorn, _gearBag);
    }

    /// <summary>
    /// Measuring only (T-711): merges textured parts by image (true) or by texture object as before T-711, and frees every pooled visual, so the characters drawn next are merged anew.
    /// Call after <see cref="WorldPresenter.Redraw"/>, which gives the visuals back.
    /// </summary>
    public void SetMergeByImage(bool byImage)
    {
        _config.MergeByImage = byImage;
        _mesher.ByImage = byImage;
        foreach (var pool in _pools.Values)
            while (pool.Count > 0) pool.Pop().Free();
        _pools.Clear();
    }

    /// <summary>Job models loaded or loading now (T-711: only once a character is drawn by one), for checking and measuring.</summary>
    public int JobModelsHeld()
    {
        var held = 0;
        foreach (var key in _bundle.ModelKeys)
            if (_bundle.IsJobModelOnly(key) && _catalog.TryFind(key, out var path, out _) && _store.Holds(path)) held++;
        return held;
    }

    /// <summary>The store (for measuring).</summary>
    public ModelStore Store => _store;

    /// <summary>Visuals still waiting for a model or for their turn to be created.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>Instances created so far.</summary>
    public int Created { get; private set; }

    /// <inheritdoc />
    public int Version => _version;

    /// <inheritdoc />
    public double ImpactDelayOf(EntityView attacker) =>
        _catalog.ImpactDelayOf(ModelOf(attacker.Kind, attacker.DisplayKey, attacker.Appearance), _config.ImpactDelaySeconds);

    /// <summary>Starts loading, in the background, every model the bundle names and the fallback models (so the first sight of one does not wait on the disk).</summary>
    public void Preload()
    {
        // Job models only when a body is missing (T-711, ADR-002 row 314): otherwise the first character drawn by one loads it then, hidden until it is in. The players'
        // fallback stands in for a missing job model, so it goes with them.
        bool HasBody(string body) => _catalog.TryFind(body, out _, out _);
        var withJobs = !ModelPreload.JobModelsUnneeded(_bundle, _config.DrawAppearance, HasBody);
        foreach (var key in ModelPreload.Keys(_bundle, _config.DrawAppearance, HasBody)) Request(_catalog.ResolveModel(key, string.Empty));
        if (_bundle.Appearance is { } looks) // the bodies; faces and hairstyles load when someone with them is seen
            foreach (var gender in looks.Genders)
                if (_catalog.TryFind(gender.BodyModelAssetKey, out var path, out var asset)) Request(new ModelResolution(gender.BodyModelAssetKey, path, asset, ResolutionLevel.Exact));
        foreach (var kind in new[] { EntityKinds.Player, EntityKinds.Monster, EntityKinds.Npc, EntityKinds.Item })
            if (withJobs || kind != EntityKinds.Player) Request(_catalog.ResolveModel(null, kind));
    }

    private void Request(ModelResolution resolution)
    {
        if (resolution.Path is null || resolution.Asset is null) return;
        _store.Request(resolution.Path);
        if (_catalog.ResolveAnimations(resolution.Asset, out var animations) is not null) _store.Request(animations);
    }

    /// <summary>The model an entity is drawn with: a player's own body when it has looks (T-706), else what its display key names.</summary>
    private ModelResolution ModelOf(string kind, string? displayKey, AppearanceRecord? looks) =>
        _config.DrawAppearance && looks is not null && _appearance.Resolve(looks, displayKey) is { } look ? look.Body : _catalog.ResolveModel(_bundle.ModelKeyOf(displayKey), kind);

    /// <summary>
    /// A visual of its own under <paramref name="parent"/>, for the creation preview and the character list (T-706): the same models, merges and materials as the world's,
    /// loaded and dressed by the same <see cref="Update"/>, but never pooled. Give it back with <see cref="Discard"/>.
    /// </summary>
    public ModelVisual Preview(Node3D parent, string? jobId, AppearanceRecord looks)
    {
        var visual = new ModelVisual(_store, _catalog, _equipment, _mesher, _needsDressing, ModelOf(EntityKinds.Player, jobId, looks), _appearance, _look);
        parent.AddChild(visual.Root);
        visual.SetCharacter(jobId, looks);
        visual.SetVisible(true);
        _waiting.Add(visual);
        return visual;
    }

    /// <summary>The body a preview of these looks would stand on, so a caller knows whether its visual still fits.</summary>
    public string BodyOf(string? jobId, AppearanceRecord looks) => ModelOf(EntityKinds.Player, jobId, looks).PoolKey;

    /// <summary>Frees a <see cref="Preview"/> visual.</summary>
    public void Discard(ModelVisual visual)
    {
        _waiting.Remove(visual);
        _dressing.Remove(visual);
        visual.Free();
    }

    /// <inheritdoc />
    public IEntityVisual Acquire(string kind, string? displayKey, AppearanceRecord? looks = null)
    {
        var resolution = ModelOf(kind, displayKey, looks);
        var pooledKey = resolution.PoolKey;
        ModelVisual visual;
        if (_pools.TryGetValue(pooledKey, out var pool) && pool.Count > 0)
        {
            visual = pool.Pop();
            visual.Attach();
        }
        else
        {
            visual = new ModelVisual(_store, _catalog, _equipment, _mesher, _needsDressing, resolution, _appearance, _look);
            _parent.AddChild(visual.Root);
        }
        visual.SetCharacter(displayKey, looks); // before it shows: a pooled one still has the last entity's looks
        visual.SetVisible(true);
        if (!visual.Ready) _waiting.Add(visual);
        return visual;
    }

    /// <inheritdoc />
    public void Release(IEntityVisual visual)
    {
        if (visual is not ModelVisual model) return;
        _waiting.Remove(model);
        _dressing.Remove(model);
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
        if (_gearPending.Count > 0)
        {
            // One preloaded piece per frame: the take from its scene is the main-thread work that equipping would otherwise do in the frame the player taps.
            for (var i = 0; i < _gearPending.Count; i++)
            {
                if (!_store.TryGetPiece(_gearPending[i], out var preloaded)) continue;
                if (preloaded is not null) _mesher.Warm(preloaded); // its arrays are read now too, so the merge on equip starts at once (T-222)
                _gearPending.RemoveAt(i);
                break;
            }
        }
        if (_waiting.Count == 0 && _dressing.Count == 0) return;
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
        // Worn pieces share the per-frame instance budget with characters, so a crowd putting on gear also spreads over a few frames.
        for (var i = 0; i < _dressing.Count;)
        {
            if (_dressing[i].TryDress(ref created, _config.InstantiatePerFrame))
            {
                _dressing.RemoveAt(i);
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
        _dressing.Clear();
        _mesher.Clear();
        _store.Clear();
    }

    private void DropPool(string path)
    {
        if (!_pools.Remove(path, out var pool)) return;
        while (pool.Count > 0) pool.Pop().Free();
    }
}
