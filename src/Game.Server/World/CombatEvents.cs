using Game.Server.Events;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>
/// <c>EntityDied</c>: an entity's HP reached 0. Published by combat (T-302) in the tick of the killing hit, with <see cref="Entity.Dead"/>
/// already set; the monster spawner despawns a dead monster it owns and schedules its respawn. <c>Killer</c> is the entity credited with
/// the kill (the one whose hit took the last HP), or null (environment, script). <c>Kind</c>, <c>DefinitionId</c> (the monster id, null for
/// players) and <c>Cell</c> describe the dead entity at the moment of death, so listeners (quests) need not look it up.
/// </summary>
public sealed record EntityDied(EntityId Entity, EntityId? Killer, EntityKind Kind, string? DefinitionId, GridCell Cell) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.EntityDied;
}

/// <summary>
/// <c>DamageDealt</c>: <c>Source</c> attacked <c>Target</c>; one per resolved attack. <c>Amount</c> is the resolver's damage: 0 on a miss or
/// an immune element, negative when the element heals. The target's HP changed by <c>Amount</c>, clamped to 0..max_hp, leaving
/// <c>HpLeft</c>. <c>Result</c> is a <see cref="HitResults"/> term. Published by combat (T-302); a passive monster that is hit takes the
/// source as its target.
/// </summary>
public sealed record DamageDealt(EntityId Source, EntityId Target, int Amount, string Result, int HpLeft) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.DamageDealt;
}
