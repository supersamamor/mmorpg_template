using Game.Content.Registry;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Inventory;
using Game.Server.World.Saving;
using Game.Server.World.Trade;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Saving;
using Game.Tests.World;

namespace Game.Tests.Trade;

/// <summary>Three saved characters in one world (T-702's rig, T-903's trade system in its pipeline), standing together at the start cell, with helpers for the trade steps.</summary>
public sealed class TradeKit : IAsyncDisposable
{
    public static readonly Guid AdaId = Guid.Parse("90300000-0000-0000-0000-000000000001");
    public static readonly Guid BoId = Guid.Parse("90300000-0000-0000-0000-000000000002");
    public static readonly Guid CyId = Guid.Parse("90300000-0000-0000-0000-000000000003");
    public const string Blade = "item.iron_blade";
    public const string Draught = "item.healing_draught";

    public required SaveRig Rig { get; init; }
    public required EntityId Ada { get; init; }
    public required EntityId Bo { get; init; }
    public required EntityId Cy { get; init; }

    public static async Task<TradeKit> StartAsync(ICharacterStore? store = null, ContentDatabase? content = null, CharacterSaveOptions? options = null)
    {
        var rig = SaveRig.Start(store ?? new MemoryCharacterStore(), options, content);
        var (ada, bo, cy) = (await rig.EnterAsync(AdaId), await rig.EnterAsync(BoId), await rig.EnterAsync(CyId));
        foreach (var id in new[] { ada, bo, cy }) rig.Bag(id).Currency = 1_000;
        return new TradeKit { Rig = rig, Ada = ada, Bo = bo, Cy = cy };
    }

    /// <summary>Content where refining needs no smith (so an offered item can be refined) and a side may offer <paramref name="maxEntries"/> entries.</summary>
    public static ContentDatabase Content(int maxEntries = 10)
    {
        var db = TestGrids.SampleContent;
        var balance = db.Balance with
        {
            Dialogue = db.Balance.Dialogue with { RefineNeedsService = false },
            Trade = db.Balance.Trade with { MaxEntries = maxEntries },
        };
        return new ContentDatabase(db.Jobs, db.Skills, db.Items, db.Monsters, db.Statuses, db.Quests, db.Npcs, db.Dialogue, db.Drops, db.Maps,
            db.ExpTables, db.Formulas, db.Elements, balance, db.Appearance, grids: db.Grids, refine: db.Refine);
    }

    public void Send(IIntent intent) => Rig.Send(intent);

    public void Tick(int n = 1) => Rig.Ticks(n);

    /// <summary><paramref name="from"/> asks, <paramref name="to"/> accepts: a trade is open.</summary>
    public void Open(EntityId from, EntityId to)
    {
        Send(new TradeAsk(from, to));
        Tick();
        Send(new TradeAnswer(to, true));
        Tick();
        Last(from).ShouldNotBeNull("the trade opened");
    }

    /// <summary>The last state of the open trade of <paramref name="who"/>, or null.</summary>
    public TradeChanged? Last(EntityId who) => Rig.Of<TradeChanged>().LastOrDefault(t => t.A.Player == who || t.B.Player == who);

    public int Version(EntityId who) => Last(who)!.Version;

    public TradeSideView SideOf(EntityId who) => Last(who) is { } t ? (t.A.Player == who ? t.A : t.B) : throw new InvalidOperationException("no trade");

    public List<TradeRefused> Refusals(EntityId who) => Rig.Of<TradeRefused>().Where(r => r.Player == who).ToList();

    public TradeClosed? Closed(EntityId who) => Rig.Of<TradeClosed>().LastOrDefault(c => c.Player == who);

    /// <summary>Both lock, then both confirm the version they saw, and the swap runs (two more ticks).</summary>
    public void LockAndConfirm(EntityId a, EntityId b)
    {
        Send(new TradeLock(a, true));
        Send(new TradeLock(b, true));
        Tick();
        var version = Version(a);
        Send(new TradeConfirm(a, version));
        Send(new TradeConfirm(b, version));
        Tick(3);
    }

    public long Put(EntityId who, string item, int quantity = 1) => Rig.Put(who, item, quantity);

