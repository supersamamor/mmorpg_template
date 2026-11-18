using Game.Content.Registry;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.World;

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
        inventory.Entries.Select(e => (e.Item, e.Quantity, e.Slot)).ShouldBe([("item.bone_dirk", 1, EquipSlots.Weapon), ("item.healing_draught", 3, (string?)null), ("item.realign_tonic", 1, null), ("item.blank_page", 1, null)]);
        inventory.Weight.ShouldBe(30 + 3 * 5 + 2 + 2);
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

    /// <summary>
    /// T-402 over the wire, on the sample content with a start kit of a worn Bone Dirk, a Padded Vest, a Gelcap Card and two Temper Shards: socket the card,
    /// refine the dirk once (the first step of refine.yaml always succeeds), and see a refusal. Each change comes with the next rev; the refine's outcome follows
    /// its change as refine_result; the full inventory after a resync carries the card and the level.
    /// </summary>
    [Fact]
    public async Task Insert_card_and_refine_change_the_inventory_and_say_how_the_refine_came_out()
    {
        var db = TestGrids.SampleContent;
        var kit = db.Balance with
        {
            NewCharacter = db.Balance.NewCharacter with
            {
                StartItems =
                [
                    new StartItemDef { ItemId = "item.bone_dirk", Equipped = true }, new StartItemDef { ItemId = "item.padded_vest" },
                    new StartItemDef { ItemId = "item.gelcap_card" }, new StartItemDef { ItemId = "item.temper_shard", Quantity = 2 },
                ],
            },
        };
        var content = new ContentDatabase(db.Jobs, db.Skills, db.Items, db.Monsters, db.Statuses, db.Quests, db.Npcs, db.Dialogue, db.Drops, db.Maps,
            db.ExpTables, db.Formulas, db.Elements, kit, db.Appearance, grids: db.Grids, refine: db.Refine);
        await using var rig = await GatewayRig.StartAsync(content: content);
        using var client = await rig.JoinAsync("smith");
        var inventory = await client.WaitForAsync<InventoryMessage>();
        long Id(string item) => inventory.Entries.Single(e => e.Item == item).Id;
        var (dirk, vest, card, shards) = (Id("item.bone_dirk"), Id("item.padded_vest"), Id("item.gelcap_card"), Id("item.temper_shard"));

        await client.SendAsync(new InsertCardMessage(vest, card));
        var socketed = await client.WaitForAsync<InventoryChangedMessage>(m => m.Rev == 1);
        socketed.Cause.ShouldBe(InventoryChanges.CardInserted);
        socketed.Item.ShouldBe("item.padded_vest");
        socketed.Removed.ShouldBe([card]);
        socketed.Entries.Single().Cards.ShouldBe(["item.gelcap_card"]);

        await client.SendAsync(new RefineMessage(dirk));
        var refined = await client.WaitForAsync<InventoryChangedMessage>(m => m.Rev == 2);
        refined.Cause.ShouldBe(InventoryChanges.Refined);
        refined.Entries.Single(e => e.Id == dirk).ShouldBe(new InventoryEntryRecord(dirk, "item.bone_dirk", 1, EquipSlots.Weapon, null, 1));
        refined.Entries.Single(e => e.Id == shards).Quantity.ShouldBe(1);
        var result = await client.WaitForAsync<RefineResultMessage>();
        result.ShouldBe(new RefineResultMessage(dirk, "item.bone_dirk", RefineResults.Success, 1));
        client.Messages.IndexOf(result).ShouldBeGreaterThan(client.Messages.IndexOf(refined));

        await client.SendAsync(new InsertCardMessage(dirk, card));   // the card is gone
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.InsertCard))
            .ShouldBe(new RejectedMessage(MessageTypes.InsertCard, ItemReasons.ItemGone, null, null, dirk));

        // A resync: the full inventory carries both.
        rig.World.TryEnqueue(new RequestSnapshot(new EntityId(client.Id))).ShouldBe(EnqueueResult.Accepted);
        var full = await client.WaitForAsync<InventoryMessage>(m => m.Rev == 2);
        full.Entries.Single(e => e.Id == vest).Cards.ShouldBe(["item.gelcap_card"]);
        full.Entries.Single(e => e.Id == dirk).Refine.ShouldBe(1);
    }
}
