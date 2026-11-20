using Game.Server.Events;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Dialogue;

/// <summary><c>NpcTalked</c>: <c>Player</c> started a dialogue with the NPC entity <c>Npc</c> (<c>NpcId</c> is its <c>npc.*</c> id). Quests (T-602) listen to this.</summary>
public sealed record NpcTalked(EntityId Player, EntityId Npc, string NpcId) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.NpcTalked;
}

/// <summary><c>DialogueShown</c>: <c>Player</c>'s dialogue is at node <c>NodeKey</c>; the owner is sent <c>Choices</c> (the shown ones, with their original indices).</summary>
public sealed record DialogueShown(EntityId Player, EntityId Npc, string DialogueId, string NodeKey, string Speaker, string? Portrait, string Text,
    IReadOnlyList<DialogueChoiceRecord> Choices, bool More) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.DialogueShown;
}

/// <summary><c>DialogueEnded</c>: <c>Player</c>'s dialogue (and any service it opened) with <c>Npc</c> ended for a <c>DialogueEndReasons</c> term.</summary>
public sealed record DialogueEnded(EntityId Player, EntityId Npc, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.DialogueEnded;
}

/// <summary><c>DialogueRefused</c>: a <c>talk</c> or <c>dialogue_choice</c> (<c>Intent</c>) was refused for a <c>DialogueReasons</c> term; nothing changed.</summary>
public sealed record DialogueRefused(EntityId Player, string Intent, string Reason, long? Target = null) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.DialogueRefused;
}

/// <summary>
/// <c>ServiceOpened</c>: an <c>open_service</c> action opened <c>Service</c> (a <c>ServiceIds</c> term) of the NPC entity <c>Npc</c> for <c>Player</c>; <c>ShopId</c> is the
/// action's <c>shop_id</c> (T-603), set for the <c>shop</c> service.
/// </summary>
public sealed record ServiceOpened(EntityId Player, EntityId Npc, string Service, string? ShopId = null) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ServiceOpened;
}

/// <summary><c>QuestStartRequested</c>: a <c>start_quest</c> action asks for <c>QuestId</c> to start for <c>Player</c>, offered by the NPC <c>NpcId</c>. Nobody handles it until T-602.</summary>
public sealed record QuestStartRequested(EntityId Player, string QuestId, string NpcId) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.QuestStartRequested;
}

/// <summary><c>SavePointSet</c>: <c>Player</c>'s save point is now <c>Cell</c> of <c>MapId</c>.</summary>
public sealed record SavePointSet(EntityId Player, string MapId, GridCell Cell) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.SavePointSet;
}

/// <summary><c>DialogueEffectsRequested</c>: a <c>run_effects</c> action asks combat to run the effects stored under <c>Key</c> (<c>DialogueActionKeys.Of</c>) on <c>Player</c>.</summary>
public sealed record DialogueEffectsRequested(EntityId Player, string Key) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.DialogueEffectsRequested;
}

/// <summary>An item and how many.</summary>
public readonly record struct ItemQuantity(string ItemId, int Quantity);

/// <summary>
/// <c>DialogueItemsRequested</c>: a choice's item actions, to apply to <c>Player</c>'s bag as one transaction (all <c>Takes</c>, then all <c>Gives</c>, or nothing).
/// The inventory answers with <see cref="DialogueItemsResolved"/> carrying the same <c>Token</c>.
/// </summary>
public sealed record DialogueItemsRequested(EntityId Player, long Token, IReadOnlyList<ItemQuantity> Takes, IReadOnlyList<ItemQuantity> Gives) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.DialogueItemsRequested;
}

/// <summary><c>DialogueItemsResolved</c>: the inventory applied the request <c>Token</c> (<c>Reason</c> null) or refused it, changing nothing, for a <c>DialogueReasons</c> term.</summary>
public sealed record DialogueItemsResolved(EntityId Player, long Token, string? Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.DialogueItemsResolved;
}
