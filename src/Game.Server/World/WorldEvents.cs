using Game.Server.Events;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>Why an entity entered or left a map.</summary>
public enum MapChangeReason
{
    /// <summary>Created in the world, or removed from it.</summary>
    Spawn,

    /// <summary>Moved by a warp (between maps or to another cell of the same map).</summary>
    Warp,
}

/// <summary><c>EntityEnteredMap</c>: raised by the instance the entity joined, in the tick it was added.</summary>
public sealed record EntityEnteredMap(EntityId Entity, EntityKind Kind, string MapId, GridCell Cell, MapChangeReason Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.EntityEnteredMap;
}

/// <summary><c>EntityLeftMap</c>: raised by the instance the entity left, in the tick it was removed.</summary>
public sealed record EntityLeftMap(EntityId Entity, EntityKind Kind, string MapId, GridCell Cell, MapChangeReason Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.EntityLeftMap;
}

/// <summary><c>WarpRejected</c>: raised by the entity's instance when a warp could not be done; the entity did not move.</summary>
public sealed record WarpRejected(EntityId Entity, string FromMapId, string TargetMapId, GridCell TargetCell, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.WarpRejected;
}

/// <summary><c>MovementStarted</c>: raised when a move begins or is re-pathed; <c>PathLength</c> is the number of cells to walk.</summary>
public sealed record MovementStarted(EntityId Entity, GridCell From, GridCell Goal, int PathLength) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.MovementStarted;
}

/// <summary><c>CellReached</c>: a walking entity fully reached <c>Cell</c>, coming from the adjacent <c>From</c>, now facing <c>Facing</c>.</summary>
public sealed record CellReached(EntityId Entity, GridCell From, GridCell Cell, string Facing) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.CellReached;
}

/// <summary><c>MovementStopped</c>: a move ended at <c>Cell</c> for a <see cref="MovementReasons"/> term.</summary>
public sealed record MovementStopped(EntityId Entity, GridCell Cell, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.MovementStopped;
}

/// <summary><c>MoveRejected</c>: a <see cref="MoveTo"/> was refused for a <see cref="MovementReasons"/> term; the entity did not change course.</summary>
public sealed record MoveRejected(EntityId Entity, GridCell Target, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.MoveRejected;
}
