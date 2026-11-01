using System.Net.WebSockets;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.World;
using Game.Tests.Server;

namespace Game.Tests.Network;

/// <summary>
/// The mobile session lifecycle (T-210, ARCHITECTURE §10 step 6) end to end over loopback WebSockets: the grace period after a drop,
/// resume with a single-use token, logout, takeover, warps while away, the brute-force guard, shutdown, and tokens kept out of the log.
/// Time moves only through the rig's <see cref="ManualClock"/>; waits poll with a timeout.
/// </summary>
public class GatewayResumeTests
{
    private const string TokenPattern = "^[A-Za-z0-9_-]{43}$"; // 32 bytes, base64url without padding

    /// <summary>A rig whose idle timeout outlasts the grace period, so watchers are not timed out when the clock jumps over it.</summary>
    private static Task<GatewayRig> StartRig() => GatewayRig.StartAsync(o => o with { IdleTimeoutMs = 10 * o.GracePeriodMs });

    [Fact]
    public async Task A_dropped_character_stays_visible_and_resumes_with_a_new_token_a_snapshot_and_deltas()
    {
        await using var rig = await StartRig();
        var watcher = await rig.JoinAsync("watcher");
        var player = await rig.JoinAsync("player");
        var id = player.Id;
        var oldToken = player.Token;
        oldToken.ShouldMatch(TokenPattern);
        await watcher.WaitForAsync<DeltaMessage>(d => d.Appeared.Any(e => e.Id == id));

        player.Dispose(); // the train enters a tunnel
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");
        rig.Clock.Advance(rig.Gateway.Options.GracePeriodMs - 1);
        await Task.Delay(50); // a few sweeps: nothing may expire yet
        rig.World.LocationOf(new EntityId(id)).ShouldBe(rig.Start.MapId);
        watcher.SeenIds.ShouldContain(id);
        watcher.Messages.OfType<DeltaMessage>().ShouldNotContain(d => d.Disappeared.Contains(id));

        var back = await rig.ResumeAsync(oldToken);
        var welcome = await back.WaitForAsync<WelcomeMessage>();
        welcome.Id.ShouldBe(id);
        welcome.MapId.ShouldBe(rig.Start.MapId);
        welcome.Content.ShouldBe(rig.ContentHash);
        welcome.Token.ShouldNotBeNull();
        welcome.Token.ShouldMatch(TokenPattern);
        welcome.Token.ShouldNotBe(oldToken);
        var snapshot = await back.WaitForAsync<SnapshotMessage>();
        back.Messages.IndexOf(welcome).ShouldBe(0);
        back.Messages.IndexOf(snapshot).ShouldBe(1);
        snapshot.Id.ShouldBe(id);
        snapshot.Entities.Select(e => e.Id).ShouldContain(id);
        snapshot.Entities.Select(e => e.Id).ShouldContain(watcher.Id);
        rig.Gateway.CharactersInGrace.ShouldBe(0);
        rig.SessionOf(id).DisplayName.ShouldBe("player");

        // Deltas continue, both ways.
        var target = rig.NearStart((2, 0), (-2, 0), (0, 2));
        await back.SendAsync(new MoveMessage(target));
        await Poll.UntilAsync(() => back.Seen(id), r => r is { Next: null } && r.Cell.Equals(target), "own walk in deltas");
        await Poll.UntilAsync(() => watcher.Seen(id), r => r is { Next: null } && r.Cell.Equals(target), "watcher sees the walk");

        // The token that was used is gone; the new one is the only one.
        var replay = await rig.ResumeAsync(oldToken);
        (await replay.WaitForByeAsync()).ShouldBe(ByeReasons.ResumeFailed);
        replay.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        back.Closed.ShouldBeFalse();
        rig.Gateway.Tokens.Count.ShouldBe(2);
    }

