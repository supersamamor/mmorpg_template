using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Network;

/// <summary>The item messages over a real gateway (T-401): the own inventory after the snapshot, changes with their counter, refusals, the visible weapon.</summary>
public class GatewayInventoryTests
{
    [Fact]
    public async Task A_new_character_gets_its_whole_inventory_after_the_snapshot_and_shows_its_weapon()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var client = await rig.JoinAsync("hauler");
        var inventory = await client.WaitForAsync<InventoryMessage>();
        var messages = client.Messages;
        messages.IndexOf(inventory).ShouldBeGreaterThan(messages.FindIndex(m => m is SnapshotMessage));
        inventory.Rev.ShouldBe(0);
        inventory.Slots.ShouldBe(rig.Content.Balance.Inventory.MaxEntries);
        inventory.Load.ShouldBe(WeightStates.Normal);
        inventory.Entries.Select(e => (e.Item, e.Quantity, e.Slot)).ShouldBe([("item.bone_dirk", 1, EquipSlots.Weapon), ("item.healing_draught", 3, (string?)null)]);
        inventory.Weight.ShouldBe(30 + 3 * 5);
        var own = await Poll.UntilAsync(() => client.Seen(client.Id), r => r is not null, "own record");
        own!.Weapon.ShouldBe(WeaponTypes.Dagger);
        own.Worn.ShouldBe(["item.bone_dirk"]);
    }

    [Fact]
    public async Task Item_intents_change_the_inventory_with_the_next_rev_or_come_back_rejected()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var client = await rig.JoinAsync("user");
        var inventory = await client.WaitForAsync<InventoryMessage>();
        var potion = inventory.Entries.Single(e => e.Item == "item.healing_draught").Id;
        var dirk = inventory.Entries.Single(e => e.Item == "item.bone_dirk").Id;

        await client.SendAsync(new UseItemMessage(potion));
        var used = await client.WaitForAsync<InventoryChangedMessage>();
        used.Rev.ShouldBe(1);
        used.Cause.ShouldBe(InventoryChanges.Used);
        used.Entries.Single().ShouldBe(new InventoryEntryRecord(potion, "item.healing_draught", 2));

        await client.SendAsync(new UnequipMessage(EquipSlots.Weapon));
        var off = await client.WaitForAsync<InventoryChangedMessage>(m => m.Rev == 2);
        off.Cause.ShouldBe(InventoryChanges.Unequipped);
        off.Entries.Single().ShouldBe(new InventoryEntryRecord(dirk, "item.bone_dirk", 1));

        await client.SendAsync(new EquipMessage(999));
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.Equip)).ShouldBe(new RejectedMessage(MessageTypes.Equip, ItemReasons.ItemGone, null, null, 999));
        await client.SendAsync(new PickUpMessage(424242));   // not in view: refused by the gateway itself
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.PickUp)).ShouldBe(new RejectedMessage(MessageTypes.PickUp, ItemReasons.ItemGone, null, 424242));
        await client.SendAsync(new DropItemMessage(potion, 1));
        var dropped = await client.WaitForAsync<InventoryChangedMessage>(m => m.Rev == 3);
        dropped.Cause.ShouldBe(InventoryChanges.Dropped);
        await Poll.UntilAsync(() => client.SeenIds.Select(client.Seen).Count(r => r?.Kind == EntityKinds.Item && r.DisplayKey == "item.healing_draught"),
            n => n == 1, "the dropped draught on the ground");
    }
}
