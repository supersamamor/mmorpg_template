using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Vocabulary;
using Game.Tests.World;

namespace Game.Tests.Items;

/// <summary>The pure inventory rules (T-401): stacks, entry and weight limits, weight states, use, equip and its swaps. A refusal changes nothing.</summary>
public class InventoryRulesTests
{
    public const string Ring = "item.test_ring";
    public const string Greatsword = "item.test_greatsword";

    public static InventoryBalanceDef Balance(int maxEntries = 5) => new()
    {
        MaxEntries = maxEntries, BurdenedPercent = 50, OverweightPercent = 90, PickupRange = 1, DroppedLifetimeMs = 10_000,
        TwoHandedWeaponTypes = [WeaponTypes.Sword2h, WeaponTypes.Staff, WeaponTypes.Bow],
    };

    /// <summary>The sample items plus a ring that fits either accessory slot and a two-handed sword with no limits.</summary>
    public static Dictionary<string, ItemDef> Items()
    {
        var items = TestGrids.SampleContent.Items.All.ToDictionary(i => i.Id, StringComparer.Ordinal);
        items[Ring] = new ItemDef
        {
            Id = Ring, Name = "Ring", Kind = ItemKind.Equip, Weight = 1,
            Equip = new ItemEquipDef { Slots = [EquipSlots.Accessory1, EquipSlots.Accessory2], Modifiers = [new ModifierDef { Stat = "luk", Op = ModifierOp.Flat, Value = "2" }] },
        };
        items[Greatsword] = new ItemDef
        {
            Id = Greatsword, Name = "Greatsword", Kind = ItemKind.Equip, Weight = 100,
            Equip = new ItemEquipDef { Slots = [EquipSlots.Weapon], WeaponType = WeaponTypes.Sword2h, WeaponAtk = 60, AttackRange = 1 },
        };
        return items;
    }

    private static readonly Wearer Novice = new(1, "job.wanderer", null);
    private static readonly Wearer Warden = new(20, "job.warden", ["unarmed", "dagger", "sword_1h", "sword_2h", "axe", "mace"]);

    private readonly InventoryRules _rules = new(new ItemLookup(Items()), Balance());
    private long _ids;

    private long NewId() => ++_ids;

    private InventoryState Holding(params (string Item, int Quantity)[] items)
    {
        var state = new InventoryState();
        var delta = new InventoryDelta();
        foreach (var (item, quantity) in items) _rules.TryAdd(state, item, quantity, int.MaxValue, NewId, delta).ShouldBeNull();
        return state;
    }

    private static string Describe(InventoryState state) =>
        string.Join(" ", state.Entries.Select(e => $"{e.Id}:{e.ItemId}x{e.Quantity}{(e.Slot is null ? "" : "@" + e.Slot)}")) + $" w{state.Weight}";

    [Fact]
    public void Stacks_fill_up_to_max_stack_before_a_new_entry_and_equipment_is_one_entry_each()
    {
        var state = Holding(("item.healing_draught", 98));
        var delta = new InventoryDelta();
        _rules.TryAdd(state, "item.healing_draught", 3, int.MaxValue, NewId, delta).ShouldBeNull();
        state.Entries.Select(e => e.Quantity).ShouldBe([99, 2]);
        delta.Upserted.Select(e => e.Quantity).ShouldBe([99, 2]);
        _rules.TryAdd(state, "item.bone_dirk", 2, int.MaxValue, NewId, new InventoryDelta()).ShouldBeNull();
        state.Entries.Count(e => e.ItemId == "item.bone_dirk").ShouldBe(2);
        state.Entries.Where(e => e.ItemId == "item.bone_dirk").ShouldAllBe(e => e.Quantity == 1);
        state.Weight.ShouldBe(101 * 5 + 2 * 30);
    }

    [Fact]
    public void The_entry_limit_refuses_what_does_not_fit_and_changes_nothing()
    {
        var state = Holding(("item.gel_glob", 1), ("item.mossy_fiber", 1), ("item.wasp_stinger", 1), ("item.bone_dirk", 1));
        var before = Describe(state);
        var delta = new InventoryDelta();
        _rules.TryAdd(state, "item.bone_dirk", 2, int.MaxValue, NewId, delta).ShouldBe(ItemReasons.InventoryFull);
        Describe(state).ShouldBe(before);
        delta.IsEmpty.ShouldBeTrue();
        // A stack with room still takes more without a new entry.
        _rules.TryAdd(state, "item.gel_glob", 5, int.MaxValue, NewId, delta).ShouldBeNull();
        _rules.TryAdd(state, "item.leather_cap", 1, int.MaxValue, NewId, new InventoryDelta()).ShouldBeNull();   // the 5th entry
        _rules.TryAdd(state, "item.antidote", 1, int.MaxValue, NewId, new InventoryDelta()).ShouldBe(ItemReasons.InventoryFull);
    }

