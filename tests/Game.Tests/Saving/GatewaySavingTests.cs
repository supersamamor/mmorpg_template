using Game.Server.Accounts;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Saving;
using Game.Shared.Protocol;
using Game.Shared.Time;
using Game.Shared.World;
using Game.Tests.Accounts;
using Game.Tests.Network;

namespace Game.Tests.Saving;

/// <summary>T-702 on the real server stack (sockets, instances on their own threads): what a player sees of loading and saving, with the store in memory.</summary>
[Collection(TimingCollection.Name)]
public class GatewaySavingTests
{
    private sealed class Setup : IAsyncDisposable
    {
        public required GatewayRig Rig { get; init; }
        public required MemoryCharacterStore Characters { get; init; }
        public required MemoryAccountStore Accounts { get; init; }
        public required string Ada { get; init; }
        public required string Lyra { get; init; }
        public required string Mira { get; init; }

        public static async Task<Setup> StartAsync(CharacterSaveOptions? saves = null, Func<GatewayOptions, GatewayOptions>? configure = null)
        {
            var characters = new MemoryCharacterStore();
            var accounts = new MemoryAccountStore();
            var rig = await GatewayRig.StartAsync(configure, accounts: new AccountRigOptions(accounts, new FakeHasher(), new AccountLimits(), new FakeTime())
            {
                Characters = characters, SaveOptions = saves ?? SaveRig.Fast with { LoginWaitMs = 2_000, ShutdownWaitMs = 5_000 },
            });
            var ada = (await rig.Accounts!.RegisterAsync("ada", ServiceRig.Password, "a", default)).Value!.Token;
            return new Setup
            {
                Rig = rig, Characters = characters, Accounts = accounts, Ada = ada,
                Lyra = (await rig.Accounts.CreateAsync(ada, "Lyra", default)).Value!.Id,
                Mira = (await rig.Accounts.CreateAsync(ada, "Mira", default)).Value!.Id,
            };
        }

        public Guid LyraId => Guid.Parse(Lyra);

        public async Task<TestClient> JoinAsync(string character)
        {
            var client = await Rig.PlayAsync(Ada, character);
            await client.WaitForAsync<SnapshotMessage>();
            return client;
        }

        public async Task<string?> RefusedAsync(string character) => await (await Rig.PlayAsync(Ada, character)).WaitForByeAsync();

        /// <summary>Changes something only a saved state can bring back.</summary>
        public void Earn(TestClient client, long currency)
        {
            Rig.Inventories.TryGet(new EntityId(client.Id), out var bag).ShouldBeTrue();
            bag.Currency = currency;
        }

        public ValueTask DisposeAsync()
        {
            Characters.Away = false;
            Characters.SaveGate?.TrySetResult();
            Characters.ClaimGate?.TrySetResult();
            return Rig.DisposeAsync();
        }
    }

    private static long Currency(TestClient client) => client.Messages.OfType<InventoryMessage>().First().Currency;

    [Fact]
    public async Task A_logout_saves_and_the_next_login_finds_it_all()
    {
        await using var s = await Setup.StartAsync();
        var first = await s.JoinAsync(s.Lyra);
        var target = s.Rig.NearStart((2, 0), (-2, 0), (0, 2));
        await first.SendAsync(new MoveMessage(target));
        await Poll.UntilAsync(() => first.Seen(first.Id)?.Cell, c => c?.Equals(target) == true, "the walk");
        s.Earn(first, 321);
        await first.SendAsync(new LogoutMessage());
        (await first.WaitForByeAsync()).ShouldBe(ByeReasons.Logout);
        // At once: the login waits for the save it needs.
        var second = await s.JoinAsync(s.Lyra);
        second.Id.ShouldNotBe(first.Id, "a new entity");
        second.Seen(second.Id)!.Cell.ShouldBe(target);
        Currency(await Known(second)).ShouldBe(321);
        var entries = second.Messages.OfType<InventoryMessage>().First().Entries;
        entries.Select(e => e.Id).ShouldBe(first.Messages.OfType<InventoryMessage>().First().Entries.Select(e => e.Id), "the same permanent ids");
        entries.ShouldAllBe(e => e.Id >> CharacterSaves.ItemIdRunShift == s.Rig.Saves!.Run);
    }

    private static async Task<TestClient> Known(TestClient client)
    {
        await client.WaitForAsync<InventoryMessage>();
        return client;
    }

