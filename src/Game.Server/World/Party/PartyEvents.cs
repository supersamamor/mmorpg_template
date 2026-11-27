using Game.Server.Events;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Party;

/// <summary>
/// <c>PartyKillCredit</c> (T-902): party members of a player with kill credit for <c>MonsterId</c> (dead on <c>Cell</c>) who were alive, on the instance and within
/// <c>party.kill_credit_range</c> of it, and had no kill credit of their own: their <c>kill</c> objectives count it. Published by the instance's party system from <c>MonsterKilled</c>;
/// the quest system listens. Neither calls the other (hard rule 7).
/// </summary>
public sealed record PartyKillCredit(EntityId Monster, string MonsterId, GridCell Cell, IReadOnlyList<EntityId> Members) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.PartyKillCredit;
}
