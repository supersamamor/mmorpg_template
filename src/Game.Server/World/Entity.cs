using Game.Server.World.Combat;
using Game.Shared.Stats;
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

    /// <summary>
    /// An item lying on the ground (a monster's drop): <see cref="Entity.DefinitionId"/> is the item id and <see cref="Entity.GroundItem"/> the
    /// quantity, loot priority and expiry. Observers see it as kind <c>item</c> with its quantity and whether they may pick it up.
    /// </summary>
    Item,
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

    /// <summary>
    /// The entity's stats, or null for an entity that has none (it then has no <c>move_speed</c> and cannot walk). Set by whoever
    /// creates the entity, on the instance's tick; systems read derived values from it (movement reads <c>move_speed</c>).
    /// </summary>
    public StatSheet? Stats { get; set; }

    /// <summary>
    /// The content id the client shows this entity as (a <c>monster.*</c> or <c>npc.*</c> id it looks up in its display bundle), or null
    /// when there is none (players, for now). Sent to observers in the entity's view record; set by whoever creates the entity, on the
    /// instance's tick.
    /// </summary>
    public string? DefinitionId { get; set; }

    /// <summary>
    /// Where this entity's view of the world goes, or null for an entity nobody watches through. An entity with a sink is an
    /// <em>observer</em>: the AOI phase sends it a snapshot when the sink is set (or changes, or the entity arrives on a map) and a delta
    /// after every tick in which something it sees changed. The sink travels with the entity across warps. Set it on the instance's
    /// tick only (from another thread, send an <see cref="Aoi.AttachView"/> intent).
    /// </summary>
    public Aoi.IViewSink? View { get; set; }

    /// <summary>
    /// Current HP and SP of a combat-capable entity (player, monster), or null for one that cannot fight. Set and changed only by combat, on
    /// the instance's tick; it lives on the entity so it travels with it across warps and any system (the AOI, later regen) can read it.
    /// </summary>
    public Vitals? Vitals { get; set; }

    /// <summary>Quantity, loot priority and expiry of an <see cref="EntityKind.Item"/> entity; null for every other kind. Set by combat.</summary>
    public GroundItem? GroundItem { get; set; }
}

/// <summary>Thread-safe, monotonically increasing id source shared by every instance of one <see cref="GameWorld"/>.</summary>
public sealed class EntityIdAllocator
{
    private long _last;

    /// <summary>Returns a new id; ids start at 1 and are never reused.</summary>
    public EntityId Next() => new(Interlocked.Increment(ref _last));
}