    /// <summary>Every unit of every item in the three bags, on the ground and used up, and the coins: what no trade may change.</summary>
    public string Count()
    {
        var units = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in new[] { Ada, Bo, Cy })
            if (Rig.Inventories.TryGet(id, out var bag))
                foreach (var e in bag.Entries) units[e.ItemId] = units.GetValueOrDefault(e.ItemId) + e.Quantity;
        foreach (var instance in Rig.World.Instances)
            foreach (var e in instance.Entities)
                if (e.GroundItem is { } g) units[g.ItemId] = units.GetValueOrDefault(g.ItemId) + g.Quantity;
        foreach (var used in Rig.Of<ItemUsed>().Where(u => u.Consumed)) units[used.ItemId] = units.GetValueOrDefault(used.ItemId) + 1;
        // A refine uses up the material of the step it tried (one above the level the entry had), and a break takes the item.
        var levels = new Dictionary<long, int>();
        foreach (var refined in Rig.Of<ItemRefined>())
        {
            var tried = levels.GetValueOrDefault(refined.EntryId) + 1;
            if (Rig.Content.Refine.Levels.FirstOrDefault(l => l.Level == tried) is { } step)
                units[step.MaterialItemId] = units.GetValueOrDefault(step.MaterialItemId) + step.MaterialQuantity;
            if (refined.Result == RefineResults.Break) units[refined.ItemId] = units.GetValueOrDefault(refined.ItemId) + 1;
            levels[refined.EntryId] = refined.Level;
        }
        var coins = new[] { Ada, Bo, Cy }.Sum(id => Rig.Inventories.TryGet(id, out var bag) ? bag.Currency : 0);
        return string.Join(", ", units.Select(u => $"{u.Key} {u.Value}")) + $"; coins {coins}";
    }

    /// <summary>No entry id is in two bags (or on the ground and in a bag).</summary>
    public void NoIdTwice()
    {
        var ids = new List<long>();
        foreach (var id in new[] { Ada, Bo, Cy })
            if (Rig.Inventories.TryGet(id, out var bag)) ids.AddRange(bag.Entries.Select(e => e.Id));
        foreach (var instance in Rig.World.Instances)
            foreach (var e in instance.Entities)
                if (e.GroundItem is { InstanceId: > 0 } g) ids.Add(g.InstanceId);
        ids.Distinct().Count().ShouldBe(ids.Count, "an entry id twice");
    }

    public ValueTask DisposeAsync() => Rig.DisposeAsync();
}

/// <summary>
/// T-903's trade on the tick: asking and every refusal, the offers, the two steps with their version, every way a trade ends without moving anything, the swap with its links, the log
/// line, and every known dupe trick (each its own test, then random sequences of all of them counting every unit and coin).
/// </summary>
public class TradeSystemTests
{
    // ---- the whole thing ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_players_swap_items_and_currency_after_both_lock_and_confirm()
    {
        await using var k = await TradeKit.StartAsync();
        var blade = k.Put(k.Ada, TradeKit.Blade);
        var draughts = k.Rig.Bag(k.Bo).Entries.Single(e => e.ItemId == TradeKit.Draught).Id;   // three, from the start items
        var before = k.Count();
        k.Send(new TradeAsk(k.Ada, k.Bo));
        k.Tick();
        k.Rig.Of<TradeAsked>().Single().ShouldBe(new TradeAsked(k.Ada, k.Bo, k.Rig.Content.Balance.Trade.AskTimeoutMs));
        k.Rig.Of<TradeNoticed>().Single().ShouldBe(new TradeNoticed(k.Ada, TradeNotices.AskSent, k.Bo));
        k.Send(new TradeAnswer(k.Bo, true));
        k.Tick();
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Send(new TradeOfferCurrency(k.Ada, 250));
        k.Send(new TradeOfferItem(k.Bo, draughts, 2));
        k.Tick();
        var view = k.Last(k.Ada)!;
        view.A.Offer.Lines.Single().Entry.Id.ShouldBe(blade);
        view.A.Offer.Currency.ShouldBe(250);
        view.B.Offer.Lines.Single().Quantity.ShouldBe(2);
        var (adaTicket, boTicket) = (k.Rig.Tickets[k.Ada], k.Rig.Tickets[k.Bo]);

        k.LockAndConfirm(k.Ada, k.Bo);

        k.Closed(k.Ada).ShouldBe(new TradeClosed(k.Ada, k.Bo, TradeEnds.Done, false));
        k.Closed(k.Bo).ShouldBe(new TradeClosed(k.Bo, k.Ada, TradeEnds.Done, false));
        k.Rig.Bag(k.Bo).Find(blade).ShouldNotBeNull("the blade keeps its id");
        k.Rig.Bag(k.Ada).Find(blade).ShouldBeNull();
        k.Rig.Bag(k.Bo).Find(draughts)!.Quantity.ShouldBe(1);
        var got = k.Rig.Bag(k.Ada).Entries.Where(e => e.ItemId == TradeKit.Draught && e.Quantity == 2).ShouldHaveSingleItem();
        (k.Rig.Bag(k.Ada).Currency, k.Rig.Bag(k.Bo).Currency).ShouldBe((750, 1_250));
        k.Count().ShouldBe(before);
        k.NoIdTwice();
        // One traded change each, then the event for quests and logs, then the log line.
        k.Rig.Of<InventoryChanged>().Where(c => c.Cause == InventoryChanges.Traded).Select(c => c.Player).ShouldBe([k.Ada, k.Bo]);
        var traded = k.Rig.Of<ItemsTraded>().ShouldHaveSingleItem();
        (traded.CharacterA, traded.CharacterB).ShouldBe((TradeKit.AdaId, TradeKit.BoId));
        traded.FromA.ShouldBe([new TradedEntry(blade, blade, TradeKit.Blade, 1)]);
        traded.FromB.ShouldBe([new TradedEntry(draughts, got.Id, TradeKit.Draught, 2)]);
        (traded.CurrencyFromA, traded.CurrencyFromB).ShouldBe((250, 0));
        var line = k.Rig.Log.Lines.Single(l => l.Contains("[trade] "));
        line.ShouldContain($"\"a\":\"{TradeKit.AdaId}\"");
        line.ShouldContain($"\"b\":\"{TradeKit.BoId}\"");
        line.ShouldContain($"{{\"from\":{blade},\"to\":{blade},\"item\":\"item.iron_blade\",\"quantity\":1}}");
        line.ShouldContain("\"a_currency\":250");
        line.ShouldContain("\"at\":\"20");
        // T-702: each save now waits for the other's, or goes with it.
        adaTicket.Links.Keys.ShouldContain(boTicket);
        boTicket.Links.Keys.ShouldContain(adaTicket);
        k.Rig.World.Instances.Sum(i => i.Systems.OfType<TradeSystem>().Sum(t => t.TradeCount)).ShouldBe(0);
    }

