using Game.Server.Events;
using Game.Server.World.Dialogue;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Quests;

/// <summary>A player asks to drop its active quest <c>QuestId</c> (<c>abandon_quest</c>): its progress is gone and nothing comes back.</summary>
public sealed record AbandonQuest(EntityId Entity, string QuestId) : IIntent;

/// <summary>
/// <c>MonsterKilled</c> (T-602): the monster entity <c>Monster</c> (<c>MonsterId</c>) died on <c>Cell</c>; <c>Credited</c> are the players with kill credit (ARCHITECTURE §6.7: every
/// player with a damage share since its last full-HP moment who is still on the map, the same players who share its EXP), in first-hit order. Published by combat.
/// </summary>
public sealed record MonsterKilled(EntityId Monster, string MonsterId, GridCell Cell, IReadOnlyList<EntityId> Credited) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.MonsterKilled;
}

/// <summary><c>QuestStarted</c>: <c>Player</c> accepted <c>QuestId</c>; <c>Entry</c> is the quest at its first step (counters of what was already true included).</summary>
public sealed record QuestStarted(EntityId Player, string QuestId, QuestEntryRecord Entry) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestStarted;
}

/// <summary><c>QuestProgressed</c>: a counter of <c>Player</c>'s quest moved, or its turn-in is held (or no longer); <c>Entry</c> is the quest now.</summary>
public sealed record QuestProgressed(EntityId Player, string QuestId, QuestEntryRecord Entry) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestProgressed;
}

/// <summary><c>QuestStepCompleted</c>: step <c>Step</c> (0-based) of <c>Player</c>'s quest was turned in; <c>Entry</c> is the quest at its next step.</summary>
public sealed record QuestStepCompleted(EntityId Player, string QuestId, int Step, QuestEntryRecord Entry) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestStepCompleted;
}

/// <summary><c>QuestCompleted</c>: <c>Player</c> finished <c>QuestId</c>; its items and currency are in the bag and its other rewards were sent to the progression host.</summary>
public sealed record QuestCompleted(EntityId Player, string QuestId) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestCompleted;
}

/// <summary><c>QuestAbandoned</c>: <c>Player</c> dropped <c>QuestId</c>; its progress is gone.</summary>
public sealed record QuestAbandoned(EntityId Player, string QuestId) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestAbandoned;
}

/// <summary>
/// <c>QuestRefused</c>: a quest start (<c>Intent</c> <c>dialogue_choice</c>: the choice whose <c>start_quest</c> asked for it) or an <c>abandon_quest</c> was refused for a
/// <c>QuestReasons</c> term; nothing changed.
/// </summary>
public sealed record QuestRefused(EntityId Player, string Intent, string QuestId, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestRefused;
}

/// <summary>
/// <c>QuestItemsRequested</c>: a step's turn-in, to apply to <c>Player</c>'s bag as one transaction: take <c>Takes</c> (consumed objectives), give <c>Gives</c> (item rewards)
/// and add <c>Currency</c>, or nothing. The inventory answers <see cref="QuestItemsResolved"/> with the same <c>Token</c>.
/// </summary>
public sealed record QuestItemsRequested(EntityId Player, long Token, IReadOnlyList<ItemQuantity> Takes, IReadOnlyList<ItemQuantity> Gives, long Currency) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestItemsRequested;
}

/// <summary><c>QuestItemsResolved</c>: the inventory applied request <c>Token</c> (<c>Reason</c> null) or refused it, changing nothing, for a <c>QuestReasons</c> term.</summary>
public sealed record QuestItemsResolved(EntityId Player, long Token, string? Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestItemsResolved;
}

/// <summary>
/// <c>QuestRewardsRequested</c>: the rewards of the completed <c>QuestId</c> that the progression host (combat) pays: base and job EXP (through <c>ProgressionRules</c>), stat and
/// skill points, job unlocks and statuses (<c>StatusKeys</c>: compiled <c>apply_status</c> effects, <c>QuestRules.RewardKey</c>). None of them can fail.
/// </summary>
public sealed record QuestRewardsRequested(EntityId Player, string QuestId, long BaseExp, long JobExp, int StatPoints, int SkillPoints, IReadOnlyList<string> UnlockJobs,
    IReadOnlyList<string> StatusKeys) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestRewardsRequested;
}

/// <summary>
/// <c>QuestRewarded</c>: combat (the progression host) paid the non-item rewards of <c>Player</c>'s completed <c>QuestId</c>: the EXP (levels reached came as
/// <c>PlayerProgressed</c>), the points and the unlocks (and the statuses, which announce themselves).
/// </summary>
public sealed record QuestRewarded(EntityId Player, string QuestId, long BaseExp, long JobExp, int StatPoints, int SkillPoints, IReadOnlyList<string> UnlockJobs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestRewarded;
}

/// <summary><c>QuestMarkersChanged</c>: the NPC ids with a quest to offer (<c>Offer</c>) or to turn in (<c>TurnIn</c>) changed for <c>Player</c>.</summary>
public sealed record QuestMarkersChanged(EntityId Player, IReadOnlyList<string> Offer, IReadOnlyList<string> TurnIn) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestMarkersChanged;
}