    [Fact]
    public async Task The_end_of_the_grace_period_saves()
    {
        await using var s = await Setup.StartAsync(configure: o => o with { GracePeriodMs = 1_000 });
        var first = await s.JoinAsync(s.Lyra);
        s.Earn(first, 12);
        first.Dispose();
        await Poll.UntilAsync(() => s.Rig.Gateway.CharactersInGrace, n => n == 1, "grace");
        s.Characters.StateOf(s.LyraId)?.Currency.ShouldBe(0, "still in the world");
        s.Rig.Clock.Advance(2_000);
        await Poll.UntilAsync(() => s.Characters.StateOf(s.LyraId)?.Currency, c => c == 12, "the save");
        await Poll.UntilAsync(() => s.Rig.Saves!.Holds(s.LyraId), h => !h, "given up");
        s.Characters.IsHeld(s.LyraId).ShouldBeFalse();
    }

    [Fact]
    public async Task A_switch_of_characters_saves_the_one_that_leaves()
    {
        await using var s = await Setup.StartAsync();
        var lyra = await s.JoinAsync(s.Lyra);
        s.Earn(lyra, 50);
        var mira = await s.JoinAsync(s.Mira);
        (await lyra.WaitForByeAsync()).ShouldBe(ByeReasons.Replaced);
        await Poll.UntilAsync(() => s.Characters.StateOf(s.LyraId)?.Currency, c => c == 50, "the save");
        Currency(await Known(mira)).ShouldBe(0);
        Currency(await Known(await s.JoinAsync(s.Lyra))).ShouldBe(50);
        (await mira.WaitForByeAsync()).ShouldBe(ByeReasons.Replaced);
    }

    [Fact]
    public async Task A_shutdown_saves_everyone_also_in_their_grace_period()
    {
        var s = await Setup.StartAsync();
        var lyra = await s.JoinAsync(s.Lyra);
        s.Earn(lyra, 7);
        var bob = (await s.Rig.Accounts!.RegisterAsync("bob", ServiceRig.Password, "a", default)).Value!.Token;
        var bron = (await s.Rig.Accounts.CreateAsync(bob, "Bron", default)).Value!.Id;
        var other = await s.Rig.PlayAsync(bob, bron);
        await other.WaitForAsync<SnapshotMessage>();
        s.Earn(other, 8);
        other.Dispose();
        await Poll.UntilAsync(() => s.Rig.Gateway.CharactersInGrace, n => n == 1, "grace");
        await s.DisposeAsync();
        s.Rig.Drained!.Failed.ShouldBeEmpty();
        s.Characters.StateOf(s.LyraId)!.Currency.ShouldBe(7);
        s.Characters.StateOf(Guid.Parse(bron))!.Currency.ShouldBe(8);
    }

    [Fact]
    public async Task A_load_that_fails_refuses_the_login_and_leaves_nothing()
    {
        await using var s = await Setup.StartAsync();
        s.Characters.ClaimsFail = true;
        (await s.RefusedAsync(s.Lyra)).ShouldBe(ByeReasons.LoadFailed);
        s.Characters.ClaimsFail = false;
        s.Characters.Away = true;
        (await s.RefusedAsync(s.Lyra)).ShouldBe(ByeReasons.LoadFailed);
        s.Characters.Away = false;
        s.Characters.Deleted.Add(s.LyraId);   // deleted between the account check and the claim (row 289)
        (await s.RefusedAsync(s.Lyra)).ShouldBe(ByeReasons.LoginRefused);
        s.Characters.Deleted.Clear();
        (s.Rig.Gateway.SlotsInUse, s.Rig.Gateway.CharacterCount, s.Characters.Groups.Count).ShouldBe((0, 0, 0));
        s.Rig.World.Instances.Sum(i => i.Players.Count).ShouldBe(0);
        await s.JoinAsync(s.Lyra);
    }

    [Fact]
    public async Task A_character_that_is_not_saved_yet_is_not_played_from_before_and_not_deleted()
    {
        await using var s = await Setup.StartAsync(SaveRig.Fast with { LoginWaitMs = 200, ShutdownWaitMs = 5_000 });
        var first = await s.JoinAsync(s.Lyra);
        await Poll.UntilAsync(() => s.Characters.VersionOf(s.LyraId), v => v > 0, "the first save");
        s.Earn(first, 99);
        s.Characters.Away = true;
        await first.SendAsync(new LogoutMessage());
        (await first.WaitForByeAsync()).ShouldBe(ByeReasons.Logout);
        (await s.RefusedAsync(s.Lyra)).ShouldBe(ByeReasons.SavePending);
        (await s.Rig.Accounts!.DeleteAsync(s.Ada, s.LyraId, default)).Error.ShouldBe("character_in_use");
        s.Characters.Away = false;
        Currency(await Known(await s.JoinAsync(s.Lyra))).ShouldBe(99);
    }

