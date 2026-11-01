using System.Net.WebSockets;
using System.Text;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.World;
using Game.Tests.Server;
using Game.Tests.World;

namespace Game.Tests.Network;

/// <summary>The gateway end to end over real loopback WebSockets (T-205): join, walk, see others, every bye, back-pressure, warps.</summary>
public class GatewayTests
{
    [Fact]
    public async Task Hello_gets_welcome_then_a_snapshot_with_the_new_character()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync("Ada");

        var welcome = await client.WaitForAsync<WelcomeMessage>();
        welcome.Version.ShouldBe(ProtocolVersion.Current);
        welcome.MapId.ShouldBe(rig.Start.MapId);
        welcome.TickRate.ShouldBe(TickRate.Hz);

        var snapshot = await client.WaitForAsync<SnapshotMessage>();
        client.Messages.IndexOf(welcome).ShouldBeLessThan(client.Messages.IndexOf(snapshot));
        snapshot.Id.ShouldBe(welcome.Id);
        snapshot.MapId.ShouldBe(rig.Start.MapId);
        var me = snapshot.Entities.Single(e => e.Id == welcome.Id);
        me.Kind.ShouldBe(EntityKinds.Player);
        me.DisplayKey.ShouldBe(rig.Start.JobId);
        me.Cell.ShouldBe(rig.Start.Cell);
        me.Facing.ShouldBe(rig.Start.Facing);
        me.Next.ShouldBeNull();
        rig.SessionOf(welcome.Id).DisplayName.ShouldBe("Ada");
    }

    [Fact]
    public async Task Move_is_walked_and_the_deltas_show_every_cell()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        var target = rig.NearStart((3, 0), (-3, 0), (0, 3));

        await client.SendAsync(new MoveMessage(target));

        var me = client.Id;
        await Poll.UntilAsync(() => client.Seen(me), r => r is { Next: null } && r.Cell.Equals(target), "arrival");
        var mine = client.Messages.OfType<DeltaMessage>().SelectMany(d => d.Changed).Where(c => c.Id == me).ToList();
        mine.First().Fields.HasFlag(ChangedFields.Next).ShouldBeTrue();          // the walk started: next cell and speed
        mine.First().Speed.ShouldBeGreaterThan(0);
        var cells = mine.Where(c => c.Fields.HasFlag(ChangedFields.Cell)).Select(c => c.Cell).ToList();
        cells.Count.ShouldBe(3);                                                 // one change per cell reached
        cells.Last().ShouldBe(target);
    }

    [Fact]
    public async Task Two_clients_see_each_other_appear_walk_and_disappear()
    {
        await using var rig = await GatewayRig.StartAsync();
        var a = await rig.JoinAsync("a");
        var b = await rig.JoinAsync("b");

        (await b.WaitForAsync<SnapshotMessage>()).Entities.Select(e => e.Id).ShouldContain(a.Id); // b joins where a stands
        await a.WaitForAsync<DeltaMessage>(d => d.Appeared.Any(e => e.Id == b.Id && e.Kind == EntityKinds.Player));

        var target = rig.NearStart((2, 1), (-2, 1), (1, 2));
        await b.SendAsync(new MoveMessage(target));
        await Poll.UntilAsync(() => a.Seen(b.Id), r => r is { Next: null } && r.Cell.Equals(target), "a sees b arrive");

        await b.SendAsync(new LogoutMessage()); // a dropped connection would keep b in the world for the grace period (GatewayResumeTests)
        (await b.WaitForByeAsync()).ShouldBe(ByeReasons.Logout);
        await a.WaitForAsync<DeltaMessage>(d => d.Disappeared.Contains(b.Id));
        a.SeenIds.ShouldNotContain(b.Id);
    }

    [Fact]
    public async Task Disconnect_keeps_the_character_for_the_grace_period_then_removes_it()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        var id = new EntityId(client.Id);
        rig.World.LocationOf(id).ShouldBe(rig.Start.MapId);

        client.Dispose(); // dropped, not closed: the mobile case
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");
        rig.World.LocationOf(id).ShouldBe(rig.Start.MapId);
        rig.Clock.Advance(rig.Gateway.Options.GracePeriodMs);
        await Poll.UntilAsync(() => rig.World.LocationOf(id), map => map is null, "despawn");
        await Poll.UntilAsync(() => rig.Gateway.SessionCount, n => n == 0, "session gone");
    }

    [Fact]
    public async Task Ping_gets_pong_with_the_same_number()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        await client.SendAsync(new PingMessage(4242));
        (await client.WaitForAsync<PongMessage>()).Seq.ShouldBe(4242);
    }

    [Fact]
    public async Task Moves_the_movement_system_refuses_come_back_as_rejected()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        var grid = rig.Content.GetGrid(rig.Start.MapId);
        var wall = new GridCell(0, 0);
        grid.IsWalkable(0, 0).ShouldBeFalse();

        await client.SendAsync(new MoveMessage(wall));
        var rejected = await client.WaitForAsync<RejectedMessage>();
        rejected.ShouldBe(new RejectedMessage(MessageTypes.Move, MovementReasons.NotWalkable, wall));

        await client.SendAsync(new MoveMessage(new GridCell(grid.Width, 0)));          // checked on the socket thread, never queued
        await client.WaitForAsync<RejectedMessage>(r => r.Reason == MovementReasons.OutOfBounds);
        client.Closed.ShouldBeFalse();
    }

    [Fact]
    public async Task Warp_moves_the_session_to_the_target_instance()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        var id = new EntityId(client.Id);
        var other = rig.World.Instances.Select(i => i.MapId).First(m => m != rig.Start.MapId);
        var grid = rig.Content.GetGrid(other);
        var arrival = Enumerable.Range(0, grid.Width * grid.Height).Select(i => new GridCell(i % grid.Width, i / grid.Width))
            .First(c => grid.IsWalkable(c.X, c.Y) && grid.IsWalkable(c.X + 1, c.Y) && grid.IsWalkable(c.X + 2, c.Y));

        rig.World.Warp(id, other, arrival).ShouldBeTrue();
        var snapshot = await client.WaitForAsync<SnapshotMessage>(s => s.MapId == other);
        snapshot.Entities.Single(e => e.Id == client.Id).Cell.ShouldBe(arrival);
        rig.SessionOf(client.Id).MapId.ShouldBe(other);

        // Inbound now goes to the new instance and is bounds-checked against the new map.
        var target = new GridCell(arrival.X + 2, arrival.Y);
        await client.SendAsync(new MoveMessage(target));
        await Poll.UntilAsync(() => client.Seen(client.Id), r => r is { Next: null } && r.Cell.Equals(target), "walk on the new map");
        await client.SendAsync(new MoveMessage(new GridCell(grid.Width, 0)));
        await client.WaitForAsync<RejectedMessage>(r => r.Reason == MovementReasons.OutOfBounds);
    }

    // ---- every bye ----

    [Fact]
    public async Task No_hello_in_time_is_bye_timeout()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.ConnectAsync();
        await Poll.UntilAsync(() => rig.Gateway.SessionCount, n => n == 1, "session");
        rig.Clock.Advance(rig.Gateway.Options.HelloTimeoutMs);
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Timeout);
    }

    [Fact]
    public async Task Silence_after_joining_is_bye_timeout()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        rig.Clock.Advance(rig.Gateway.Options.IdleTimeoutMs - 1);
        await client.SendAsync(new PingMessage(1));                       // any message keeps it alive
        await client.WaitForAsync<PongMessage>();
        rig.Clock.Advance(rig.Gateway.Options.IdleTimeoutMs - 1);
        await Task.Delay(50);
        client.Closed.ShouldBeFalse();
        rig.Clock.Advance(1);
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Timeout);
    }

    public static TheoryData<string, string, string> BadFirstMessages => new()
    {
        { "bad version", """{"type":"hello","version":999,"name":"x"}""", ByeReasons.BadVersion },
        { "malformed json", """{"type":"hello",""", ByeReasons.Malformed },
        { "not an object", "[1,2]", ByeReasons.Malformed },
        { "missing field", """{"type":"hello","name":"x"}""", ByeReasons.Malformed },
        { "wrong field type", """{"type":"hello","version":"1","name":"x"}""", ByeReasons.Malformed },
        { "unknown type", """{"type":"dance"}""", ByeReasons.UnknownType },
        { "server type", """{"type":"welcome"}""", ByeReasons.UnknownType },
        { "move before hello", """{"type":"move","cell":[1,1]}""", ByeReasons.BeforeHello },
        { "ping before hello", """{"type":"ping","seq":1}""", ByeReasons.BeforeHello },
        { "empty name", """{"type":"hello","version":1,"name":"  "}""", ByeReasons.LoginRefused },
    };

    [Theory]
    [MemberData(nameof(BadFirstMessages))]
    public async Task A_bad_first_message_is_bye_with_its_reason(string what, string json, string reason)
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.ConnectAsync();
        await client.SendRawAsync(json);
        (await client.WaitForByeAsync()).ShouldBe(reason, what);
        client.CloseStatus.ShouldBe(ClientSession.CloseStatusFor(reason));
        client.Messages.Count.ShouldBe(1, "bye is the only message");
        rig.World.Instances.Sum(i => i.Players.Count).ShouldBe(0, "nobody was spawned");   // the sample maps' monsters take the first ids (T-301)
    }

    [Fact]
    public async Task A_binary_frame_is_bye_malformed()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        await client.SendRawAsync([1, 2, 3], WebSocketMessageType.Binary);
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Malformed);
    }

    [Fact]
    public async Task An_oversize_message_is_bye_oversize()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        var max = rig.Gateway.Options.MaxMessageBytes;
        var ping = "{\"type\":\"ping\",\"seq\":1,\"pad\":\"";
        await client.SendRawAsync(ping + new string('x', max - ping.Length - 2) + "\"}");  // exactly the cap: fine
        await client.WaitForAsync<PongMessage>();
        await client.SendRawAsync(ping + new string('x', max - ping.Length - 1) + "\"}");  // one byte over
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Oversize);
        client.CloseStatus.ShouldBe(WebSocketCloseStatus.MessageTooBig);
    }

    [Fact]
    public async Task A_second_hello_is_bye_duplicate_hello()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        await client.SendAsync(new HelloMessage(ProtocolVersion.Current, "again"));
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.DuplicateHello);
    }

    [Fact]
    public async Task Flooding_drops_over_the_limit_then_ends_in_bye_rate_limit()
    {
        await using var rig = await GatewayRig.StartAsync(o => o with { RateLimitBurst = 10, RateLimitPerSecond = 5, RateLimitDropsBeforeBye = 20 });
        var client = await rig.JoinAsync();     // hello took 1 token: 9 left
        var session = rig.SessionOf(client.Id);

        for (var i = 0; i < 15; i++) await client.SendAsync(new PingMessage(i));
        await Poll.UntilAsync(() => session.RateLimited, n => n == 6, "6 drops");
        await Task.Delay(50);
        client.Messages.OfType<PongMessage>().Select(p => p.Seq).ShouldBe(Enumerable.Range(0, 9).Select(i => (long)i));
        client.Closed.ShouldBeFalse();

        rig.Clock.Advance(1_000);  // 5 tokens back; the bucket was not full again, so the drop count keeps going
        for (var i = 0; i < 5; i++) await client.SendAsync(new PingMessage(100 + i));
        await client.WaitForAsync<PongMessage>(p => p.Seq == 104);
        for (var i = 0; i < 14; i++) await client.SendAsync(new PingMessage(200 + i));
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.RateLimit);
        rig.Gateway.Stats.RateLimited.ShouldBe(20);
    }

    [Fact]
    public async Task Refilling_the_bucket_forgives_earlier_drops()
    {
        await using var rig = await GatewayRig.StartAsync(o => o with { RateLimitBurst = 4, RateLimitPerSecond = 4, RateLimitDropsBeforeBye = 3 });
        var client = await rig.JoinAsync();
        var session = rig.SessionOf(client.Id);
        // `hello` took one token, so round 0 drops 2 pings; later rounds start full and drop 1. The drops are the last pings of a round,
        // so waiting for the exact running total means the whole round was processed before the clock moves (waiting for `>= round + 1`
        // let round 1's pings land after the refill of round 2 and trip the bye).
        var expectedDrops = 2L;
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 5; i++) await client.SendAsync(new PingMessage(i));
            var expected = expectedDrops;
            await Poll.UntilAsync(() => session.RateLimited, n => n >= expected, "drops");
            session.RateLimited.ShouldBe(expected);
            rig.Clock.Advance(2_000);                                                // full again
            expectedDrops += 1;
        }
        client.Closed.ShouldBeFalse();
    }

    [Fact]
    public async Task Shutdown_says_bye_shutdown()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync();
        var id = new EntityId(client.Id);
        await rig.Gateway.ShutdownAsync();
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Shutdown);
        await Poll.UntilAsync(() => rig.World.LocationOf(id), map => map is null, "despawn");
    }

    [Fact]
    public async Task Over_max_sessions_is_bye_server_full()
    {
        await using var rig = await GatewayRig.StartAsync(o => o with { MaxSessions = 1 });
        await rig.JoinAsync();
        var second = await rig.ConnectAsync();
        (await second.WaitForByeAsync()).ShouldBe(ByeReasons.ServerFull);
    }

    // ---- development only ----

    [Fact]
    public async Task Outside_development_the_dev_login_is_refused_over_wss()
    {
        await using var rig = await GatewayRig.StartAsync(development: false, tls: true);
        rig.Endpoint.Scheme.ShouldBe("wss");
        var client = await rig.ConnectAsync();
        await client.SendAsync(new HelloMessage(ProtocolVersion.Current, "Ada", rig.ContentHash));
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused);
        rig.Gateway.Stats.Byes[ByeReasons.LoginRefused].ShouldBe(1);
    }

    [Fact]
    public async Task Matching_content_hash_gets_a_welcome_carrying_the_servers_hash()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync("Ada"); // JoinAsync sends the rig's hash

        (await client.WaitForAsync<WelcomeMessage>()).Content.ShouldBe(rig.ContentHash);
        rig.ContentHash.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Mismatching_content_hash_gets_update_required()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.ConnectAsync();
        await client.SendAsync(new HelloMessage(ProtocolVersion.Current, "Ada", new string('0', 64)));

        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.UpdateRequired);
        client.Messages.OfType<WelcomeMessage>().ShouldBeEmpty();
        rig.Gateway.Stats.Byes[ByeReasons.UpdateRequired].ShouldBe(1);
    }

    [Fact]
    public async Task Missing_content_hash_is_accepted_in_development_with_a_warning()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.ConnectAsync();
        await client.SendAsync(new HelloMessage(ProtocolVersion.Current, "Ada"));

        (await client.WaitForAsync<WelcomeMessage>()).Content.ShouldBe(rig.ContentHash);
        rig.Log.Lines.ShouldContain(l => l.StartsWith("WARN") && l.Contains("without a content hash"));
    }

    [Fact]
    public async Task Missing_content_hash_is_refused_outside_development()
    {
        await using var rig = await GatewayRig.StartAsync(development: false, tls: true);
        var client = await rig.ConnectAsync();
        await client.SendAsync(new HelloMessage(ProtocolVersion.Current, "Ada"));

        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.UpdateRequired);
    }

    [Fact]
    public async Task Outside_development_the_host_refuses_to_start_without_a_certificate()
    {
        var options = new GatewayOptions { Development = false, Port = 0 };
        options.Problems().ShouldContain(p => p.Contains("TLS certificate"));
        var world = new GameWorld(TestGrids.SampleContent.Grids.Values, new SystemPipeline(), 1, new ManualClock(), new ListLog());
        var gateway = new Gateway(world, new NewCharacters(TestGrids.SampleContent), options, new ManualClock(), new ListLog(), "hash");
        var error = await Should.ThrowAsync<InvalidOperationException>(() => GatewayHost.StartAsync(gateway));
        error.Message.ShouldContain("TLS certificate");

        (options with { Development = true }).Problems().ShouldBeEmpty();
        (options with { CertificatePath = "missing.pfx" }).Problems().ShouldContain(p => p.Contains("does not exist"));
    }

    [Fact]
    public async Task Development_listens_on_loopback_with_plain_ws()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Endpoint.Scheme.ShouldBe("ws");
        rig.Endpoint.Host.ShouldBe("127.0.0.1");
        rig.Endpoint.AbsolutePath.ShouldBe(rig.Gateway.Options.Path);
    }

    [Fact]
    public async Task Plain_http_and_other_paths_are_refused()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var http = new HttpClient();
        var root = new UriBuilder(rig.Endpoint) { Scheme = "http" }.Uri;
        (await http.GetAsync(root)).StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await http.GetAsync(new Uri(root, "/other"))).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    // ---- back-pressure ----

    [Fact]
    public async Task A_reader_that_falls_behind_is_resynced_with_a_fresh_snapshot()
    {
        await using var rig = await GatewayRig.StartAsync(o => o with { SendQueueMessages = 4, ResyncBelowMessages = 1 });
        var slow = await rig.JoinAsync("slow");
        var walker = await rig.JoinAsync("walker");
        await slow.WaitForAsync<DeltaMessage>(d => d.Appeared.Any(e => e.Id == walker.Id));
        var session = rig.SessionOf(slow.Id);
        var snapshotsBefore = slow.Messages.OfType<SnapshotMessage>().Count();

        session.SendGate.Reset();   // stops reading, as far as the server can tell
        var there = rig.NearStart((4, 0), (-4, 0), (0, 4));
        await walker.SendAsync(new MoveMessage(there));
        await Poll.UntilAsync(() => session.Queue.Resyncs, n => n >= 1, "overflow");
        session.Queue.DeltasDropped.ShouldBeGreaterThan(0);
        session.Queue.Count.ShouldBeLessThanOrEqualTo(4);
        await Poll.UntilAsync(() => walker.Seen(walker.Id), r => r is { Next: null } && r.Cell.Equals(there), "walker arrives");

        session.SendGate.Set();     // catches up: a fresh snapshot instead of the backlog
        await Poll.UntilAsync(() => slow.Messages.OfType<SnapshotMessage>().Count(), n => n > snapshotsBefore, "resync snapshot");
        var resync = slow.Messages.OfType<SnapshotMessage>().Last();
        resync.Entities.Single(e => e.Id == walker.Id).Cell.ShouldBe(there);
        slow.Seen(walker.Id)!.Cell.ShouldBe(there);
        session.Queue.Resyncing.ShouldBeFalse();
        slow.Closed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_reader_that_stays_behind_is_disconnected()
    {
        await using var rig = await GatewayRig.StartAsync(o => o with { SendQueueMessages = 2, ResyncBelowMessages = 0 });
        var slow = await rig.JoinAsync("slow");
        var walker = await rig.JoinAsync("walker");
        var session = rig.SessionOf(slow.Id);
        var id = new EntityId(slow.Id);
        await slow.WaitForAsync<DeltaMessage>(d => d.Appeared.Any(e => e.Id == walker.Id));

        session.SendGate.Reset();
        await walker.SendAsync(new MoveMessage(rig.NearStart((4, 0), (-4, 0), (0, 4))));
        await Poll.UntilAsync(() => session.Queue.Resyncing, r => r, "resync");
        rig.Clock.Advance(rig.Gateway.Options.SlowClientTimeoutMs);
        await Poll.UntilAsync(() => session.ByeReason, r => r == ByeReasons.TooSlow, "too_slow");
        rig.Clock.Advance(rig.Gateway.Options.CloseTimeoutMs); // the bye cannot be delivered: the socket is dropped
        await slow.WaitClosedAsync();
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");
        rig.Clock.Advance(rig.Gateway.Options.GracePeriodMs);
        await Poll.UntilAsync(() => rig.World.LocationOf(id), map => map is null, "despawn");
    }

    [Fact]
    public async Task Server_messages_decode_and_the_wire_text_is_utf8_json()
    {
        await using var rig = await GatewayRig.StartAsync();
        var client = await rig.JoinAsync("Zoë");
        rig.SessionOf(client.Id).DisplayName.ShouldBe("Zoë");
        Encoding.UTF8.GetString(ProtocolJson.ToBytes(new ByeMessage(ByeReasons.Timeout))).ShouldBe("""{"type":"bye","reason":"timeout"}""");
    }
}