    [Fact]
    public void The_weight_limit_is_inclusive_and_a_pick_up_past_it_changes_nothing()
    {
        var state = Holding(("item.healing_draught", 10));   // 50
        _rules.TryAdd(state, "item.bone_dirk", 1, 80, NewId, new InventoryDelta()).ShouldBeNull();   // exactly 80
        var before = Describe(state);
        _rules.TryAdd(state, "item.gel_glob", 1, 80, NewId, new InventoryDelta()).ShouldBe(ItemReasons.TooHeavy);
        Describe(state).ShouldBe(before);
        _rules.TryAdd(state, "item.warden_token", 1, 80, NewId, new InventoryDelta()).ShouldBeNull();   // weightless always fits
    }

    [Theory]
    [InlineData(0, 100, WeightStates.Normal)]
    [InlineData(49, 100, WeightStates.Normal)]
    [InlineData(50, 100, WeightStates.Burdened)]
    [InlineData(89, 100, WeightStates.Burdened)]
    [InlineData(90, 100, WeightStates.Overweight)]
    [InlineData(150, 100, WeightStates.Overweight)]
    [InlineData(1019, 2040, WeightStates.Normal)]
    [InlineData(1020, 2040, WeightStates.Burdened)]
    [InlineData(1836, 2040, WeightStates.Overweight)]
    [InlineData(5, 0, WeightStates.Overweight)]
    [InlineData(0, 0, WeightStates.Normal)]
    public void Weight_states_switch_at_the_balance_thresholds(int weight, int limit, string expected) => _rules.LoadOf(weight, limit).ShouldBe(expected);

    [Fact]
    public void Removing_checks_the_entry_quantity_and_worn_state_first()
    {
        var state = Holding(("item.healing_draught", 3), ("item.bone_dirk", 1));
        var dirk = state.Entries.Single(e => e.ItemId == "item.bone_dirk").Id;
        var potion = state.Entries.Single(e => e.ItemId == "item.healing_draught").Id;
        _rules.ApplyEquip(state, Plan(state, dirk, null, Novice), new InventoryDelta());
        var before = Describe(state);
        _rules.TryRemove(state, 999, 1, new InventoryDelta()).ShouldBe(ItemReasons.ItemGone);
        _rules.TryRemove(state, dirk, 1, new InventoryDelta()).ShouldBe(ItemReasons.ItemEquipped);
        _rules.TryRemove(state, potion, 0, new InventoryDelta()).ShouldBe(ItemReasons.InvalidQuantity);
        _rules.TryRemove(state, potion, 4, new InventoryDelta()).ShouldBe(ItemReasons.InvalidQuantity);
        Describe(state).ShouldBe(before);
        var delta = new InventoryDelta();
        _rules.TryRemove(state, potion, 3, delta).ShouldBeNull();
        delta.Removed.ShouldBe([potion]);
        state.Find(potion).ShouldBeNull();
    }

    [Fact]
    public void Use_checks_kind_and_cooldown_and_uses_one_up()
    {
        var state = Holding(("item.healing_draught", 2), ("item.gel_glob", 1));
        var potion = state.Entries[0].Id;
        _rules.CanUse(state, state.Entries[1].Id, 0, out _, out _).ShouldBe(ItemReasons.NotUsable);
        _rules.CanUse(state, 77, 0, out _, out _).ShouldBe(ItemReasons.ItemGone);
        _rules.CanUse(state, potion, 1000, out var entry, out var item).ShouldBeNull();
        _rules.ApplyUse(state, entry!, item!, 1000, new InventoryDelta());
        state.Find(potion)!.Quantity.ShouldBe(1);
        _rules.CanUse(state, potion, 1999, out _, out _).ShouldBe(ItemReasons.Cooldown);   // cooldown_ms 1000
        _rules.CanUse(state, potion, 2000, out _, out _).ShouldBeNull();
    }

    private EquipPlan Plan(InventoryState state, long entry, string? slot, Wearer wearer)
    {
        _rules.PlanEquip(state, entry, slot, wearer, out var plan).ShouldBeNull();
        return plan!;
    }

    [Fact]
    public void Equip_checks_kind_slot_level_and_job()
    {
        var state = Holding(("item.gel_glob", 1), ("item.iron_blade", 1), ("item.oak_staff", 1), ("item.leather_cap", 1));
        long Id(string item) => state.Entries.Single(e => e.ItemId == item).Id;
        var before = Describe(state);
        _rules.PlanEquip(state, Id("item.gel_glob"), null, Warden, out _).ShouldBe(ItemReasons.NotEquipment);
        _rules.PlanEquip(state, Id("item.leather_cap"), EquipSlots.Weapon, Warden, out _).ShouldBe(ItemReasons.WrongSlot);
        _rules.PlanEquip(state, Id("item.iron_blade"), null, Warden with { BaseLevel = 7 }, out _).ShouldBe(ItemReasons.LevelTooLow);
        _rules.PlanEquip(state, Id("item.iron_blade"), null, new Wearer(20, "job.wanderer", null), out _).ShouldBe(ItemReasons.WrongJob);   // job_ids
        _rules.PlanEquip(state, Id("item.oak_staff"), null, new Wearer(20, "job.spellbinder", ["unarmed", "dagger"]), out _).ShouldBe(ItemReasons.WrongJob); // weapon type
        _rules.PlanEquip(state, 404, null, Warden, out _).ShouldBe(ItemReasons.ItemGone);
        Describe(state).ShouldBe(before);
        Plan(state, Id("item.iron_blade"), null, Warden).Slot.ShouldBe(EquipSlots.Weapon);
    }

