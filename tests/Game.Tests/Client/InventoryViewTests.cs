using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Items;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Client;

/// <summary>The client's inventory view model (T-401): full message, changes with the counter, a gap, selection and the panel's actions.</summary>
public class InventoryViewTests
{
    private static InventoryMessage Full(long rev = 3) => new(rev, 100, 45, 2040, WeightStates.Normal,
        [new InventoryEntryRecord(5, "item.healing_draught", 3), new InventoryEntryRecord(2, "item.bone_dirk", 1, EquipSlots.Weapon)]);

    [Fact]
    public void A_full_message_replaces_everything_sorted_by_id()
    {
        var view = new InventoryView();
        view.Apply(new InventoryMessage(1, 100, 1, 2000, WeightStates.Normal, [new InventoryEntryRecord(9, "item.gel_glob", 1)]));
        view.Select(9);
        var version = view.Version;
        view.Apply(Full());
        view.Version.ShouldBeGreaterThan(version);
        view.Known.ShouldBeTrue();
        view.Entries.Select(e => e.Id).ShouldBe([2, 5]);
        view.InSlot(EquipSlots.Weapon)!.Item.ShouldBe("item.bone_dirk");
        view.Selected.ShouldBeNull();   // the selected entry is gone
        view.WeightFraction.ShouldBe(45 / 2040.0, 1e-9);
        view.Rev.ShouldBe(3);
    }

    [Fact]
    public void Changes_apply_only_in_counter_order_and_a_gap_waits_for_the_next_full_message()
    {
        var view = new InventoryView();
        var early = new InventoryChangedMessage(1, InventoryChanges.Used, "item.healing_draught", 40, 2040, WeightStates.Normal, [], []);
        view.Apply(early).ShouldBeFalse();   // nothing known yet

        view.Apply(Full());
        var used = new InventoryChangedMessage(4, InventoryChanges.Used, "item.healing_draught", 40, 2040, WeightStates.Normal, [new InventoryEntryRecord(5, "item.healing_draught", 2)], []);
        view.Apply(used).ShouldBeTrue();
        view.Find(5)!.Quantity.ShouldBe(2);
        view.Weight.ShouldBe(40);
        view.Apply(used).ShouldBeFalse();   // a repeat of rev 4

        var picked = new InventoryChangedMessage(5, InventoryChanges.PickedUp, "item.gel_glob", 41, 2040, WeightStates.Normal, [new InventoryEntryRecord(3, "item.gel_glob", 1)], []);
        view.Apply(picked).ShouldBeTrue();
        view.Entries.Select(e => e.Id).ShouldBe([2, 3, 5]);

        var skipped = new InventoryChangedMessage(7, InventoryChanges.Dropped, "item.gel_glob", 40, 2040, WeightStates.Normal, [], [3]);
        view.Apply(skipped).ShouldBeFalse();
        view.Stale.ShouldBeTrue();
        view.Find(3).ShouldNotBeNull();   // kept as it was
        view.Apply(Full(7));
        view.Stale.ShouldBeFalse();
        view.Find(3).ShouldBeNull();
    }

