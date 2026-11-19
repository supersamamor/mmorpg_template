using System;
using System.Collections.Generic;
using Game.Client.Core.Bundle;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Items;

/// <summary>What the inventory panel offers for one entry: it only sends intents, and the server decides (CLAUDE.md rule 1).</summary>
[Flags]
public enum ItemActions
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary><c>use_item</c>: a consumable that is not worn.</summary>
    Use = 1,

    /// <summary><c>equip</c>: equipment that is not worn.</summary>
    Equip = 2,

    /// <summary><c>unequip</c>: something worn.</summary>
    Unequip = 4,

    /// <summary><c>drop_item</c>: anything not worn.</summary>
    Drop = 8,

    /// <summary><c>insert_card</c>: a card (the panel then lists the equipment it fits).</summary>
    InsertCard = 16,

    /// <summary><c>refine</c>: refinable equipment, carried or worn (the panel then shows the next step before it is sent).</summary>
    Refine = 32,
}

/// <summary>
/// The client's own inventory, as the server last said (<c>inventory</c>, then <c>inventory_changed</c> with the next <c>rev</c>), plus which entry the
/// player has selected in the panel. A change whose <c>rev</c> is not the next one is ignored and the view is marked <see cref="Stale"/> until the next
/// full message (every snapshot brings one). Nothing here decides anything; <see cref="Version"/> counts changes so the HUD redraws only then.
/// </summary>
public sealed class InventoryView
{
    private readonly List<InventoryEntryRecord> _entries = new List<InventoryEntryRecord>();

    /// <summary>True once a full <c>inventory</c> arrived (since the last <see cref="Reset"/>).</summary>
    public bool Known { get; private set; }

    /// <summary>True when a change was missed (a gap in <c>rev</c>): the view waits for the next full message.</summary>
    public bool Stale { get; private set; }

    /// <summary>The change counter of the last message applied.</summary>
    public long Rev { get; private set; }

    /// <summary>Most entries the inventory holds.</summary>
    public int Slots { get; private set; }

    /// <summary>Carried weight.</summary>
    public int Weight { get; private set; }

    /// <summary>The weight limit.</summary>
    public int WeightLimit { get; private set; }

    /// <summary>A "Weight states" term.</summary>
    public string Load { get; private set; } = WeightStates.Normal;

    /// <summary>The character's currency, as the server last said (T-602).</summary>
    public long Currency { get; private set; }

    /// <summary>Every entry, by id.</summary>
    public IReadOnlyList<InventoryEntryRecord> Entries => _entries;

    /// <summary>The selected entry's id, or null.</summary>
    public long? Selected { get; private set; }

    /// <summary>Counts every change (entries, weight, selection), so the panel redraws only on a change.</summary>
    public int Version { get; private set; }

    /// <summary>Carried weight as a share of the limit, 0–1 (1 when the limit is 0 and something is carried).</summary>
    public double WeightFraction => WeightLimit > 0 ? Math.Min(1, (double)Weight / WeightLimit) : Weight > 0 ? 1 : 0;

    /// <summary>The selected entry, or null (also when it is gone).</summary>
    public InventoryEntryRecord? SelectedEntry => Selected is { } id ? Find(id) : null;

