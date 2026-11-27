using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Network;

/// <summary>
/// T-903 over a real gateway: two new characters at the start trade an item and currency through the wire messages only (the client says nothing but intents), a sale of an offered
/// item at the merchant changes the offer the other side sees, and a dropped connection ends a trade with nothing moved.
/// </summary>
public class GatewayTradeTests
{
    [Fact]
    public async Task Two_clients_trade_an_item_and_currency_and_a_dropped_connection_ends_the_next_trade()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var ada = await rig.JoinAsync("ada");
        using var bo = await rig.JoinAsync("bo");
        var adaBag = await ada.WaitForAsync<InventoryMessage>();
        var boBag = await bo.WaitForAsync<InventoryMessage>();
        var draughts = adaBag.Entries.Single(e => e.Item == "item.healing_draught");
        var boDraughts = boBag.Entries.Single(e => e.Item == "item.healing_draught");

        // Ada sells one draught at the merchant for some currency (a new character has none).
        var merchant = await Poll.UntilAsync(() => ada.SeenIds.FirstOrDefault(id => ada.Seen(id) is { Kind: EntityKinds.Npc } r && r.DisplayKey == "npc.merchant"), id => id != 0, "the merchant");
        await ada.SendAsync(new TalkMessage(merchant));
        var greeting = await ada.WaitForAsync<DialogueMessage>(m => m.Speaker == "npc.merchant");
        await ada.SendAsync(new DialogueChoiceMessage(greeting.Choices.Single(c => c.Text.Contains("wares", StringComparison.Ordinal)).Index));
        await ada.WaitForAsync<ShopMessage>();
        await ada.SendAsync(new ShopSellMessage(draughts.Id, 1));
        (await ada.WaitForAsync<InventoryChangedMessage>(c => c.Cause == InventoryChanges.ShopSell)).Currency.ShouldBe(12);

        await ada.SendAsync(new TradeAskMessage(bo.Id));
        (await ada.WaitForAsync<TradeNoticeMessage>()).ShouldBe(new TradeNoticeMessage(TradeNotices.AskSent, "bo"));
        var asked = await bo.WaitForAsync<TradeAskedMessage>();
        (asked.From, asked.Name).ShouldBe((ada.Id, "ada"));
        await bo.SendAsync(new TradeAnswerMessage(true));
        (await ada.WaitForAsync<TradeMessage>()).Partner.ShouldBe(bo.Id);

        await ada.SendAsync(new TradeOfferMessage(draughts.Id, 2));
        await ada.SendAsync(new TradeCurrencyMessage(12));
        await bo.SendAsync(new TradeOfferMessage(boDraughts.Id, 1));
        var seen = await bo.WaitForAsync<TradeMessage>(t => t.Theirs.Currency == 12 && t.Theirs.Items.Count == 1 && t.Mine.Items.Count == 1);
        seen.Theirs.Items.Single().ShouldBe(new TradeItemRecord(draughts.Id, "item.healing_draught", 2));

        // Ada sells one of the offered draughts: the offer follows what she carries, and both sides see it.
        await ada.SendAsync(new TradeLockMessage());
        await bo.SendAsync(new TradeLockMessage());
        await bo.WaitForAsync<TradeMessage>(t => t.Mine.Locked && t.Theirs.Locked);
        await ada.SendAsync(new ShopSellMessage(draughts.Id, 1));
        var followed = await bo.WaitForAsync<TradeMessage>(t => t.Theirs.Items.Count == 1 && t.Theirs.Items[0].Quantity == 1);
        (followed.Mine.Locked, followed.Theirs.Locked).ShouldBe((false, false));
        followed.Theirs.Currency.ShouldBe(12, "the currency it carries still covers the offer");

        await ada.SendAsync(new TradeLockMessage());
        await bo.SendAsync(new TradeLockMessage());
        var locked = await ada.WaitForAsync<TradeMessage>(t => t.Mine.Locked && t.Theirs.Locked && t.Version > followed.Version);
        await ada.SendAsync(new TradeConfirmMessage(locked.Version));
        await bo.SendAsync(new TradeConfirmMessage(locked.Version));
        (await ada.WaitForAsync<TradeClosedMessage>()).ShouldBe(new TradeClosedMessage(TradeEnds.Done, "bo"));
        (await bo.WaitForAsync<TradeClosedMessage>()).ShouldBe(new TradeClosedMessage(TradeEnds.Done, "ada"));
        var adaAfter = ada.Messages.OfType<InventoryChangedMessage>().Last(c => c.Cause == InventoryChanges.Traded);
        var boAfter = bo.Messages.OfType<InventoryChangedMessage>().Last(c => c.Cause == InventoryChanges.Traded);
        (adaAfter.Currency, boAfter.Currency).ShouldBe((12L, 12L), "12 from the second sale stayed, 12 went to Bo");
        adaAfter.Removed.ShouldContain(draughts.Id, "the last offered draught went whole, with its id");
        boAfter.Entries.ShouldContain(e => e.Id == draughts.Id && e.Quantity == 1);

        // A second trade; Bo's connection drops: Ada is told, and nothing moves.
        await ada.SendAsync(new TradeAskMessage(bo.Id));
        await bo.WaitForAsync<TradeAskedMessage>(a => bo.Messages.OfType<TradeAskedMessage>().Count() == 2);
        await bo.SendAsync(new TradeAnswerMessage(true));
        await ada.WaitForAsync<TradeMessage>(t => ada.Messages.OfType<TradeClosedMessage>().Count() == 1 && t.Mine.Items.Count == 0);
        await ada.SendAsync(new TradeCurrencyMessage(12));
        await ada.WaitForAsync<TradeMessage>(t => t.Mine.Currency == 12);
        bo.Dispose();
        (await ada.WaitForAsync<TradeClosedMessage>(c => c.Reason != TradeEnds.Done)).ShouldBe(new TradeClosedMessage(TradeEnds.Disconnected, "bo"));
        ada.Messages.OfType<InventoryChangedMessage>().Count(c => c.Cause == InventoryChanges.Traded).ShouldBe(1);
    }
}
