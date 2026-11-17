using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Random;
using Game.Shared.Vocabulary;

namespace Game.Tests.Items;

/// <summary>
/// The pure card and refine rules (T-402): which cards fit, every refusal with nothing changed, refining with a seeded random source (success, keep,
/// down, break), the max level, and entries keeping their cards and refine level through equip, unequip and a pick-up of a dropped item.
/// </summary>
public class CardRefineRulesTests
{
    public const string Vest = "item.padded_vest";        // body, 1 card slot, refinable
    public const string Card = "item.gelcap_card";        // fits body or shield: max_hp +8%
    public const string Shard = "item.temper_shard";      // material
    public const string Buckler = "item.wooden_buckler";  // shield, 0 card slots, refinable
    public const string Dirk = "item.bone_dirk";          // weapon, 0 card slots, refinable
    public const string Cap = "item.leather_cap";         // head_top, not refinable

    /// <summary>A small test table: +1 always, +2 at 50% (keep, 2 shards), +3 at 50% (down), +4 at 50% (break); max 4.</summary>
    public static RefineDef Table() => new()
    {
        MaxLevel = 4,
        Levels =
        [
            new RefineLevelDef { Level = 1, SuccessPercent = 100, MaterialItemId = Shard, MaterialQuantity = 1, Failure = RefineFailures.Keep },
            new RefineLevelDef { Level = 2, SuccessPercent = 50, MaterialItemId = Shard, MaterialQuantity = 2, Failure = RefineFailures.Keep },
            new RefineLevelDef { Level = 3, SuccessPercent = 50, MaterialItemId = Shard, MaterialQuantity = 1, Failure = RefineFailures.Down },
            new RefineLevelDef { Level = 4, SuccessPercent = 50, MaterialItemId = Shard, MaterialQuantity = 1, Failure = RefineFailures.Break },
        ],
    };

    private readonly InventoryRules _rules = new(new ItemLookup(InventoryRulesTests.Items()), InventoryRulesTests.Balance(10), Table());
    private long _ids;

    private long NewId() => ++_ids;

    private long Add(InventoryState state, string item, int quantity = 1, IReadOnlyList<string>? cards = null, int refine = 0)
    {
        var delta = new InventoryDelta();
        _rules.TryAdd(state, item, quantity, int.MaxValue, NewId, delta, 0, cards, refine).ShouldBeNull();
        return delta.Upserted.Last().Id;
    }

    private static string Describe(InventoryState state) =>
        string.Join(" ", state.Entries.Select(e => $"{e.Id}:{e.ItemId}x{e.Quantity}{(e.Slot is null ? "" : "@" + e.Slot)}" +
            $"{(e.Refine > 0 ? "+" + e.Refine : "")}{(e.Cards.Count > 0 ? "[" + string.Join(",", e.Cards) + "]" : "")}")) + $" w{state.Weight}";

    // ---- cards ----

    [Fact]
    public void A_card_goes_into_a_free_slot_of_equipment_it_fits_for_good_and_one_unit_is_used_up()
    {
        var state = new InventoryState();
        var vest = Add(state, Vest);
        var card = Add(state, Card);
        var spare = Add(state, Card);   // cards do not stack (max_stack 1): one entry each
        var weight = state.Weight;

        _rules.PlanInsertCard(state, card, vest, out var plan).ShouldBeNull();
        plan!.Socket.ShouldBe(0);
        var delta = new InventoryDelta();
        _rules.ApplyInsertCard(state, plan, delta);

        state.Find(vest)!.Cards.ShouldBe([Card]);
        state.Find(card).ShouldBeNull();
        state.Weight.ShouldBe(weight - 1);   // the card's weight 1; the vest's own weight does not change
        delta.Upserted.Select(e => e.Id).ShouldBe([vest]);
        delta.Removed.ShouldBe([card]);
        // The one slot is full now.
        _rules.PlanInsertCard(state, spare, vest, out _).ShouldBe(ItemReasons.NoFreeSocket);
    }

    [Fact]
    public void A_card_fits_only_equipment_with_one_of_its_slots()
    {
        var card = InventoryRulesTests.Items()[Card].Card!;
        InventoryRules.Fits(card, InventoryRulesTests.Items()[Vest].Equip!).ShouldBeTrue();      // body
        InventoryRules.Fits(card, InventoryRulesTests.Items()[Buckler].Equip!).ShouldBeTrue();   // shield
        InventoryRules.Fits(card, InventoryRulesTests.Items()[Dirk].Equip!).ShouldBeFalse();     // weapon
        InventoryRules.Fits(card, InventoryRulesTests.Items()[Cap].Equip!).ShouldBeFalse();      // head_top
    }

