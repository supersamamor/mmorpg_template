using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Shop;
using Game.Shared.Vocabulary;

namespace Game.Shared.Items
{
    /// <summary>One entry in a trade offer (T-903): the entry exactly as it was when it was offered, and how many of its units are offered (all of them, or part of a stack).</summary>
    public sealed record TradeLine(InventoryEntry Entry, int Quantity)
    {
        /// <summary>The whole entry is offered: it moves with its id, cards and refine level. Otherwise the units become a new entry on the receiving side.</summary>
        public bool Whole => Quantity >= Entry.Quantity;
    }

    /// <summary>One side's offer in a trade (T-903): entries (each at most once) and currency. Immutable.</summary>
    public sealed record TradeOffer(IReadOnlyList<TradeLine> Lines, long Currency)
    {
        /// <summary>Nothing offered.</summary>
        public static readonly TradeOffer Empty = new TradeOffer(new TradeLine[0], 0);
    }

    /// <summary>Which side of a trade a refusal is about.</summary>
    public enum TradeSide
    {
        /// <summary>Neither (the swap happened).</summary>
        None,

        /// <summary>The first side.</summary>
        A,

        /// <summary>The second side.</summary>
        B,
    }

    /// <summary>
    /// The trade's swap (T-903, hard rule 10), pure and deterministic: both bags change, or neither. Everything is checked again against what each side carries now, because a tick may
    /// pass between the confirm and the swap: each offered entry must still be carried, not worn, tradeable and exactly as it was when offered (same quantity, cards and refine level),
    /// and no entry twice; the currency must be carried; the receiving side must stay within <c>inventory.max_entries</c>, its weight limit (unless the swap makes it lighter) and the
    /// most currency a bag holds. A whole entry moves with its id; part of a stack becomes a new entry with a new id on the receiving side, never merged into a stack there.
    /// </summary>
    public static class TradeRules
    {
        /// <summary>
        /// Swaps <paramref name="fromA"/> (out of <paramref name="a"/>) and <paramref name="fromB"/> (out of <paramref name="b"/>). Returns null when done, with each side's changes in
        /// <paramref name="deltaA"/> and <paramref name="deltaB"/>; else a "Trade ends" term (<c>offer_changed</c>, <c>inventory_full</c>, <c>too_heavy</c>, <c>currency_full</c>),
        /// <paramref name="side"/> the side it is about, both bags as they were and both deltas empty. When given, <paramref name="idsAtB"/> and <paramref name="idsAtA"/> (one slot per
        /// line of <paramref name="fromA"/> and <paramref name="fromB"/>) get the entry id each line has on the receiving side.
        /// </summary>
        public static string? TrySwap(InventoryRules rules, InventoryState a, TradeOffer fromA, int limitA, InventoryState b, TradeOffer fromB, int limitB,
            Func<long> newId, InventoryDelta deltaA, InventoryDelta deltaB, out TradeSide side, long[]? idsAtB = null, long[]? idsAtA = null)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (newId == null) throw new ArgumentNullException(nameof(newId));
            deltaA.Clear();
            deltaB.Clear();
            side = TradeSide.A;
            var reason = Check(rules, a, fromA);
            if (reason != null) return reason;
            side = TradeSide.B;
            reason = Check(rules, b, fromB);
            if (reason != null) return reason;
            // Currency: what each ends with fits a bag (it cannot go below 0: each carries what it gives).
            if (!ShopRules.TryCredit(a.Currency - fromA.Currency, fromB.Currency, out var currencyA))
            {
                side = TradeSide.A;
                return TradeEnds.CurrencyFull;
            }
            if (!ShopRules.TryCredit(b.Currency - fromB.Currency, fromA.Currency, out var currencyB))
            {
                side = TradeSide.B;
                return TradeEnds.CurrencyFull;
            }
            // An entry id given whole must be free on the receiving side (ids are unique over all characters, so only a corrupt state fails this).
            foreach (var line in fromA.Lines)
                if (line.Whole && b.Find(line.Entry.Id) != null) { side = TradeSide.A; return TradeEnds.OfferChanged; }
            foreach (var line in fromB.Lines)
                if (line.Whole && a.Find(line.Entry.Id) != null) { side = TradeSide.B; return TradeEnds.OfferChanged; }

