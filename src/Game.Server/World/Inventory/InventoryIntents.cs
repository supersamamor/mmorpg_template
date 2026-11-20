namespace Game.Server.World.Inventory;

/// <summary>
/// A player asks to pick up the ground item <c>Target</c>: the inventory walks it into <c>inventory.pickup_range</c> with <see cref="MoveTo"/>,
/// then checks <see cref="Combat.LootRules.MayPickUp"/>, the weight and the free entries. A new order (a move, a stop) cancels the walk.
/// </summary>
public sealed record PickUp(EntityId Entity, EntityId Target) : IIntent;

/// <summary>A player asks to use the consumable in inventory entry <c>EntryId</c>.</summary>
public sealed record UseItem(EntityId Entity, long EntryId) : IIntent;

/// <summary>A player asks to put on the equipment in entry <c>EntryId</c>, in <c>Slot</c> or (null) the first of its slots that is free.</summary>
public sealed record Equip(EntityId Entity, long EntryId, string? Slot) : IIntent;

/// <summary>A player asks to take off what it wears in <c>Slot</c>.</summary>
public sealed record Unequip(EntityId Entity, string Slot) : IIntent;

/// <summary>A player asks to put <c>Quantity</c> units of entry <c>EntryId</c> on the ground at its cell, for anyone to take.</summary>
public sealed record DropItem(EntityId Entity, long EntryId, int Quantity) : IIntent;

/// <summary>A player asks to socket the card in entry <c>CardEntryId</c> into the equipment entry <c>EntryId</c> (carried or worn), for good.</summary>
public sealed record InsertCard(EntityId Entity, long EntryId, long CardEntryId) : IIntent;

/// <summary>
/// T-603: a player asks to buy <c>Quantity</c> units of <c>ItemId</c> from the shop it has open. Routed although the entity is dead, so a dead player is told <c>dead</c>.
/// The price is never part of the intent: the inventory reads it from the shop's data.
/// </summary>
public sealed record ShopBuy(EntityId Entity, string ItemId, int Quantity) : IDeadEntityIntent;

/// <summary>T-603: a player asks to sell <c>Quantity</c> units of the carried entry <c>EntryId</c> to the shop it has open, at the item's sell price.</summary>
public sealed record ShopSell(EntityId Entity, long EntryId, int Quantity) : IDeadEntityIntent;

/// <summary>A player asks to refine the equipment entry <c>EntryId</c> (carried or worn) by one level, using the next step's material.</summary>
public sealed record RefineItem(EntityId Entity, long EntryId) : IIntent;
