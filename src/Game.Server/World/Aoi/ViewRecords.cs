using Game.Shared.World;

namespace Game.Server.World.Aoi;

/// <summary>
/// What one observer is told about one entity it can see (ARCHITECTURE §10): enough to place, orient and animate it, and to
/// interpolate a walk between ticks. Nothing the client must not know (§11): no AI state, no HP, no path beyond the next cell, no
/// spawn data.
/// </summary>
/// <param name="Id">Which entity; the key of every later change and disappearance.</param>
/// <param name="Kind">Player, monster or NPC: picks the client's presentation rules (nameplate, tap action).</param>
/// <param name="DisplayKey">The content id the client looks up in its display bundle (model, name), or null when there is none.</param>
/// <param name="Cell">The last cell the entity fully reached.</param>
/// <param name="Facing">A <c>Directions</c> term; while walking it points at <paramref name="Next"/>.</param>
/// <param name="Next">The cell being walked to, or null when standing. The only part of the path that is ever sent.</param>
/// <param name="Speed">Cells per second while walking (0 when standing), so the client can time the step from <paramref name="Cell"/> to <paramref name="Next"/>.</param>
public readonly record struct EntityView(EntityId Id, EntityKind Kind, string? DisplayKey, GridCell Cell, string Facing, GridCell? Next, double Speed);

/// <summary>The parts of an <see cref="EntityView"/> that changed (VOCABULARY "View fields").</summary>
[Flags]
public enum ViewFields
{
    /// <summary>Nothing changed.</summary>
    None = 0,

    /// <summary><c>cell</c>: <see cref="EntityView.Cell"/>.</summary>
    Cell = 1,

    /// <summary><c>facing</c>: <see cref="EntityView.Facing"/>.</summary>
    Facing = 2,

    /// <summary><c>next</c>: <see cref="EntityView.Next"/> (a step began, or the walk started or stopped).</summary>
    Next = 4,

    /// <summary><c>speed</c>: <see cref="EntityView.Speed"/> (the walk started or stopped, or the speed changed at a cell).</summary>
    Speed = 8,

    /// <summary><c>display_key</c>: <see cref="EntityView.DisplayKey"/>.</summary>
    DisplayKey = 16,
}

/// <summary>
/// An entity the observer already knows that changed this tick. <see cref="View"/> holds the whole current record so a sink never has
/// to keep state; <see cref="Fields"/> says which parts changed, and a serializer should send only those (plus the id).
/// </summary>
public readonly record struct EntityChange(ViewFields Fields, EntityView View);

/// <summary>
/// Everything an observer sees, sent when it starts observing, arrives on a map, or asks (<see cref="RequestSnapshot"/>). It replaces
/// whatever the client had: later deltas are relative to it. Valid only during the <see cref="IViewSink"/> call; the instance reuses it.
/// </summary>
public sealed class ViewSnapshot
{
    internal readonly List<EntityView> EntityList = new();

    internal ViewSnapshot()
    {
    }

    /// <summary>The instance tick this describes.</summary>
    public long Tick { get; internal set; }

    /// <summary>The map the observer is on.</summary>
    public string MapId { get; internal set; } = string.Empty;

    /// <summary>The observing entity (it is always among <see cref="Entities"/>).</summary>
    public EntityId Observer { get; internal set; }

    /// <summary>Every visible entity, by entity id.</summary>
    public IReadOnlyList<EntityView> Entities => EntityList;
}

/// <summary>
/// What changed for one observer in one tick, relative to its last snapshot or delta. Sent only when at least one list is non-empty.
/// Each list is ordered by entity id. Valid only during the <see cref="IViewSink"/> call; the instance reuses it.
/// </summary>
public sealed class ViewDelta
{
    internal readonly List<EntityView> AppearedList = new();
    internal readonly List<EntityChange> ChangedList = new();
    internal readonly List<EntityId> DisappearedList = new();

    internal ViewDelta()
    {
    }

    /// <summary>The instance tick this describes.</summary>
    public long Tick { get; internal set; }

    /// <summary>The map the observer is on.</summary>
    public string MapId { get; internal set; } = string.Empty;

    /// <summary>The observing entity.</summary>
    public EntityId Observer { get; internal set; }

    /// <summary>Entities that came into view (spawned, walked in, warped in), as full records.</summary>
    public IReadOnlyList<EntityView> Appeared => AppearedList;

    /// <summary>Entities still in view whose cell, facing, next cell, speed or display key changed.</summary>
    public IReadOnlyList<EntityChange> Changed => ChangedList;

    /// <summary>Entities that went out of view (walked out, despawned, warped away).</summary>
    public IReadOnlyList<EntityId> Disappeared => DisappearedList;

    internal bool IsEmpty => AppearedList.Count == 0 && ChangedList.Count == 0 && DisappearedList.Count == 0;

    internal void Clear()
    {
        AppearedList.Clear();
        ChangedList.Clear();
        DisappearedList.Clear();
    }
}

/// <summary>
/// Receives one observer's view (implemented by the gateway, T-205). Called on the owning instance's tick thread, in tick phase 5, so an
/// implementation must not block it: copy or serialize the message and queue it for sending; never do I/O inline. The message objects and
/// their lists are reused after the call returns, so do not keep them. Calls for one observer come from one instance at a time; after a
/// warp they come from the target instance's thread. A sink that throws is logged and detached (<see cref="Entity.View"/> is cleared);
/// the tick goes on.
/// </summary>
public interface IViewSink
{
    /// <summary>A full view that replaces the client's current one.</summary>
    void OnSnapshot(ViewSnapshot snapshot);

    /// <summary>The changes of one tick.</summary>
    void OnDelta(ViewDelta delta);
}