    // ---- asking ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_ask_is_refused_for_each_of_its_reasons()
    {
        await using var k = await TradeKit.StartAsync();
        string Ask(EntityId from, EntityId to)
        {
            var before = k.Refusals(from).Count;
            k.Send(new TradeAsk(from, to));
            k.Tick();
            return k.Refusals(from).Count > before ? k.Refusals(from).Last().Reason : "asked";
        }
        Ask(k.Ada, k.Ada).ShouldBe(TradeReasons.InvalidTarget);
        Ask(k.Ada, new EntityId(987_654)).ShouldBe(TradeReasons.NotOnline);
        k.Rig.Get(k.Cy).Dead = true;
        Ask(k.Ada, k.Cy).ShouldBe(TradeReasons.Dead);
        k.Rig.Get(k.Cy).Dead = false;
        k.Rig.Get(k.Cy).Cell = new GridCell(k.Rig.Get(k.Ada).Cell.X + k.Rig.Content.Balance.Trade.Range + 1, k.Rig.Get(k.Ada).Cell.Y);
        Ask(k.Ada, k.Cy).ShouldBe(TradeReasons.TooFar);
        k.Rig.Get(k.Cy).Cell = k.Rig.Get(k.Ada).Cell;
        Ask(k.Ada, k.Bo).ShouldBe("asked");
        Ask(k.Ada, k.Cy).ShouldBe(TradeReasons.AskPending);
        Ask(k.Cy, k.Bo).ShouldBe(TradeReasons.Busy, "Bo has an ask to answer");
        Ask(k.Cy, k.Ada).ShouldBe(TradeReasons.Busy, "Ada waits for an answer");
        k.Send(new TradeAnswer(k.Bo, true));
        k.Tick();
        Ask(k.Cy, k.Bo).ShouldBe(TradeReasons.Busy, "Bo is in a trade");
        Ask(k.Bo, k.Cy).ShouldBe(TradeReasons.Busy, "and cannot ask anyone");
        k.Send(new TradeAnswer(k.Cy, true));
        k.Tick();
        k.Refusals(k.Cy).Last().Reason.ShouldBe(TradeReasons.NoAsk);
        k.Rig.Get(k.Ada).Dead = true;
        Ask(k.Ada, k.Cy).ShouldBe(TradeReasons.Dead, "a dead asker is told why");
    }

    [Fact]
    public async Task A_saved_character_and_a_development_login_cannot_trade()
    {
        await using var k = await TradeKit.StartAsync();
        var start = new NewCharacters(k.Rig.Content);
        var dev = k.Rig.World.Spawn(EntityKind.Player, start.MapId, start.Cell, start.Facing, e => e.Stats = start.CreateStats(), null);
        k.Tick(2);
        k.Send(new TradeAsk(k.Ada, dev));
        k.Tick();
        k.Refusals(k.Ada).Single().Reason.ShouldBe(TradeReasons.CannotTrade);
    }

