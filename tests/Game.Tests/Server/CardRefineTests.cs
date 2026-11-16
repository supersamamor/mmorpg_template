using Game.Server.Events;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Combat;
using Game.Server.World.Inventory;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Items;
using static Game.Tests.Items.CardRefineRulesTests;

namespace Game.Tests.Server;

/// <summary>
/// Cards and refining in the server's inventory system (T-402): card modifiers on and off with the equipment and their own source, refine reaching
/// armour modifiers and combat (golden values for one refined weapon hit), a break while worn, refusals, entries keeping their cards and refine level
/// through a drop and pick-up, the owner's messages, and that other players see neither.
/// </summary>
public class CardRefineTests
{
    private static readonly GridCell Start = new(2, 2);

    /// <summary>Every refine fails (0%) and breaks the item: a test table only, since validation wants at least 1% in content.</summary>
    private static RefineDef Breaking() => new()
    {
        MaxLevel = 1,
        Levels = [new RefineLevelDef { Level = 1, SuccessPercent = 0, MaterialItemId = Shard, MaterialQuantity = 1, Failure = RefineFailures.Break }],
    };

    [Fact]
    public void Card_modifiers_count_only_while_the_equipment_is_worn_with_their_own_source()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        var sheet = rig.Get(player).Stats!;
        var maxHp = sheet.Get(DerivedStats.MaxHp);
        var count = sheet.Modifiers.Count;
        var vest = rig.Give(player, Vest);
        var card = rig.Give(player, Card);

        // Socketed while carried: in the entry, but not on the sheet.
        rig.Send(new InsertCard(player, vest, card));
        rig.Ticks();
        rig.State(player).Find(vest)!.Cards.ShouldBe([Card]);
        rig.State(player).Find(card).ShouldBeNull();
        rig.Of<CardInserted>().Single().ShouldBe(new CardInserted(player, vest, Vest, Card, 0));
        var change = rig.Of<InventoryChanged>().Last();
        change.Cause.ShouldBe(InventoryChanges.CardInserted);
        change.ItemId.ShouldBe(Vest);
        change.Removed.ShouldBe([card]);
        sheet.Get(DerivedStats.MaxHp).ShouldBe(maxHp);
        sheet.Modifiers.HasSource(InventorySystem.CardSourceOf(vest, 0)).ShouldBeFalse();

        // Worn: the equipment's and the card's sources; gelcap is max_hp +8%.
        rig.Send(new Equip(player, vest, null));
        rig.Ticks();
        sheet.Modifiers.HasSource(InventorySystem.SourceOf(vest)).ShouldBeTrue();
        sheet.Modifiers.HasSource(InventorySystem.CardSourceOf(vest, 0)).ShouldBeTrue();
        sheet.Get(DerivedStats.MaxHp).ShouldBe(maxHp * 1.08, 1e-9);

