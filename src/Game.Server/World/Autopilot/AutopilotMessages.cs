using Game.Server.Events;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.World.Autopilot;

/// <summary>
/// A player asks the server to play its active quests <c>Quests</c> in this order, with the shortcut skills <c>Skills</c> in slot order (T-604). Refused with
/// <see cref="AutopilotRefused"/>; a run in progress is replaced. Routed although the entity is dead, so a dead player is told <c>dead</c>. With <c>Battle</c>, an
/// <see cref="AutoBattleFilters"/> term (T-811), it is auto-battle instead: no quests, the monsters around the cell it starts on.
/// </summary>
public sealed record StartAutopilot(EntityId Entity, IReadOnlyList<string> Quests, IReadOnlyList<SkillLevelRecord> Skills, string? Battle = null) : IDeadEntityIntent;

/// <summary>Ends a player's autopilot run for <c>Reason</c>, an <see cref="AutopilotStops"/> term: <c>stopped</c> (the player) or <c>disconnected</c> (the gateway). Ignored without a run.</summary>
public sealed record StopAutopilot(EntityId Entity, string Reason) : IDeadEntityIntent, ICancelsAutomation;

/// <summary>
/// The player sent an order of its own: a run ends (<c>manual_input</c>). Sent by the gateway just before the order itself. <c>IsOrder</c>: that order says where to go or what to
/// fight, so the walk in progress is its to end; otherwise (an item used, a dialogue closed) the run's own walk is stopped too.
/// </summary>
public sealed record ManualInput(EntityId Entity, bool IsOrder) : IDeadEntityIntent, ICancelsAutomation;

/// <summary>What a run is doing, as its owner is told: an <see cref="AutopilotModes"/> term, the quest, an <see cref="AutopilotActivities"/> term and an
/// <see cref="AutopilotPauses"/> or <see cref="AutopilotStops"/> term. An auto-battle run (T-811) has no quest.</summary>
public sealed record AutopilotStatus(string Mode, string? QuestId, string? Doing, string? Reason);

/// <summary><c>AutopilotChanged</c> (T-604): <c>Player</c>'s autopilot status is now <c>Status</c>, different from the last one.</summary>
public sealed record AutopilotChanged(EntityId Player, AutopilotStatus Status) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.AutopilotChanged;
}

/// <summary><c>AutopilotRefused</c> (T-604): a <see cref="StartAutopilot"/> was refused for an <see cref="AutopilotReasons"/> (or, T-811, <see cref="AutoBattleReasons"/>) term about <c>QuestId</c> or <c>SkillId</c>; nothing changed.</summary>
public sealed record AutopilotRefused(EntityId Player, string Reason, string? QuestId, string? SkillId) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.AutopilotRefused;
}
