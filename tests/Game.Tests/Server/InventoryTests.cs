using Game.Server.Events;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Combat;
using Game.Server.World.Inventory;
using Game.Server.World.Monsters;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Items;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>
/// One open map running movement, the spawner, combat, the inventory and (optionally) the AOI, with every event recorded. Players are wanderers with
/// the test sheet; new characters start with nothing unless <c>startItems</c> says otherwise.
/// </summary>
public sealed class InventoryRig
{
    public readonly GameWorld World;
    public readonly ManualClock Clock = new();
    public readonly ListLog Log = new();
    public readonly InMemoryInventoryStore Store = new();
    public readonly InMemoryProgressionStore Progressions = new();
    public readonly List<(long Tick, IGameEvent Event)> Events = new();
    public readonly InventoryContent Items;
    public readonly MonsterCatalog Catalog;

    public InventoryRig(IEnumerable<MonsterDef>? monsters = null, StartItemDef[]? startItems = null, int maxEntries = 5, bool aoi = false,
        IEnumerable<DropTableDef>? drops = null, int width = 20, int height = 12)
    {
        var balance = TestCombat.Balance() with
        {
            Inventory = InventoryRulesTests.Balance(maxEntries),
            NewCharacter = TestCombat.Balance().NewCharacter with { StartItems = startItems ?? [] },
        };
        Catalog = TestMonsters.Catalog((monsters ?? []).ToArray());
        var combat = TestCombat.Content(Catalog, balance, drops);
        var jobs = TestGrids.SampleContent.Jobs.All.ToDictionary(j => j.Id, j => j.WeaponTypes, StringComparer.Ordinal);
        Items = new InventoryContent(InventoryRulesTests.Items(), balance, jobs);
        var pipeline = new SystemPipeline()
            .Add(() => new MovementSystem(TestGrids.Roomy))
            .Add(() => new MonsterSpawnerSystem(Catalog, 0))
            .Add(() => new CombatSystem(combat, Progressions))
            .Add(() => new InventorySystem(Items, Store, Clock));
        if (aoi) pipeline.Add(() => new AoiSystem(4, 2, int.MaxValue, 0, default));
        World = new GameWorld([TestCombat.Open(width, height)], pipeline, 1, Clock, Log);
        Instance.Events.SubscribeAll(e => Events.Add((Instance.TickNumber, e)));
    }

    public MapInstance Instance => World.Instances[0];

    public long Now => Instance.TickNumber * TickRate.IntervalMs;

    public EntityId AddPlayer(GridCell cell, StatSheet? sheet = null, IViewSink? sink = null) =>
        World.Spawn(EntityKind.Player, Instance.MapId, cell, Directions.S, e =>
        {
            e.Stats = sheet ?? TestCombat.PlayerSheet();
            e.DefinitionId = "job.wanderer";
            e.View = sink;
        });

    public EntityId AddMonster(MonsterDef def, GridCell cell) =>
        World.Spawn(EntityKind.Monster, Instance.MapId, cell, Directions.S, e =>
        {
            e.DefinitionId = def.Id;
            e.Stats = Catalog.CreateStats(def);
        });

    /// <summary>A ground item as combat makes one: <paramref name="owner"/> has priority for <paramref name="priorityMs"/>.</summary>
    public EntityId AddGround(string itemId, int quantity, GridCell cell, EntityId? owner = null, long priorityMs = 0)
    {
        var now = Now + TickRate.IntervalMs;   // it joins on the next tick
        var item = new GroundItem(itemId, quantity, new EntityId(0), owner, now, now + priorityMs, now + 60_000);
        return World.Spawn(EntityKind.Item, Instance.MapId, cell, Directions.S, e =>
        {
            e.DefinitionId = itemId;
            e.GroundItem = item;
        });
    }

    public Entity Get(EntityId id) => Instance.TryGetEntity(id, out var e) ? e : throw new InvalidOperationException($"{id} is gone");

    public bool Has(EntityId id) => Instance.TryGetEntity(id, out _);

    public InventoryState State(EntityId player) => Store.TryGet(player, out var s) ? s : throw new InvalidOperationException("no inventory");