    [Fact]
    public async Task An_unanswered_ask_expires_for_both_and_a_declined_one_tells_the_asker()
    {
        await using var k = await TradeKit.StartAsync();
        k.Send(new TradeAsk(k.Ada, k.Bo));
        k.Tick(k.Rig.Content.Balance.Trade.AskTimeoutMs / TickRate.IntervalMs + 1);
        k.Rig.Of<TradeNoticed>().Where(n => n.Notice == TradeNotices.Expired).Select(n => n.Player).ShouldBe([k.Ada, k.Bo], ignoreOrder: true);
        k.Send(new TradeAnswer(k.Bo, true));
        k.Tick();
        k.Refusals(k.Bo).Single().Reason.ShouldBe(TradeReasons.NoAsk);

        k.Send(new TradeAsk(k.Ada, k.Bo));
        k.Tick();
        k.Send(new TradeAnswer(k.Bo, false));
        k.Tick();
        k.Rig.Of<TradeNoticed>().Last().ShouldBe(new TradeNoticed(k.Ada, TradeNotices.Declined, k.Bo));
        k.Last(k.Ada).ShouldBeNull("no trade opened");

        k.Send(new TradeAsk(k.Ada, k.Bo));
        k.Tick();
        k.Send(new TradeCancel(k.Ada, TradeEnds.Cancelled));   // the asker withdraws
        k.Tick();
        k.Rig.Of<TradeNoticed>().Last().ShouldBe(new TradeNoticed(k.Bo, TradeNotices.Expired, k.Ada));
    }

    // ---- offers ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Worn_untradeable_missing_too_many_and_too_much_are_refused()
    {
        await using var k = await TradeKit.StartAsync(content: TradeKit.Content(maxEntries: 2));
        k.Send(new TradeOfferItem(k.Ada, 1, 1));
        k.Tick();
        k.Refusals(k.Ada).Last().Reason.ShouldBe(TradeReasons.NotTrading);
        k.Open(k.Ada, k.Bo);
        var worn = k.Rig.Bag(k.Ada).Entries.Single(e => e.Slot is not null).Id;
        var token = k.Put(k.Ada, "item.warden_token");
        var draughts = k.Rig.Bag(k.Ada).Entries.Single(e => e.ItemId == TradeKit.Draught).Id;
        var (b1, b2) = (k.Put(k.Ada, TradeKit.Blade), k.Put(k.Ada, TradeKit.Blade));
        var version = k.Version(k.Ada);
        foreach (var (intent, reason) in new (IIntent, string)[]
                 {
                     (new TradeOfferItem(k.Ada, worn, 1), TradeReasons.ItemEquipped),
                     (new TradeOfferItem(k.Ada, token, 1), TradeReasons.NotTradeable),
                     (new TradeOfferItem(k.Ada, 424_242, 1), TradeReasons.ItemGone),
                     (new TradeOfferItem(k.Ada, draughts, 4), TradeReasons.InvalidQuantity),
                     (new TradeOfferItem(k.Ada, draughts, -1), TradeReasons.InvalidQuantity),
                     (new TradeOfferCurrency(k.Ada, 1_001), TradeReasons.InvalidQuantity),
                     (new TradeOfferCurrency(k.Ada, -1), TradeReasons.InvalidQuantity),
                 })
        {
            k.Send(intent);
            k.Tick();
            k.Refusals(k.Ada).Last().Reason.ShouldBe(reason, intent.ToString());
        }
        k.Version(k.Ada).ShouldBe(version, "nothing changed");
        k.Send(new TradeOfferItem(k.Ada, b1, 1));
        k.Send(new TradeOfferItem(k.Ada, b2, 1));
        k.Send(new TradeOfferItem(k.Ada, draughts, 1));
        k.Tick();
        k.Refusals(k.Ada).Last().Reason.ShouldBe(TradeReasons.OfferFull);
        k.SideOf(k.Ada).Offer.Lines.Count.ShouldBe(2);
        k.Send(new TradeOfferItem(k.Ada, b1, 0));   // taken out
        k.Send(new TradeOfferItem(k.Ada, draughts, 3));
        k.Tick();
        k.SideOf(k.Ada).Offer.Lines.Select(l => (l.Entry.Id, l.Quantity)).ShouldBe([(b2, 1), (draughts, 3)]);
    }

    [Fact]
    public async Task The_same_entry_offered_twice_is_one_line_with_the_last_quantity()
    {
        await using var k = await TradeKit.StartAsync();
        k.Open(k.Ada, k.Bo);
        var draughts = k.Rig.Bag(k.Ada).Entries.Single(e => e.ItemId == TradeKit.Draught).Id;
        var before = k.Count();
        k.Send(new TradeOfferItem(k.Ada, draughts, 2));
        k.Send(new TradeOfferItem(k.Ada, draughts, 3));
        k.Send(new TradeOfferItem(k.Ada, draughts, 1));
        k.Tick();
        k.SideOf(k.Ada).Offer.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(1);
        k.LockAndConfirm(k.Ada, k.Bo);
        k.Rig.Bag(k.Ada).Find(draughts)!.Quantity.ShouldBe(2);
        k.Rig.Bag(k.Bo).Entries.Where(e => e.ItemId == TradeKit.Draught).Sum(e => e.Quantity).ShouldBe(4);
        k.Count().ShouldBe(before);
    }