    [Fact]
    public async Task After_the_grace_period_the_character_is_gone_and_its_token_fails()
    {
        await using var rig = await StartRig();
        var watcher = await rig.JoinAsync("watcher");
        var player = await rig.JoinAsync("player");
        var id = new EntityId(player.Id);
        await watcher.WaitForAsync<DeltaMessage>(d => d.Appeared.Any(e => e.Id == id.Value));

        player.Dispose();
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");
        rig.Clock.Advance(rig.Gateway.Options.GracePeriodMs);
        await Poll.UntilAsync(() => rig.World.LocationOf(id), map => map is null, "despawn");
        await watcher.WaitForAsync<DeltaMessage>(d => d.Disappeared.Contains(id.Value));
        rig.Gateway.CharacterCount.ShouldBe(1); // the watcher

        var late = await rig.ResumeAsync(player.Token);
        (await late.WaitForByeAsync()).ShouldBe(ByeReasons.ResumeFailed);
        late.Messages.OfType<WelcomeMessage>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Logout_says_bye_logout_and_removes_the_character_without_a_grace_period()
    {
        await using var rig = await GatewayRig.StartAsync();
        var player = await rig.JoinAsync();
        var id = new EntityId(player.Id);

        await player.SendAsync(new LogoutMessage());
        (await player.WaitForByeAsync()).ShouldBe(ByeReasons.Logout);
        player.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        await Poll.UntilAsync(() => rig.World.LocationOf(id), map => map is null, "despawn without advancing the clock");
        rig.Gateway.CharacterCount.ShouldBe(0);
        rig.Gateway.Tokens.Count.ShouldBe(0);

        var again = await rig.ResumeAsync(player.Token);
        (await again.WaitForByeAsync()).ShouldBe(ByeReasons.ResumeFailed);
    }

    [Fact]
    public async Task Logout_before_hello_is_bye_before_hello()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.ConnectAsync();
        await client.SendAsync(new LogoutMessage());
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.BeforeHello);
    }

    [Fact]
    public async Task A_resume_while_the_old_session_is_open_replaces_it()
    {
        await using var rig = await GatewayRig.StartAsync();
        var first = await rig.JoinAsync("player");
        var id = first.Id;
        var oldSession = rig.SessionOf(id);

        var second = await rig.ResumeAsync(first.Token); // the server has not noticed that the first connection is dead
        (await second.WaitForAsync<WelcomeMessage>()).Id.ShouldBe(id);
        await second.WaitForAsync<SnapshotMessage>();
        (await first.WaitForByeAsync()).ShouldBe(ByeReasons.Replaced);
        first.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);

        // The old session's end does not start a grace period: the character is the new session's.
        await Poll.UntilAsync(() => rig.Gateway.Sessions.Contains(oldSession), open => !open, "old session gone");
        rig.Gateway.CharactersInGrace.ShouldBe(0);
        rig.Gateway.LinkOf(new EntityId(id))!.Session.ShouldBe(rig.SessionOf(id));
        rig.SessionOf(id).ShouldNotBe(oldSession);
        rig.Gateway.Sessions.Count(s => s.Entity?.Value == id).ShouldBe(1);

        var target = rig.NearStart((2, 1), (-2, 1), (1, 2));
        await second.SendAsync(new MoveMessage(target));
        await Poll.UntilAsync(() => second.Seen(id), r => r is { Next: null } && r.Cell.Equals(target), "the new session plays it");
    }

    [Fact]
    public async Task Resume_after_a_warp_lands_on_the_new_map()
    {
        await using var rig = await GatewayRig.StartAsync();
        var player = await rig.JoinAsync();
        var id = new EntityId(player.Id);
        var other = rig.World.Instances.Select(i => i.MapId).First(m => m != rig.Start.MapId);
        var grid = rig.Content.GetGrid(other);
        var arrival = Enumerable.Range(0, grid.Width * grid.Height).Select(i => new GridCell(i % grid.Width, i / grid.Width))
            .First(c => grid.IsWalkable(c.X, c.Y));

        player.Dispose();
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");
        rig.World.Warp(id, other, arrival).ShouldBeTrue(); // a portal, a script, an admin: the world moves it while it is away
        await Poll.UntilAsync(() => rig.World.LocationOf(id), map => map == other, "warped");

        var back = await rig.ResumeAsync(player.Token);
        var welcome = await back.WaitForAsync<WelcomeMessage>();
        welcome.Id.ShouldBe(id.Value);
        welcome.MapId.ShouldBe(other);
        var snapshot = await back.WaitForAsync<SnapshotMessage>();
        snapshot.MapId.ShouldBe(other);
        snapshot.Entities.Single(e => e.Id == id.Value).Cell.ShouldBe(arrival);
        rig.SessionOf(id.Value).MapId.ShouldBe(other);
    }

    [Fact]
    public async Task A_dropped_character_stops_walking()
    {
        await using var rig = await GatewayRig.StartAsync();
        var watcher = await rig.JoinAsync("watcher");
        var player = await rig.JoinAsync("player");
        var id = player.Id;
        await watcher.WaitForAsync<DeltaMessage>(d => d.Appeared.Any(e => e.Id == id));
        var far = rig.NearStart((9, 0), (-9, 0), (0, 9), (0, -9), (7, 0), (-7, 0));

        await player.SendAsync(new MoveMessage(far));
        await Poll.UntilAsync(() => watcher.Seen(id), r => r?.Next is not null, "walking");
        player.Dispose();

        var stopped = await Poll.UntilAsync(() => watcher.Seen(id), r => r is { Next: null }, "stopped");
        stopped!.Cell.ShouldNotBe(far);
        await Task.Delay(300); // three ticks: it stays put
        watcher.Seen(id)!.Cell.ShouldBe(stopped.Cell);
        watcher.Seen(id)!.Next.ShouldBeNull();
        rig.World.LocationOf(new EntityId(id)).ShouldBe(rig.Start.MapId);
    }

    [Fact]
    public async Task A_protocol_error_starts_the_grace_period_too()
    {
        await using var rig = await GatewayRig.StartAsync();
        var player = await rig.JoinAsync();
        await player.SendRawAsync("{\"type\":");
        (await player.WaitForByeAsync()).ShouldBe(ByeReasons.Malformed);
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");

        var back = await rig.ResumeAsync(player.Token);
        (await back.WaitForAsync<WelcomeMessage>()).Id.ShouldBe(player.Id);
    }

    [Fact]
    public async Task Resume_checks_version_and_content_hash_before_the_token()
    {
        await using var rig = await GatewayRig.StartAsync();
        var player = await rig.JoinAsync();
        var token = player.Token;

        var stale = await rig.ConnectAsync();
        await stale.SendAsync(new HelloMessage(ProtocolVersion.Current, null, new string('0', 64), token));
        (await stale.WaitForByeAsync()).ShouldBe(ByeReasons.UpdateRequired);
        var old = await rig.ConnectAsync();
        await old.SendRawAsync($$"""{"type":"hello","version":999,"resume":"{{token}}"}""");
        (await old.WaitForByeAsync()).ShouldBe(ByeReasons.BadVersion);

        player.Closed.ShouldBeFalse("neither attempt touched the character or its token");
        var back = await rig.ResumeAsync(token);
        (await back.WaitForAsync<WelcomeMessage>()).Id.ShouldBe(player.Id);
    }

    [Fact]
    public async Task Resume_works_outside_development()
    {
        await using var rig = await GatewayRig.StartAsync(development: false, tls: true);
        var unknown = await rig.ResumeAsync(new string('A', 43));
        (await unknown.WaitForByeAsync()).ShouldBe(ByeReasons.ResumeFailed); // not login_refused: resume is not the dev login
    }

    [Fact]
    public async Task Repeated_failed_resumes_end_in_rate_limit_and_lock_the_address_out()
    {
        await using var rig = await GatewayRig.StartAsync(o => o with { ResumeFailureBurst = 3, ResumeFailuresPerMinute = 1, ResumeLockoutMs = 30_000 });
        var player = await rig.JoinAsync();
        player.Dispose();
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");

        for (var i = 0; i < 3; i++)
        {
            var guess = await rig.ResumeAsync(Guess(i));
            (await guess.WaitForByeAsync()).ShouldBe(ByeReasons.ResumeFailed, $"guess {i}");
        }
        var fourth = await rig.ResumeAsync(Guess(3));
        (await fourth.WaitForByeAsync()).ShouldBe(ByeReasons.RateLimit);

        // Locked out: even the right token is refused, and it is not used up.
        var right = await rig.ResumeAsync(player.Token);
        (await right.WaitForByeAsync()).ShouldBe(ByeReasons.RateLimit);
        rig.Log.Lines.ShouldContain(l => l.Contains("resume failed") && l.Contains("locked out"));

        rig.Clock.Advance(30_000);
        var later = await rig.ResumeAsync(player.Token);
        (await later.WaitForAsync<WelcomeMessage>()).Id.ShouldBe(player.Id);
        rig.Gateway.Stats.Byes[ByeReasons.RateLimit].ShouldBe(2);
    }

    [Fact]
    public async Task Shutdown_removes_characters_in_their_grace_period()
    {
        await using var rig = await GatewayRig.StartAsync();
        var away = await rig.JoinAsync("away");
        var here = await rig.JoinAsync("here");
        var awayId = new EntityId(away.Id);
        away.Dispose();
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");

        await rig.Gateway.ShutdownAsync();
        (await here.WaitForByeAsync()).ShouldBe(ByeReasons.Shutdown);
        await Poll.UntilAsync(() => rig.World.LocationOf(awayId), map => map is null, "away removed");
        await Poll.UntilAsync(() => rig.World.LocationOf(new EntityId(here.Id)), map => map is null, "here removed");
        rig.Gateway.CharacterCount.ShouldBe(0);
        rig.Gateway.Tokens.Count.ShouldBe(0);

        var late = await rig.ResumeAsync(away.Token);
        (await late.WaitForByeAsync()).ShouldBe(ByeReasons.Shutdown);
    }

    [Fact]
    public async Task No_token_ever_appears_in_the_log()
    {
        await using var rig = await GatewayRig.StartAsync();
        var tokens = new List<string>();
        var a = await rig.JoinAsync("a");
        tokens.Add(a.Token);
        a.Dispose();
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");
        var b = await rig.ResumeAsync(tokens[0]);                          // resume
        await b.WaitForAsync<SnapshotMessage>();
        tokens.Add(b.Token);
        var replay = await rig.ResumeAsync(tokens[0]);                     // a used token
        await replay.WaitForByeAsync();
        var c = await rig.ResumeAsync(tokens[1]);                          // takeover
        await c.WaitForAsync<SnapshotMessage>();
        tokens.Add(c.Token);
        await b.WaitForByeAsync();
        await c.SendAsync(new LogoutMessage());                            // logout
        await c.WaitForByeAsync();
        var gone = await rig.ResumeAsync(tokens[2]);
        await gone.WaitForByeAsync();

        tokens.Distinct().Count().ShouldBe(3);
        var log = rig.Log.Lines;
        log.ShouldContain(l => l.Contains("resumed") && l.Contains(ResumeTokens.Fingerprint(tokens[1])));
        foreach (var token in tokens)
        {
            log.ShouldNotContain(l => l.Contains(token));
            var prefix = token.Substring(0, 12);
            log.ShouldNotContain(l => l.Contains(prefix), "not even a prefix");
        }
    }

    private static string Guess(int i) => new string((char)('a' + i), 43);
}
