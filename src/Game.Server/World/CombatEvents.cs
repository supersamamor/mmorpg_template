using Game.Server.Events;
using Game.Shared.Vocabulary;

namespace Game.Server.World;

/// <summary>
/// <c>EntityDied</c>: an entity's HP reached 0. Published by combat (T-302); the monster spawner despawns a dead monster it owns and
/// schedules its respawn. <c>Killer</c> is the entity credited with the kill, or null (environment, script).
/// </summary>
public sealed record EntityDied(EntityId Entity, EntityId? Killer) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.EntityDied;
}

/// <summary>
/// <c>DamageDealt</c>: <c>Source</c> hit <c>Target</c> for <c>Amount</c> HP. Published by combat (T-302); a passive monster that is hit
/// takes the source as its target.
/// </summary>
public sealed record DamageDealt(EntityId Source, EntityId Target, int Amount) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.DamageDealt;
}