    // ---- the two steps --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_confirm_needs_both_locks_and_the_version_the_trade_is_at()
    {
        await using var k = await TradeKit.StartAsync();
        k.Open(k.Ada, k.Bo);
        var blade = k.Put(k.Ada, TradeKit.Blade);
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Send(new TradeLock(k.Ada, true));
        k.Tick();
        k.Send(new TradeConfirm(k.Ada, k.Version(k.Ada)));
        k.Tick();
        k.Refusals(k.Ada).Last().Reason.ShouldBe(TradeReasons.NotLocked);
        k.Send(new TradeLock(k.Bo, true));
        k.Tick();
        k.Send(new TradeConfirm(k.Ada, k.Version(k.Ada) - 1));
        k.Tick();
        k.Refusals(k.Ada).Last().Reason.ShouldBe(TradeReasons.Stale);
        k.SideOf(k.Ada).Confirmed.ShouldBeFalse();
    }

    [Fact]
    public async Task Changing_an_offer_after_the_other_side_confirmed_unlocks_both_and_voids_that_confirm()
    {
        await using var k = await TradeKit.StartAsync();
        k.Open(k.Ada, k.Bo);
        var blade = k.Put(k.Ada, TradeKit.Blade);
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Send(new TradeOfferCurrency(k.Bo, 500));
        k.Send(new TradeLock(k.Ada, true));
        k.Send(new TradeLock(k.Bo, true));
        k.Tick();
        var seen = k.Version(k.Ada);
        k.Send(new TradeConfirm(k.Ada, seen));   // Ada agrees to blade for 500
        k.Tick();
        k.Send(new TradeOfferCurrency(k.Bo, 1));   // Bo lowers the price at the last moment
        k.Tick();
        k.SideOf(k.Ada).Locked.ShouldBeFalse();
        k.SideOf(k.Ada).Confirmed.ShouldBeFalse("Ada's confirm was for the old offers");
        k.Send(new TradeLock(k.Ada, true));
        k.Send(new TradeLock(k.Bo, true));
        k.Tick();
        k.Send(new TradeConfirm(k.Bo, k.Version(k.Bo)));
        k.Send(new TradeConfirm(k.Ada, seen));   // Ada's client still shows the old version
        k.Tick(3);
        k.Refusals(k.Ada).Last().Reason.ShouldBe(TradeReasons.Stale);
        k.Closed(k.Ada).ShouldBeNull("no swap without both confirming the same offers");
        k.Rig.Bag(k.Ada).Find(blade).ShouldNotBeNull();
        // An unlock also voids a confirm.
        k.Send(new TradeLock(k.Ada, false));
        k.Tick();
        (k.SideOf(k.Bo).Confirmed, k.SideOf(k.Bo).Locked).ShouldBe((false, false));
    }

    [Fact]
    public async Task Once_both_confirmed_nothing_changes_and_a_cancel_is_refused_until_the_swap()
    {
        await using var k = await TradeKit.StartAsync();
        k.Open(k.Ada, k.Bo);
        var blade = k.Put(k.Ada, TradeKit.Blade);
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Send(new TradeLock(k.Ada, true));
        k.Send(new TradeLock(k.Bo, true));
        k.Tick();
        k.Send(new TradeConfirm(k.Ada, k.Version(k.Ada)));
        k.Send(new TradeConfirm(k.Bo, k.Version(k.Ada)));
        k.Tick();
        k.Send(new TradeCancel(k.Ada, TradeEnds.Cancelled));
        k.Send(new TradeOfferCurrency(k.Bo, 1));
        k.Tick(2);
        k.Refusals(k.Ada).Last().Reason.ShouldBe(TradeReasons.Swapping);
        k.Refusals(k.Bo).Last().Reason.ShouldBe(TradeReasons.Swapping);
        k.Closed(k.Ada)!.Reason.ShouldBe(TradeEnds.Done);
        k.Rig.Bag(k.Bo).Find(blade).ShouldNotBeNull();
    }

    // ---- every way it ends without moving anything ----------------------------------------------------------------------------------------

    public static TheoryData<string> Ends => ["cancel", "far", "dead", "warp", "disconnect", "leave", "partner cancel"];

