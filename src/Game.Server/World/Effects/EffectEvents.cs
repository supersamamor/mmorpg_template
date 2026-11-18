using Game.Server.Events;
using Game.Shared.Effects;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Effects;

/// <summary>
/// <c>StatusApplied</c>: <c>Caster</c> applied <c>StatusId</c> to <c>Target</c>; <c>Change</c> says whether it started, stacked or was only refreshed.
/// It now has <c>Stacks</c> and ends at <c>EndsAtMs</c> (simulated time). Raised by the effect runner's host (combat).
/// </summary>
public sealed record StatusApplied(EntityId Caster, EntityId Target, string StatusId, StatusChange Change, int Stacks, long EndsAtMs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.StatusApplied;
}

/// <summary><c>StatusExpired</c>: <c>StatusId</c> on <c>Target</c> ran out; its modifiers are gone.</summary>
public sealed record StatusExpired(EntityId Target, string StatusId) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.StatusExpired;
}

/// <summary><c>StatusRemoved</c>: <c>StatusId</c> on <c>Target</c> ended early, for a <see cref="StatusEndReasons"/> term; its modifiers are gone.</summary>
public sealed record StatusRemoved(EntityId Target, string StatusId, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.StatusRemoved;
}

/// <summary><c>HpRestored</c>: a <c>heal</c> by <c>Source</c> gave <c>Target</c> <c>Amount</c> HP (after the cap at max HP), leaving <c>Hp</c>.</summary>
public sealed record HpRestored(EntityId Source, EntityId Target, int Amount, int Hp) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.HpRestored;
}

/// <summary><c>SpRestored</c>: a <c>restore_sp</c> by <c>Source</c> gave <c>Target</c> <c>Amount</c> SP (after the cap at max SP), leaving <c>Sp</c>.</summary>
public sealed record SpRestored(EntityId Source, EntityId Target, int Amount, int Sp) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.SpRestored;
}

/// <summary>
/// <c>KnockedBack</c>: a <c>knockback</c> by <c>Source</c> pushes <c>Target</c> from <c>From</c> to <c>To</c>. The move itself is a same-map warp,
/// applied in phase 3 of the same tick: walkers stop (<c>MovementStopped</c> <c>warped</c>), and observers see a cell change.
/// </summary>
public sealed record KnockedBack(EntityId Source, EntityId Target, GridCell From, GridCell To) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.KnockedBack;
}

/// <summary><c>Teleported</c>: a <c>teleport</c> sends <c>Entity</c> to <c>Cell</c> of <c>MapId</c> (a warp, applied in phase 3 of the same tick).</summary>
public sealed record Teleported(EntityId Entity, string MapId, GridCell Cell) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.Teleported;
}

/// <summary>
/// <c>SummonRequested</c>: a <c>summon</c> by <c>Owner</c> asks for <c>Count</c> × <c>MonsterId</c> near <c>Near</c>, each living <c>DurationMs</c>
/// (null: until killed). The spawner creates them on its next tick, by its own rules and within the cap.
/// </summary>
public sealed record SummonRequested(EntityId Owner, string MonsterId, int Count, GridCell Near, int? DurationMs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.SummonRequested;
}

/// <summary>
/// <c>MonsterSummoned</c>: the spawner created <c>Monster</c> (a <c>MonsterId</c>) on <c>Cell</c> for <c>Owner</c>; it is despawned at <c>ExpiresAtMs</c>
/// (simulated time; <see cref="long.MaxValue"/> for never), and is never respawned.
/// </summary>
public sealed record MonsterSummoned(EntityId Owner, EntityId Monster, string MonsterId, GridCell Cell, long ExpiresAtMs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.MonsterSummoned;
}

/// <summary><c>AreaApplied</c>: an <c>area</c> by <c>Caster</c> around <c>Center</c> (<c>Radius</c> cells) ran its children on <c>Targets</c> entities.</summary>
public sealed record AreaApplied(EntityId Caster, GridCell Center, int Radius, int Targets) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.AreaApplied;
}

/// <summary>
/// <c>SkillCastStarted</c>: <c>Caster</c> began casting <c>SkillId</c> at <c>Level</c> on <c>Target</c> (an entity) or <c>Cell</c>; it resolves
/// after <c>CastMs</c> of simulated time (the skill's <c>cast_time_ms</c> × the caster's <c>cast_time_mod</c>) unless interrupted.
/// </summary>
public sealed record SkillCastStarted(EntityId Caster, string SkillId, int Level, EntityId? Target, GridCell? Cell, int CastMs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.SkillCastStarted;
}

/// <summary>
/// <c>SkillRequested</c>: <c>Caster</c> finished casting <c>SkillId</c> at <c>Level</c> on <c>Target</c> (null for a self or ground skill) or
/// <c>Cell</c> (ground). Combat resolves it on its next tick, re-checking everything (like <c>AttackRequested</c>), and runs its effects.
/// </summary>
public sealed record SkillRequested(EntityId Caster, string SkillId, int Level, EntityId? Target, GridCell? Cell, long SimTimeMs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.SkillRequested;
}

/// <summary><c>SkillUsed</c>: <c>SkillId</c> by <c>Caster</c> resolved on <c>Target</c> (the caster for a self skill) or <c>Cell</c>; its effects ran.</summary>
public sealed record SkillUsed(EntityId Caster, string SkillId, int Level, EntityId? Target, GridCell? Cell) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.SkillUsed;
}

/// <summary><c>SkillRefused</c>: a <c>SkillRequested</c> was dropped for a <see cref="SkillReasons"/> term; nothing ran.</summary>
public sealed record SkillRefused(EntityId Caster, string SkillId, EntityId? Target, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.SkillRefused;
}

/// <summary>One status as others see it (ARCHITECTURE §10): id, stacks, when it ends and whether it is a debuff. Never chances or numbers.</summary>
public readonly record struct StatusIcon(string Id, int Stacks, long EndsAtMs, bool Debuff);

/// <summary>
/// What observers see of an entity's statuses, on <see cref="Entity.StatusLook"/>: set by combat (the runner's host) whenever the set changes, read
/// by the AOI. Immutable; a new object means a change (as <see cref="Inventory.WornLook"/>). In application order.
/// </summary>
public sealed class StatusLook
{
    /// <summary>No statuses.</summary>
    public static readonly StatusLook None = new([], 0);

    /// <summary>Creates a look.</summary>
    public StatusLook(IReadOnlyList<StatusIcon> icons, int revision)
    {
        Icons = icons;
        Revision = revision;
    }

    /// <summary>The statuses.</summary>
    public IReadOnlyList<StatusIcon> Icons { get; }

    /// <summary>The <see cref="StatusSet.Revision"/> it was built from.</summary>
    public int Revision { get; }
}
