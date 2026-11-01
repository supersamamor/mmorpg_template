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
