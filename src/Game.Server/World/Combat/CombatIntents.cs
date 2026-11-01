namespace Game.Server.World.Combat;

/// <summary>
/// A player asks to auto-attack <c>Target</c> (a monster): combat walks it into range with <see cref="MoveTo"/> intents and attacks at its
/// <c>aspd</c> until the target dies or leaves, the player stops (<see cref="StopAttack"/>, a new <see cref="MoveTo"/> or
/// <see cref="StopMove"/>), or no path leads into range. A new <c>AttackTarget</c> switches target without resetting the attack timer.
/// </summary>
public sealed record AttackTarget(EntityId Entity, EntityId Target) : IIntent;

/// <summary>A player asks to stop auto-attacking. Ignored when it is not attacking.</summary>
public sealed record StopAttack(EntityId Entity) : IIntent;

/// <summary>
/// A dead player asks to come back: it is revived at the respawn point with <c>death.respawn_hp_percent</c> of its <c>max_hp</c>. Routed
/// although the entity is dead (<see cref="IDeadEntityIntent"/>); ignored for a living entity or a non-player.
/// </summary>
public sealed record Respawn(EntityId Entity) : IDeadEntityIntent;
