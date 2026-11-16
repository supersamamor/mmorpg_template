using Game.Server.Events;
using Game.Shared.Items;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Inventory;

/// <summary><c>ItemAcquired</c>: <c>Player</c> put <c>Quantity</c> × <c>ItemId</c> in its inventory (picked up from the ground item <c>Source</c>).</summary>
public sealed record ItemAcquired(EntityId Player, string ItemId, int Quantity, EntityId Source) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemAcquired;
}

/// <summary><c>ItemUsed</c>: <c>Player</c> used one <c>ItemId</c> (a consumable; it was used up when <c>Consumed</c>).</summary>
public sealed record ItemUsed(EntityId Player, string ItemId, bool Consumed) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemUsed;
}

/// <summary><c>ItemEquipped</c>: <c>Player</c> put on entry <c>EntryId</c> (<c>ItemId</c>) in <c>Slot</c>.</summary>
public sealed record ItemEquipped(EntityId Player, string ItemId, long EntryId, string Slot) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemEquipped;
}

/// <summary><c>ItemUnequipped</c>: <c>Player</c> took entry <c>EntryId</c> (<c>ItemId</c>) off <c>Slot</c>, itself or because another item displaced it.</summary>
public sealed record ItemUnequipped(EntityId Player, string ItemId, long EntryId, string Slot) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemUnequipped;
}

/// <summary><c>ItemDiscarded</c>: <c>Player</c> put <c>Quantity</c> × <c>ItemId</c> on <c>Cell</c>, now the ground item <c>Item</c> (no owner).</summary>
public sealed record ItemDiscarded(EntityId Player, EntityId Item, string ItemId, int Quantity, GridCell Cell, long ExpiresAtMs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemDiscarded;
}

/// <summary><c>CardInserted</c>: <c>Player</c> socketed <c>CardId</c> into socket <c>Socket</c> (0-based) of entry <c>EntryId</c> (<c>ItemId</c>), for good.</summary>
public sealed record CardInserted(EntityId Player, long EntryId, string ItemId, string CardId, int Socket) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.CardInserted;
}

/// <summary>
/// <c>ItemRefined</c>: <c>Player</c> tried to refine entry <c>EntryId</c> (<c>ItemId</c>); <c>Result</c> is a <see cref="RefineResults"/> term and
/// <c>Level</c> its refine level now (the level it had when it broke). The material was used up whatever the result.
/// </summary>
public sealed record ItemRefined(EntityId Player, long EntryId, string ItemId, string Result, int Level) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemRefined;
}

/// <summary>
/// <c>ItemRefused</c>: the inventory refused <c>Intent</c> (a <c>MessageTypes</c> item intent: <c>pick_up</c>, <c>use_item</c>, ...) for an
/// <see cref="ItemReasons"/> term; nothing changed. <c>Target</c> (pick-up), <c>EntryId</c> and <c>Slot</c> say what it was about.
/// </summary>
public sealed record ItemRefused(EntityId Player, string Intent, string Reason, long? Target = null, long? EntryId = null, string? Slot = null) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemRefused;
}

/// <summary>
/// <c>InventoryChanged</c>: <c>Player</c>'s inventory changed, for an <see cref="InventoryChanges"/> term (<c>Cause</c>) about <c>ItemId</c>. The
/// owner is sent exactly this: <c>Revision</c> (one more than before), the entries added or changed (<c>Upserted</c>, immutable), the ids removed,
/// and the weight, limit and weight state after it.
/// </summary>
public sealed record InventoryChanged(EntityId Player, long Revision, string Cause, string? ItemId, IReadOnlyList<InventoryEntry> Upserted,
    IReadOnlyList<long> Removed, int Weight, int WeightLimit, string Load) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.InventoryChanged;
}
