using Game.Server.Accounts;
using Game.Server.Net;
using Game.Shared.Protocol;
using Game.Shared.World;
using Game.Tests.Accounts;

namespace Game.Tests.Network;

/// <summary>T-701: <c>hello</c> with an account's session and character, one character per account, and a login that takes its time.</summary>
public class GatewayAccountTests
{
    private sealed class Setup : IAsyncDisposable
    {
        public required GatewayRig Rig { get; init; }
        public required MemoryAccountStore Store { get; init; }
        public required FakeTime Time { get; init; }
        public required string Ada { get; init; }
        public required string Lyra { get; init; }
        public required string Mira { get; init; }

        public static async Task<Setup> StartAsync(Func<GatewayOptions, GatewayOptions>? configure = null, bool development = true, bool tls = false)
        {
            var store = new MemoryAccountStore();
            var time = new FakeTime();
            var rig = await GatewayRig.StartAsync(configure, development, tls, accounts: new AccountRigOptions(store, new FakeHasher(), new AccountLimits(), time));
            var ada = (await rig.Accounts!.RegisterAsync("ada", ServiceRig.Password, "a", default)).Value!.Token;
            return new Setup
            {
                Rig = rig, Store = store, Time = time, Ada = ada,
                Lyra = (await rig.Accounts.CreateAsync(ada, "Lyra", default)).Value!.Id,
                Mira = (await rig.Accounts.CreateAsync(ada, "Mira", default)).Value!.Id,
            };
        }

        public async Task<TestClient> JoinAsync(string character)
        {
            var client = await Rig.PlayAsync(Ada, character);
            await client.WaitForAsync<WelcomeMessage>();
            await client.WaitForAsync<SnapshotMessage>();
            return client;
        }

