using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Vocabulary;
using Game.Tests.Items;

namespace Game.Tests.Trade;

/// <summary>T-903's swap on its own: both bags change or neither, ids, cards and refine travel, a part of a stack is a new entry, and every limit is checked on the side it is about.</summary>
public class TradeRulesTests
{
    private const string Blade = "item.iron_blade";
    private const string Draught = "item.healing_draught";
    private const string Token = "item.warden_token";
    private long _next = 5_000;
    private readonly InventoryRules _rules = new(new ItemLookup(InventoryRulesTests.Items()), InventoryRulesTests.Balance(maxEntries: 4));

    private long NewId() => ++_next;

    private InventoryState Bag(long currency, params InventoryEntry[] entries)
    {
        var bag = new InventoryState { Currency = currency, WeightLimit = 1_000 };
        bag.SetEntries(entries);
        _rules.Refresh(bag);
        return bag;
    }

    private static TradeOffer Offer(long currency, params (InventoryEntry Entry, int Quantity)[] lines) =>
        new(lines.Select(l => new TradeLine(l.Entry, l.Quantity)).ToArray(), currency);

    private string? Swap(InventoryState a, TradeOffer fromA, InventoryState b, TradeOffer fromB, out TradeSide side, long[]? toB = null, long[]? toA = null) =>
        TradeRules.TrySwap(_rules, a, fromA, a.WeightLimit, b, fromB, b.WeightLimit, NewId, new InventoryDelta(), new InventoryDelta(), out side, toB, toA);

    private static string Picture(InventoryState bag) =>
        string.Join(";", bag.Entries.Select(e => $"{e.Id}:{e.ItemId}x{e.Quantity}+{e.Refine}[{string.Join(",", e.Cards)}]{e.Slot}")) + $"|{bag.Currency}";

    [Fact]
    public void A_whole_entry_keeps_its_id_cards_and_refine_and_a_part_of_a_stack_is_a_new_entry()
    {
        var blade = new InventoryEntry(1, Blade, 1) { Refine = 4, Cards = ["item.gelcap_card"] };
        var draughts = new InventoryEntry(2, Draught, 10);
        var theirs = new InventoryEntry(3, Draught, 5);
        var a = Bag(100, blade, draughts);
        var b = Bag(7, theirs);
        var (toB, toA) = (new long[2], new long[1]);
        Swap(a, Offer(60, (blade, 1), (draughts, 4)), b, Offer(0, (theirs, 5)), out var side, toB, toA).ShouldBeNull();
        side.ShouldBe(TradeSide.None);
        b.Find(1).ShouldBe(blade, "the blade moved as it was");
        b.Find(3).ShouldBeNull();
        var part = b.Entries.Single(e => e.ItemId == Draught);
        part.Quantity.ShouldBe(4);
        part.Id.ShouldBeGreaterThan(5_000, "a new id, not merged into a stack there");
        toB.ShouldBe([1, part.Id]);
        a.Find(2)!.Quantity.ShouldBe(6);
        a.Find(3).ShouldBe(theirs, "the other side's whole stack keeps its id, and is not merged with the one left here");
        toA.ShouldBe([3]);
        (a.Currency, b.Currency).ShouldBe((40, 67));
        a.Weight.ShouldBe(_rules.WeightOf(a));
        b.Weight.ShouldBe(_rules.WeightOf(b));
    }

    public static TheoryData<string> Refusals => ["changed", "gone", "worn", "untradeable", "twice", "more", "money", "full", "heavy", "rich"];

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_refused_swap_changes_neither_bag(string what)
    {
        var blade = new InventoryEntry(1, Blade, 1) { Refine = 2 };
        var draughts = new InventoryEntry(2, Draught, 10);
        var token = new InventoryEntry(3, Token, 1);
        var a = Bag(100, blade, draughts, token);
        var b = Bag(5, new InventoryEntry(4, Draught, 1));
        var fromA = Offer(10, (blade, 1));
        var fromB = Offer(0);
        var expected = (TradeEnds.OfferChanged, TradeSide.A);
        switch (what)
        {
            case "changed": fromA = Offer(0, (blade with { Refine = 1 }, 1)); break;   // refined since it was offered
            case "gone": fromA = Offer(0, (new InventoryEntry(99, Blade, 1), 1)); break;
            case "worn":
                a.SetEntries([blade with { Slot = "weapon" }, draughts, token]);
                fromA = Offer(0, (blade with { Slot = "weapon" }, 1));
                break;
            case "untradeable": fromA = Offer(0, (token, 1)); break;
            case "twice": fromA = Offer(0, (draughts, 3), (draughts, 3)); break;
            case "more": fromA = Offer(0, (draughts, 11)); break;
            case "money": fromA = Offer(101); break;
            case "full":
                b.SetEntries([new InventoryEntry(4, Draught, 1), new InventoryEntry(5, Draught, 1), new InventoryEntry(6, Draught, 1), new InventoryEntry(7, Draught, 1)]);
                _rules.Refresh(b);
                expected = (TradeEnds.InventoryFull, TradeSide.B);
                break;
            case "heavy":
                b.WeightLimit = b.Weight;
                expected = (TradeEnds.TooHeavy, TradeSide.B);
                break;
            case "rich":
                b.Currency = long.MaxValue - 5;
                expected = (TradeEnds.CurrencyFull, TradeSide.B);
                break;
        }
        var (beforeA, beforeB) = (Picture(a), Picture(b));
        var reason = TradeRules.TrySwap(_rules, a, fromA, a.WeightLimit, b, fromB, b.WeightLimit, NewId, new InventoryDelta(), new InventoryDelta(), out var side);
        (reason, side).ShouldBe(expected, what);
        (Picture(a), Picture(b)).ShouldBe((beforeA, beforeB), what);
    }

