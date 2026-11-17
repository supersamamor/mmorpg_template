using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Protocol;

namespace Game.Server.World.Inventory;

/// <summary>
/// Builds a player's own <c>inventory</c> and <c>inventory_changed</c> messages (ARCHITECTURE §10) from the store the inventory systems write to.
/// Call <see cref="For"/> on the tick thread of the instance that holds the player: the store's state for a character is only touched there.
/// </summary>
public sealed class OwnInventory
{
    private readonly InventoryBalanceDef _balance;
    private readonly IInventoryStore _store;

    /// <summary>Reads <paramref name="store"/>, the one the inventory systems use; <paramref name="balance"/> gives the entry count.</summary>
    public OwnInventory(InventoryBalanceDef balance, IInventoryStore store)
    {
        _balance = balance ?? throw new ArgumentNullException(nameof(balance));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>The player's whole inventory, or null when the inventory has not seen it yet.</summary>
    public InventoryMessage? For(EntityId player)
    {
        if (!_store.TryGet(player, out var state)) return null;
        var entries = new InventoryEntryRecord[state.Entries.Count];
        for (var i = 0; i < entries.Length; i++) entries[i] = Record(state.Entries[i]);
        return new InventoryMessage(state.Revision, _balance.MaxEntries, state.Weight, state.WeightLimit, state.Load, entries);
    }

    /// <summary>The owner's <c>inventory_changed</c> for one change.</summary>
    public static InventoryChangedMessage Message(InventoryChanged e)
    {
        var entries = new InventoryEntryRecord[e.Upserted.Count];
        for (var i = 0; i < entries.Length; i++) entries[i] = Record(e.Upserted[i]);
        return new InventoryChangedMessage(e.Revision, e.Cause, e.ItemId, e.Weight, e.WeightLimit, e.Load, entries, e.Removed);
    }

    /// <summary>One entry as its owner sees it, cards and refine level included (only the owner ever gets these).</summary>
    public static InventoryEntryRecord Record(InventoryEntry entry) =>
        new(entry.Id, entry.ItemId, entry.Quantity, entry.Slot, entry.Cards.Count > 0 ? entry.Cards : null, entry.Refine);

    /// <summary>The owner's <c>refine_result</c>.</summary>
    public static RefineResultMessage Message(ItemRefined e) => new(e.EntryId, e.ItemId, e.Result, e.Level);
}
