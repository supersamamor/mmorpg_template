using Game.Shared.World;

namespace Game.Server.World;

/// <summary>Server-run-unique entity id. Allocated by <see cref="EntityIdAllocator"/>; never reused within a run.</summary>
public readonly record struct EntityId(long Value)
{
    /// <inheritdoc />
    public override string ToString() => "#" + Value;
}

/// <summary>What an entity is. Engine-level categories, not content ids.</summary>
public enum EntityKind
{
    /// <summary>A player character.</summary>
    Player,

    /// <summary>A monster.</summary>
    Monster,

    /// <summary>A non-player character.</summary>
    Npc,
}

/// <summary>
/// One thing in a map instance. A plain mutable class owned by exactly one <see cref="MapInstance"/> and touched only
/// on that instance's tick. This is deliberately not an ECS: systems that need more state (path, HP, AI) add fields
/// here or keep their own per-entity tables keyed by <see cref="Id"/>, which is enough at our entity counts and keeps
/// the code obvious. Structural changes (add, remove, map change) go through <see cref="TickContext"/> and are applied
/// in tick phase 3; systems may change <see cref="Cell"/> and <see cref="Facing"/> directly.
/// </summary>
public sealed class Entity
{
    internal Entity(EntityId id, EntityKind kind, string mapId, GridCell cell, string facing)
    {
        Id = id;
        Kind = kind;
        MapId = mapId;
        Cell = cell;
        Facing = facing;
    }

    /// <summary>Unique id for this server run.</summary>
    public EntityId Id { get; }

    /// <summary>Player, monster or NPC.</summary>
    public EntityKind Kind { get; }

    /// <summary>The map whose instance owns this entity (the target map while a warp is in flight).</summary>
    public string MapId { get; internal set; }

    /// <summary>Current cell on the map's logic grid.</summary>
    public GridCell Cell { get; set; }

    /// <summary>Facing, a <c>Directions</c> term.</summary>
    public string Facing { get; set; }

    /// <summary>
    /// Set by game systems when the entity can no longer act (e.g. died). Intents for a dead entity are dropped.
    /// Being dead does not remove it; despawning does.
    /// </summary>
    public bool Dead { get; set; }
}

/// <summary>Thread-safe, monotonically increasing id source shared by every instance of one <see cref="GameWorld"/>.</summary>
public sealed class EntityIdAllocator
{
    private long _last;

    /// <summary>Returns a new id; ids start at 1 and are never reused.</summary>
    public EntityId Next() => new(Interlocked.Increment(ref _last));
}