            var beforeA = a.Snapshot();
            var beforeB = b.Snapshot();
            var (weightA, weightB) = (a.Weight, b.Weight);
            Take(a, fromA, deltaA);
            Take(b, fromB, deltaB);
            Give(b, fromA, newId, deltaB, idsAtB);
            Give(a, fromB, newId, deltaA, idsAtA);
            a.Weight = rules.WeightOf(a);
            b.Weight = rules.WeightOf(b);
            side = TradeSide.None;
            reason = null;
            if (a.Entries.Count > rules.Balance.MaxEntries) (reason, side) = (TradeEnds.InventoryFull, TradeSide.A);
            else if (b.Entries.Count > rules.Balance.MaxEntries) (reason, side) = (TradeEnds.InventoryFull, TradeSide.B);
            else if (a.Weight > weightA && a.Weight > limitA) (reason, side) = (TradeEnds.TooHeavy, TradeSide.A);
            else if (b.Weight > weightB && b.Weight > limitB) (reason, side) = (TradeEnds.TooHeavy, TradeSide.B);
            if (reason != null)
            {
                a.Restore(beforeA);
                b.Restore(beforeB);
                (a.Weight, b.Weight) = (weightA, weightB);
                deltaA.Clear();
                deltaB.Clear();
                return reason;
            }
            a.Currency = currencyA;
            b.Currency = currencyB;
            return null;
        }

        /// <summary>
        /// Whether <paramref name="entry"/> may be offered at all (T-903): carried, not worn, and tradeable. Null when it may; else a "Trade reasons" term. Quantities are the caller's.
        /// </summary>
        public static string? CanOffer(InventoryRules rules, InventoryEntry? entry)
        {
            if (entry == null) return TradeReasons.ItemGone;
            if (entry.Slot != null) return TradeReasons.ItemEquipped;
            if (!rules.Items.TryGet(entry.ItemId, out var item)) return TradeReasons.ItemGone;
            if (!item.Tradeable) return TradeReasons.NotTradeable;
            return null;
        }

        /// <summary>The side still carries exactly what it offers. Null, or <c>offer_changed</c>.</summary>
        private static string? Check(InventoryRules rules, InventoryState bag, TradeOffer offer)
        {
            if (offer.Currency < 0 || offer.Currency > bag.Currency) return TradeEnds.OfferChanged;
            var seen = new HashSet<long>();
            foreach (var line in offer.Lines)
            {
                if (!seen.Add(line.Entry.Id)) return TradeEnds.OfferChanged;
                var now = bag.Find(line.Entry.Id);
                if (CanOffer(rules, now) != null || !now!.Equals(line.Entry)) return TradeEnds.OfferChanged;
                if (line.Quantity < 1 || line.Quantity > now.Quantity) return TradeEnds.OfferChanged;
            }
            return null;
        }

        private static void Take(InventoryState bag, TradeOffer offer, InventoryDelta delta)
        {
            foreach (var line in offer.Lines)
            {
                var entry = bag.Find(line.Entry.Id)!;
                if (line.Whole)
                {
                    bag.Remove(entry.Id);
                    delta.Gone(entry.Id);
                }
                else
                {
                    var shrunk = entry with { Quantity = entry.Quantity - line.Quantity };
                    bag.Put(shrunk);
                    delta.Upsert(shrunk);
                }
            }
        }

        private static void Give(InventoryState bag, TradeOffer offer, Func<long> newId, InventoryDelta delta, long[]? ids)
        {
            for (var i = 0; i < offer.Lines.Count; i++)
            {
                var line = offer.Lines[i];
                var entry = line.Whole ? line.Entry with { Slot = null } : new InventoryEntry(newId(), line.Entry.ItemId, line.Quantity);
                bag.Put(entry);
                delta.Upsert(entry);
                if (ids != null && i < ids.Length) ids[i] = entry.Id;
            }
        }
    }
}