    [Theory]
    [MemberData(nameof(Ends))]
    public async Task A_trade_that_ends_before_the_swap_moves_nothing(string how)
    {
        await using var k = await TradeKit.StartAsync();
        k.Open(k.Ada, k.Bo);
        var blade = k.Put(k.Ada, TradeKit.Blade);
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Send(new TradeOfferCurrency(k.Bo, 300));
        k.Send(new TradeLock(k.Ada, true));
        k.Send(new TradeLock(k.Bo, true));
        k.Tick();
        k.Send(new TradeConfirm(k.Ada, k.Version(k.Ada)));
        k.Tick();
        var before = k.Count();
        var (reason, own) = how switch
        {
            "cancel" => (TradeEnds.Cancelled, true),
            "partner cancel" => (TradeEnds.Cancelled, false),
            "far" => (TradeEnds.TooFar, false),
            "dead" => (TradeEnds.Dead, true),
            "warp" => (TradeEnds.LeftMap, true),
            "disconnect" => (TradeEnds.Disconnected, true),
            _ => (TradeEnds.Left, true),
        };
        switch (how)
        {
            case "cancel": k.Send(new TradeCancel(k.Ada, TradeEnds.Cancelled)); break;
            case "partner cancel": k.Send(new TradeCancel(k.Bo, TradeEnds.Cancelled)); break;
            case "far": k.Rig.Get(k.Ada).Cell = new GridCell(k.Rig.Get(k.Bo).Cell.X + k.Rig.Content.Balance.Trade.Range + 1, k.Rig.Get(k.Bo).Cell.Y); break;
            case "dead": k.Rig.Get(k.Ada).Dead = true; break;
            case "warp": k.Rig.World.Warp(k.Ada, "map.thistle_hollow", TestGrids.SampleContent.GetGrid("map.starter_meadow").Warps.First(w => w.TargetMapId == "map.thistle_hollow").TargetCell); break;
            case "disconnect": k.Send(new TradeCancel(k.Ada, TradeEnds.Disconnected)); break;
            case "leave": k.Rig.World.Despawn(k.Ada); break;
        }
        k.Rig.TickUntil(() => k.Closed(k.Bo) is not null, "the end", 60);
        k.Send(new TradeConfirm(k.Bo, k.Version(k.Bo)));   // too late
        k.Tick(3);
        k.Closed(k.Bo).ShouldBe(new TradeClosed(k.Bo, k.Ada, reason, !own && how != "far"));
        if (how != "leave") k.Closed(k.Ada).ShouldBe(new TradeClosed(k.Ada, k.Bo, reason, own));
        k.Rig.Of<TradeSwapRequested>().ShouldBeEmpty();
        if (how != "leave") k.Rig.Bag(k.Ada).Find(blade).ShouldNotBeNull();
        k.Rig.Bag(k.Bo).Find(blade).ShouldBeNull();
        k.Rig.Bag(k.Bo).Currency.ShouldBe(1_000);
        if (how != "leave") k.Count().ShouldBe(before);
    }

    // ---- the inventory changing under an offer (the dupe tricks) ---------------------------------------------------------------------------

    public static TheoryData<string> Tricks => ["drop", "use", "equip", "refine", "drop part", "sell"];

    [Theory]
    [MemberData(nameof(Tricks))]
    public async Task Changing_an_offered_entry_unlocks_both_and_the_offer_follows_what_is_carried(string trick)
    {
        await using var k = await TradeKit.StartAsync(content: TradeKit.Content());
        k.Open(k.Ada, k.Bo);
        var blade = k.Put(k.Ada, TradeKit.Blade);
        k.Put(k.Ada, "item.temper_shard");
        var dirk = k.Put(k.Ada, "item.bone_dirk");   // a wanderer can wear it: equipping it takes the worn one off
        var draughts = k.Rig.Bag(k.Ada).Entries.Single(e => e.ItemId == TradeKit.Draught).Id;
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Send(new TradeOfferItem(k.Ada, draughts, 3));
        k.Send(new TradeOfferItem(k.Ada, dirk, 1));
        k.Send(new TradeLock(k.Ada, true));
        k.Send(new TradeLock(k.Bo, true));
        k.Tick();
        var seen = k.Version(k.Ada);
        k.Send(new TradeConfirm(k.Bo, seen));
        k.Tick();
        var before = k.Count();
        IIntent change = trick switch
        {
            "drop" => new DropItem(k.Ada, blade, 1),
            "drop part" => new DropItem(k.Ada, draughts, 1),
            "use" => new UseItem(k.Ada, draughts),
            "equip" => new Equip(k.Ada, dirk, null),
            "refine" => new RefineItem(k.Ada, blade),
            _ => new ShopSell(k.Ada, blade, 1),   // no shop is open: refused, and nothing changes
        };
        k.Send(change);
        k.Tick(2);
        var side = k.SideOf(k.Ada);
        if (trick == "sell")
        {
            k.Version(k.Ada).ShouldBe(seen, "a refused sale changes nothing, and a confirm keeps the version");
            side.Offer.Lines.Count.ShouldBe(3);
        }
        else
        {
            (side.Locked, k.SideOf(k.Bo).Locked, k.SideOf(k.Bo).Confirmed).ShouldBe((false, false, false), trick);
            k.Version(k.Ada).ShouldBeGreaterThan(seen);
            var bladeLine = side.Offer.Lines.FirstOrDefault(l => l.Entry.Id == blade);
            var draughtLine = side.Offer.Lines.FirstOrDefault(l => l.Entry.Id == draughts);
            switch (trick)
            {
                case "drop": bladeLine.ShouldBeNull(); break;
                case "equip": side.Offer.Lines.ShouldNotContain(l => l.Entry.Id == dirk, "worn now"); break;
                case "refine": bladeLine!.Entry.Refine.ShouldBe(1, "the offer shows the blade as it is now"); break;
                case "use" or "drop part": draughtLine!.Quantity.ShouldBe(2); break;
            }
        }
        // Ada confirms what she sees now; Bo's old confirm does not count.
        k.Send(new TradeConfirm(k.Ada, k.Version(k.Ada)));
        k.Send(new TradeConfirm(k.Bo, seen));
        k.Tick(3);
        k.Count().ShouldBe(before);
        k.NoIdTwice();
        if (trick != "sell") k.Closed(k.Ada).ShouldBeNull("nothing was swapped");
    }