    [Fact]
    public void Equipping_into_a_filled_slot_swaps_in_one_step()
    {
        var state = Holding(("item.bone_dirk", 2));
        var first = state.Entries[0].Id;
        var second = state.Entries[1].Id;
        _rules.ApplyEquip(state, Plan(state, first, null, Novice), new InventoryDelta());
        var delta = new InventoryDelta();
        var plan = Plan(state, second, null, Novice);
        plan.Displaced.Select(d => d.Id).ShouldBe([first]);
        _rules.ApplyEquip(state, plan, delta);
        state.InSlot(EquipSlots.Weapon)!.Id.ShouldBe(second);
        state.Find(first)!.Slot.ShouldBeNull();
        delta.TakenOff.Select(e => e.Id).ShouldBe([first]);
        delta.Upserted.Select(e => (e.Id, e.Slot)).ShouldBe([(first, (string?)null), (second, EquipSlots.Weapon)]);
        state.Weight.ShouldBe(60);   // worn or carried, both weigh
    }

    [Fact]
    public void An_accessory_goes_in_the_first_free_listed_slot_then_swaps_the_first()
    {
        var state = Holding((Ring, 1), (Ring, 1), (Ring, 1));
        var ids = state.Entries.Select(e => e.Id).ToArray();
        _rules.ApplyEquip(state, Plan(state, ids[0], null, Novice), new InventoryDelta());
        _rules.ApplyEquip(state, Plan(state, ids[1], null, Novice), new InventoryDelta());
        state.InSlot(EquipSlots.Accessory1)!.Id.ShouldBe(ids[0]);
        state.InSlot(EquipSlots.Accessory2)!.Id.ShouldBe(ids[1]);
        var third = Plan(state, ids[2], null, Novice);
        third.Slot.ShouldBe(EquipSlots.Accessory1);
        third.Displaced.Select(d => d.Id).ShouldBe([ids[0]]);
        Plan(state, ids[2], EquipSlots.Accessory2, Novice).Displaced.Select(d => d.Id).ShouldBe([ids[1]]);
    }

    [Fact]
    public void A_two_handed_weapon_and_a_shield_take_each_other_off()
    {
        var state = Holding((Greatsword, 1), ("item.wooden_buckler", 1), ("item.bone_dirk", 1));
        long Id(string item) => state.Entries.Single(e => e.ItemId == item).Id;
        _rules.ApplyEquip(state, Plan(state, Id("item.bone_dirk"), null, Warden), new InventoryDelta());
        _rules.ApplyEquip(state, Plan(state, Id("item.wooden_buckler"), null, Warden), new InventoryDelta());

        var sword = Plan(state, Id(Greatsword), null, Warden);
        sword.Displaced.Select(d => d.ItemId).ShouldBe(["item.bone_dirk", "item.wooden_buckler"]);
        _rules.ApplyEquip(state, sword, new InventoryDelta());
        state.InSlot(EquipSlots.Weapon)!.ItemId.ShouldBe(Greatsword);
        state.InSlot(EquipSlots.Shield).ShouldBeNull();

        var shield = Plan(state, Id("item.wooden_buckler"), null, Warden);
        shield.Displaced.Select(d => d.ItemId).ShouldBe([Greatsword]);
        _rules.IsTwoHanded(Items()["item.bone_dirk"]).ShouldBeFalse();
        _rules.IsTwoHanded(Items()["item.oak_staff"]).ShouldBeTrue();
    }

    [Fact]
    public void Unequip_needs_something_in_the_slot()
    {
        var state = Holding(("item.leather_cap", 1));
        var before = Describe(state);
        _rules.TryUnequip(state, EquipSlots.HeadTop, new InventoryDelta()).ShouldBe(ItemReasons.NotEquipped);
        _rules.TryUnequip(state, "pocket", new InventoryDelta()).ShouldBe(ItemReasons.NotEquipped);
        Describe(state).ShouldBe(before);
        _rules.ApplyEquip(state, Plan(state, state.Entries[0].Id, null, Novice), new InventoryDelta());
        var delta = new InventoryDelta();
        _rules.TryUnequip(state, EquipSlots.HeadTop, delta).ShouldBeNull();
        state.Entries[0].Slot.ShouldBeNull();
        delta.TakenOff.Count.ShouldBe(1);
    }

    [Fact]
    public void An_equipment_item_put_back_keeps_its_instance_id()
    {
        var state = Holding(("item.gel_glob", 1));
        _rules.TryAdd(state, "item.bone_dirk", 1, int.MaxValue, NewId, new InventoryDelta(), instanceId: 500).ShouldBeNull();
        state.Find(500)!.ItemId.ShouldBe("item.bone_dirk");
    }
}
