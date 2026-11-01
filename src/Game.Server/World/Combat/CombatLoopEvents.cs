using Game.Server.Events;
using Game.Shared.Progression;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Combat;

/// <summary>
/// <c>AttackRefused</c>: combat dropped an attack before resolving it: a monster's <c>AttackRequested</c> that no longer holds on the tick it
/// is resolved, or a player's <see cref="AttackTarget"/> that may not start. <c>Reason</c> is an <see cref="AttackReasons"/> term.
/// </summary>
public sealed record AttackRefused(EntityId Attacker, EntityId Target, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.AttackRefused;
}

/// <summary><c>AttackStopped</c>: a player's auto-attack on <c>Target</c> ended, for an <see cref="AttackReasons"/> term.</summary>
public sealed record AttackStopped(EntityId Attacker, EntityId Target, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.AttackStopped;
}

/// <summary>
/// <c>ExpGained</c>: <c>Player</c> earned its share of the EXP of <c>Monster</c> (a <c>MonsterId</c>) for dealing <c>Damage</c> of the HP it
/// lost. <c>BaseExp</c> / <c>JobExp</c> are the shares granted (EXP past the level cap is then discarded by progression); <c>BaseLevel</c> /
/// <c>JobLevel</c> are the levels after the grant. Level-ups follow as <see cref="PlayerProgressed"/>.
/// </summary>
public sealed record ExpGained(EntityId Player, EntityId Monster, string MonsterId, long Damage, long BaseExp, long JobExp, int BaseLevel, int JobLevel)
    : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ExpGained;
}

/// <summary>
/// A progression event of <c>Player</c>, passed through from <see cref="ProgressionRules"/>: <c>Change</c> is a <see cref="LevelUp"/> or
/// <see cref="JobLevelUp"/> (one per level reached, in order), and <see cref="Name"/> is its name (<c>LevelUp</c> / <c>JobLevelUp</c>).
/// </summary>
public sealed record PlayerProgressed(EntityId Player, IProgressionEvent Change) : IGameEvent
{
    /// <inheritdoc />
    public string Name => Change.Name;
}

/// <summary><c>ExpLost</c>: <c>Player</c> died and lost <c>BaseExp</c> (the <c>death.exp_penalty_percent</c> penalty; never a level).</summary>
public sealed record ExpLost(EntityId Player, long BaseExp) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ExpLost;
}

/// <summary>
/// <c>PlayerRespawned</c>: a dead player was revived with <c>Hp</c> and sent to <c>Cell</c> of <c>MapId</c> (a warp, applied in phase 3 of
/// the same tick; to another map it arrives on that instance's next tick).
/// </summary>
public sealed record PlayerRespawned(EntityId Player, string MapId, GridCell Cell, int Hp) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.PlayerRespawned;
}

/// <summary>
/// <c>ItemDropped</c>: <c>Monster</c> died and dropped <c>Quantity</c> × <c>ItemId</c>, now the ground item entity <c>Item</c> on <c>Cell</c>
/// (raised in the tick it is added, so handlers find it). <c>Owner</c> has loot priority until <c>OwnerUntilMs</c>; it expires at
/// <c>ExpiresAtMs</c> (simulated time). The same values are on the entity's <see cref="Entity.GroundItem"/>.
/// </summary>
public sealed record ItemDropped(EntityId Item, string ItemId, int Quantity, GridCell Cell, EntityId Monster, EntityId? Owner, long OwnerUntilMs,
    long ExpiresAtMs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemDropped;
}

/// <summary><c>ItemExpired</c>: nobody picked the ground item <c>Item</c> up in time; it was despawned from <c>Cell</c>.</summary>
public sealed record ItemExpired(EntityId Item, string ItemId, int Quantity, GridCell Cell) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemExpired;
}