    [Fact]
    public async Task An_offered_item_dropped_in_the_tick_of_the_last_confirm_ends_the_trade_with_nothing_moved()
    {
        await using var k = await TradeKit.StartAsync();
        k.Open(k.Ada, k.Bo);
        var blade = k.Put(k.Ada, TradeKit.Blade);
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Send(new TradeOfferCurrency(k.Bo, 400));
        k.Send(new TradeLock(k.Ada, true));
        k.Send(new TradeLock(k.Bo, true));
        k.Tick();
        k.Send(new TradeConfirm(k.Ada, k.Version(k.Ada)));
        k.Tick();
        var before = k.Count();
        // The inventory runs before the trade system: the drop lands, then Bo's confirm freezes offers that name the blade.
        k.Send(new DropItem(k.Ada, blade, 1));
        k.Send(new TradeConfirm(k.Bo, k.Version(k.Bo)));
        k.Tick(4);
        k.Closed(k.Bo).ShouldBe(new TradeClosed(k.Bo, k.Ada, TradeEnds.OfferChanged, false));
        k.Closed(k.Ada)!.Own.ShouldBeTrue();
        k.Rig.Bag(k.Bo).Find(blade).ShouldBeNull();
        k.Rig.Bag(k.Bo).Currency.ShouldBe(1_000);
        k.Count().ShouldBe(before);
        k.NoIdTwice();
    }

    [Fact]
    public async Task A_full_or_overloaded_receiver_ends_the_trade_at_the_swap_and_keeps_what_it_had()
    {
        await using var k = await TradeKit.StartAsync();
        k.Open(k.Ada, k.Bo);
        var blade = k.Put(k.Ada, TradeKit.Blade);
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Tick();
        k.Send(new TradeLock(k.Ada, true));
        k.Send(new TradeLock(k.Bo, true));
        k.Tick();
        var version = k.Version(k.Ada);
        k.Send(new TradeConfirm(k.Ada, version));
        k.Send(new TradeConfirm(k.Bo, version));
        k.Tick();
        var bag = k.Rig.Bag(k.Bo);
        while (bag.Entries.Count < k.Rig.Content.Balance.Inventory.MaxEntries) k.Rig.Put(k.Bo, "item.wooden_buckler");
        var before = k.Count();
        k.Tick(2);
        k.Closed(k.Bo).ShouldBe(new TradeClosed(k.Bo, k.Ada, TradeEnds.InventoryFull, true));
        k.Rig.Bag(k.Ada).Find(blade).ShouldNotBeNull();
        k.Count().ShouldBe(before);
    }

    [Fact]
    public async Task Two_trades_at_once_with_a_third_player_cannot_be_and_one_after_the_other_move_each_unit_once()
    {
        await using var k = await TradeKit.StartAsync();
        var blade = k.Put(k.Ada, TradeKit.Blade);
        k.Open(k.Ada, k.Bo);
        k.Send(new TradeAsk(k.Cy, k.Ada));
        k.Send(new TradeAsk(k.Ada, k.Cy));
        k.Tick();
        k.Refusals(k.Cy).Single().Reason.ShouldBe(TradeReasons.Busy);
        k.Refusals(k.Ada).Single().Reason.ShouldBe(TradeReasons.Busy);
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Tick();
        k.LockAndConfirm(k.Ada, k.Bo);
        k.Rig.Bag(k.Bo).Find(blade).ShouldNotBeNull();
        // Ada offers the blade she no longer has to Cy: refused, and Bo passes it on.
        k.Open(k.Ada, k.Cy);
        k.Send(new TradeOfferItem(k.Ada, blade, 1));
        k.Tick();
        k.Refusals(k.Ada).Last().Reason.ShouldBe(TradeReasons.ItemGone);
        k.Send(new TradeCancel(k.Cy, TradeEnds.Cancelled));
        k.Tick();
        k.Open(k.Bo, k.Cy);
        k.Send(new TradeOfferItem(k.Bo, blade, 1));
        k.Tick();
        k.LockAndConfirm(k.Bo, k.Cy);
        k.Rig.Bag(k.Cy).Find(blade).ShouldNotBeNull();
        k.NoIdTwice();
    }

