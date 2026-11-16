using System;
using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Items
{
    /// <summary>
    /// One inventory entry: a stack of one item, or one equipment item with its own instance id (T-402 adds cards and refine to it). Immutable:
    /// a change replaces the entry in <see cref="InventoryState"/>, so a copy handed to another thread (an event, a message) never changes.
    /// </summary>
    /// <param name="Id">Unique for the server run (equipment: its instance id, for dupe auditing, ARCHITECTURE §11).</param>
    /// <param name="ItemId">The item (<c>item.*</c>).</param>
    /// <param name="Quantity">How many, 1..<c>max_stack</c> (always 1 for equipment).</param>
    /// <param name="Slot">The equipment slot it is worn in (<see cref="EquipSlots"/>), or null when carried.</param>
    public sealed record InventoryEntry(long Id, string ItemId, int Quantity, string? Slot = null);

    /// <summary>
    /// One character's inventory and equipment. Equipped entries stay in the inventory (their <see cref="InventoryEntry.Slot"/> is set), so they
    /// count toward the weight and the entry count, and putting something on or taking it off never needs room. Changed only by
    /// <see cref="InventoryRules"/>, on the tick thread of the instance that holds the character. Persistence (T-702) stores exactly this.
    /// </summary>
    public sealed class InventoryState
    {
        private readonly List<InventoryEntry> _entries = new List<InventoryEntry>();

        /// <summary>Entries in id order.</summary>
        public IReadOnlyList<InventoryEntry> Entries => _entries;

        /// <summary>Simulated or server time (ms) until which each item id is on cooldown after a use.</summary>
        public Dictionary<string, long> CooldownUntilMs { get; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Counts every change the owner was told about (the <c>rev</c> of the wire messages).</summary>
        public long Revision { get; set; }

        /// <summary>Carried weight: the sum of <c>weight × quantity</c> of every entry. Kept by <see cref="InventoryRules"/>.</summary>
        public int Weight { get; internal set; }

        /// <summary>The character's <c>weight_limit</c> as last read from its stats (kept by the server).</summary>
        public int WeightLimit { get; set; }

        /// <summary>The <see cref="WeightStates"/> term for <see cref="Weight"/> of <see cref="WeightLimit"/> (kept by the server).</summary>
        public string Load { get; set; } = WeightStates.Normal;

        /// <summary>The entry with this id, or null.</summary>
        public InventoryEntry? Find(long id)
        {
            var index = IndexOf(id);
            return index >= 0 ? _entries[index] : null;
        }

        /// <summary>The entry worn in <paramref name="slot"/>, or null.</summary>
        public InventoryEntry? InSlot(string slot)
        {
            for (var i = 0; i < _entries.Count; i++)
                if (string.Equals(_entries[i].Slot, slot, StringComparison.Ordinal)) return _entries[i];
            return null;
        }

        internal int IndexOf(long id)
        {
            int lo = 0, hi = _entries.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var value = _entries[mid].Id;
                if (value == id) return mid;
                if (value < id) lo = mid + 1;
                else hi = mid - 1;
            }
            return ~lo;
        }

        internal void Put(InventoryEntry entry)
        {
            var index = IndexOf(entry.Id);
            if (index >= 0) _entries[index] = entry;
            else _entries.Insert(~index, entry);
        }

        internal bool Remove(long id)
        {
            var index = IndexOf(id);
            if (index < 0) return false;
            _entries.RemoveAt(index);
            return true;
        }
    }

    /// <summary>What one operation changed, for the owner's <c>inventory_changed</c>: entries added or replaced (their new values) and entries removed.</summary>
    public sealed class InventoryDelta
    {
        /// <summary>Entries added or changed, as they are now.</summary>
        public List<InventoryEntry> Upserted { get; } = new List<InventoryEntry>();

        /// <summary>Ids of entries that are gone.</summary>
        public List<long> Removed { get; } = new List<long>();

        /// <summary>Entries taken off by this operation (unequip, or displaced by an equip), in the order they came off.</summary>
        public List<InventoryEntry> TakenOff { get; } = new List<InventoryEntry>();

        /// <summary>True when nothing was recorded.</summary>
        public bool IsEmpty => Upserted.Count == 0 && Removed.Count == 0;

        /// <summary>Forgets everything recorded.</summary>
        public void Clear()
        {
            Upserted.Clear();
            Removed.Clear();
            TakenOff.Clear();
        }

        internal void Upsert(InventoryEntry entry)
        {
            Removed.Remove(entry.Id);
            for (var i = 0; i < Upserted.Count; i++)
            {
                if (Upserted[i].Id != entry.Id) continue;
                Upserted[i] = entry;
                return;
            }
            Upserted.Add(entry);
        }

        internal void Gone(long id)
        {
            for (var i = Upserted.Count - 1; i >= 0; i--)
                if (Upserted[i].Id == id) Upserted.RemoveAt(i);
            if (!Removed.Contains(id)) Removed.Add(id);
        }
    }
}
