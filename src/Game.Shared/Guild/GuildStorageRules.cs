using System;
using Game.Shared.Items;
using Game.Shared.Shop;
using Game.Shared.Vocabulary;

namespace Game.Shared.Guild
{
    /// <summary>
    /// The bag's side of a guild storage move (T-905, ARCHITECTURE §6.11), pure and deterministic: all or nothing, checked against what the bag holds on the tick that moves it. The storage's
    /// side is the database's (a hold made before, settled by the save that has this side). Refusals are "Guild reasons" terms; on a refusal the bag and the delta are as they were.
    /// </summary>
    public static class GuildStorageRules
    {
        /// <summary>
        /// Whether <paramref name="entry"/> may go into a guild's storage at all: carried, not worn, and tradeable (content's <c>tradeable</c>). Null when it may.
        /// </summary>
        public static string? CanDeposit(InventoryRules rules, InventoryEntry? entry)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (entry == null) return GuildReasons.ItemGone;
            if (entry.Slot != null) return GuildReasons.ItemEquipped;
            if (!rules.Items.TryGet(entry.ItemId, out var item)) return GuildReasons.ItemGone;
            if (!item.Tradeable) return GuildReasons.NotTradeable;
            return null;
        }

        /// <summary>
        /// Takes a deposit out of <paramref name="bag"/>: <paramref name="quantity"/> units of entry <paramref name="entryId"/> (when given) and <paramref name="currency"/>. A whole entry leaves with
        /// its id, cards and refine level; part of a stack becomes a new entry with an id from <paramref name="newId"/> (<paramref name="moved"/>, what the storage gets). Null when done.
        /// </summary>
        public static string? TryTake(InventoryRules rules, InventoryState bag, long? entryId, int quantity, long currency, Func<long> newId, InventoryDelta delta, out InventoryEntry? moved)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (bag == null) throw new ArgumentNullException(nameof(bag));
            if (newId == null) throw new ArgumentNullException(nameof(newId));
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            moved = null;
            if (currency < 0 || currency > bag.Currency) return GuildReasons.ItemGone;
            InventoryEntry? entry = null;
            if (entryId is long id)
            {
                entry = bag.Find(id);
                var refusal = CanDeposit(rules, entry);
                if (refusal != null) return refusal;
                if (quantity < 1 || quantity > entry!.Quantity) return GuildReasons.ItemGone;
            }
            else if (currency == 0) return GuildReasons.ItemGone;   // nothing to move

            if (entry != null)
            {
                if (quantity == entry.Quantity)
                {
                    bag.Remove(entry.Id);
                    delta.Gone(entry.Id);
                    moved = entry;
                }
                else
                {
                    var shrunk = entry with { Quantity = entry.Quantity - quantity };
                    bag.Put(shrunk);
                    delta.Upsert(shrunk);
                    moved = new InventoryEntry(newId(), entry.ItemId, quantity);
                }
                rules.Refresh(bag);
            }
            bag.Currency -= currency;
            return null;
        }

        /// <summary>
        /// Gives a withdrawal to <paramref name="bag"/>: <paramref name="entry"/> as the storage had it (its id kept, never merged into a stack) and <paramref name="currency"/>. Refused when the bag
        /// would pass <c>inventory.max_entries</c>, <paramref name="weightLimit"/> or the most currency a bag holds, or already has that id. Null when done.
        /// </summary>
        public static string? TryGive(InventoryRules rules, InventoryState bag, InventoryEntry? entry, long currency, int weightLimit, InventoryDelta delta)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (bag == null) throw new ArgumentNullException(nameof(bag));
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            if (currency < 0 || (entry == null && currency == 0)) return GuildReasons.ItemGone;
            if (!ShopRules.TryCredit(bag.Currency, currency, out var after)) return GuildReasons.CurrencyFull;
            if (entry != null)
            {
                if (entry.Quantity < 1 || !rules.Items.TryGet(entry.ItemId, out _)) return GuildReasons.ItemGone;
                if (bag.Find(entry.Id) != null) return GuildReasons.ItemGone;   // ids are unique over all bags and storages: only a corrupt state gets here
                if (bag.Entries.Count >= rules.Balance.MaxEntries) return GuildReasons.InventoryFull;
                var weight = bag.Weight;
                var given = entry with { Slot = null };
                bag.Put(given);
                rules.Refresh(bag);
                if (bag.Weight > weight && bag.Weight > weightLimit)
                {
                    bag.Remove(given.Id);
                    rules.Refresh(bag);
                    return GuildReasons.TooHeavy;
                }
                delta.Upsert(given);
            }
            bag.Currency = after;
            return null;
        }
    }
}
