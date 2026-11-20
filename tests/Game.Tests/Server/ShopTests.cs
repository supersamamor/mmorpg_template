using Game.Server.Events;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Shop;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Items;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>
/// One map with the real merchant (<c>npc.merchant</c>, <c>shop.meadow_goods</c>, <c>dialogue.merchant_intro</c>) running movement, the dialogue system and the inventory,
/// every event recorded (T-603). Players are new characters with nothing in the bag and no currency unless a test gives some.
/// </summary>
public sealed class ShopRig
{
    public static readonly GridCell MerchantCell = new(12, 9);
    public static readonly GridCell Beside = new(12, 11);

    public readonly GameWorld World;
    public readonly ManualClock Clock = new();
    public readonly ListLog Log = new();
    public readonly InMemoryInventoryStore Store = new();
    public readonly InMemoryProgressionStore Progressions = new();
    public readonly InMemorySavePointStore SavePoints = new();
    public readonly List<(long Tick, IGameEvent Event)> Events = new();
    public readonly InventoryContent Items;
    public readonly ShopContent Shops;

    public ShopRig(int maxEntries = 30, ShopContent? shops = null)
    {
        var db = TestGrids.SampleContent;
        var balance = db.Balance with
        {
            Inventory = db.Balance.Inventory with { MaxEntries = maxEntries },
            NewCharacter = db.Balance.NewCharacter with { StartItems = [] },
        };
        var jobs = db.Jobs.All.ToDictionary(j => j.Id, j => j.WeaponTypes, StringComparer.Ordinal);
        Items = new InventoryContent(db.Items.All.ToDictionary(i => i.Id, StringComparer.Ordinal), balance, jobs, refine: db.Refine);
        Shops = shops ?? ShopContent.From(db);
        var dialogue = DialogueContent.From(db);
        var grid = DialogueRig.Grid(TestCombat.MapId, 20, 14, [new GridNpc { NpcId = "npc.merchant", Cell = MerchantCell, Facing = Directions.S }], null);
        var pipeline = new SystemPipeline()
            .Add(() => new MovementSystem(TestGrids.Roomy))
            .Add(() => new DialogueSystem(dialogue, SavePoints, Progressions, Store))
            .Add(() => new InventorySystem(Items, Store, Clock, Progressions, Shops));
        World = new GameWorld([grid], pipeline, 1, Clock, Log);
        Instance.Events.SubscribeAll(e => Events.Add((Instance.TickNumber, e)));
    }

    public MapInstance Instance => World.Instances[0];

    public EntityId AddPlayer(GridCell? cell = null) =>
        World.Spawn(EntityKind.Player, Instance.MapId, cell ?? Beside, Directions.S, e =>
        {
            e.Stats = TestCombat.PlayerSheet();
            e.DefinitionId = "job.wanderer";
        });

    public Entity Get(EntityId id) => Instance.TryGetEntity(id, out var e) ? e : throw new InvalidOperationException($"{id} is gone");

    public EntityId Merchant => Instance.Entities.First(e => e.Kind == EntityKind.Npc && e.DefinitionId == "npc.merchant").Id;

    public InventoryState State(EntityId player) => Store.TryGet(player, out var s) ? s : throw new InvalidOperationException("no inventory");

    public long Give(EntityId player, string itemId, int quantity = 1)
    {
        var delta = new InventoryDelta();
        Items.Rules.TryAdd(State(player), itemId, quantity, int.MaxValue, Store.NextEntryId, delta).ShouldBeNull();
        return delta.Upserted.Last().Id;
    }

    public int Count(EntityId player, string itemId) => State(player).Entries.Where(e => e.ItemId == itemId && e.Slot is null).Sum(e => e.Quantity);