    /// <summary>
    /// Every trick at random, many times: asks and answers among three, offers of any entry and quantity, currency, locks, unlocks, confirms of the right and of an old version, cancels,
    /// disconnects, drops, uses, equips, refines and walks. Every unit and every coin is still somewhere (in a bag, on the ground, used up), and no entry id is in two places.
    /// </summary>
    [Fact]
    public async Task Random_sequences_of_every_trick_never_create_or_lose_a_unit_or_a_coin()
    {
        await using var k = await TradeKit.StartAsync(content: TradeKit.Content());
        var players = new[] { k.Ada, k.Bo, k.Cy };
        foreach (var p in players)
        {
            k.Put(p, TradeKit.Blade);
            k.Put(p, TradeKit.Draught, 20);
            k.Put(p, "item.temper_shard", 5);
        }
        var start = k.Rig.Get(k.Ada).Cell;
        var random = new Random(9030);
        var before = k.Count();
        var swaps = 0;
        for (var step = 0; step < 1_500; step++)
        {
            var who = players[random.Next(3)];
            var other = players[random.Next(3)];
            var entries = k.Rig.Bag(who).Entries;
            var entry = entries.Count == 0 ? 1 : entries[random.Next(entries.Count)].Id;
            var version = k.Last(who)?.Version ?? 0;
            IIntent intent = random.Next(16) switch
            {
                0 => new TradeAsk(who, other),
                1 => new TradeAnswer(who, random.Next(4) > 0),
                2 or 3 => new TradeOfferItem(who, entry, random.Next(0, 4)),
                4 => new TradeOfferCurrency(who, random.Next(0, 1_200)),
                5 or 6 => new TradeLock(who, random.Next(5) > 0),
                7 or 8 => new TradeConfirm(who, random.Next(4) == 0 ? version - 1 : version),
                9 => new TradeCancel(who, random.Next(2) == 0 ? TradeEnds.Cancelled : TradeEnds.Disconnected),
                10 => new DropItem(who, entry, 1),
                11 => new UseItem(who, entry),
                12 => new Equip(who, entry, null),
                13 => new RefineItem(who, entry),
                14 => new MoveTo(who, new GridCell(start.X + random.Next(-6, 7), start.Y + random.Next(-6, 7))),
                _ => new PickUp(who, k.Rig.On(who).Entities.FirstOrDefault(e => e.GroundItem is not null)?.Id ?? who),
            };
            k.Rig.World.TryEnqueue(intent);
            if (intent is TradeAsk && random.Next(3) > 0)
            {
                k.Tick();
                k.Send(new TradeAnswer(other, random.Next(5) > 0));
            }
            if (random.Next(3) == 0) k.Tick(random.Next(1, 4));
            if (random.Next(6) == 0 && k.Last(who) is { } open && k.Rig.Events.IndexOf(open) > (k.Closed(who) is { } end ? k.Rig.Events.IndexOf(end) : -1))
            {
                // Both sides agree, as players finishing a trade do (the other tricks above may still get in between).
                var partner = open.A.Player == who ? open.B.Player : open.A.Player;
                k.Send(new TradeLock(who, true));
                k.Send(new TradeLock(partner, true));
                k.Tick();
                if (random.Next(4) == 0) k.Rig.World.TryEnqueue(new DropItem(who, entry, 1));
                var now = k.Last(who)!.Version;
                k.Send(new TradeConfirm(who, now));
                k.Send(new TradeConfirm(partner, now));
                k.Tick(random.Next(1, 4));
            }
            if (step % 50 == 0)
            {
                k.Tick(3);
                k.Count().ShouldBe(before, $"step {step}");
                k.NoIdTwice();
            }
        }
        k.Tick(5);
        swaps = k.Rig.Of<ItemsTraded>().Count;
        k.Count().ShouldBe(before);
        k.NoIdTwice();
        swaps.ShouldBeGreaterThan(0, "some trades went through");
        var ends = string.Join(", ", k.Rig.Of<TradeClosed>().GroupBy(c => c.Reason).Select(g => $"{g.Key} {g.Count()}"));
        k.Rig.Of<TradeClosed>().Select(c => c.Reason).Distinct().Count().ShouldBeGreaterThan(3, "and many ended in other ways: " + ends);
    }
}
