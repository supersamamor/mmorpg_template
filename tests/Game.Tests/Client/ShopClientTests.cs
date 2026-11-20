using System.Text;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Client.Core.Shop;
using Game.Content.ClientExport;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;
using ClientSession = Game.Client.Core.ClientSession;

namespace Game.Tests.Client;

/// <summary>The client's half of the shop (T-603): what it holds of the <c>shop</c> message, what it sends (an item or an entry and a count, never a price), and the words it uses.</summary>
public class ShopClientTests
{
    private sealed class ScriptedSocket : IClientSocket
    {
        public readonly Queue<SocketEvent> Events = new();
        public readonly List<string> Sent = new();

        public void Open(string url)
        {
        }

        public void Send(byte[] utf8) => Sent.Add(Encoding.UTF8.GetString(utf8));

        public void Close()
        {
        }

        public bool TryDequeue(out SocketEvent socketEvent) => Events.TryDequeue(out socketEvent);

        public void Push(ServerMessage message) => Events.Enqueue(new SocketEvent(SocketEventKind.Message, message));
    }

    private static readonly ShopMessage Opened = new(12, "shop.meadow_goods", 99,
        [new ShopStockRecord("item.healing_draught", 40), new ShopStockRecord("item.antidote", 30)],
        [new ShopStockRecord("item.gel_glob", 4), new ShopStockRecord("item.healing_draught", 12)]);

    private static (ClientSession Session, ScriptedSocket Socket) Playing()
    {
        var file = ClientBundleExporter.Export(TestGrids.SampleContent);
        DisplayBundle.TryRead(file.FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        var socket = new ScriptedSocket();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, bundle!, socket, new Pcg32Random(1));
        session.Login("Ada", 0);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        socket.Push(new WelcomeMessage(1, 1, "map.starter_meadow", 10, null, "secret-token"));
        var all = new List<EntityRecord>
        {
            new(1, EntityKinds.Player, "job.wanderer", new GridCell(12, 12), Directions.N, null, 0),
            new(12, EntityKinds.Npc, "npc.merchant", new GridCell(12, 9), Directions.S, null, 0),
        };
        socket.Push(new SnapshotMessage(1, "map.starter_meadow", 1, all, new VitalsRecord(40, 40, 10, 10)));
        session.Update(0, 0);
        session.Connection.Phase.ShouldBe(ConnectionPhase.Online);
        socket.Sent.Clear();
        return (session, socket);
    }

    [Fact]
    public void The_shop_message_opens_the_book_and_a_finished_dialogue_leaves_it_open()
    {
        var (session, socket) = Playing();

        socket.Push(new ServiceOpenedMessage(ServiceIds.Shop, 12));
        socket.Push(Opened);
        socket.Push(new DialogueEndedMessage(DialogueEndReasons.Finished));
        session.Update(1, 1);

        var shop = session.Shop;
        shop.Open.ShouldBeTrue();
        (shop.Npc, shop.ShopId, shop.MaxQuantity, shop.Tab).ShouldBe((12L, "shop.meadow_goods", 99, ShopTab.Buy));
        shop.PriceOf("item.antidote").ShouldBe(30);
        shop.PriceOf("item.gel_glob").ShouldBeNull();
        shop.SellPriceOf("item.gel_glob").ShouldBe(4);
        shop.SellPriceOf("item.antidote").ShouldBeNull();
        session.Dialogue.Service.ShouldBe(ServiceIds.Shop);
    }

    [Theory]
    [InlineData(DialogueEndReasons.Closed)]
    [InlineData(DialogueEndReasons.MovedAway)]
    [InlineData(DialogueEndReasons.Died)]
    [InlineData(DialogueEndReasons.Replaced)]
    public void Any_other_ending_closes_the_shop(string reason)
    {
        var (session, socket) = Playing();
        socket.Push(new ServiceOpenedMessage(ServiceIds.Shop, 12));
        socket.Push(Opened);
        session.Update(1, 1);

        socket.Push(new DialogueEndedMessage(reason));
        session.Update(2, 2);

        session.Shop.Open.ShouldBeFalse();
        session.Dialogue.Service.ShouldBeNull();
    }

    [Fact]
    public void A_refusal_for_the_service_or_the_range_closes_the_shop()
    {
        var (session, socket) = Playing();
        socket.Push(new ServiceOpenedMessage(ServiceIds.Shop, 12));
        socket.Push(Opened);
        session.Update(1, 1);

        socket.Push(new RejectedMessage(MessageTypes.ShopBuy, ItemReasons.CannotAfford, null, Item: "item.antidote"));
        session.Update(2, 2);
        session.Shop.Open.ShouldBeTrue();   // being poor is not the end of the visit
        session.Ui.Flash.ShouldBe(UiText.ItemRefused(ItemReasons.CannotAfford));

        socket.Push(new RejectedMessage(MessageTypes.ShopSell, ItemReasons.OutOfRange, null, Entry: 4));
        session.Update(3, 3);
        session.Shop.Open.ShouldBeFalse();
        session.Dialogue.Service.ShouldBeNull();
    }