    [Fact]
    public void Every_card_refusal_has_a_reason_and_changes_nothing()
    {
        var state = new InventoryState();
        var vest = Add(state, Vest);
        var card = Add(state, Card, 3);
        var dirk = Add(state, Dirk);
        var buckler = Add(state, Buckler);
        var glob = Add(state, "item.gel_glob", 2);
        var before = Describe(state);

        (string Reason, long Card, long Target)[] cases =
        [
            (ItemReasons.ItemGone, 999, vest),
            (ItemReasons.ItemGone, card, 999),
            (ItemReasons.NotACard, glob, vest),
            (ItemReasons.NotEquipment, card, glob),
            (ItemReasons.CardDoesNotFit, card, dirk),     // weapon: the card is for body or shield
            (ItemReasons.NoFreeSocket, card, buckler),    // a shield, but with no card slots
        ];
        foreach (var (reason, c, target) in cases)
        {
            _rules.PlanInsertCard(state, c, target, out var plan).ShouldBe(reason, $"card {c} into {target}");
            plan.ShouldBeNull();
            Describe(state).ShouldBe(before);
        }
    }

    [Fact]
    public void A_worn_item_takes_a_card_too_and_keeps_its_slot()
    {
        var state = new InventoryState();
        var vest = Add(state, Vest);
        var card = Add(state, Card);
        _rules.PlanEquip(state, vest, null, new Wearer(1, "job.wanderer", null), out var equip).ShouldBeNull();
        _rules.ApplyEquip(state, equip!, new InventoryDelta());
        _rules.PlanInsertCard(state, card, vest, out var plan).ShouldBeNull();
        var delta = new InventoryDelta();
        _rules.ApplyInsertCard(state, plan!, delta);
        state.Find(vest)!.Slot.ShouldBe(EquipSlots.Body);
        state.Find(vest)!.Cards.ShouldBe([Card]);
        state.Find(card).ShouldBeNull();
        delta.Removed.ShouldBe([card]);
    }

    // ---- refine ----

    [Fact]
    public void Refining_with_a_sure_step_raises_the_level_and_uses_the_material()
    {
        var state = new InventoryState();
        var vest = Add(state, Vest);
        var shards = Add(state, Shard, 5);
        var weight = state.Weight;

        _rules.PlanRefine(state, vest, out var plan).ShouldBeNull();
        plan!.Step.Level.ShouldBe(1);
        var delta = new InventoryDelta();
        _rules.ApplyRefine(state, plan, new Pcg32Random(7), delta).ShouldBe(RefineResults.Success);

        state.Find(vest)!.Refine.ShouldBe(1);
        state.Find(shards)!.Quantity.ShouldBe(4);
        state.Weight.ShouldBe(weight - 2);   // one shard, weight 2
        delta.Upserted.Select(e => (e.Id, e.Refine)).ShouldBe([(shards, 0), (vest, 1)], ignoreOrder: true);
    }

