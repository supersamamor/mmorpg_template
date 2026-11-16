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