        /// <summary>From now on every database call waits for the returned gate.</summary>
        public TaskCompletionSource SlowDatabase() => Store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            Store.Gate?.TrySetResult();
            return Rig.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_live_session_enters_with_its_character()
    {
        await using var s = await Setup.StartAsync();
        var client = await s.JoinAsync(s.Lyra);
        var link = s.Rig.Gateway.LinkOf(new Game.Server.World.EntityId(client.Id))!;
        link.CharacterId.ShouldBe(Guid.Parse(s.Lyra));
        link.AccountId.ShouldNotBeNull();
        link.DisplayName.ShouldBe("Lyra");
        s.Rig.Gateway.HoldsCharacter(Guid.Parse(s.Lyra)).ShouldBeTrue();
        s.Rig.Gateway.HoldsCharacter(Guid.Parse(s.Mira)).ShouldBeFalse();
        client.Seen(client.Id)!.DisplayKey.ShouldBe(s.Rig.Start.JobId);
        (await s.Rig.Accounts!.DeleteAsync(s.Ada, Guid.Parse(s.Lyra), default)).Error.ShouldBe("character_in_use");
        await client.SendAsync(new LogoutMessage());
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Logout);
        (await s.Rig.Accounts.DeleteAsync(s.Ada, Guid.Parse(s.Lyra), default)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task A_bad_session_and_another_accounts_character_get_the_same_refusal()
    {
        await using var s = await Setup.StartAsync();
        var bob = (await s.Rig.Accounts!.RegisterAsync("bob", ServiceRig.Password, "a", default)).Value!.Token;
        var gone = (await s.Rig.Accounts.CreateAsync(s.Ada, "Nara", default)).Value!.Id;
        await s.Rig.Accounts.DeleteAsync(s.Ada, Guid.Parse(gone), default);
        foreach (var (session, character) in new[]
                 {
                     (new string('A', 43), s.Lyra), ("", s.Lyra), (bob, s.Lyra), (s.Ada, gone), (s.Ada, Guid.NewGuid().ToString("D")), (s.Ada, "x"),
                 })
            (await (await s.Rig.PlayAsync(session, character)).WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused, $"{session.Length} {character}");
        s.Rig.Gateway.SlotsInUse.ShouldBe(0);
    }

    [Fact]
    public async Task An_expired_and_a_revoked_session_are_refused()
    {
        await using var s = await Setup.StartAsync();
        var second = (await s.Rig.Accounts!.LoginAsync("ada", ServiceRig.Password, "a", default)).Value!.Token;
        await s.Rig.Accounts.LogoutAsync(second, default);
        (await (await s.Rig.PlayAsync(second, s.Lyra)).WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused);
        s.Time.Now += TimeSpan.FromDays(2);
        (await (await s.Rig.PlayAsync(s.Ada, s.Lyra)).WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused);
    }

    [Fact]
    public async Task A_password_change_shuts_old_sessions_out()
    {
        await using var s = await Setup.StartAsync();
        (await s.Rig.Accounts!.ChangePasswordAsync(s.Ada, ServiceRig.Password, "a brand new one", default)).Ok.ShouldBeTrue();
        (await (await s.Rig.PlayAsync(s.Ada, s.Lyra)).WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused);
    }

    [Fact]
    public async Task The_hello_carries_exactly_one_way_in()
    {
        await using var s = await Setup.StartAsync();
        foreach (var fields in new[]
                 {
                     $"\"session\":\"{s.Ada}\"", $"\"character\":\"{s.Lyra}\"", $"\"name\":\"x\",\"session\":\"{s.Ada}\",\"character\":\"{s.Lyra}\"",
                     $"\"resume\":\"x\",\"session\":\"{s.Ada}\",\"character\":\"{s.Lyra}\"", "\"name\":\"x\",\"resume\":\"y\"",
                 })
        {
            var client = await s.Rig.ConnectAsync();
            await client.SendRawAsync($"{{\"type\":\"hello\",\"version\":{ProtocolVersion.Current},\"content\":\"{s.Rig.ContentHash}\",{fields}}}");
            (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Malformed, fields);
        }
    }

    [Fact]
    public async Task Outside_development_accounts_work_over_TLS_and_the_dev_login_does_not()
    {
        await using var s = await Setup.StartAsync(development: false, tls: true);
        await s.JoinAsync(s.Lyra);
        var dev = await s.Rig.ConnectAsync();
        await dev.SendAsync(new HelloMessage(ProtocolVersion.Current, "ada", s.Rig.ContentHash));
        (await dev.WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused);
    }

    [Fact]
    public async Task Without_accounts_a_session_is_refused()
    {
        await using var rig = await GatewayRig.StartAsync();
        (await (await rig.PlayAsync(new string('A', 43), Guid.NewGuid().ToString("D"))).WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused);
    }

    [Fact]
    public async Task A_database_that_is_away_ends_the_session_with_a_server_error()
    {
        await using var s = await Setup.StartAsync();
        s.Store.Away = true;
        (await (await s.Rig.PlayAsync(s.Ada, s.Lyra)).WaitForByeAsync()).ShouldBe(ByeReasons.ServerError);
    }

    // ---- one character per account ----

    [Fact]
    public async Task The_same_character_again_is_taken_over_where_it_stands()
    {
        await using var s = await Setup.StartAsync();
        var first = await s.JoinAsync(s.Lyra);
        var token = first.Token;
        var second = await s.JoinAsync(s.Lyra);
        second.Id.ShouldBe(first.Id);
        (await first.WaitForByeAsync()).ShouldBe(ByeReasons.Replaced);
        s.Rig.Gateway.CharacterCount.ShouldBe(1);
        s.Rig.Gateway.CharactersInGrace.ShouldBe(0);
        (await (await s.Rig.ResumeAsync(token)).WaitForByeAsync()).ShouldBe(ByeReasons.ResumeFailed, "the replaced session's resume token stopped working");
        await second.SendAsync(new MoveMessage(s.Rig.NearStart((1, 0), (-1, 0), (0, 1))));
        await second.WaitForAsync<DeltaMessage>(d => d.Changed.Any(c => c.Id == second.Id));
    }

    [Fact]
    public async Task A_character_in_its_grace_period_is_taken_over_by_a_login()
    {
        await using var s = await Setup.StartAsync();
        var first = await s.JoinAsync(s.Lyra);
        var id = first.Id;
        first.Dispose();
        await Poll.UntilAsync(() => s.Rig.Gateway.CharactersInGrace, n => n == 1, "grace");
        (await s.Rig.Accounts!.DeleteAsync(s.Ada, Guid.Parse(s.Lyra), default)).Error.ShouldBe("character_in_use");
        (await s.JoinAsync(s.Lyra)).Id.ShouldBe(id);
        s.Rig.Gateway.CharactersInGrace.ShouldBe(0);
        s.Rig.Gateway.SlotsInUse.ShouldBe(1);
    }

    [Fact]
    public async Task Another_character_of_the_account_replaces_the_first()
    {
        await using var s = await Setup.StartAsync();
        var first = await s.JoinAsync(s.Lyra);
        var second = await s.JoinAsync(s.Mira);
        second.Id.ShouldNotBe(first.Id);
        (await first.WaitForByeAsync()).ShouldBe(ByeReasons.Replaced);
        await Poll.UntilAsync(() => s.Rig.World.LocationOf(new Game.Server.World.EntityId(first.Id)), m => m is null, "the first character to leave");
        s.Rig.Gateway.CharacterCount.ShouldBe(1);
        s.Rig.Gateway.HoldsCharacter(Guid.Parse(s.Lyra)).ShouldBeFalse();
        // and one in its grace period, too
        second.Dispose();
        await Poll.UntilAsync(() => s.Rig.Gateway.CharactersInGrace, n => n == 1, "grace");
        await s.JoinAsync(s.Lyra);
        s.Rig.Gateway.CharacterCount.ShouldBe(1);
        s.Rig.Gateway.SlotsInUse.ShouldBe(1);
    }

    [Fact]
    public async Task Logins_at_once_leave_one_character()
    {
        await using var s = await Setup.StartAsync();
        var gate = s.SlowDatabase();
        var clients = new List<TestClient>();
        for (var i = 0; i < 8; i++) clients.Add(await s.Rig.PlayAsync(s.Ada, i % 2 == 0 ? s.Lyra : s.Mira));
        await Poll.UntilAsync(() => Volatile.Read(ref s.Store.Calls), c => c >= 8, "eight logins waiting for the database");
        gate.SetResult();
        await Poll.UntilAsync(() => (clients.Count(c => c.Closed), s.Rig.Gateway.CharacterCount, s.Rig.Gateway.SlotsInUse), c => c == (7, 1, 1), "one session and one character left");
        clients.Where(c => c.Closed).ShouldAllBe(c => c.Messages.OfType<ByeMessage>().Any(b => b.Reason == ByeReasons.Replaced));
        await Poll.UntilAsync(() => s.Rig.World.Instances.Sum(i => i.Players.Count), n => n == 1, "one player in the world");
    }

    [Fact]
    public async Task Two_accounts_do_not_replace_each_other_and_resume_keeps_its_rules()
    {
        await using var s = await Setup.StartAsync();
        var bob = (await s.Rig.Accounts!.RegisterAsync("bob", ServiceRig.Password, "a", default)).Value!.Token;
        var bobs = (await s.Rig.Accounts.CreateAsync(bob, "Bron", default)).Value!.Id;
        var ada = await s.JoinAsync(s.Lyra);
        var other = await s.Rig.PlayAsync(bob, bobs);
        await other.WaitForAsync<SnapshotMessage>();
        var dev = await s.Rig.JoinAsync("dev");
        ada.Closed.ShouldBeFalse();
        var token = ada.Token;
        ada.Dispose();
        await Poll.UntilAsync(() => s.Rig.Gateway.CharactersInGrace, n => n == 1, "grace");
        var resumed = await s.Rig.ResumeAsync(token);
        (await resumed.WaitForAsync<WelcomeMessage>()).Id.ShouldBe(ada.Id);
        // The account's next login still replaces the resumed session.
        (await s.JoinAsync(s.Lyra)).Id.ShouldBe(ada.Id);
        (await resumed.WaitForByeAsync()).ShouldBe(ByeReasons.Replaced);
        other.Closed.ShouldBeFalse();
        dev.Closed.ShouldBeFalse();
        s.Rig.Gateway.CharacterCount.ShouldBe(3);
    }

    // ---- a login that is pending ----

    [Fact]
    public async Task Messages_right_behind_the_hello_wait_for_the_login()
    {
        await using var s = await Setup.StartAsync();
        var gate = s.SlowDatabase();
        var calls = s.Store.Calls;
        var client = await s.Rig.PlayAsync(s.Ada, s.Lyra);
        var target = s.Rig.NearStart((2, 0), (-2, 0), (0, 2));
        await client.SendAsync(new PingMessage(1));
        await client.SendAsync(new MoveMessage(target));
        await client.SendAsync(new PingMessage(2));
        await Poll.UntilAsync(() => s.Store.Calls, c => c > calls, "the login to reach the database");
        await Task.Delay(300);
        client.Messages.ShouldBeEmpty("nothing is handled while the login is pending: no pong, no bye before_hello");
        s.Rig.Gateway.SlotsInUse.ShouldBe(0);
        gate.SetResult();
        (await client.WaitForAsync<PongMessage>(p => p.Seq == 2)).ShouldNotBeNull();
        await client.WaitForAsync<WelcomeMessage>();
        client.Closed.ShouldBeFalse();
        // The session goes on: a move now walks.
        await client.WaitForAsync<SnapshotMessage>();
        await client.SendAsync(new MoveMessage(target));
        await client.WaitForAsync<DeltaMessage>(d => d.Changed.Any(c => c.Id == client.Id));
    }

    [Fact]
    public async Task A_second_hello_behind_the_first_is_still_a_duplicate()
    {
        await using var s = await Setup.StartAsync();
        var gate = s.SlowDatabase();
        var client = await s.Rig.PlayAsync(s.Ada, s.Lyra);
        await client.SendAsync(new HelloMessage(ProtocolVersion.Current, null, s.Rig.ContentHash, null, s.Ada, s.Mira));
        gate.SetResult();
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.DuplicateHello);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_connection_that_drops_while_its_login_is_pending_leaves_nothing_behind(bool politely)
    {
        await using var s = await Setup.StartAsync();
        var gate = s.SlowDatabase();
        var calls = s.Store.Calls;
        var client = await s.Rig.PlayAsync(s.Ada, s.Lyra);
        await Poll.UntilAsync(() => s.Store.Calls, c => c > calls, "the login to reach the database");
        var closing = politely ? client.CloseAsync() : Task.CompletedTask;
        if (!politely) client.Dispose();
        await Poll.UntilAsync(() => s.Rig.Gateway.SessionCount, n => n == 0, "the session to end while the database still has not answered");
        await closing;
        gate.SetResult();
        await Task.Delay(300); // several ticks
        s.Rig.Gateway.SlotsInUse.ShouldBe(0);
        s.Rig.Gateway.CharacterCount.ShouldBe(0);
        s.Rig.World.Instances.Sum(i => i.Players.Count).ShouldBe(0);
        s.Store.Gate = null;
        (await s.JoinAsync(s.Lyra)).Id.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_shutdown_while_a_login_is_pending_leaves_nothing_behind()
    {
        await using var s = await Setup.StartAsync();
        var gate = s.SlowDatabase();
        var calls = s.Store.Calls;
        var client = await s.Rig.PlayAsync(s.Ada, s.Lyra);
        await Poll.UntilAsync(() => s.Store.Calls, c => c > calls, "the login to reach the database");
        var shutdown = s.Rig.Gateway.ShutdownAsync();
        (await client.WaitForAsync<ByeMessage>()).Reason.ShouldBe(ByeReasons.Shutdown);
        gate.SetResult();
        await shutdown;
        await client.WaitClosedAsync();
        await Poll.UntilAsync(() => s.Rig.Gateway.SessionCount, n => n == 0, "the session to end");
        await Task.Delay(300);
        s.Rig.Gateway.SlotsInUse.ShouldBe(0);
        s.Rig.Gateway.CharacterCount.ShouldBe(0);
        s.Rig.World.Instances.Sum(i => i.Players.Count).ShouldBe(0);
    }

    [Fact]
    public async Task A_login_that_takes_too_long_times_out_and_leaves_nothing_behind()
    {
        await using var s = await Setup.StartAsync();
        var gate = s.SlowDatabase();
        var calls = s.Store.Calls;
        var client = await s.Rig.PlayAsync(s.Ada, s.Lyra);
        await Poll.UntilAsync(() => s.Store.Calls, c => c > calls, "the login to reach the database");
        s.Rig.Clock.Advance(s.Rig.Gateway.Options.HelloTimeoutMs);
        (await client.WaitForAsync<ByeMessage>()).Reason.ShouldBe(ByeReasons.Timeout);
        gate.SetResult();
        await client.WaitClosedAsync();
        await Task.Delay(300);
        s.Rig.Gateway.SlotsInUse.ShouldBe(0);
        s.Rig.Gateway.CharacterCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_pending_login_does_not_replace_the_one_that_plays()
    {
        await using var s = await Setup.StartAsync();
        var playing = await s.JoinAsync(s.Lyra);
        s.SlowDatabase();
        var calls = s.Store.Calls;
        var late = await s.Rig.PlayAsync(s.Ada, s.Lyra);
        await Poll.UntilAsync(() => s.Store.Calls, c => c > calls, "the login to reach the database");
        late.Dispose();
        await Poll.UntilAsync(() => s.Rig.Gateway.SessionCount, n => n == 1, "the late session to end");
        s.Store.Gate!.SetResult();
        await Task.Delay(300);
        playing.Closed.ShouldBeFalse();
        await playing.SendAsync(new PingMessage(7));
        await playing.WaitForAsync<PongMessage>(p => p.Seq == 7);
    }
}

