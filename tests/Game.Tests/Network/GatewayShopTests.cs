using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Network;

/// <summary>The merchant's shop over a real gateway (T-603): the real content and pipeline, a new character beside the merchant in the starter meadow's safe zone.</summary>
public class GatewayShopTests
{
    private static async Task<long> SeeNpcAsync(TestClient client, string npcId) =>
        await Poll.UntilAsync(() => client.SeenIds.FirstOrDefault(id => client.Seen(id) is { Kind: EntityKinds.Npc } r && r.DisplayKey == npcId), id => id != 0, "the NPC " + npcId);

    [Fact]
    public async Task Talk_open_the_shop_sell_buy_and_close_over_the_wire()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var client = await rig.JoinAsync("shopper");
        var inventory = await client.WaitForAsync<InventoryMessage>();
        inventory.Currency.ShouldBe(0);
        var draughts = inventory.Entries.Single(e => e.Item == "item.healing_draught");
        var merchant = await SeeNpcAsync(client, "npc.merchant");

        await client.SendAsync(new TalkMessage(merchant));
        var greeting = await client.WaitForAsync<DialogueMessage>(m => m.Speaker == "npc.merchant");
        await client.SendAsync(new DialogueChoiceMessage(greeting.Choices.Single(c => c.Text.Contains("wares", StringComparison.Ordinal)).Index));
        var opened = await client.WaitForAsync<ServiceOpenedMessage>();
        (opened.Service, opened.Npc).ShouldBe((ServiceIds.Shop, merchant));
        var shop = await client.WaitForAsync<ShopMessage>();
        (shop.Npc, shop.Shop, shop.MaxQuantity).ShouldBe((merchant, "shop.meadow_goods", 99));
        shop.Stock.Single(s => s.Item == "item.antidote").Price.ShouldBe(30);
        shop.Sell.Single(s => s.Item == "item.healing_draught").Price.ShouldBe(12);

        // A new character is poor: a purchase it cannot pay for is refused, naming the item.
        await client.SendAsync(new ShopBuyMessage("item.antidote", 1));
        var poor = await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.ShopBuy);
        (poor.Reason, poor.Item).ShouldBe((ItemReasons.CannotAfford, "item.antidote"));

        // Selling the three draughts pays 3 x 12; the same change tells the new balance.
        await client.SendAsync(new ShopSellMessage(draughts.Id, 3));
        var sold = await client.WaitForAsync<InventoryChangedMessage>(c => c.Cause == InventoryChanges.ShopSell);
        (sold.Item, sold.Currency).ShouldBe(("item.healing_draught", 36L));
        sold.Removed.ShouldBe([draughts.Id]);

        await client.SendAsync(new ShopBuyMessage("item.antidote", 1));
        var bought = await client.WaitForAsync<InventoryChangedMessage>(c => c.Cause == InventoryChanges.ShopBuy);
        (bought.Item, bought.Currency, bought.Rev).ShouldBe(("item.antidote", 6L, sold.Rev + 1));
        bought.Entries.Single().Item.ShouldBe("item.antidote");

        // Nothing the client sends carries a price, and a worn or unsellable item is refused with its reason.
        var tonic = inventory.Entries.Single(e => e.Item == "item.realign_tonic");
        await client.SendAsync(new ShopSellMessage(tonic.Id, 1));
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.ShopSell)).Reason.ShouldBe(ItemReasons.NotSellable);

        await client.SendAsync(new DialogueCloseMessage());
        (await client.WaitForAsync<DialogueEndedMessage>(m => m.Reason == DialogueEndReasons.Closed)).Reason.ShouldBe(DialogueEndReasons.Closed);
        await client.SendAsync(new ShopBuyMessage("item.antidote", 1));
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.ShopBuy && r.Reason != ItemReasons.CannotAfford)).Reason.ShouldBe(ItemReasons.ServiceRequired);
    }
}