    /// <summary>Puts items straight into the inventory (as a test fixture, through the rules).</summary>
    public long Give(EntityId player, string itemId, int quantity = 1)
    {
        var delta = new InventoryDelta();
        Items.Rules.TryAdd(State(player), itemId, quantity, int.MaxValue, Store.NextEntryId, delta).ShouldBeNull();
        return delta.Upserted.Last().Id;
    }

    public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++) Instance.Tick();
    }

    public List<T> Of<T>() where T : IGameEvent => Events.Select(e => e.Event).OfType<T>().ToList();

    public static string Describe(InventoryState state) =>
        string.Join(" ", state.Entries.Select(e => $"{e.Id}:{e.ItemId}x{e.Quantity}{(e.Slot is null ? "" : "@" + e.Slot)}")) +
        $" w{state.Weight} rev{state.Revision} cd{state.CooldownUntilMs.Count}";
}

public class InventoryTests
{
    private static readonly GridCell Start = new(2, 2);

    [Fact]
    public void A_pick_up_in_range_takes_the_item_at_once_and_despawns_it()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        var item = rig.AddGround("item.gel_glob", 2, new GridCell(3, 3));
        rig.Ticks();
        rig.Send(new PickUp(player, item));
        rig.Ticks();
        rig.Has(item).ShouldBeFalse();
        rig.State(player).Entries.Single().ShouldBe(new InventoryEntry(rig.State(player).Entries[0].Id, "item.gel_glob", 2));
        rig.Of<ItemAcquired>().Single().ShouldBe(new ItemAcquired(player, "item.gel_glob", 2, item));
        var change = rig.Of<InventoryChanged>().Single();
        change.Cause.ShouldBe(InventoryChanges.PickedUp);
        change.Revision.ShouldBe(1);
        change.Weight.ShouldBe(2);
    }

    [Fact]
    public void A_pick_up_out_of_range_walks_there_first()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        var item = rig.AddGround("item.bone_dirk", 1, new GridCell(10, 2));
        rig.Ticks();
        rig.Send(new PickUp(player, item));
        rig.Ticks(3);
        rig.Has(item).ShouldBeTrue();
        rig.Of<MovementStarted>().ShouldContain(e => e.Entity == player && e.Goal.Equals(new GridCell(10, 2)));
        for (var i = 0; i < 60 && rig.Has(item); i++) rig.Ticks();
        rig.Has(item).ShouldBeFalse();
        GridDistance.Chebyshev(rig.Get(player).Cell, new GridCell(10, 2)).ShouldBeLessThanOrEqualTo(1);   // picked up from the next cell
        rig.State(player).Entries.Single().ItemId.ShouldBe("item.bone_dirk");
    }

    [Fact]
    public void A_new_move_order_cancels_the_walk_to_an_item()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        var item = rig.AddGround("item.gel_glob", 1, new GridCell(15, 2));
        rig.Ticks();
        rig.Send(new PickUp(player, item));
        rig.Ticks(3);
        rig.Send(new MoveTo(player, new GridCell(2, 8)));
        rig.Ticks(3);
        rig.Of<ItemRefused>().Single().ShouldBe(new ItemRefused(player, MessageTypes.PickUp, ItemReasons.Cancelled, item.Value));
        rig.Ticks(40);
        rig.Has(item).ShouldBeTrue();
        rig.State(player).Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Loot_priority_holds_off_everyone_but_the_owner_until_it_ends()
    {
        var rig = new InventoryRig();
        var owner = rig.AddPlayer(new GridCell(5, 5));
        var other = rig.AddPlayer(new GridCell(4, 4));
        rig.Ticks();
        var item = rig.AddGround("item.gel_glob", 1, new GridCell(4, 5), owner, priorityMs: 1000);
        rig.Ticks();
        var before = InventoryRig.Describe(rig.State(other));
        rig.Send(new PickUp(other, item));
        rig.Ticks();
        rig.Of<ItemRefused>().Single().Reason.ShouldBe(ItemReasons.NotYours);
        InventoryRig.Describe(rig.State(other)).ShouldBe(before);
        rig.Has(item).ShouldBeTrue();
        rig.Ticks(10);   // priority over
        rig.Send(new PickUp(other, item));
        rig.Ticks();
        rig.Has(item).ShouldBeFalse();
        rig.State(other).Entries.Single().ItemId.ShouldBe("item.gel_glob");
    }

    [Fact]
    public void Two_players_on_the_same_item_in_one_tick_the_lower_id_wins_and_the_other_is_told_it_is_gone()
    {
        var rig = new InventoryRig();
        var a = rig.AddPlayer(new GridCell(4, 4));
        var b = rig.AddPlayer(new GridCell(6, 4));
        var item = rig.AddGround("item.gel_glob", 1, new GridCell(5, 4));
        rig.Ticks();
        rig.Send(new PickUp(b, item));
        rig.Send(new PickUp(a, item));
        rig.Ticks();
        rig.State(b).Entries.Count.ShouldBe(1);   // b's intent arrived first
        rig.State(a).Entries.ShouldBeEmpty();
        rig.Of<ItemRefused>().Single().ShouldBe(new ItemRefused(a, MessageTypes.PickUp, ItemReasons.ItemGone, item.Value));
    }

    [Fact]
    public void Too_heavy_and_inventory_full_pick_ups_change_nothing()
    {
        var rig = new InventoryRig(maxEntries: 2);
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        rig.Give(player, "item.gel_glob");
        rig.Give(player, "item.mossy_fiber");
        var full = rig.AddGround("item.bone_dirk", 1, Start);
        var limit = rig.State(player).WeightLimit;
        var heavy = rig.AddGround("item.gel_glob", limit, new GridCell(2, 3));   // the stack has room, the back does not
        rig.Ticks();
        var before = InventoryRig.Describe(rig.State(player));
        rig.Send(new PickUp(player, full));
        rig.Ticks();
        rig.Send(new PickUp(player, heavy));
        rig.Ticks();
        rig.Of<ItemRefused>().Select(r => r.Reason).ShouldBe([ItemReasons.InventoryFull, ItemReasons.TooHeavy]);
        InventoryRig.Describe(rig.State(player)).ShouldBe(before);
        rig.Has(full).ShouldBeTrue();
        rig.Has(heavy).ShouldBeTrue();
        rig.Of<InventoryChanged>().ShouldBeEmpty();
    }

    [Fact]
    public void Every_refusal_reason_changes_nothing()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        var potion = rig.Give(player, "item.healing_draught", 2);
        var antidote = rig.Give(player, "item.antidote");
        var glob = rig.Give(player, "item.gel_glob");
        var blade = rig.Give(player, "item.iron_blade");
        rig.Get(player).Vitals!.Hp = 1;
        rig.Clock.NowMilliseconds = 10_000;
        rig.Send(new UseItem(player, potion));   // works, starts the cooldown
        rig.Ticks();
        var before = InventoryRig.Describe(rig.State(player));
        var hp = rig.Get(player).Vitals!.Hp;
        var modifiers = rig.Get(player).Stats!.Modifiers.Count;
        rig.Events.Clear();

        rig.Send(new UseItem(player, potion));                       // cooldown
        rig.Send(new UseItem(player, antidote));                     // remove_status: until T-501
        rig.Send(new UseItem(player, glob));                         // a material
        rig.Send(new UseItem(player, 999));                          // no such entry
        rig.Send(new Equip(player, glob, null));                     // not equipment
        rig.Send(new Equip(player, blade, null));                    // min_base_level 8 (and job_ids)
        rig.Send(new Equip(player, blade, EquipSlots.Shield));       // the blade does not fit there
        rig.Send(new Unequip(player, EquipSlots.Weapon));            // nothing worn
        rig.Send(new DropItem(player, potion, 5));                   // more than it holds
        rig.Send(new DropItem(player, 999, 1));                      // no such entry
        rig.Ticks();

        rig.Of<ItemRefused>().Select(r => r.Reason).ShouldBe([
            ItemReasons.Cooldown, ItemReasons.UnsupportedEffect, ItemReasons.NotUsable, ItemReasons.ItemGone, ItemReasons.NotEquipment,
            ItemReasons.LevelTooLow, ItemReasons.WrongSlot, ItemReasons.NotEquipped, ItemReasons.InvalidQuantity, ItemReasons.ItemGone,
        ]);
        InventoryRig.Describe(rig.State(player)).ShouldBe(before);
        rig.Get(player).Vitals!.Hp.ShouldBe(hp);
        rig.Get(player).Stats!.Modifiers.Count.ShouldBe(modifiers);
        rig.Of<InventoryChanged>().ShouldBeEmpty();
        rig.Instance.Entities.Count(e => e.Kind == EntityKind.Item).ShouldBe(0);
    }

    [Fact]
    public void A_worn_item_cannot_be_used_or_dropped_and_the_job_gates_equipment()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start, TestCombat.PlayerSheet());
        rig.Ticks();
        var dirk = rig.Give(player, "item.bone_dirk");
        var staff = rig.Give(player, "item.oak_staff");   // spellbinders only
        rig.Get(player).Stats!.Base.BaseLevel = 10;
        rig.Send(new Equip(player, dirk, null));
        rig.Send(new DropItem(player, dirk, 1));
        rig.Send(new Equip(player, staff, null));
        rig.Ticks();
        rig.Of<ItemRefused>().Select(r => r.Reason).ShouldBe([ItemReasons.ItemEquipped, ItemReasons.WrongJob]);
        rig.State(player).InSlot(EquipSlots.Weapon)!.Id.ShouldBe(dirk);
    }

    [Fact]
    public void Healing_draught_heals_through_the_effect_seam_and_is_used_up_with_a_cooldown()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start, TestCombat.PlayerSheet(maxHp: 200, hpRegen: 0));
        rig.Ticks();
        var potion = rig.Give(player, "item.healing_draught", 2);
        var tonic = rig.Give(player, "item.focus_tonic");
        var vitals = rig.Get(player).Vitals!;
        vitals.Hp = 50;
        vitals.Sp = 0;
        rig.Send(new UseItem(player, potion));
        rig.Send(new UseItem(player, tonic));
        rig.Ticks();
        vitals.Hp.ShouldBe(130);   // heal "80"
        vitals.Sp.ShouldBe(Math.Min(40, vitals.MaxSp));   // restore_sp "40"
        rig.State(player).Find(potion)!.Quantity.ShouldBe(1);
        rig.State(player).Find(tonic).ShouldBeNull();
        rig.Of<ItemUsed>().Select(u => u.ItemId).ShouldBe(["item.healing_draught", "item.focus_tonic"]);

        vitals.Hp = 190;
        rig.Clock.Advance(999);
        rig.Send(new UseItem(player, potion));
        rig.Ticks();
        rig.Of<ItemRefused>().Single().Reason.ShouldBe(ItemReasons.Cooldown);
        rig.Clock.Advance(1);
        rig.Send(new UseItem(player, potion));
        rig.Ticks();
        vitals.Hp.ShouldBe(200);   // never above max
        rig.State(player).Find(potion).ShouldBeNull();
    }

    [Fact]
    public void Equipment_modifiers_go_on_with_their_source_and_come_off_cleanly()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        var sheet = rig.Get(player).Stats!;
        var luk = sheet.Get("luk");
        var aspd = sheet.Get("aspd");
        var count = sheet.Modifiers.Count;
        var ring = rig.Give(player, InventoryRulesTests.Ring);
        rig.Send(new Equip(player, ring, null));
        rig.Ticks();
        sheet.Modifiers.HasSource(InventorySystem.SourceOf(ring)).ShouldBeTrue();
        sheet.Get("luk").ShouldBe(luk + 2);
        rig.Of<ItemEquipped>().Single().ShouldBe(new ItemEquipped(player, InventoryRulesTests.Ring, ring, EquipSlots.Accessory1));
        // Moving it to the other accessory slot keeps one copy of its modifiers.
        rig.Send(new Equip(player, ring, EquipSlots.Accessory2));
        rig.Ticks();
        sheet.Get("luk").ShouldBe(luk + 2);
        rig.Send(new Unequip(player, EquipSlots.Accessory2));
        rig.Ticks();
        sheet.Modifiers.HasSource(InventorySystem.SourceOf(ring)).ShouldBeFalse();
        sheet.Get("luk").ShouldBe(luk);
        sheet.Modifiers.Count.ShouldBe(count);
        sheet.Get("aspd").ShouldBe(aspd);
        rig.Of<ItemUnequipped>().Single().ShouldBe(new ItemUnequipped(player, InventoryRulesTests.Ring, ring, EquipSlots.Accessory2));
    }

    [Fact]
    public void A_swap_takes_the_old_weapon_off_with_its_modifiers_in_the_same_tick()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start, TestCombat.PlayerSheet(aspd: null));
        rig.Ticks();
        var sheet = rig.Get(player).Stats!;
        var dirkA = rig.Give(player, "item.bone_dirk");
        var dirkB = rig.Give(player, "item.bone_dirk");
        rig.Send(new Equip(player, dirkA, null));
        rig.Ticks();
        var withOne = sheet.Get("aspd");
        rig.Send(new Equip(player, dirkB, null));
        rig.Ticks();
        sheet.Modifiers.HasSource(InventorySystem.SourceOf(dirkA)).ShouldBeFalse();
        sheet.Modifiers.HasSource(InventorySystem.SourceOf(dirkB)).ShouldBeTrue();
        sheet.Get("aspd").ShouldBe(withOne);   // +5% once, not twice
        var change = rig.Of<InventoryChanged>().Last();
        change.Upserted.Select(e => (e.Id, e.Slot)).ShouldBe([(dirkA, (string?)null), (dirkB, EquipSlots.Weapon)]);
        rig.Of<ItemUnequipped>().Single().EntryId.ShouldBe(dirkA);
    }

    [Fact]
    public void The_weapon_reaches_combat_golden_damage_for_one_known_hit()
    {
        // A target with 0 DEF that is always hit and never crit: the hit is exactly physical_damage = (atk + weapon_atk + refine * 2) * 100 / 100.
        var dummy = TestCombat.Monster("monster.dummy", maxHp: 100_000, def: 0, flee: -1000, atk: 0);
        var rig = new InventoryRig([dummy]);
        var sheet = TestCombat.PlayerSheet(hpRegen: 0);
        sheet.Modifiers.Add("test", DerivedStats.Crit, ModifierOp.Override, -1000);
        var player = rig.AddPlayer(Start, sheet);
        var monster = rig.AddMonster(dummy, new GridCell(3, 2));
        rig.Ticks();
        sheet.GetInt("atk").ShouldBe(1);   // the wanderer test sheet at level 1 (formulas.yaml atk)

        rig.Send(new AttackTarget(player, monster));
        rig.Ticks(10);
        rig.Of<DamageDealt>().Where(d => d.Source == player && d.Result == HitResults.Hit).Select(d => d.Amount).Distinct().ShouldBe([1]);

        var dirk = rig.Give(player, "item.bone_dirk");
        rig.Send(new Equip(player, dirk, null));
        rig.Ticks();
        rig.Get(player).Weapon.ShouldBe(new EquippedWeapon("item.bone_dirk", WeaponTypes.Dagger, 12, 0, 1, null));
        rig.Events.Clear();
        rig.Ticks(10);
        rig.Of<DamageDealt>().Where(d => d.Source == player && d.Result == HitResults.Hit).Select(d => d.Amount).Distinct().ShouldBe([13]);   // 1 + 12
    }

    [Fact]
    public void Overweight_stops_attacks_and_burdened_stops_regeneration()
    {
        var dummy = TestCombat.Monster("monster.dummy", maxHp: 100_000, atk: 0);
        var rig = new InventoryRig([dummy]);
        var player = rig.AddPlayer(Start, TestCombat.PlayerSheet(hpRegen: 10, maxHp: 500));
        var monster = rig.AddMonster(dummy, new GridCell(3, 2));
        rig.Ticks();
        var limit = rig.State(player).WeightLimit;
        limit.ShouldBeGreaterThan(0);

        rig.Give(player, "item.gel_glob", limit / 2);   // burdened: 50%
        rig.Ticks();
        rig.Get(player).Load.ShouldBe(WeightStates.Burdened);
        rig.Of<InventoryChanged>().Last().ShouldBe(rig.Of<InventoryChanged>().Last() with { Cause = InventoryChanges.Weight, Load = WeightStates.Burdened });
        var vitals = rig.Get(player).Vitals!;
        vitals.Hp = 100;
        rig.Ticks(20);
        vitals.Hp.ShouldBe(100);

        rig.Give(player, "item.gel_glob", limit * 9 / 10 - limit / 2);   // overweight: 90%
        rig.Ticks();
        rig.Get(player).Load.ShouldBe(WeightStates.Overweight);
        rig.Send(new AttackTarget(player, monster));
        rig.Ticks();
        rig.Of<AttackRefused>().Single().Reason.ShouldBe(AttackReasons.Overweight);
        rig.Of<DamageDealt>().ShouldBeEmpty();
    }

    [Fact]
    public void Dropping_puts_an_ownerless_ground_item_on_the_cell_and_equipment_keeps_its_instance_id()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        var other = rig.AddPlayer(new GridCell(3, 2));
        rig.Ticks();
        var potions = rig.Give(player, "item.healing_draught", 3);
        var dirk = rig.Give(player, "item.bone_dirk");
        rig.Send(new DropItem(player, potions, 2));
        rig.Send(new DropItem(player, dirk, 1));
        rig.Ticks(2);
        var grounds = rig.Instance.Entities.Where(e => e.Kind == EntityKind.Item).ToList();
        grounds.Select(g => (g.DefinitionId, g.GroundItem!.Quantity, g.Cell, g.GroundItem.Owner)).ShouldBe([
            ("item.healing_draught", 2, Start, (EntityId?)null), ("item.bone_dirk", 1, Start, (EntityId?)null)]);
        rig.State(player).Find(potions)!.Quantity.ShouldBe(1);
        rig.Of<ItemDiscarded>().Count.ShouldBe(2);

        var dropped = grounds[1].Id;
        rig.Send(new PickUp(other, dropped));
        rig.Ticks();
        rig.State(other).Find(dirk)!.ItemId.ShouldBe("item.bone_dirk");   // the same instance

        rig.Ticks(10_000 / TickRate.IntervalMs + 1);   // dropped_lifetime_ms 10000 in the test balance
        rig.Has(grounds[0].Id).ShouldBeFalse();
        rig.Of<ItemExpired>().Single().Item.ShouldBe(grounds[0].Id);
    }

    [Fact]
    public void A_new_character_gets_its_start_items_and_wears_the_equipped_ones_once()
    {
        var rig = new InventoryRig(startItems: [new StartItemDef { ItemId = "item.bone_dirk", Equipped = true }, new StartItemDef { ItemId = "item.healing_draught", Quantity = 3 }]);
        var player = rig.AddPlayer(Start, TestCombat.PlayerSheet(aspd: null));
        rig.Ticks();
        var state = rig.State(player);
        state.Entries.Select(e => (e.ItemId, e.Quantity, e.Slot)).ShouldBe([("item.bone_dirk", 1, EquipSlots.Weapon), ("item.healing_draught", 3, (string?)null)]);
        var sheet = rig.Get(player).Stats!;
        sheet.Modifiers.HasSource(InventorySystem.SourceOf(state.Entries[0].Id)).ShouldBeTrue();
        rig.Get(player).Weapon!.WeaponType.ShouldBe(WeaponTypes.Dagger);
        rig.Get(player).Look.ShouldBe(new WornLook(WeaponTypes.Dagger, ["item.bone_dirk"]));
        state.Weight.ShouldBe(30 + 15);
        state.Revision.ShouldBe(0);
        rig.Of<InventoryChanged>().ShouldBeEmpty();   // the whole inventory goes out after the snapshot instead
    }

    [Fact]
    public void Leaving_the_world_releases_the_inventory()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        rig.Store.Count.ShouldBe(1);
        rig.World.Despawn(player);
        rig.Ticks(2);
        rig.Store.Count.ShouldBe(0);
    }

    [Fact]
    public void Others_see_the_weapon_type_and_the_worn_items_and_a_change_of_look()
    {
        var rig = new InventoryRig(aoi: true);
        var watcher = new RecordingSink();
        var player = rig.AddPlayer(Start);
        rig.AddPlayer(new GridCell(4, 2), sink: watcher);
        rig.Ticks(2);
        watcher.Snapshots.Last().Entities.Single(e => e.Id == player).Look.ShouldBe(WornLook.Unarmed);
        var dirk = rig.Give(player, "item.bone_dirk");
        var cap = rig.Give(player, "item.leather_cap");
        rig.Send(new Equip(player, cap, null));
        rig.Send(new Equip(player, dirk, null));
        rig.Ticks();
        var change = watcher.Deltas.Last().Changed.Single(c => c.View.Id == player);
        change.Fields.ShouldBe(ViewFields.Look);
        change.View.Look.ShouldBe(new WornLook(WeaponTypes.Dagger, ["item.leather_cap", "item.bone_dirk"]));   // slot order: head_top, weapon

        var writer = new ViewMessageWriter();
        var json = System.Text.Encoding.UTF8.GetString(writer.Write(ToDelta(watcher.Deltas.Last())));
        json.ShouldContain("""{"id":""" + player.Value + ""","weapon":"dagger","worn":["item.leather_cap","item.bone_dirk"]}""");
        json.ShouldNotContain("weapon_atk");
    }

    private static ViewDelta ToDelta(RecordingSink.DeltaCopy copy)
    {
        var delta = (ViewDelta)Activator.CreateInstance(typeof(ViewDelta), nonPublic: true)!;
        typeof(ViewDelta).GetProperty(nameof(ViewDelta.Tick))!.SetValue(delta, copy.Tick);
        typeof(ViewDelta).GetProperty(nameof(ViewDelta.Observer))!.SetValue(delta, copy.Observer);
        var list = (List<EntityChange>)typeof(ViewDelta).GetField("ChangedList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(delta)!;
        list.AddRange(copy.Changed);
        return delta;
    }

    [Fact]
    public void The_own_inventory_message_and_its_change_counter()
    {
        var rig = new InventoryRig(startItems: [new StartItemDef { ItemId = "item.healing_draught", Quantity = 2 }]);
        var player = rig.AddPlayer(Start, TestCombat.PlayerSheet(maxHp: 300));
        rig.Ticks();
        var own = new OwnInventory(rig.Items.Balance.Inventory, rig.Store);
        var full = own.For(player)!;
        full.Rev.ShouldBe(0);
        full.Slots.ShouldBe(5);
        full.Load.ShouldBe(WeightStates.Normal);
        full.Entries.Select(e => (e.Item, e.Quantity, e.Slot)).ShouldBe([("item.healing_draught", 2, (string?)null)]);
        full.WeightLimit.ShouldBe(rig.Get(player).Stats!.GetInt("weight_limit"));

        var potion = full.Entries[0].Id;
        rig.Get(player).Vitals!.Hp = 1;
        rig.Send(new UseItem(player, potion));
        rig.Ticks();
        rig.Clock.Advance(1000);
        rig.Send(new UseItem(player, potion));
        rig.Ticks();
        var changes = rig.Of<InventoryChanged>().Select(OwnInventory.Message).ToList();
        changes.Select(c => c.Rev).ShouldBe([1, 2]);
        changes[0].Entries.Single().ShouldBe(new InventoryEntryRecord(potion, "item.healing_draught", 1));
        changes[1].Entries.ShouldBeEmpty();
        changes[1].Removed.ShouldBe([potion]);
        changes.ShouldAllBe(c => c.Cause == InventoryChanges.Used && c.Item == "item.healing_draught");
        own.For(player)!.Rev.ShouldBe(2);
        own.For(new EntityId(999)).ShouldBeNull();
    }

    [Fact]
    public void A_stronger_character_carries_more_and_is_told_its_new_limit()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        var limit = rig.State(player).WeightLimit;
        rig.Get(player).Stats!.Modifiers.Add("test", PrimaryStats.Str, ModifierOp.Flat, 10);
        rig.Ticks();
        var change = rig.Of<InventoryChanged>().Single();
        change.Cause.ShouldBe(InventoryChanges.Weight);
        change.WeightLimit.ShouldBe(limit + 300);   // weight_limit: 2000 + str * 30 + base_level * 10
        rig.Ticks(3);
        rig.Of<InventoryChanged>().Count.ShouldBe(1);   // nothing more while nothing changes
    }
}