    /// <summary>The entry with this id, or null.</summary>
    public InventoryEntryRecord? Find(long id)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].Id == id) return _entries[i];
        return null;
    }

    /// <summary>What is worn in <paramref name="slot"/>, or null.</summary>
    public InventoryEntryRecord? InSlot(string slot)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (string.Equals(_entries[i].Slot, slot, StringComparison.Ordinal)) return _entries[i];
        return null;
    }

    /// <summary><c>inventory</c>: replaces everything.</summary>
    public void Apply(InventoryMessage message)
    {
        _entries.Clear();
        _entries.AddRange(message.Entries);
        _entries.Sort((a, b) => a.Id.CompareTo(b.Id));
        Rev = message.Rev;
        Slots = message.Slots;
        SetWeight(message.Weight, message.WeightLimit, message.Load);
        Currency = message.Currency;
        Known = true;
        Stale = false;
        DropSelectionIfGone();
        Version++;
    }

    /// <summary><c>inventory_changed</c>: applied only when it is the next change; true if it was.</summary>
    public bool Apply(InventoryChangedMessage change)
    {
        if (!Known || change.Rev != Rev + 1)
        {
            if (Known && change.Rev > Rev) Stale = true;   // missed one: keep what we have until the next full message
            return false;
        }
        for (var i = 0; i < change.Removed.Count; i++)
        {
            var index = IndexOf(change.Removed[i]);
            if (index >= 0) _entries.RemoveAt(index);
        }
        for (var i = 0; i < change.Entries.Count; i++)
        {
            var entry = change.Entries[i];
            var index = IndexOf(entry.Id);
            if (index >= 0) _entries[index] = entry;
            else
            {
                var at = 0;
                while (at < _entries.Count && _entries[at].Id < entry.Id) at++;
                _entries.Insert(at, entry);
            }
        }
        Rev = change.Rev;
        SetWeight(change.Weight, change.WeightLimit, change.Load);
        Currency = change.Currency;
        DropSelectionIfGone();
        Version++;
        return true;
    }

    /// <summary>Selects an entry (or clears the selection with null, or by selecting the selected one again).</summary>
    public void Select(long? id)
    {
        var next = id is { } wanted && Find(wanted) is not null && Selected != wanted ? id : null;
        if (next == Selected) return;
        Selected = next;
        Version++;
    }

    /// <summary>Forgets everything (a new login).</summary>
    public void Reset()
    {
        _entries.Clear();
        Known = Stale = false;
        Rev = 0;
        Slots = Weight = WeightLimit = 0;
        Currency = 0;
        Load = WeightStates.Normal;
        Selected = null;
        Version++;
    }

    /// <summary>
    /// What the panel offers for <paramref name="entry"/>, from the bundle's item kind and whether it is worn: a worn item can be taken off (and refined),
    /// a carried one dropped and, by kind, used, put on, refined or (a card) socketed. The server checks everything again.
    /// </summary>
    public static ItemActions ActionsFor(InventoryEntryRecord entry, ItemDisplay? item)
    {
        var refine = item?.Kind == ItemKinds.Equip && item.Refinable ? ItemActions.Refine : ItemActions.None;
        if (entry.Slot is not null) return ItemActions.Unequip | refine;
        var actions = ItemActions.Drop | refine;
        if (item?.Kind == ItemKinds.Consumable) actions |= ItemActions.Use;
        if (item?.Kind == ItemKinds.Equip && item.Slots.Count > 0) actions |= ItemActions.Equip;
        if (item?.Kind == ItemKinds.Card) actions |= ItemActions.InsertCard;
        return actions;
    }

    /// <summary>
    /// The equipment entries (carried or worn, by id) that a card could go into: one of the card's slots is one of the item's, and it has a free card
    /// slot. All of it is display data from the bundle and the server's entries; the server decides.
    /// </summary>
    public List<InventoryEntryRecord> CardTargets(ItemDisplay? card, Func<string, ItemDisplay?> items)
    {
        var targets = new List<InventoryEntryRecord>();
        if (card is null || card.Kind != ItemKinds.Card) return targets;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (items(entry.Item) is not { Kind: ItemKinds.Equip } item || entry.CardCount >= item.CardSlots) continue;
            for (var s = 0; s < card.Slots.Count; s++)
            {
                if (!Contains(item.Slots, card.Slots[s])) continue;
                targets.Add(entry);
                break;
            }
        }
        return targets;
    }

    /// <summary>How many units of <paramref name="itemId"/> are carried (not worn), over every stack.</summary>
    public int Carried(string itemId)
    {
        var count = 0;
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].Slot is null && string.Equals(_entries[i].Item, itemId, StringComparison.Ordinal)) count += _entries[i].Quantity;
        return count;
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
        return false;
    }

    private int IndexOf(long id)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].Id == id) return i;
        return -1;
    }

    private void SetWeight(int weight, int limit, string load)
    {
        Weight = weight;
        WeightLimit = limit;
        Load = load;
    }

    private void DropSelectionIfGone()
    {
        if (Selected is { } id && Find(id) is null) Selected = null;
    }
}