    [Fact]
    public void A_swap_that_makes_an_overweight_side_lighter_is_allowed()
    {
        var draughts = new InventoryEntry(1, Draught, 50);
        var a = Bag(0, draughts);
        a.WeightLimit = 10;   // far over
        var b = Bag(0, new InventoryEntry(2, Draught, 1));
        Swap(a, Offer(0, (draughts, 50)), b, Offer(0, (b.Find(2)!, 1)), out _).ShouldBeNull();
        a.Weight.ShouldBe(5);
    }

    [Fact]
    public void Only_carried_tradeable_entries_may_be_offered()
    {
        TradeRules.CanOffer(_rules, null).ShouldBe(TradeReasons.ItemGone);
        TradeRules.CanOffer(_rules, new InventoryEntry(1, Blade, 1, "weapon")).ShouldBe(TradeReasons.ItemEquipped);
        TradeRules.CanOffer(_rules, new InventoryEntry(1, Token, 1)).ShouldBe(TradeReasons.NotTradeable);
        TradeRules.CanOffer(_rules, new InventoryEntry(1, "item.nothing", 1)).ShouldBe(TradeReasons.ItemGone);
        TradeRules.CanOffer(_rules, new InventoryEntry(1, Blade, 1)).ShouldBeNull();
    }

    [Fact]
    public void Random_swaps_never_create_or_lose_a_unit_or_a_coin()
    {
        var random = new Random(903);
        for (var round = 0; round < 500; round++)
        {
            var a = Bag(random.Next(0, 500), RandomEntries(random, 10));
            var b = Bag(random.Next(0, 500), RandomEntries(random, 20));
            b.WeightLimit = random.Next(0, 400);
            var fromA = RandomOffer(random, a);
            var fromB = RandomOffer(random, b);
            if (random.Next(5) == 0 && a.Entries.Count > 0) a.SetEntries(a.Entries.Skip(1).ToList());   // something changed under the offer
            var before = Units(a, b);
            var coins = a.Currency + b.Currency;
            var ids = a.Entries.Concat(b.Entries).Select(e => e.Id).ToHashSet();
            var reason = Swap(a, fromA, b, fromB, out _);
            Units(a, b).ShouldBe(before, $"round {round} ({reason ?? "done"})");
            (a.Currency + b.Currency).ShouldBe(coins);
            a.Entries.Concat(b.Entries).Select(e => e.Id).Distinct().Count().ShouldBe(a.Entries.Count + b.Entries.Count, "no id on both sides");
            if (reason is not null) a.Entries.Concat(b.Entries).Select(e => e.Id).ShouldBeSubsetOf(ids);
        }

        static string Units(InventoryState a, InventoryState b) =>
            string.Join(", ", a.Entries.Concat(b.Entries).GroupBy(e => e.ItemId).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Sum(e => (long)e.Quantity)}"));
    }

    private InventoryEntry[] RandomEntries(Random random, long baseId)
    {
        var entries = new List<InventoryEntry>();
        for (var i = 0; i < random.Next(0, 4); i++)
            entries.Add(random.Next(3) == 0 ? new InventoryEntry(baseId * 100 + i, Blade, 1) { Refine = random.Next(0, 5) } : new InventoryEntry(baseId * 100 + i, Draught, random.Next(1, 30)));
        return entries.ToArray();
    }

    private static TradeOffer RandomOffer(Random random, InventoryState bag)
    {
        var lines = bag.Entries.Where(_ => random.Next(2) == 0).Select(e => (e, random.Next(1, e.Quantity + 1))).ToArray();
        return Offer(random.Next(0, (int)bag.Currency + 2), lines);
    }
}
