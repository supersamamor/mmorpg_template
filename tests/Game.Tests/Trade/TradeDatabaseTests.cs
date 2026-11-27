using Game.Server.Accounts;
using Game.Server.Persistence;
using Game.Server.World;
using Game.Server.World.Saving;
using Game.Server.World.Trade;
using Game.Shared.Accounts;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Network;
using Game.Tests.Saving;

namespace Game.Tests.Trade;

/// <summary>
/// T-903 against a real PostgreSQL: the two saves after a swap land together or not at all (T-702's links, both ways, put them in one transaction), whatever step a crash hits; a save already
/// on its way when the swap happens; and a server stop in the middle of a trade. Afterwards every item and coin exists once, on one side of the swap.
/// </summary>
[Trait("Category", "Database")]
public sealed class TradeDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private GameDatabase Database => fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set.");

    private PostgresCharacterStore Store => new(Database);

    private async Task<Guid> CharacterAsync()
    {
        var accounts = new PostgresAccountStore(Database);
        var account = Guid.NewGuid();
        (await accounts.TryCreateAccountAsync(account, "u" + Guid.NewGuid().ToString("N")[..16], "hash", Now, default)).ShouldBeTrue();
        var name = new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 12));
        var (outcome, character) = await accounts.CreateCharacterAsync(account, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.wanderer", 3, null, Now, default);
        outcome.ShouldBe(CreateCharacterOutcome.Created);
        return character!.Id;
    }

    private sealed class Killed : Exception;

    /// <summary>Two saved characters next to each other, entered and saved once, with a blade and 500 coins on Ada's side and 300 on Bo's.</summary>
    private async Task<(SaveRig Rig, EntityId Ada, EntityId Bo, Guid AdaId, Guid BoId, long Blade)> TwoAsync(ICharacterStore store, CharacterSaveOptions? options = null)
    {
        var (adaId, boId) = (await CharacterAsync(), await CharacterAsync());
        var rig = SaveRig.Start(store, options);
        var (ada, bo) = (await rig.EnterAsync(adaId), await rig.EnterAsync(boId));
        var blade = rig.Put(ada, TradeKit.Blade);
        (rig.Bag(ada).Currency, rig.Bag(bo).Currency) = (500, 300);
        await rig.SaveAsync(ada);
        await rig.SaveAsync(bo);
        await rig.SettledAsync();
        return (rig, ada, bo, adaId, boId, blade);
    }

    /// <summary>Opens a trade, offers the blade and 200 of Ada's coins for 2 of Bo's draughts, locks and confirms: the swap runs in the last tick.</summary>
    private static void Trade(SaveRig rig, EntityId ada, EntityId bo, long blade)
    {
        rig.Send(new TradeAsk(ada, bo));
        rig.Ticks();
        rig.Send(new TradeAnswer(bo, true));
        rig.Ticks();
        var draughts = rig.Bag(bo).Entries.First(e => e.ItemId == TradeKit.Draught).Id;
        rig.Send(new TradeOfferItem(ada, blade, 1));
        rig.Send(new TradeOfferCurrency(ada, 200));
        rig.Send(new TradeOfferItem(bo, draughts, 2));
        rig.Send(new TradeLock(ada, true));
        rig.Send(new TradeLock(bo, true));
        rig.Ticks();
        var version = rig.Of<TradeChanged>().Last().Version;
        rig.Send(new TradeConfirm(ada, version));
        rig.Send(new TradeConfirm(bo, version));
        rig.Ticks(2);
        rig.Of<ItemsTraded>().ShouldHaveSingleItem("the swap ran");
    }

    /// <summary>What the next server finds: both from the same side of the swap, each unit and coin once.</summary>
    private async Task<bool> NextServerAsync(long run, Guid adaId, Guid boId, long blade)
    {
        await Store.EndRunAsync(run, default);
        var next = await Store.StartRunAsync(default);
        var (a, b) = ((await Store.ClaimAsync(adaId, next, default)).State!, (await Store.ClaimAsync(boId, next, default)).State!);
        var all = a.Items.Concat(b.Items).ToList();
        all.Select(i => i.Id).Distinct().Count().ShouldBe(all.Count, "no item twice");
        all.Count(i => i.Id == blade).ShouldBe(1, "the blade exists once");
        all.Where(i => i.ItemId == TradeKit.Draught).Sum(i => i.Quantity).ShouldBe(6, "three each from the start");
        (a.Currency + b.Currency).ShouldBe(800);
        var after = b.Items.Any(i => i.Id == blade);
        (a.Currency, b.Currency).ShouldBe(after ? (300L, 500L) : (500L, 300L), "the coins went with the blade, or neither did");
        a.Items.Where(i => i.ItemId == TradeKit.Draught).Sum(i => i.Quantity).ShouldBe(after ? 5 : 3);
        await Store.EndRunAsync(next, default);
        return after;
    }

    public static TheoryData<string> Steps => [.. PostgresCharacterStore.SaveSteps];

    /// <summary>The swap's saves die at <paramref name="step"/>, and the process with them: the next server finds both characters before the swap, or both after it.</summary>
    [DatabaseTheory]
    [MemberData(nameof(Steps))]
    public async Task A_crash_at_any_step_of_the_saves_after_a_swap_leaves_both_on_one_side(string step)
    {
        var dead = false;
        var armed = false;
        var store = new PostgresCharacterStore(Database)
        {
            OnStep = async at =>
            {
                await Task.Yield();
                if (Volatile.Read(ref dead)) throw new StoreUnavailableException("test: the process is gone");
                if (at == step && Volatile.Read(ref armed))
                {
                    Volatile.Write(ref dead, true);
                    throw new Killed();
                }
            },
        };
        var (rig, ada, bo, adaId, boId, blade) = await TwoAsync(store);
        Volatile.Write(ref armed, true);
        Trade(rig, ada, bo, blade);
        rig.Tickets[ada].Links.Keys.ShouldContain(rig.Tickets[bo]);
        rig.Tickets[bo].Links.Keys.ShouldContain(rig.Tickets[ada]);
        rig.Tickets[ada].SaveSoon = rig.Tickets[bo].SaveSoon = true;
        rig.Ticks(2);
        await Poll.UntilAsync(() => Volatile.Read(ref dead) || (rig.Tickets[ada].LandedVersion >= rig.Tickets[ada].TakenVersion && rig.Tickets[bo].LandedVersion >= rig.Tickets[bo].TakenVersion),
            d => d, "the crash or the saves");
        Volatile.Write(ref dead, true);
        await rig.StopAsync();
        var after = await NextServerAsync(rig.Saves.Run, adaId, boId, blade);
        after.ShouldBe(step == "committed", $"only a commit makes it so ({step})");
    }

    /// <summary>Saves are taken every 30 ms and some are slow, so one is on its way while the swap happens; when everything has landed both are after the swap.</summary>
    [DatabaseFact]
    public async Task A_save_on_its_way_when_the_swap_happens_does_not_split_it()
    {
        var random = new Random(9031);
        var store = new PostgresCharacterStore(Database)
        {
            OnStep = async at =>
            {
                int wait;
                lock (random) wait = random.Next(0, 4) == 0 ? random.Next(5, 40) : 0;
                if (wait > 0) await Task.Delay(wait);
            },
        };
        for (var round = 0; round < 5; round++)
        {
            var (rig, ada, bo, adaId, boId, blade) = await TwoAsync(store, SaveRig.Fast with { IntervalMs = 30 });
            rig.Tickets[ada].SaveSoon = true;   // a copy of Ada before the swap is taken now and written slowly
            rig.Ticks();
            Trade(rig, ada, bo, blade);
            for (var i = 0; i < 10; i++)
            {
                rig.Ticks();
                await Task.Delay(5);
            }
            rig.Tickets[ada].SaveSoon = rig.Tickets[bo].SaveSoon = true;
            rig.Ticks(2);
            await rig.SettledAsync();
            await rig.StopAsync();
            (await NextServerAsync(rig.Saves.Run, adaId, boId, blade)).ShouldBeTrue($"round {round}: everything landed");
        }
    }

    /// <summary>The server stops while a trade is open (both locked, one confirmed): both leave, are saved as they were, and nothing moved.</summary>
    [DatabaseFact]
    public async Task A_server_stop_in_the_middle_of_a_trade_moves_nothing()
    {
        var (rig, ada, bo, adaId, boId, blade) = await TwoAsync(Store);
        rig.Send(new TradeAsk(ada, bo));
        rig.Ticks();
        rig.Send(new TradeAnswer(bo, true));
        rig.Ticks();
        rig.Send(new TradeOfferItem(ada, blade, 1));
        rig.Send(new TradeOfferCurrency(bo, 300));
        rig.Send(new TradeLock(ada, true));
        rig.Send(new TradeLock(bo, true));
        rig.Ticks();
        rig.Send(new TradeConfirm(ada, rig.Of<TradeChanged>().Last().Version));
        rig.Ticks();
        // What a shutdown does: every character leaves the world and its last save lands.
        rig.World.Despawn(ada);
        rig.World.Despawn(bo);
        rig.TickUntil(() => rig.World.LocationOf(ada) is null && rig.World.LocationOf(bo) is null, "leaving");
        rig.Ticks(2);
        await rig.SettledAsync();
        rig.Of<TradeClosed>().Select(c => c.Reason).Distinct().ShouldBe([TradeEnds.Left]);
        rig.Of<TradeSwapRequested>().ShouldBeEmpty();
        await rig.StopAsync();
        (await NextServerAsync(rig.Saves.Run, adaId, boId, blade)).ShouldBeFalse("nothing moved");
    }
}
