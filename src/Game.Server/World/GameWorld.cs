using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Game.Shared.Random;
using Game.Shared.Time;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>
/// Every map instance of one server run, plus what they share: the entity id allocator and the entity → instance
/// directory used to route intents. One instance per map. Thread-safe entry points (<see cref="TryEnqueue"/>,
/// <see cref="Spawn"/>, <see cref="Despawn"/>, <see cref="Warp"/>) only queue work; it happens on the target instance's
/// next tick. Does no I/O and owns no threads; the host ticks the instances.
/// </summary>
public sealed class GameWorld
{
    private readonly Dictionary<string, MapInstance> _byMap = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<EntityId, MapInstance> _locations = new();
    private long _unknownEntityIntents;

    /// <summary>
    /// Creates one instance per grid, ordered by map id. Each instance gets its own systems from
    /// <paramref name="pipeline"/> and its own PCG32 stream: <paramref name="seed"/> with a stream number derived from
    /// the map id, so adding a map does not change other maps' sequences.
    /// </summary>
    public GameWorld(IEnumerable<MapGrid> grids, SystemPipeline pipeline, ulong seed, IClock clock, IServerLog log,
        int intentCapacity = IntentQueue.DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(grids);
        ArgumentNullException.ThrowIfNull(pipeline);
        var instances = new List<MapInstance>();
        foreach (var grid in grids.OrderBy(g => g.MapId, StringComparer.Ordinal))
        {
            if (_byMap.ContainsKey(grid.MapId)) throw new ArgumentException($"Two grids for map '{grid.MapId}'.", nameof(grids));
            var instance = new MapInstance(this, grid, pipeline.Build(), new Pcg32Random(seed, StreamFor(grid.MapId)), clock, log, intentCapacity);
            _byMap.Add(grid.MapId, instance);
            instances.Add(instance);
        }
        Instances = instances;
    }

    /// <summary>Instances in map id order.</summary>
    public IReadOnlyList<MapInstance> Instances { get; }

    /// <summary>The shared entity id source.</summary>
    public EntityIdAllocator Ids { get; } = new();

    /// <summary>Intents refused by <see cref="TryEnqueue"/> because the entity is not in the world.</summary>
    public long IntentsUnknownEntity => Interlocked.Read(ref _unknownEntityIntents);

    /// <summary>Finds a map's instance.</summary>
    public bool TryGetInstance(string mapId, [NotNullWhen(true)] out MapInstance? instance) => _byMap.TryGetValue(mapId, out instance);

    /// <summary>A map's instance; throws <see cref="KeyNotFoundException"/> if there is none.</summary>
    public MapInstance GetInstance(string mapId) =>
        _byMap.TryGetValue(mapId, out var instance) ? instance : throw new KeyNotFoundException($"No instance for map '{mapId}'.");

    /// <summary>The map the entity is on (or warping to), or null if it is not in the world. Thread-safe.</summary>
    public string? LocationOf(EntityId id) => _locations.TryGetValue(id, out var instance) ? instance.MapId : null;

    /// <summary>
    /// Queues an intent on the instance that holds (or is about to receive) the entity. Thread-safe. Returns
    /// <see cref="EnqueueResult.UnknownEntity"/> without queuing when the world does not know the entity.
    /// </summary>
    public EnqueueResult TryEnqueue(IIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (_locations.TryGetValue(intent.Entity, out var instance)) return instance.TryEnqueue(intent);
        Interlocked.Increment(ref _unknownEntityIntents);
        return EnqueueResult.UnknownEntity;
    }

    /// <summary>
    /// Allocates an id and queues a new entity on a map; it appears on that instance's next tick. Thread-safe. Throws
    /// <see cref="ArgumentException"/> for an unknown map, an unwalkable cell or a facing that is not a <c>Directions</c> term.
    /// </summary>
    public EntityId Spawn(EntityKind kind, string mapId, GridCell cell, string facing)
    {
        var instance = GetInstanceOrThrow(mapId);
        var entity = CreateEntity(kind, mapId, cell, facing);
        _locations[entity.Id] = instance;
        instance.Post(new StructuralChange.Add(entity, MapChangeReason.Spawn));
        return entity.Id;
    }

    /// <summary>Queues removal of an entity on its instance's next tick. Thread-safe. False if the world does not know it.</summary>
    public bool Despawn(EntityId id)
    {
        if (!_locations.TryGetValue(id, out var instance)) return false;
        instance.Post(new StructuralChange.Remove(id));
        return true;
    }

    /// <summary>
    /// Queues a warp on the entity's instance's next tick (same rules as <see cref="TickContext.Warp"/>). Thread-safe.
    /// False if the world does not know the entity.
    /// </summary>
    public bool Warp(EntityId id, string targetMapId, GridCell targetCell)
    {
        if (!_locations.TryGetValue(id, out var instance)) return false;
        instance.Post(new StructuralChange.Warp(id, targetMapId, targetCell));
        return true;
    }

    internal Entity CreateEntity(EntityKind kind, string mapId, GridCell cell, string facing)
    {
        var grid = GetInstanceOrThrow(mapId).Grid;
        if (!grid.IsWalkable(cell.X, cell.Y)) throw new ArgumentException($"Cell {cell} of '{mapId}' is not walkable.", nameof(cell));
        TickContext.CheckFacing(facing);
        return new Entity(Ids.Next(), kind, mapId, cell, facing);
    }

    internal void SetLocation(EntityId id, MapInstance instance) => _locations[id] = instance;

    /// <summary>Forgets the entity only if <paramref name="instance"/> still holds it (a later warp may have moved it on).</summary>
    internal void ClearLocation(EntityId id, MapInstance instance) =>
        _locations.TryRemove(new KeyValuePair<EntityId, MapInstance>(id, instance));

    private MapInstance GetInstanceOrThrow(string mapId) =>
        mapId is not null && _byMap.TryGetValue(mapId, out var instance)
            ? instance
            : throw new ArgumentException($"No instance for map '{mapId}'.", nameof(mapId));

    /// <summary>FNV-1a 64 of the map id: a stable PCG32 stream selector per map.</summary>
    private static ulong StreamFor(string mapId)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in mapId)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