    [Fact]
    public void A_seeded_roll_decides_success_or_keep_the_same_way_every_time()
    {
        // Step 2 succeeds at 50%: the outcome is whatever one Next(100) of the same seed says, and the material (2) is used up either way.
        var seen = new HashSet<string>();
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var state = new InventoryState();
            var vest = Add(state, Vest, refine: 1);
            var shards = Add(state, Shard, 3);
            _rules.PlanRefine(state, vest, out var plan).ShouldBeNull();
            var expected = new Pcg32Random(seed).Next(100) < 50 ? RefineResults.Success : RefineResults.Keep;

            var result = _rules.ApplyRefine(state, plan!, new Pcg32Random(seed), new InventoryDelta());

            result.ShouldBe(expected, $"seed {seed}");
            state.Find(vest)!.Refine.ShouldBe(expected == RefineResults.Success ? 2 : 1);
            state.Find(shards)!.Quantity.ShouldBe(1);
            seen.Add(result);
        }
        seen.ShouldBe([RefineResults.Success, RefineResults.Keep], ignoreOrder: true);
    }

    [Fact]
    public void A_failed_down_step_loses_one_level()
    {
        var (state, vest, result) = FailAt(2);   // step 3: down
        result.ShouldBe(RefineResults.Down);
        state.Find(vest)!.Refine.ShouldBe(1);
    }

    [Fact]
    public void A_failed_break_step_destroys_the_item_with_its_cards()
    {
        var (state, vest, result) = FailAt(3, [Card]);   // step 4: break
        result.ShouldBe(RefineResults.Break);
        state.Find(vest).ShouldBeNull();
        state.Weight.ShouldBe(_rules.WeightOf(state));
        state.Entries.ShouldAllBe(e => e.ItemId != Card);   // the socketed card went with it; nothing came back
    }

    [Fact]
    public void Down_never_goes_below_zero()
    {
        var rules = new InventoryRules(new ItemLookup(InventoryRulesTests.Items()), InventoryRulesTests.Balance(10), new RefineDef
        {
            MaxLevel = 1,
            Levels = [new RefineLevelDef { Level = 1, SuccessPercent = 0, MaterialItemId = Shard, Failure = RefineFailures.Down }],
        });
        var state = new InventoryState();
        var delta = new InventoryDelta();
        rules.TryAdd(state, Vest, 1, int.MaxValue, NewId, delta).ShouldBeNull();
        var vest = delta.Upserted[0].Id;
        rules.TryAdd(state, Shard, 1, int.MaxValue, NewId, delta).ShouldBeNull();
        rules.PlanRefine(state, vest, out var plan).ShouldBeNull();
        delta.Clear();
        rules.ApplyRefine(state, plan!, new Pcg32Random(1), delta).ShouldBe(RefineResults.Down);
        state.Find(vest)!.Refine.ShouldBe(0);
        delta.Upserted.ShouldBeEmpty();   // only the shard went
        delta.Removed.Count.ShouldBe(1);
    }

    [Fact]
    public void The_max_level_and_every_refine_refusal_change_nothing()
    {
        var state = new InventoryState();
        var maxed = Add(state, Vest, refine: 4);
        var poor = Add(state, Dirk);                 // refinable, but there are no shards yet
        var cap = Add(state, Cap);                   // not refinable
        var glob = Add(state, "item.gel_glob");
        var before = Describe(state);

        (string Reason, long Entry)[] cases =
        [
            (ItemReasons.ItemGone, 999),
            (ItemReasons.NotEquipment, glob),
            (ItemReasons.NotRefinable, cap),
            (ItemReasons.AtMaxRefine, maxed),
            (ItemReasons.MissingMaterial, poor),
        ];
        foreach (var (reason, entry) in cases)
        {
            _rules.PlanRefine(state, entry, out var plan).ShouldBe(reason, $"entry {entry}");
            plan.ShouldBeNull();
            Describe(state).ShouldBe(before);
        }

        // One shard is not enough for step 2 (needs 2); with no refine table at all nothing refines.
        var vest = Add(state, Vest, refine: 1);
        Add(state, Shard);
        _rules.PlanRefine(state, vest, out _).ShouldBe(ItemReasons.MissingMaterial);
        new InventoryRules(new ItemLookup(InventoryRulesTests.Items()), InventoryRulesTests.Balance(10)).PlanRefine(state, poor, out _)
            .ShouldBe(ItemReasons.AtMaxRefine);
    }

    // ---- entries keep their cards and refine level ----

    [Fact]
    public void Cards_and_refine_stay_with_the_entry_through_equip_unequip_and_a_pick_up_of_the_dropped_item()
    {
        var state = new InventoryState();
        var vest = Add(state, Vest, cards: [Card], refine: 3);
        var entry = state.Find(vest)!;
        entry.Cards.ShouldBe([Card]);
        entry.Refine.ShouldBe(3);

        _rules.PlanEquip(state, vest, null, new Wearer(1, "job.wanderer", null), out var plan).ShouldBeNull();
        _rules.ApplyEquip(state, plan!, new InventoryDelta());
        state.Find(vest)!.ShouldBe(entry with { Slot = EquipSlots.Body });
        _rules.TryUnequip(state, EquipSlots.Body, new InventoryDelta()).ShouldBeNull();
        state.Find(vest)!.ShouldBe(entry);

        // Dropped (the server keeps id, cards and refine on the ground item) and picked up again by another inventory.
        _rules.TryRemove(state, vest, 1, new InventoryDelta()).ShouldBeNull();
        var other = new InventoryState();
        var delta = new InventoryDelta();
        _rules.TryAdd(other, Vest, 1, int.MaxValue, NewId, delta, vest, entry.Cards, entry.Refine).ShouldBeNull();
        other.Find(vest).ShouldBe(entry);
        // A stackable item never takes cards or a level.
        _rules.TryAdd(other, Shard, 1, int.MaxValue, NewId, delta, 0, [Card], 2).ShouldBeNull();
        other.Entries.Single(e => e.ItemId == Shard).ShouldBe(new InventoryEntry(other.Entries.Single(e => e.ItemId == Shard).Id, Shard, 1));
    }

    [Fact]
    public void Entries_are_equal_by_value_cards_included()
    {
        var a = new InventoryEntry(1, Vest, 1) { Cards = [Card], Refine = 2 };
        var b = new InventoryEntry(1, Vest, 1) { Cards = new List<string> { Card }, Refine = 2 };
        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(b with { Refine = 1 });
        a.ShouldNotBe(b with { Cards = [] });
        new InventoryEntry(1, Vest, 1).Cards.ShouldBeEmpty();
    }

    /// <summary>An item at <paramref name="level"/> fails the next step (the roll is the first seed whose Next(100) is not below 50).</summary>
    private (InventoryState State, long Vest, string Result) FailAt(int level, IReadOnlyList<string>? cards = null)
    {
        var seed = 1UL;
        while (new Pcg32Random(seed).Next(100) < 50) seed++;
        var state = new InventoryState();
        var vest = Add(state, Vest, cards: cards, refine: level);
        Add(state, Shard, 2);
        _rules.PlanRefine(state, vest, out var plan).ShouldBeNull();
        return (state, vest, _rules.ApplyRefine(state, plan!, new Pcg32Random(seed), new InventoryDelta()));
    }
}