        // Taken off: both sources go, and nothing else.
        rig.Send(new Unequip(player, EquipSlots.Body));
        rig.Ticks();
        sheet.Modifiers.HasSource(InventorySystem.SourceOf(vest)).ShouldBeFalse();
        sheet.Modifiers.HasSource(InventorySystem.CardSourceOf(vest, 0)).ShouldBeFalse();
        sheet.Modifiers.Count.ShouldBe(count);
        sheet.Get(DerivedStats.MaxHp).ShouldBe(maxHp);
        rig.State(player).Find(vest)!.Cards.ShouldBe([Card]);   // still socketed: removing cards is a later service
    }

    [Fact]
    public void A_card_socketed_into_worn_equipment_counts_at_once()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        var sheet = rig.Get(player).Stats!;
        var maxHp = sheet.Get(DerivedStats.MaxHp);
        var vest = rig.Give(player, Vest);
        var card = rig.Give(player, Card);
        rig.Send(new Equip(player, vest, null));
        rig.Ticks();
        rig.Send(new InsertCard(player, vest, card));
        rig.Ticks();
        sheet.Get(DerivedStats.MaxHp).ShouldBe(maxHp * 1.08, 1e-9);
        rig.State(player).Find(vest)!.Slot.ShouldBe(EquipSlots.Body);
    }

    [Fact]
    public void Refusals_come_back_as_item_refused_with_their_intent_and_change_nothing()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        var dirk = rig.Give(player, Dirk);
        var card = rig.Give(player, Card);
        var before = InventoryRig.Describe(rig.State(player));
        rig.Send(new InsertCard(player, dirk, card));   // a weapon: the card is for body or shield
        rig.Send(new RefineItem(player, dirk));         // no shards
        rig.Ticks();
        rig.Of<ItemRefused>().ShouldBe([
            new ItemRefused(player, MessageTypes.InsertCard, ItemReasons.CardDoesNotFit, null, dirk),
            new ItemRefused(player, MessageTypes.Refine, ItemReasons.MissingMaterial, null, dirk),
        ]);
        InventoryRig.Describe(rig.State(player)).ShouldBe(before);
        rig.Of<InventoryChanged>().ShouldBeEmpty();
        rig.Of<ItemRefined>().ShouldBeEmpty();
        // The gateway's rejected messages for them.
        rig.Of<ItemRefused>().Select(e => new RejectedMessage(e.Intent, e.Reason, null, e.Target, e.EntryId, e.Slot))
            .Select(m => System.Text.Encoding.UTF8.GetString(ProtocolJson.ToBytes(m)))
            .ShouldBe([
                $$"""{"type":"rejected","intent":"insert_card","reason":"card_does_not_fit","entry":{{dirk}}}""",
                $$"""{"type":"rejected","intent":"refine","reason":"missing_material","entry":{{dirk}}}""",
            ]);
    }

    [Fact]
    public void Refining_worn_armour_re_evaluates_its_modifiers_with_the_new_level()
    {
        // padded_vest: def 4 + refine * 2.
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        var sheet = rig.Get(player).Stats!;
        var vest = rig.Give(player, Vest);
        rig.Give(player, Shard, 3);
        rig.Send(new Equip(player, vest, null));
        rig.Ticks();
        var def = sheet.Get(DerivedStats.Def);

        rig.Send(new RefineItem(player, vest));   // +1 always succeeds in the test table
        rig.Ticks();
        rig.State(player).Find(vest)!.Refine.ShouldBe(1);
        sheet.Get(DerivedStats.Def).ShouldBe(def + 2);
        sheet.Modifiers.HasSource(InventorySystem.SourceOf(vest)).ShouldBeTrue();
        rig.Of<ItemRefined>().Single().ShouldBe(new ItemRefined(player, vest, Vest, RefineResults.Success, 1));
        var change = rig.Of<InventoryChanged>().Last();
        change.Cause.ShouldBe(InventoryChanges.Refined);
        change.Upserted.Single(e => e.Id == vest).Refine.ShouldBe(1);
        // Off and on again: still +1, so still def + 2.
        rig.Send(new Unequip(player, EquipSlots.Body));
        rig.Ticks();
        sheet.Get(DerivedStats.Def).ShouldBe(def - 4);
        rig.Send(new Equip(player, vest, null));
        rig.Ticks();
        sheet.Get(DerivedStats.Def).ShouldBe(def + 2);
    }

    [Fact]
    public void A_refined_weapon_reaches_combat_golden_damage_for_one_known_hit()
    {
        // A target with 0 DEF that is always hit and never crit: physical_damage = (atk + weapon_atk + refine * 2) * 100 / 100.
        var dummy = TestCombat.Monster("monster.dummy", maxHp: 100_000, def: 0, flee: -1000, atk: 0);
        var rig = new InventoryRig([dummy]);
        var sheet = TestCombat.PlayerSheet(hpRegen: 0);
        sheet.Modifiers.Add("test", DerivedStats.Crit, ModifierOp.Override, -1000);
        var player = rig.AddPlayer(Start, sheet);
        var monster = rig.AddMonster(dummy, new GridCell(3, 2));
        rig.Ticks();
        var dirk = rig.Give(player, Dirk);
        rig.Give(player, Shard, 1);
        rig.Send(new Equip(player, dirk, null));
        rig.Ticks();
        rig.Send(new AttackTarget(player, monster));
        rig.Ticks(10);
        rig.Of<DamageDealt>().Where(d => d.Source == player && d.Result == HitResults.Hit).Select(d => d.Amount).Distinct().ShouldBe([13]);   // 1 + 12

        rig.Send(new RefineItem(player, dirk));
        rig.Ticks();
        rig.Get(player).Weapon.ShouldBe(new EquippedWeapon(Dirk, WeaponTypes.Dagger, 12, 1, 1, null));
        rig.Events.Clear();
        rig.Ticks(10);
        rig.Of<DamageDealt>().Where(d => d.Source == player && d.Result == HitResults.Hit).Select(d => d.Amount).Distinct().ShouldBe([15]);   // 1 + 12 + 1 * 2
    }

    [Fact]
    public void A_worn_item_that_breaks_is_gone_with_its_modifiers_cards_and_look()
    {
        var rig = new InventoryRig(refine: Breaking());
        var player = rig.AddPlayer(Start);
        rig.Ticks();
        var sheet = rig.Get(player).Stats!;
        var count = sheet.Modifiers.Count;
        var maxHp = sheet.Get(DerivedStats.MaxHp);
        var vest = rig.Give(player, Vest);
        var card = rig.Give(player, Card);
        var shard = rig.Give(player, Shard, 2);
        rig.Send(new Equip(player, vest, null));
        rig.Send(new InsertCard(player, vest, card));
        rig.Ticks();
        rig.Get(player).Look.ShouldBe(new WornLook(WeaponTypes.Unarmed, [Vest]));

        rig.Send(new RefineItem(player, vest));
        rig.Ticks();
        rig.State(player).Find(vest).ShouldBeNull();
        rig.State(player).Find(shard)!.Quantity.ShouldBe(1);   // the material is used up whatever the result
        sheet.Modifiers.HasSource(InventorySystem.SourceOf(vest)).ShouldBeFalse();
        sheet.Modifiers.HasSource(InventorySystem.CardSourceOf(vest, 0)).ShouldBeFalse();
        sheet.Modifiers.Count.ShouldBe(count);
        sheet.Get(DerivedStats.MaxHp).ShouldBe(maxHp);
        rig.Get(player).Look.ShouldBe(WornLook.Unarmed);
        rig.Of<ItemRefined>().Single().ShouldBe(new ItemRefined(player, vest, Vest, RefineResults.Break, 0));
        rig.Of<InventoryChanged>().Last().Removed.ShouldBe([vest]);
        OwnInventory.Message(rig.Of<ItemRefined>().Single()).ShouldBe(new RefineResultMessage(vest, Vest, RefineResults.Break, 0));
    }

    [Fact]
    public void Dropped_equipment_keeps_its_cards_and_refine_level_when_picked_up_again()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(Start);
        var other = rig.AddPlayer(new GridCell(3, 2));
        rig.Ticks();
        var vest = rig.Give(player, Vest);
        var card = rig.Give(player, Card);
        rig.Give(player, Shard, 1);
        rig.Send(new InsertCard(player, vest, card));
        rig.Send(new RefineItem(player, vest));
        rig.Ticks();
        var entry = rig.State(player).Find(vest)!;
        entry.Cards.ShouldBe([Card]);
        entry.Refine.ShouldBe(1);

        rig.Send(new DropItem(player, vest, 1));
        rig.Ticks(2);
        var ground = rig.Instance.Entities.Single(e => e.Kind == EntityKind.Item);
        ground.GroundItem!.InstanceId.ShouldBe(vest);
        rig.Send(new PickUp(other, ground.Id));
        rig.Ticks();
        rig.State(other).Find(vest).ShouldBe(entry);
        // And on through equip and unequip on the new owner.
        rig.Send(new Equip(other, vest, null));
        rig.Ticks();
        rig.State(other).Find(vest).ShouldBe(entry with { Slot = EquipSlots.Body });
        rig.Get(other).Stats!.Modifiers.HasSource(InventorySystem.CardSourceOf(vest, 0)).ShouldBeTrue();
    }

    [Fact]
    public void The_owner_sees_cards_and_refine_in_its_inventory_messages_and_other_players_see_neither()
    {
        var rig = new InventoryRig(aoi: true);
        var watcher = new RecordingSink();
        var player = rig.AddPlayer(Start);
        rig.AddPlayer(new GridCell(4, 2), sink: watcher);
        rig.Ticks(2);
        var vest = rig.Give(player, Vest);
        var card = rig.Give(player, Card);
        rig.Give(player, Shard, 1);
        rig.Send(new Equip(player, vest, null));
        rig.Ticks();
        var looks = watcher.Deltas.Count;
        rig.Send(new InsertCard(player, vest, card));
        rig.Send(new RefineItem(player, vest));
        rig.Ticks(2);

        var own = new OwnInventory(rig.Items.Balance.Inventory, rig.Store).For(player)!;
        own.Entries.Single(e => e.Id == vest).ShouldBe(new InventoryEntryRecord(vest, Vest, 1, EquipSlots.Body, [Card], 1), new RecordComparer());
        System.Text.Encoding.UTF8.GetString(ProtocolJson.ToBytes(own))
            .ShouldContain($$"""{"id":{{vest}},"item":"item.padded_vest","quantity":1,"slot":"body","cards":["item.gelcap_card"],"refine":1}""");
        var changes = rig.Of<InventoryChanged>().Select(OwnInventory.Message).ToList();
        changes.Select(c => c.Cause).ShouldBe([InventoryChanges.Equipped, InventoryChanges.CardInserted, InventoryChanges.Refined]);
        changes[^1].Entries.Single(e => e.Id == vest).Refine.ShouldBe(1);

        // Others: the look is the worn item ids only, and socketing or refining is no change of look. (The card's max HP does move the HP bar fraction.)
        watcher.Deltas.Skip(looks).SelectMany(d => d.Changed).Where(c => c.View.Id == player && (c.Fields & ViewFields.Look) != 0).ShouldBeEmpty();
        rig.Get(player).Look.ShouldBe(new WornLook(WeaponTypes.Unarmed, [Vest]));
    }

    /// <summary>Compares inventory records field by field, the cards by content.</summary>
    private sealed class RecordComparer : IEqualityComparer<InventoryEntryRecord>
    {
        public bool Equals(InventoryEntryRecord? a, InventoryEntryRecord? b) =>
            a is not null && b is not null && a.Id == b.Id && a.Item == b.Item && a.Quantity == b.Quantity && a.Slot == b.Slot && a.Refine == b.Refine
            && (a.Cards ?? []).SequenceEqual(b.Cards ?? []);

        public int GetHashCode(InventoryEntryRecord r) => r.Id.GetHashCode();
    }
}