    public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++) Instance.Tick();
    }

    public List<T> Of<T>() where T : IGameEvent => Events.Select(e => e.Event).OfType<T>().ToList();

    /// <summary>Joins a player beside the merchant, talks, picks "Show me your wares" and ticks until the shop is announced.</summary>
    public EntityId Shopping(long currency = 0)
    {
        var player = AddPlayer();
        Ticks(2);
        State(player).Currency = currency;
        Send(new TalkTo(player, Merchant));
        Ticks(2);
        Send(new DialogueChoose(player, 0));
        Ticks(3);
        return player;
    }

    public string Snapshot(EntityId player) =>
        string.Join(" ", State(player).Entries.Select(e => $"{e.Id}:{e.ItemId}x{e.Quantity}{(e.Slot is null ? "" : "@" + e.Slot)}")) + $" c{State(player).Currency} w{State(player).Weight} rev{State(player).Revision}";

    public string LastRefusal(string intent) => Of<ItemRefused>().Last(r => r.Intent == intent).Reason;
}

public class ShopTests
{
    // ---- opening --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Picking_the_wares_opens_the_shop_and_announces_its_priced_stock()
    {
        var rig = new ShopRig();
        var player = rig.Shopping();

        var entity = rig.Get(player);
        entity.OpenService.ShouldBe(ServiceIds.Shop);
        entity.OpenShopId.ShouldBe("shop.meadow_goods");
        entity.OpenServiceNpc.ShouldBe(rig.Merchant);
        rig.Of<ServiceOpened>().Single().ShouldBe(new ServiceOpened(player, rig.Merchant, ServiceIds.Shop, "shop.meadow_goods"));
        var opened = rig.Of<ShopOpened>().Single();
        opened.MaxQuantity.ShouldBe(99);
        opened.Stock.Select(s => s.Item).ShouldContain("item.healing_draught");
        opened.Stock.Single(s => s.Item == "item.healing_draught").Price.ShouldBe(40);   // the item's buy_price: the real stock row sets none of its own
        opened.Stock.Select(s => s.Item).ShouldContain("item.realign_tonic");
        opened.Stock.Select(s => s.Item).ShouldContain("item.blank_page");
        opened.Sell.Single(s => s.Item == "item.gel_glob").Price.ShouldBe(4);
        opened.Sell.Select(s => s.Item).ShouldNotContain("item.warden_token");   // a quest item is never bought
        opened.Sell.Select(s => s.Item).ShouldNotContain("item.realign_tonic");   // sell_price 0: the shop will not take it
    }

    [Fact]
    public void Closing_the_dialogue_closes_the_shop()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(1000);

        rig.Send(new DialogueClose(player));
        rig.Ticks(2);
        rig.Get(player).OpenShopId.ShouldBeNull();
        rig.Get(player).OpenServiceNpc.ShouldBeNull();
        rig.Send(new ShopBuy(player, "item.healing_draught", 1));
        rig.Ticks(2);