    [Fact]
    public void Buying_and_selling_send_the_item_or_entry_and_the_count_and_never_a_price()
    {
        var (session, socket) = Playing();

        session.ShopBuy("item.antidote", 2);
        session.ShopSell(9, 3);

        socket.Sent.ShouldBe(["""{"type":"shop_buy","item":"item.antidote","quantity":2}""", """{"type":"shop_sell","entry":9,"quantity":3}"""]);
        socket.Sent.ShouldAllBe(s => !s.Contains("price", StringComparison.Ordinal));
    }

    [Fact]
    public void A_trade_in_the_inventory_message_says_what_happened_on_the_flash_line()
    {
        var (session, socket) = Playing();
        socket.Push(new InventoryMessage(1, 30, 0, 2000, WeightStates.Normal, [], 36));
        socket.Push(new InventoryChangedMessage(2, InventoryChanges.ShopBuy, "item.antidote", 1, 2000, WeightStates.Normal,
            [new InventoryEntryRecord(5, "item.antidote", 1)], [], 6));
        session.Update(1, 1);

        session.Inventory.Currency.ShouldBe(6);
        session.Ui.Flash.ShouldBe(UiText.Bought("Antidote"));

        socket.Push(new InventoryChangedMessage(3, InventoryChanges.ShopSell, "item.antidote", 0, 2000, WeightStates.Normal, [], [5], 36));
        session.Update(2, 2);
        session.Ui.Flash.ShouldBe(UiText.Sold("Antidote"));
        session.Inventory.Currency.ShouldBe(36);
    }

    [Fact]
    public void Picking_sets_the_quantity_to_one_and_the_quantity_stays_within_its_limits()
    {
        var book = new ShopBook();
        book.Apply(Opened);

        book.SelectItem("item.gel_glob");
        book.SelectedItem.ShouldBeNull();   // not in the stock
        book.SelectItem("item.antidote");
        book.SelectedItem.ShouldBe("item.antidote");
        book.Quantity.ShouldBe(1);
        book.SetQuantity(500, 99);
        book.Quantity.ShouldBe(99);
        book.SetQuantity(-4, 99);
        book.Quantity.ShouldBe(1);
        book.SetQuantity(5, 3);
        book.Quantity.ShouldBe(3);

        book.SelectItem("item.antidote");   // the same item again clears it
        book.SelectedItem.ShouldBeNull();
        book.SelectItem("item.healing_draught");
        book.SetQuantity(7, 99);
        book.SetTab(ShopTab.Sell);          // a new tab starts over
        (book.SelectedItem, book.SelectedEntry, book.Quantity).ShouldBe((null, null, 1));
        book.SelectEntry(9);
        book.SelectedEntry.ShouldBe(9);
        book.SelectEntry(9);
        book.SelectedEntry.ShouldBeNull();
    }

    [Fact]
    public void The_preview_total_never_wraps_and_a_new_shop_message_starts_a_clean_visit()
    {
        ShopBook.Total(40, 5).ShouldBe(200);
        ShopBook.Total(long.MaxValue / 2, 3).ShouldBeNull();
        ShopBook.Total(40, 0).ShouldBeNull();

        var book = new ShopBook();
        book.Apply(Opened);
        book.SelectItem("item.antidote");
        book.SetQuantity(4, 99);
        book.SetTab(ShopTab.Sell);
        book.Apply(Opened);
        (book.Tab, book.SelectedItem, book.Quantity, book.Open).ShouldBe((ShopTab.Buy, null, 1, true));
        book.Reset();
        (book.Open, book.Stock.Count, book.SellPriceOf("item.gel_glob")).ShouldBe((false, 0, null));
    }

    [Fact]
    public void Every_shop_reason_has_words_for_the_flash_line()
    {
        foreach (var reason in new[] { ItemReasons.OutOfRange, ItemReasons.NotInStock, ItemReasons.QuantityLimit, ItemReasons.CannotAfford, ItemReasons.NotSellable, ItemReasons.CurrencyFull, ItemReasons.Dead })
            UiText.ItemRefused(reason).ShouldNotBeEmpty(reason);
        UiText.ShopRow("Antidote", "crowns", 30).ShouldBe("Antidote  -  Crowns: 30");
        UiText.ShopRow("Realign Tonic", "crowns", null).ShouldContain(UiText.ShopNoPrice);
        UiText.ShopQuantity(5, "crowns", 150).ShouldBe("x5   Crowns: 150");
        UiText.ShopQuantity(5, "crowns", null).ShouldBe("x5   -");
    }
}
