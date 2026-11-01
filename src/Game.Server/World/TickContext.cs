using Game.Server.Events;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>
/// What a system sees during one tick of one instance. Reused across ticks; do not keep it or its
/// <see cref="Intents"/> list after <see cref="ITickSystem.Tick"/> returns. Structural requests
/// (<see cref="Spawn"/>, <see cref="Despawn"/>, <see cref="Warp"/>) are queued and applied in tick phase 3, after every
/// system has run, in request order.
/// </summary>
public sealed class TickContext
{
    private static readonly IReadOnlyList<IIntent> NoIntents = [];
    private readonly MapInstance _instance;

    internal TickContext(MapInstance instance) => _instance = instance;

    /// <summary>This tick's number: 0 for an instance's first tick, then +1 per tick.</summary>
    public long TickNumber { get; internal set; }

    /// <summary>Simulated time at the start of this tick: <see cref="TickNumber"/> × <see cref="TickRate.IntervalMs"/>.</summary>
    public long SimTimeMs => TickNumber * TickRate.IntervalMs;

    /// <summary>Simulated milliseconds this tick covers.</summary>
    public int DeltaMs => TickRate.IntervalMs;

    /// <summary>The instance being ticked: its map id, grid and entities.</summary>
    public MapInstance Instance => _instance;

    /// <summary>The instance's random source. The only randomness a system may use.</summary>
    public IRandom Random => _instance.Random;

    /// <summary>The intents routed to the system currently running, in arrival order. Owning entities exist and are not dead.</summary>
    public IReadOnlyList<IIntent> Intents { get; internal set; } = NoIntents;

    /// <summary>Queues an event; it is dispatched in phase 4 of this tick.</summary>
    public void Publish<T>(T e) where T : IGameEvent => _instance.Events.Publish(e);

    /// <summary>
    /// Allocates an id and queues a new entity on this instance's map; it appears in phase 3. Throws
    /// <see cref="ArgumentException"/> if the cell is not walkable or the facing is not a <c>Directions</c> term.
    /// </summary>
    public EntityId Spawn(EntityKind kind, GridCell cell, string facing)
    {
        var entity = _instance.World.CreateEntity(kind, _instance.MapId, cell, facing);
        _instance.Defer(new StructuralChange.Add(entity, MapChangeReason.Spawn));
        return entity.Id;
    }

    /// <summary>Queues removal of an entity of this instance (phase 3). Unknown ids are ignored then.</summary>
    public void Despawn(EntityId id) => _instance.Defer(new StructuralChange.Remove(id));

    /// <summary>
    /// Queues a warp (phase 3): to another cell of this map, or to another map's instance, which the entity joins on
    /// that instance's next tick. Rejected with a <see cref="WarpRejected"/> event if the map is unknown or the cell is
    /// not walkable there.
    /// </summary>
    public void Warp(EntityId id, string targetMapId, GridCell targetCell) =>
        _instance.Defer(new StructuralChange.Warp(id, targetMapId, targetCell));

    internal static void CheckFacing(string facing)
    {
        if (facing is null || !Directions.All.Contains(facing))
            throw new ArgumentException($"'{facing}' is not a Directions term.", nameof(facing));
    }
}

/// <summary>A change to an instance's entity set, applied only in tick phase 3.</summary>
internal abstract record StructuralChange
{
    private StructuralChange()
    {
    }

    /// <summary>Add an entity (spawn or warp arrival).</summary>
    public sealed record Add(Entity Entity, MapChangeReason Reason) : StructuralChange;

    /// <summary>Remove (despawn) an entity.</summary>
    public sealed record Remove(EntityId Id) : StructuralChange;

    /// <summary>Move an entity to a cell of this or another map.</summary>
    public sealed record Warp(EntityId Id, string TargetMapId, GridCell TargetCell) : StructuralChange;
}
