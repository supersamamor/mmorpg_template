using Game.Server.World.Guild;
using Game.Server.World.Saving;
using Game.Shared.Guild;
using Game.Shared.Items;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Items;
using Game.Tests.Server;

namespace Game.Tests.Guild;

/// <summary>
/// T-905: the bag's side of a guild storage move on its own (all or nothing, ids, cards and refine kept, a part of a stack a new entry, worn and untradeable items stay), the inventory system
/// running it on the tick with the note on the save, and a property test: random deposits and withdrawals over a model storage never make or lose an item id, a unit or a coin.
/// </summary>
public class GuildStorageRulesTests
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

    [Fact]
    public void A_deposit_takes_a_whole_entry_with_its_id_cards_and_refine_or_a_part_of_a_stack_as_a_new_entry()
    {
        var blade = new InventoryEntry(1, Blade, 1) { Refine = 4, Cards = ["item.gelcap_card"] };
        var bag = Bag(100, blade, new InventoryEntry(2, Draught, 10));
        var delta = new InventoryDelta();
        GuildStorageRules.TryTake(_rules, bag, 1, 1, 30, NewId, delta, out var moved).ShouldBeNull();
        moved.ShouldBe(blade);
        (bag.Find(1), bag.Currency).ShouldBe((null, 70L));
        delta.Removed.ShouldBe([1L]);

        GuildStorageRules.TryTake(_rules, bag, 2, 4, 0, NewId, new InventoryDelta(), out var part).ShouldBeNull();
        (part!.ItemId, part.Quantity, part.Id > 5_000).ShouldBe((Draught, 4, true));
        bag.Find(2)!.Quantity.ShouldBe(6);
        bag.Weight.ShouldBe(_rules.WeightOf(bag));
    }

    [Fact]
    public void Worn_untradeable_missing_too_many_and_too_much_are_refused_and_change_nothing()
    {
        var worn = new InventoryEntry(1, Blade, 1, "weapon");
        var token = new InventoryEntry(2, Token, 1);
        var draughts = new InventoryEntry(3, Draught, 3);
        var bag = Bag(50, worn, token, draughts);
        var before = Picture(bag);
        foreach (var (entry, quantity, currency, reason) in new (long?, int, long, string)[]
                 {
                     (1, 1, 0, GuildReasons.ItemEquipped), (2, 1, 0, GuildReasons.NotTradeable), (99, 1, 0, GuildReasons.ItemGone), (3, 4, 0, GuildReasons.ItemGone),
                     (3, 0, 0, GuildReasons.ItemGone), (null, 0, 51, GuildReasons.ItemGone), (null, 0, 0, GuildReasons.ItemGone), (null, 0, -1, GuildReasons.ItemGone),
                 })
        {
            var delta = new InventoryDelta();
            GuildStorageRules.TryTake(_rules, bag, entry, quantity, currency, NewId, delta, out var moved).ShouldBe(reason);
            (moved, delta.IsEmpty, Picture(bag)).ShouldBe((null, true, before));
        }
    }

    [Fact]
    public void A_withdrawal_keeps_the_id_and_is_refused_by_a_full_bag_the_weight_limit_and_the_currency_limit()
    {
        var bag = Bag(long.MaxValue - 5, new InventoryEntry(1, Draught, 1));
        var blade = new InventoryEntry(70, Blade, 1) { Refine = 2 };
        GuildStorageRules.TryGive(_rules, bag, null, 6, bag.WeightLimit, new InventoryDelta()).ShouldBe(GuildReasons.CurrencyFull);
        GuildStorageRules.TryGive(_rules, bag, blade, 5, bag.WeightLimit, new InventoryDelta()).ShouldBeNull();
        bag.Find(70).ShouldBe(blade);
        bag.Currency.ShouldBe(long.MaxValue);

        var light = Bag(0, new InventoryEntry(1, Draught, 1));
        GuildStorageRules.TryGive(_rules, light, new InventoryEntry(71, Blade, 1), 0, 0, new InventoryDelta()).ShouldBe(GuildReasons.TooHeavy);
        light.Entries.Count.ShouldBe(1);
        light.Weight.ShouldBe(_rules.WeightOf(light));

        var full = Bag(0, new InventoryEntry(1, Draught, 1), new InventoryEntry(2, Draught, 1), new InventoryEntry(3, Draught, 1), new InventoryEntry(4, Draught, 1));
        GuildStorageRules.TryGive(_rules, full, new InventoryEntry(72, Draught, 1), 0, 1_000, new InventoryDelta()).ShouldBe(GuildReasons.InventoryFull);
        GuildStorageRules.TryGive(_rules, full, new InventoryEntry(1, Draught, 1), 0, 1_000, new InventoryDelta()).ShouldBe(GuildReasons.ItemGone, "an id the bag has already");
    }

    /// <summary>
    /// Random deposits and withdrawals of three bags against one model storage (a hold first, then the bag's side, then the save lands or never does and the hold is undone): after every
    /// step every item id is in exactly one place, and the units of each item and the coins add up to what there was.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Random_moves_never_make_or_lose_an_item_or_a_coin(int seed)
    {
        var random = new Random(seed);
        var bags = Enumerable.Range(0, 3).Select(i => Bag(500, new InventoryEntry(100 + i * 10, Draught, 9), new InventoryEntry(101 + i * 10, Blade, 1) { Refine = i })).ToArray();
        var storage = new Dictionary<long, InventoryEntry>();
        long storageCurrency = 0;
        var start = Totals();
        for (var step = 0; step < 400; step++)
        {
            var bag = bags[random.Next(bags.Length)];
            var lands = random.Next(4) != 0;   // one save in four never lands
            if (random.Next(2) == 0)
            {
                // A deposit: no storage change until the save lands.
                var entry = bag.Entries.Count == 0 || random.Next(4) == 0 ? null : bag.Entries[random.Next(bag.Entries.Count)];
                var quantity = entry is null ? 0 : random.Next(1, entry.Quantity + 2);
                var currency = entry is null ? random.Next(0, 80) : 0;
                var copy = Copy(bag);
                if (GuildStorageRules.TryTake(_rules, bag, entry?.Id, quantity, currency, NewId, new InventoryDelta(), out var moved) is not null) continue;
                if (!lands)
                {
                    Restore(bag, copy);   // the save never landed: the character is back as it was saved
                    continue;
                }
                if (moved is not null) storage.Add(moved.Id, moved);
                storageCurrency += currency;
            }
            else
            {
                // A withdrawal: the hold takes it out of the storage first.
                InventoryEntry? held = null;
                long currency = 0;
                if (storage.Count > 0 && random.Next(3) != 0)
                {
                    var source = storage.Values.ElementAt(random.Next(storage.Count));
                    var quantity = random.Next(1, source.Quantity + 1);
                    storage.Remove(source.Id);
                    if (quantity == source.Quantity) held = source;
                    else
                    {
                        storage.Add(source.Id, source with { Quantity = source.Quantity - quantity });
                        held = new InventoryEntry(NewId(), source.ItemId, quantity);
                    }
                }
                else if (storageCurrency > 0)
                {
                    currency = random.Next(1, (int)Math.Min(storageCurrency, 100) + 1);
                    storageCurrency -= currency;
                }
                else continue;
                var copy = Copy(bag);
                var refused = GuildStorageRules.TryGive(_rules, bag, held, currency, bag.WeightLimit, new InventoryDelta()) is not null;
                if (refused || !lands)
                {
                    if (!refused) Restore(bag, copy);
                    if (held is not null) storage.Add(held.Id, held);   // the hold is undone (not merged back: a part stays its own entry)
                    storageCurrency += currency;
                }
            }
            var ids = bags.SelectMany(b => b.Entries.Select(e => e.Id)).Concat(storage.Keys).ToList();
            ids.Distinct().Count().ShouldBe(ids.Count, $"an id in two places after step {step}");
            Totals().ShouldBe(start, $"after step {step}");
        }

        string Totals()
        {
            var byItem = bags.SelectMany(b => b.Entries).Concat(storage.Values).GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.Sum(e => e.Quantity));
            return string.Join(",", byItem.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value)) + "|" + (bags.Sum(b => b.Currency) + storageCurrency);
        }
    }

    private static (List<InventoryEntry> Entries, long Currency) Copy(InventoryState bag) => (bag.Entries.ToList(), bag.Currency);

    private void Restore(InventoryState bag, (List<InventoryEntry> Entries, long Currency) copy)
    {
        bag.SetEntries(copy.Entries);
        bag.Currency = copy.Currency;
        _rules.Refresh(bag);
    }

    private static string Picture(InventoryState bag) =>
        string.Join(";", bag.Entries.Select(e => $"{e.Id}:{e.ItemId}x{e.Quantity}+{e.Refine}[{string.Join(",", e.Cards)}]{e.Slot}")) + $"|{bag.Currency}|{bag.Weight}";

    // ---- on the tick ------------------------------------------------------------------------------------------------------------------------

    private static (InventoryRig Rig, Game.Server.World.EntityId Player, CharacterTicket? Ticket) Player(bool saved = true)
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(new GridCell(2, 2));
        rig.Ticks();
        var ticket = saved ? new CharacterTicket(Guid.NewGuid()) : null;
        rig.Get(player).Save = ticket;
        rig.State(player).Currency = 200;
        return (rig, player, ticket);
    }

    [Fact]
    public async Task On_the_tick_a_deposit_moves_the_bag_side_and_puts_the_move_on_the_save_and_the_same_entry_twice_moves_once()
    {
        var (rig, player, ticket) = Player();
        var draughts = rig.Give(player, Draught, 5);
        var first = new GuildStorageTransfer(player, Guid.NewGuid(), 9, GuildStorageMoves.Deposit, draughts, 5, null, 0);
        var second = new GuildStorageTransfer(player, Guid.NewGuid(), 9, GuildStorageMoves.Deposit, draughts, 5, null, 0);
        rig.Send(first);
        rig.Send(second);
        rig.Ticks();
        (await first.Done).ShouldBeNull();
        (await second.Done).ShouldBe(GuildReasons.ItemGone, "the same entry twice: the second finds it gone");
        rig.State(player).Find(draughts).ShouldBeNull();
        var move = ticket!.PendingNotes().ShouldHaveSingleItem().Storage!;
        (move.Hold, move.Guild, move.Move, move.Entry!.Id, move.Entry.Quantity).ShouldBe((first.Hold, 9L, GuildStorageMoves.Deposit, draughts, 5));
        rig.Of<Game.Server.World.Inventory.InventoryChanged>().Last().Cause.ShouldBe(InventoryChanges.GuildStorage);
    }

    [Fact]
    public async Task On_the_tick_a_drop_before_the_deposit_leaves_nothing_to_deposit_and_a_withdrawal_lands_its_entry()
    {
        var (rig, player, ticket) = Player();
        var draughts = rig.Give(player, Draught, 2);
        rig.Send(new Game.Server.World.Inventory.DropItem(player, draughts, 2));
        var deposit = new GuildStorageTransfer(player, Guid.NewGuid(), 9, GuildStorageMoves.Deposit, draughts, 2, null, 0);
        rig.Send(deposit);
        rig.Ticks();
        (await deposit.Done).ShouldBe(GuildReasons.ItemGone);
        ticket!.PendingNotes().ShouldBeEmpty();

        var withdrawal = new GuildStorageTransfer(player, Guid.NewGuid(), 9, GuildStorageMoves.Withdraw, null, 0, new InventoryEntry(888_001, Draught, 3), 40);
        rig.Send(withdrawal);
        rig.Ticks();
        (await withdrawal.Done).ShouldBeNull();
        (rig.State(player).Find(888_001)!.Quantity, rig.State(player).Currency).ShouldBe((3, 240L));
        ticket.PendingNotes().ShouldHaveSingleItem().Storage!.Currency.ShouldBe(40);
    }

    [Fact]
    public async Task A_character_that_is_not_saved_moves_nothing_and_a_transfer_given_up_never_runs()
    {
        var (rig, player, _) = Player(saved: false);
        var draughts = rig.Give(player, Draught, 2);
        var unsaved = new GuildStorageTransfer(player, Guid.NewGuid(), 9, GuildStorageMoves.Deposit, draughts, 2, null, 0);
        rig.Send(unsaved);
        var abandoned = new GuildStorageTransfer(player, Guid.NewGuid(), 9, GuildStorageMoves.Deposit, draughts, 1, null, 0);
        abandoned.TryAbandon().ShouldBeTrue();
        rig.Send(abandoned);
        rig.Ticks();
        (await unsaved.Done).ShouldBe(GuildReasons.Unavailable);
        abandoned.Done.IsCompleted.ShouldBeFalse();
        rig.State(player).Find(draughts)!.Quantity.ShouldBe(2);
    }
}