    [Fact]
    public void Removed_entries_go_and_the_load_state_follows_the_server()
    {
        var view = new InventoryView();
        view.Apply(Full());
        view.Apply(new InventoryChangedMessage(4, InventoryChanges.Dropped, "item.healing_draught", 1900, 2040, WeightStates.Overweight, [], [5])).ShouldBeTrue();
        view.Find(5).ShouldBeNull();
        view.Load.ShouldBe(WeightStates.Overweight);
        view.Reset();
        view.Known.ShouldBeFalse();
        view.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Selecting_toggles_and_ignores_unknown_entries()
    {
        var view = new InventoryView();
        view.Apply(Full());
        view.Select(5);
        view.SelectedEntry!.Item.ShouldBe("item.healing_draught");
        view.Select(5);
        view.Selected.ShouldBeNull();
        view.Select(77);
        view.Selected.ShouldBeNull();
    }

    [Fact]
    public void The_panel_offers_use_equip_unequip_and_drop_from_the_bundle_kind_and_the_worn_state()
    {
        var potion = new ItemDisplay("item.p", "P", ItemKinds.Consumable, null, [], null, null);
        var dirk = new ItemDisplay("item.d", "D", ItemKinds.Equip, null, [EquipSlots.Weapon], WeaponTypes.Dagger, "model.equip.d");
        var glob = new ItemDisplay("item.g", "G", ItemKinds.Material, null, [], null, null);
        InventoryView.ActionsFor(new InventoryEntryRecord(1, "item.p", 3), potion).ShouldBe(ItemActions.Use | ItemActions.Drop);
        InventoryView.ActionsFor(new InventoryEntryRecord(2, "item.d", 1), dirk).ShouldBe(ItemActions.Equip | ItemActions.Drop);
        InventoryView.ActionsFor(new InventoryEntryRecord(2, "item.d", 1, EquipSlots.Weapon), dirk).ShouldBe(ItemActions.Unequip);
        InventoryView.ActionsFor(new InventoryEntryRecord(3, "item.g", 9), glob).ShouldBe(ItemActions.Drop);
        InventoryView.ActionsFor(new InventoryEntryRecord(4, "item.unknown", 1), null).ShouldBe(ItemActions.Drop);
    }

    [Fact]
    public void The_bundle_describes_items_for_the_panel_and_worn_visuals_without_server_numbers()
    {
        var bundle = ClientBundleFixture.Bundle();
        var dirk = bundle.Item("item.bone_dirk")!;
        dirk.Kind.ShouldBe(ItemKinds.Equip);
        dirk.Icon.ShouldBe("icon.item.bone_dirk");
        dirk.Slots.ShouldBe([EquipSlots.Weapon]);
        dirk.WeaponType.ShouldBe(WeaponTypes.Dagger);
        dirk.Visual.ShouldBe("model.equip.bone_dirk");
        bundle.Item("item.healing_draught")!.Kind.ShouldBe(ItemKinds.Consumable);
        bundle.Item("monster.gel_slime").ShouldBeNull();
    }

    [Fact]
    public void A_player_record_carries_its_weapon_and_worn_items_and_a_look_change_updates_them()
    {
        var world = new WorldView(new ClientConfig());
        world.ApplyWelcome(new WelcomeMessage(1, 1, "map.x", 10));
        world.ApplySnapshot(new SnapshotMessage(1, "map.x", 1, [new EntityRecord(7, EntityKinds.Player, "job.wanderer", new GridCell(1, 1), "s", null, 0, Weapon: "dagger", Worn: ["item.bone_dirk"])]), 0);
        var other = world.Find(7)!;
        other.Weapon.ShouldBe(WeaponTypes.Dagger);
        other.Worn.ShouldBe(["item.bone_dirk"]);
        var look = other.LookVersion;
        world.ApplyDelta(new DeltaMessage(2, [], [new ChangeRecord(7, ChangedFields.Look, default, null, null, 0, null, Weapon: "unarmed", Worn: [])], []), 0.1);
        other.Weapon.ShouldBe(WeaponTypes.Unarmed);
        other.Worn.ShouldBeEmpty();
        other.LookVersion.ShouldBeGreaterThan(look);
    }

    [Fact]
    public void Item_refusals_and_pick_ups_become_the_flash_line()
    {
        var ui = new UiState(new ClientConfig(), ClientBundleFixture.Bundle());
        ui.OnRejected(new RejectedMessage(MessageTypes.PickUp, ItemReasons.TooHeavy, null, 9), 1);
        ui.Flash.ShouldBe(UiText.ItemRefused(ItemReasons.TooHeavy));
        ui.OnRejected(new RejectedMessage(MessageTypes.PickUp, ItemReasons.Cancelled, null, 9), 2);
        ui.Flash.ShouldBe(UiText.ItemRefused(ItemReasons.TooHeavy));   // a cancelled walk says nothing new
        ui.OnInventoryChanged(new InventoryChangedMessage(1, InventoryChanges.PickedUp, "item.bone_dirk", 30, 2040, WeightStates.Normal, [], []), 3);
        ui.Flash.ShouldBe(UiText.PickedUp("Bone Dirk"));
        foreach (var reason in ItemReasons.All.Ordered.Where(r => r != ItemReasons.Cancelled)) UiText.ItemRefused(reason).ShouldNotBeEmpty(reason);
    }

    // ---- T-402: cards and refining ----

    [Fact]
    public void A_card_offers_set_card_and_refinable_gear_offers_refine_carried_or_worn()
    {
        var card = new ItemDisplay("item.c", "C", ItemKinds.Card, null, [EquipSlots.Body], null, null);
        var vest = new ItemDisplay("item.v", "V", ItemKinds.Equip, null, [EquipSlots.Body], null, null) { CardSlots = 1, Refinable = true };
        InventoryView.ActionsFor(new InventoryEntryRecord(1, "item.c", 1), card).ShouldBe(ItemActions.InsertCard | ItemActions.Drop);
        InventoryView.ActionsFor(new InventoryEntryRecord(2, "item.v", 1), vest).ShouldBe(ItemActions.Equip | ItemActions.Drop | ItemActions.Refine);
        InventoryView.ActionsFor(new InventoryEntryRecord(2, "item.v", 1, EquipSlots.Body), vest).ShouldBe(ItemActions.Unequip | ItemActions.Refine);
    }

    [Fact]
    public void A_card_is_offered_the_equipment_it_fits_that_has_a_free_slot()
    {
        var bundle = ClientBundleFixture.Bundle();
        var view = new InventoryView();
        view.Apply(new InventoryMessage(1, 100, 100, 2040, WeightStates.Normal,
        [
            new InventoryEntryRecord(1, "item.gelcap_card", 1),
            new InventoryEntryRecord(2, "item.padded_vest", 1),                                       // body, 1 free slot
            new InventoryEntryRecord(3, "item.padded_vest", 1, EquipSlots.Body),                      // worn, 1 free slot
            new InventoryEntryRecord(4, "item.padded_vest", 1, null, ["item.gelcap_card"], 2),         // its one slot is full
            new InventoryEntryRecord(5, "item.wooden_buckler", 1),                                    // a shield, but no card slots
            new InventoryEntryRecord(6, "item.bone_dirk", 1),                                         // a weapon: the card does not fit
            new InventoryEntryRecord(7, "item.temper_shard", 3),
            new InventoryEntryRecord(8, "item.temper_shard", 2),
        ]));
        view.CardTargets(bundle.Item("item.gelcap_card"), bundle.Item).Select(e => e.Id).ShouldBe([2, 3]);
        view.CardTargets(bundle.Item("item.temper_shard"), bundle.Item).ShouldBeEmpty();
        view.Carried("item.temper_shard").ShouldBe(5);
        view.Carried("item.padded_vest").ShouldBe(2);   // the worn one is not carried
    }

    [Fact]
    public void The_bundle_carries_card_slots_refinable_and_the_refine_steps_a_player_decides_on()
    {
        var bundle = ClientBundleFixture.Bundle();
        var content = Game.Tests.World.TestGrids.SampleContent;
        bundle.Item("item.padded_vest")!.CardSlots.ShouldBe(1);
        bundle.Item("item.padded_vest")!.Refinable.ShouldBeTrue();
        bundle.Item("item.leather_cap")!.Refinable.ShouldBeFalse();
        bundle.Item("item.bone_dirk")!.CardSlots.ShouldBe(0);
        bundle.RefineMaxLevel.ShouldBe(content.Refine.MaxLevel);
        for (var level = 1; level <= content.Refine.MaxLevel; level++)
        {
            var step = content.Refine.StepTo(level)!;
            bundle.RefineStepTo(level).ShouldBe(new RefineStep(level, step.SuccessPercent, step.MaterialItemId, step.MaterialQuantity, step.Failure));
        }
        bundle.RefineStepTo(0).ShouldBeNull();
        bundle.RefineStepTo(content.Refine.MaxLevel + 1).ShouldBeNull();
    }

    [Fact]
    public void Equipment_lines_refine_offers_and_results_are_the_clients_words_with_the_servers_and_bundles_numbers()
    {
        UiText.EquipmentLine("Padded Vest", 0, [], 1).ShouldBe("Padded Vest [free slot]");
        UiText.EquipmentLine("Padded Vest", 2, ["Gelcap Card"], 1).ShouldBe("+2 Padded Vest [Gelcap Card]");
        UiText.EquipmentLine("Bone Dirk", 1, [], 0).ShouldBe("+1 Bone Dirk");
        UiText.RefineOffer("+4 Bone Dirk", 5, 60, "Temper Shard", 2, 7, RefineFailures.Down)
            .ShouldBe("Refine +4 Bone Dirk to +5: 60% chance. Uses Temper Shard x2 (you have 7), success or not. If it fails, it loses a level.");
        UiText.RefineOffer("X", 7, 30, "M", 3, 0, RefineFailures.Break).ShouldContain("it breaks and is gone, with its cards");
        UiText.RefineOffer("X", 1, 100, "M", 1, 1, RefineFailures.Keep).ShouldContain("it stays as it is");

        var ui = new UiState(new ClientConfig(), ClientBundleFixture.Bundle());
        foreach (var (result, level, expected) in new[]
                 {
                     (RefineResults.Success, 3, "Refined: +3 Bone Dirk!"), (RefineResults.Keep, 2, "The refine failed. +2 Bone Dirk is unchanged."),
                     (RefineResults.Down, 1, "The refine failed. +1 Bone Dirk lost a level."), (RefineResults.Break, 6, "The refine failed and +6 Bone Dirk broke."),
                 })
        {
            ui.OnRefineResult(new RefineResultMessage(9, "item.bone_dirk", result, level), 1);
            ui.Flash.ShouldBe(expected);
        }
        ui.OnInventoryChanged(new InventoryChangedMessage(1, InventoryChanges.CardInserted, "item.padded_vest", 30, 2040, WeightStates.Normal, [], []), 2);
        ui.Flash.ShouldBe("Card set in Padded Vest.");
        ui.OnRejected(new RejectedMessage(MessageTypes.Refine, ItemReasons.MissingMaterial, null, null, 9), 3);
        ui.Flash.ShouldBe(UiText.ItemRefused(ItemReasons.MissingMaterial));
    }

    [Fact]
    public void Entries_with_cards_and_a_level_replace_the_old_ones_on_a_change()
    {
        var view = new InventoryView();
        view.Apply(new InventoryMessage(1, 100, 45, 2040, WeightStates.Normal, [new InventoryEntryRecord(2, "item.padded_vest", 1, EquipSlots.Body)]));
        view.Apply(new InventoryChangedMessage(2, InventoryChanges.Refined, "item.padded_vest", 43, 2040, WeightStates.Normal,
            [new InventoryEntryRecord(2, "item.padded_vest", 1, EquipSlots.Body, ["item.gelcap_card"], 3)], [7])).ShouldBeTrue();
        view.Find(2)!.Refine.ShouldBe(3);
        view.Find(2)!.Cards.ShouldBe(["item.gelcap_card"]);
        view.Find(2)!.CardCount.ShouldBe(1);
    }
}

/// <summary>The display bundle of the sample content, read once.</summary>
internal static class ClientBundleFixture
{
    private static readonly Lazy<DisplayBundle> Cached = new(() =>
    {
        var file = Game.Content.ClientExport.ClientBundleExporter.Export(Game.Tests.World.TestGrids.SampleContent);
        DisplayBundle.TryRead(file.FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        return bundle!;
    });

    public static DisplayBundle Bundle() => Cached.Value;
}