        rig.LastRefusal(MessageTypes.ShopBuy).ShouldBe(ItemReasons.ServiceRequired);
        rig.Count(player, "item.healing_draught").ShouldBe(0);
    }

    [Fact]
    public void Walking_away_closes_the_shop()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(1000);

        rig.Send(new MoveTo(player, new GridCell(3, 5)));
        for (var i = 0; i < 40 && rig.Get(player).OpenService is not null; i++) rig.Ticks();

        rig.Get(player).OpenService.ShouldBeNull();
        rig.Of<DialogueEnded>().Last().Reason.ShouldBe(DialogueEndReasons.MovedAway);
    }

    // ---- buying ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Buying_takes_the_price_and_puts_the_items_in_the_bag_in_one_change()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(1000);
        var revision = rig.State(player).Revision;

        rig.Send(new ShopBuy(player, "item.healing_draught", 5));
        rig.Ticks(2);

        rig.State(player).Currency.ShouldBe(800);   // 5 x 40
        rig.Count(player, "item.healing_draught").ShouldBe(5);
        var change = rig.Of<InventoryChanged>().Single();
        (change.Cause, change.ItemId, change.Currency, change.Revision).ShouldBe((InventoryChanges.ShopBuy, "item.healing_draught", 800L, revision + 1));
        rig.Of<ItemRefused>().ShouldBeEmpty();
    }

    [Fact]
    public void A_stock_row_with_its_own_price_overrides_the_items_buy_price()
    {
        var db = TestGrids.SampleContent;
        var shop = new ShopDef { Id = "shop.test", Stock = [new ShopStockDef { ItemId = "item.healing_draught", Price = 7 }] };
        var shops = new ShopContent([shop], db.Items.All.ToDictionary(i => i.Id, StringComparer.Ordinal), new ShopBalanceDef(), 2);
        shops.TryGet("shop.test", out var priced).ShouldBeTrue();
        priced.Prices["item.healing_draught"].ShouldBe(7);
        priced.Stock.Single().Price.ShouldBe(7);
    }

    [Theory]
    [InlineData("item.gel_glob", 1, ItemReasons.NotInStock)]          // the shop does not sell it (a material)
    [InlineData("item.nothing", 1, ItemReasons.NotInStock)]           // an id the content does not have
    [InlineData("item.healing_draught", 0, ItemReasons.InvalidQuantity)]
    [InlineData("item.healing_draught", -3, ItemReasons.InvalidQuantity)]
    [InlineData("item.healing_draught", 100, ItemReasons.QuantityLimit)]   // shop.max_buy_quantity is 99
    [InlineData("item.healing_draught", int.MaxValue, ItemReasons.QuantityLimit)]
    public void A_buy_that_breaks_a_rule_is_refused_with_its_reason_and_changes_nothing(string item, int quantity, string reason)
    {
        var rig = new ShopRig();
        var player = rig.Shopping(100_000);
        var before = rig.Snapshot(player);

        rig.Send(new ShopBuy(player, item, quantity));
        rig.Ticks(2);

        rig.LastRefusal(MessageTypes.ShopBuy).ShouldBe(reason);
        rig.Of<ItemRefused>().Last().ItemId.ShouldBe(item);
        rig.Snapshot(player).ShouldBe(before);
        rig.Of<InventoryChanged>().ShouldBeEmpty();
    }

    [Fact]
    public void A_buy_without_enough_currency_is_refused_and_the_bag_is_untouched()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(199);   // 5 draughts cost 200
        var before = rig.Snapshot(player);

        rig.Send(new ShopBuy(player, "item.healing_draught", 5));
        rig.Ticks(2);

        rig.LastRefusal(MessageTypes.ShopBuy).ShouldBe(ItemReasons.CannotAfford);
        rig.Snapshot(player).ShouldBe(before);
    }

    [Fact]
    public void A_buy_the_bag_cannot_hold_leaves_the_currency_unchanged_when_full()
    {
        var rig = new ShopRig(maxEntries: 2);
        var player = rig.Shopping(1000);
        rig.Give(player, "item.gel_glob");
        rig.Give(player, "item.mossy_fiber");   // two entries: the bag is full
        var before = rig.Snapshot(player);

        rig.Send(new ShopBuy(player, "item.bone_dirk", 1));   // equipment never stacks: it needs a new entry
        rig.Ticks(2);

        rig.LastRefusal(MessageTypes.ShopBuy).ShouldBe(ItemReasons.InventoryFull);
        rig.Snapshot(player).ShouldBe(before);   // the currency is exactly as it was
        rig.State(player).Currency.ShouldBe(1000);
        rig.Of<InventoryChanged>().ShouldBeEmpty();
    }

    [Fact]
    public void A_buy_that_is_too_heavy_leaves_the_currency_unchanged()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(1000);
        var limit = rig.State(player).WeightLimit;
        rig.Give(player, "item.gel_glob", limit - 4);   // weight 1 each: room for less than one 5-weight draught
        rig.Ticks(2);   // the inventory notices the new load (a weight change of its own) before the buy
        rig.Events.Clear();
        var before = rig.Snapshot(player);

        rig.Send(new ShopBuy(player, "item.healing_draught", 1));
        rig.Ticks(2);

        rig.LastRefusal(MessageTypes.ShopBuy).ShouldBe(ItemReasons.TooHeavy);
        rig.Snapshot(player).ShouldBe(before);
    }

    [Fact]
    public void Each_buy_and_a_refused_one_after_it_leave_the_exact_balance()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(120);

        rig.Send(new ShopBuy(player, "item.healing_draught", 2));   // 80
        rig.Send(new ShopBuy(player, "item.healing_draught", 2));   // 80 more: only 40 left, refused
        rig.Ticks(2);
        rig.Send(new ShopBuy(player, "item.healing_draught", 1));   // exactly 40
        rig.Ticks(2);

        rig.State(player).Currency.ShouldBe(0);
        rig.Count(player, "item.healing_draught").ShouldBe(3);
        rig.Of<ItemRefused>().Select(r => r.Reason).ShouldBe([ItemReasons.CannotAfford]);
    }

    [Fact]
    public void A_dead_player_cannot_trade()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(1000);
        rig.Get(player).Dead = true;

        rig.Send(new ShopBuy(player, "item.healing_draught", 1));
        rig.Ticks(2);

        rig.LastRefusal(MessageTypes.ShopBuy).ShouldBe(ItemReasons.Dead);
        rig.State(player).Currency.ShouldBe(1000);
    }

    [Fact]
    public void A_shop_whose_npc_is_out_of_reach_refuses_with_out_of_range()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(1000);
        rig.Get(player).OpenServiceNpc = new EntityId(999_999);   // the NPC is gone (the dialogue system would end the service on its next tick; the shop checks for itself)

        rig.Send(new ShopBuy(player, "item.healing_draught", 1));
        rig.Ticks(1);

        rig.LastRefusal(MessageTypes.ShopBuy).ShouldBe(ItemReasons.OutOfRange);
        rig.State(player).Currency.ShouldBe(1000);
    }

    [Fact]
    public void Buying_with_no_shop_open_is_refused()
    {
        var rig = new ShopRig();
        var player = rig.AddPlayer();
        rig.Ticks(2);
        rig.State(player).Currency = 1000;

        rig.Send(new ShopBuy(player, "item.healing_draught", 1));
        rig.Send(new ShopSell(player, 1, 1));
        rig.Ticks(2);

        rig.Of<ItemRefused>().Select(r => (r.Intent, r.Reason)).ShouldBe([(MessageTypes.ShopBuy, ItemReasons.ServiceRequired), (MessageTypes.ShopSell, ItemReasons.ServiceRequired)]);
        rig.State(player).Currency.ShouldBe(1000);
    }

    [Fact]
    public void Another_open_service_is_not_a_shop()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(1000);
        rig.Get(player).OpenService = ServiceIds.Refine;   // a refine service has no shop behind it

        rig.Send(new ShopBuy(player, "item.healing_draught", 1));
        rig.Ticks(2);

        rig.LastRefusal(MessageTypes.ShopBuy).ShouldBe(ItemReasons.ServiceRequired);
    }

    // ---- selling --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Selling_pays_the_sell_price_and_takes_the_items_in_one_change()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(10);
        var globs = rig.Give(player, "item.gel_glob", 10);

        rig.Send(new ShopSell(player, globs, 3));
        rig.Ticks(2);

        rig.State(player).Currency.ShouldBe(22);   // 10 + 3 x 4
        rig.Count(player, "item.gel_glob").ShouldBe(7);
        var change = rig.Of<InventoryChanged>().Last();
        (change.Cause, change.ItemId, change.Currency).ShouldBe((InventoryChanges.ShopSell, "item.gel_glob", 22L));

        rig.Send(new ShopSell(player, globs, 7));   // the whole rest: the entry goes
        rig.Ticks(2);
        rig.State(player).Currency.ShouldBe(50);
        rig.State(player).Find(globs).ShouldBeNull();
    }

    [Fact]
    public void Starter_gear_and_potions_can_be_sold_when_carried_and_the_price_is_the_items_own()
    {
        var rig = new ShopRig();
        var player = rig.Shopping();
        var dirk = rig.Give(player, "item.bone_dirk");
        var potions = rig.Give(player, "item.healing_draught", 3);

        rig.Send(new ShopSell(player, dirk, 1));
        rig.Send(new ShopSell(player, potions, 3));
        rig.Ticks(2);

        rig.State(player).Currency.ShouldBe(30 + 3 * 12);
    }

    [Fact]
    public void Worn_items_quest_items_and_items_with_no_sell_price_are_refused_with_reasons()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(5);
        var dirk = rig.Give(player, "item.bone_dirk");
        rig.Send(new Equip(player, dirk, null));
        rig.Ticks(2);
        var token = rig.Give(player, "item.warden_token");
        var tonic = rig.Give(player, "item.realign_tonic");
        var globs = rig.Give(player, "item.gel_glob", 2);
        rig.Events.Clear();
        var before = rig.Snapshot(player);

        rig.Send(new ShopSell(player, dirk, 1));
        rig.Send(new ShopSell(player, token, 1));
        rig.Send(new ShopSell(player, tonic, 1));
        rig.Send(new ShopSell(player, globs, 3));    // more than carried
        rig.Send(new ShopSell(player, globs, 0));
        rig.Send(new ShopSell(player, 123456, 1));   // no such entry
        rig.Ticks(2);

        rig.Of<ItemRefused>().Select(r => r.Reason).ShouldBe([ItemReasons.ItemEquipped, ItemReasons.NotSellable, ItemReasons.NotSellable,
            ItemReasons.InvalidQuantity, ItemReasons.InvalidQuantity, ItemReasons.ItemGone]);
        rig.Snapshot(player).ShouldBe(before);
        rig.Of<InventoryChanged>().ShouldBeEmpty();
    }

    // ---- overflow -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_sale_that_would_push_the_currency_past_the_number_range_is_refused_and_changes_nothing()
    {
        var rig = new ShopRig();
        var player = rig.Shopping(long.MaxValue - 5);
        var globs = rig.Give(player, "item.gel_glob", 10);   // 40 crowns for ten, but only 5 fit
        var before = rig.Snapshot(player);

        rig.Send(new ShopSell(player, globs, 10));
        rig.Ticks(2);

        rig.LastRefusal(MessageTypes.ShopSell).ShouldBe(ItemReasons.CurrencyFull);
        rig.Snapshot(player).ShouldBe(before);
        rig.State(player).Currency.ShouldBe(long.MaxValue - 5);
    }

    [Fact]
    public void A_price_times_quantity_beyond_the_number_range_never_wraps_around()
    {
        ShopRules.TryTotal(long.MaxValue / 2, 3, out var total).ShouldBeFalse();
        total.ShouldBe(0);
        ShopRules.TryTotal(ShopRules.MaxUnitPrice, 99, out var biggest).ShouldBeTrue();
        biggest.ShouldBe(99_000_000_000);
        ShopRules.TryTotal(0, 5, out _).ShouldBeFalse();
        ShopRules.TryTotal(5, 0, out _).ShouldBeFalse();
        ShopRules.TryTotal(5, -1, out _).ShouldBeFalse();
        ShopRules.TryCredit(long.MaxValue - 1, 2, out var unchanged).ShouldBeFalse();
        unchanged.ShouldBe(long.MaxValue - 1);
        ShopRules.TryCredit(long.MaxValue - 2, 2, out var full).ShouldBeTrue();
        full.ShouldBe(long.MaxValue);
        ShopRules.CanAfford(10, 10).ShouldBeTrue();
        ShopRules.CanAfford(9, 10).ShouldBeFalse();
        ShopRules.CanAfford(10, -1).ShouldBeFalse();
    }

    [Fact]
    public void A_dear_item_bought_in_bulk_is_unaffordable_not_wrapped()
    {
        var db = TestGrids.SampleContent;
        var items = db.Items.All.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var shop = new ShopDef { Id = "shop.dear", Stock = [new ShopStockDef { ItemId = "item.healing_draught", Price = ShopRules.MaxUnitPrice }] };
        var rig = new ShopRig(shops: new ShopContent([shop], items, new ShopBalanceDef { MaxBuyQuantity = 99 }, 2));
        // The merchant's dialogue names shop.meadow_goods, which this content lacks: open the dear shop directly on the player instead.
        var player = rig.AddPlayer();
        rig.Ticks(2);
        rig.State(player).Currency = 98_999_999_999;   // one short of 99 x 1,000,000,000
        var entity = rig.Get(player);
        (entity.OpenService, entity.OpenShopId, entity.OpenServiceNpc) = (ServiceIds.Shop, "shop.dear", rig.Merchant);

        rig.Send(new ShopBuy(player, "item.healing_draught", 99));
        rig.Ticks(2);

        rig.LastRefusal(MessageTypes.ShopBuy).ShouldBe(ItemReasons.CannotAfford);
        rig.State(player).Currency.ShouldBe(98_999_999_999);
    }

    // ---- content --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Shop_content_with_a_bad_price_or_item_or_limit_fails_at_boot()
    {
        var items = TestGrids.SampleContent.Items.All.ToDictionary(i => i.Id, StringComparer.Ordinal);
        ShopContent Make(ShopDef shop, int limit = 99) => new([shop], items, new ShopBalanceDef { MaxBuyQuantity = limit }, 2);

        Should.Throw<ArgumentException>(() => Make(new ShopDef { Id = "shop.a", Stock = [new ShopStockDef { ItemId = "item.nothing", Price = 5 }] }));
        Should.Throw<ArgumentException>(() => Make(new ShopDef { Id = "shop.a", Stock = [new ShopStockDef { ItemId = "item.healing_draught", Price = 0 }] }));
        Should.Throw<ArgumentException>(() => Make(new ShopDef { Id = "shop.a", Stock = [new ShopStockDef { ItemId = "item.healing_draught", Price = ShopRules.MaxUnitPrice + 1 }] }));
        Should.Throw<ArgumentException>(() => Make(new ShopDef { Id = "shop.a", Stock = [new ShopStockDef { ItemId = "item.gel_glob" }] }));   // no buy_price either
        Should.Throw<ArgumentException>(() => Make(new ShopDef { Id = "shop.a", Stock = [new ShopStockDef { ItemId = "item.healing_draught" }, new ShopStockDef { ItemId = "item.healing_draught" }] }));
        Should.Throw<ArgumentOutOfRangeException>(() => Make(new ShopDef { Id = "shop.a", Stock = [new ShopStockDef { ItemId = "item.healing_draught" }] }, limit: 0));
    }

    [Fact]
    public void The_real_merchant_is_placed_in_the_meadow_and_every_stock_row_has_a_positive_price()
    {
        var db = TestGrids.SampleContent;
        db.Grids["map.starter_meadow"].Npcs.Select(n => n.NpcId).ShouldContain("npc.merchant");
        var shop = db.Shops.Get("shop.meadow_goods");
        foreach (var row in shop.Stock)
            (row.Price ?? db.Items.Get(row.ItemId).BuyPrice).ShouldBeGreaterThan(0, row.ItemId);
        shop.Stock.Select(s => s.ItemId).ShouldContain("item.realign_tonic");
        shop.Stock.Select(s => s.ItemId).ShouldContain("item.blank_page");
        var dialogue = db.Dialogue.Get("dialogue.merchant_intro");
        dialogue.Nodes[dialogue.Start].Choices.SelectMany(c => c.Actions).OfType<OpenServiceAction>().Single().ShopId.ShouldBe("shop.meadow_goods");
    }
}