    [Fact]
    public async Task A_character_lost_to_the_server_ends_its_session()
    {
        await using var s = await Setup.StartAsync();
        var client = await s.JoinAsync(s.Lyra);
        await Poll.UntilAsync(() => s.Characters.VersionOf(s.LyraId), v => v > 0, "the first save");
        s.Characters.Steal(s.LyraId);
        s.Rig.Gateway.LinkOf(new EntityId(client.Id))!.Save!.SaveSoon = true;
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.SaveLost);
        await Poll.UntilAsync(() => s.Rig.World.Instances.Sum(i => i.Players.Count), n => n == 0, "it leaves the world");
        s.Rig.Gateway.CharacterCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_resume_takes_the_living_character_and_reads_nothing()
    {
        await using var s = await Setup.StartAsync();
        var first = await s.JoinAsync(s.Lyra);
        s.Earn(first, 3);
        first.Dispose();
        await Poll.UntilAsync(() => s.Rig.Gateway.CharactersInGrace, n => n == 1, "grace");
        var back = await s.Rig.ResumeAsync(first.Token);
        (await back.WaitForAsync<WelcomeMessage>()).Id.ShouldBe(first.Id);
        var taken = await s.JoinAsync(s.Lyra);   // the same account's login takes it over too
        taken.Id.ShouldBe(first.Id);
        s.Characters.Claims.ShouldBe(1);
    }

    [Fact]
    public async Task A_connection_that_drops_while_its_character_loads_leaves_nothing_and_does_not_wait()
    {
        await using var s = await Setup.StartAsync();
        s.Characters.ClaimGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = await s.Rig.PlayAsync(s.Ada, s.Lyra);
        await Poll.UntilAsync(() => Volatile.Read(ref s.Characters.Claims), n => n == 1, "the claim");
        client.Dispose();
        await Poll.UntilAsync(() => s.Rig.Gateway.SessionCount, n => n == 0, "the session ends while the database still has not answered");
        s.Rig.Saves!.Holds(s.LyraId).ShouldBeTrue("until the claim is known");
        s.Characters.ClaimGate.SetResult();
        await Poll.UntilAsync(() => s.Rig.Saves.Holds(s.LyraId), h => !h, "given up");
        s.Characters.IsHeld(s.LyraId).ShouldBeFalse();
        s.Rig.Gateway.SlotsInUse.ShouldBe(0);
        await s.JoinAsync(s.Lyra);
    }

    [Fact]
    public async Task Logins_at_once_leave_one_character_in_the_world_and_one_hold()
    {
        await using var s = await Setup.StartAsync();
        s.Characters.ClaimGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new List<TestClient>();
        for (var i = 0; i < 6; i++) clients.Add(await s.Rig.PlayAsync(s.Ada, i % 2 == 0 ? s.Lyra : s.Mira));
        await Task.Delay(200);
        s.Characters.ClaimGate.SetResult();
        await Poll.UntilAsync(() => (clients.Count(c => c.Closed), s.Rig.Gateway.CharacterCount, s.Rig.Gateway.SlotsInUse), c => c == (5, 1, 1), "one session and one character left");
        await Poll.UntilAsync(() => s.Rig.World.Instances.Sum(i => i.Players.Count), n => n == 1, "one player in the world");
        await Poll.UntilAsync(() => (s.Rig.Saves!.Holds(s.LyraId), s.Rig.Saves.Holds(Guid.Parse(s.Mira))), h => h == (false, true), "only the last one is held");
    }

    [Fact]
    public async Task A_slow_database_does_not_slow_a_tick()
    {
        await using var s = await Setup.StartAsync(SaveRig.Fast with { IntervalMs = 100, ShutdownWaitMs = 5_000 });
        s.Characters.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = await s.JoinAsync(s.Lyra);
        await Task.Delay(1_500);   // copies come due many times; nothing lands
        s.Rig.Saves!.Stats.Saves.ShouldBe(0);
        s.Rig.Saves.Stats.Waiting.ShouldBe(1, "one copy waits, the newest");
        var stats = s.Rig.World.GetInstance(client.Map!).GetStats();
        stats.Timings.P50Micros.ShouldBeLessThan(20_000);
        await client.SendAsync(new PingMessage(5));
        (await client.WaitForAsync<PongMessage>(p => p.Seq == 5)).ShouldNotBeNull();
        var seen = client.FramesReceived;
        await client.SendAsync(new MoveMessage(s.Rig.NearStart((2, 0), (-2, 0), (0, 2))));
        await Poll.UntilAsync(() => client.FramesReceived, n => n > seen, "the world goes on");
    }
}
