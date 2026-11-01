using Game.Server.Events;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Monsters;

/// <summary>
/// <c>EntitySpawned</c>: the spawner created a monster of <c>DefinitionId</c> at <c>Cell</c>, its home, for entry <c>SpawnIndex</c> of the
/// map grid's <c>spawns</c>. Raised in the tick the entity is added, so handlers already find it in the instance.
/// </summary>
public sealed record EntitySpawned(EntityId Entity, EntityKind Kind, string DefinitionId, GridCell Cell, int SpawnIndex) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.EntitySpawned;
}

/// <summary><c>AggroGained</c>: <c>Monster</c> took <c>Target</c> as its target, for an <see cref="AggroReasons"/> term.</summary>
public sealed record AggroGained(EntityId Monster, EntityId Target, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.AggroGained;
}

/// <summary><c>AggroLost</c>: <c>Monster</c> dropped <c>Target</c> and walks home, for an <see cref="AggroReasons"/> term.</summary>
public sealed record AggroLost(EntityId Monster, EntityId Target, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.AggroLost;
}

/// <summary>
/// <c>AttackRequested</c>: <c>Attacker</c> decided to auto-attack <c>Target</c> this tick. It stood on <c>AttackerCell</c>, the target on
/// <c>TargetCell</c>, at Chebyshev distance at most <c>Range</c> (the monster's <c>attack_range</c>). The AI only decides: combat (T-302)
/// resolves hit, damage and death, and may still refuse (target dead or out of range by then). One request per attack, at the monster's
/// <c>aspd</c> (attacks per second); <c>SimTimeMs</c> is the tick's simulated time.
/// </summary>
public sealed record AttackRequested(EntityId Attacker, EntityId Target, GridCell AttackerCell, GridCell TargetCell, int Range, long SimTimeMs)
    : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.AttackRequested;
}
