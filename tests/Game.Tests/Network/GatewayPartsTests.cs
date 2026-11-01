using System.Text;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Tests.World;

namespace Game.Tests.Network;

/// <summary>The gateway's pure parts: send queue back-pressure, token bucket, options, login stub, new characters.</summary>
public class GatewayPartsTests
{
    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    private static List<string> Drain(SendQueue queue, out int snapshotRequests)
    {
        var taken = new List<string>();
        snapshotRequests = 0;
        while (true)
        {
            var took = queue.TryTake(out var bytes, out var request);
            if (request) snapshotRequests++;
            if (!took) return taken;
            taken.Add(Encoding.UTF8.GetString(bytes));
        }
    }

    [Fact]
    public void Send_queue_keeps_order_under_capacity()
    {
        var queue = new SendQueue(4, 1);
        queue.PostControl(B("welcome")).ShouldBeTrue();
        queue.PostSnapshot(B("s1")).ShouldBeTrue();
        queue.PostDelta(B("d1"), 0).ShouldBeTrue();
        Drain(queue, out var requests).ShouldBe(["welcome", "s1", "d1"]);
        requests.ShouldBe(0);
    }

    [Fact]
    public void Overflow_drops_views_resyncs_and_a_snapshot_ends_it()
    {
        var queue = new SendQueue(4, 1);
        queue.PostSnapshot(B("s1"));
        queue.PostControl(B("pong"));
        queue.PostDelta(B("d1"), 0);
        queue.PostDelta(B("d2"), 0);
        queue.PostDelta(B("d3"), 500).ShouldBeFalse();     // full: views dropped, resync starts
        queue.Resyncing.ShouldBeTrue();
        queue.BehindSinceMs.ShouldBe(500);
        queue.Resyncs.ShouldBe(1);
        queue.DeltasDropped.ShouldBe(3);                    // d1, d2 thrown away, d3 refused
        queue.PostDelta(B("d4"), 600).ShouldBeFalse();     // relative to a state the client will not see
        queue.DeltasDropped.ShouldBe(4);

        Drain(queue, out var requests).ShouldBe(["pong"]); // control messages survive
        requests.ShouldBe(1);                               // once, when drained below the mark
        Drain(queue, out requests);
        requests.ShouldBe(0);

        queue.PostSnapshot(B("s2"));
        queue.Resyncing.ShouldBeFalse();
        queue.BehindSinceMs.ShouldBeNull();
        queue.PostDelta(B("d5"), 700).ShouldBeTrue();
        Drain(queue, out _).ShouldBe(["s2", "d5"]);
    }

    [Fact]
    public void A_failed_snapshot_request_is_asked_again()
    {
        var queue = new SendQueue(2, 0);
        queue.PostDelta(B("d1"), 0);
        queue.PostDelta(B("d2"), 0);
        queue.PostDelta(B("d3"), 0);
        Drain(queue, out var requests);
        requests.ShouldBe(1);
        queue.ResyncRequestFailed();
        Drain(queue, out requests);
        requests.ShouldBe(1);
    }

    [Fact]
    public void A_snapshot_replaces_queued_views_but_not_control_messages()
    {
        var queue = new SendQueue(8, 1);
        queue.PostSnapshot(B("s1"));
        queue.PostDelta(B("d1"), 0);
        queue.PostControl(B("rejected"));
        queue.PostSnapshot(B("s2"));
        Drain(queue, out _).ShouldBe(["rejected", "s2"]);
    }

    [Fact]
    public void Control_messages_overflowing_twice_the_capacity_fail_and_final_ends_the_queue()
    {
        var queue = new SendQueue(2, 0);
        for (var i = 0; i < 4; i++) queue.PostControl(B("c" + i)).ShouldBeTrue();
        queue.PostControl(B("c4")).ShouldBeFalse();
        queue.PostFinal(B("bye")).ShouldBeTrue();
        queue.Completed.ShouldBeTrue();
        queue.PostFinal(B("bye2")).ShouldBeFalse();
        queue.PostSnapshot(B("s")).ShouldBeFalse();
        Drain(queue, out _).ShouldBe(["c0", "c1", "c2", "c3", "bye"]);
    }

    [Fact]
    public void Token_bucket_refills_at_its_rate_up_to_its_burst()
    {
        var bucket = new TokenBucket(perSecond: 10, burst: 3, nowMs: 0);
        for (var i = 0; i < 3; i++) bucket.TryTake(0).ShouldBeTrue();
        bucket.TryTake(0).ShouldBeFalse();
        bucket.TryTake(99).ShouldBeFalse();     // 0.99 tokens
        bucket.TryTake(100).ShouldBeTrue();
        bucket.Drops.ShouldBe(2);
        bucket.DropsSinceFull.ShouldBe(2);
        bucket.TryTake(10_000).ShouldBeTrue();  // capped at 3, and full again: drops forgiven
        bucket.DropsSinceFull.ShouldBe(0);
        bucket.TryTake(10_000).ShouldBeTrue();
        bucket.TryTake(10_000).ShouldBeTrue();
        bucket.TryTake(10_000).ShouldBeFalse();
    }

    [Fact]
    public void Options_come_from_defaults_then_environment_then_command_line()
    {
        var env = new Dictionary<string, string> { ["GAME_NET_PORT"] = "9000", ["GAME_NET_PATH"] = "/game", ["GAME_NET_RATE_LIMIT_PER_SECOND"] = "2.5" };
        var options = GatewayOptions.Parse(["--net-port", "9100", "--net-max-message-bytes", "2048"], n => env.GetValueOrDefault(n), true, out var error);
        error.ShouldBeNull();
        options!.Port.ShouldBe(9100);
        options.Path.ShouldBe("/game");
        options.RateLimitPerSecond.ShouldBe(2.5);
        options.MaxMessageBytes.ShouldBe(2048);
        options.Development.ShouldBeTrue();
        options.HelloTimeoutMs.ShouldBe(new GatewayOptions().HelloTimeoutMs);
        options.Problems().ShouldBeEmpty();

        GatewayOptions.Parse(["--net-nope", "1"], _ => null, true, out error).ShouldBeNull();
        error!.ShouldContain("--net-nope");
        GatewayOptions.Parse(["--net-port", "many"], _ => null, true, out error).ShouldBeNull();
        GatewayOptions.Parse(["--net-port"], _ => null, true, out error).ShouldBeNull();
        GatewayOptions.Usage.ShouldContain("send-queue-messages");
    }

    [Fact]
    public void Development_listens_only_on_loopback()
    {
        new GatewayOptions { Development = true }.ListenAddress.ShouldBe(System.Net.IPAddress.Loopback);
        new GatewayOptions { Development = false }.ListenAddress.ShouldBe(System.Net.IPAddress.Any);
        new GatewayOptions { Development = true, BindAddress = "0.0.0.0" }.Problems().ShouldContain(p => p.Contains("loopback"));
        new GatewayOptions { Development = true, BindAddress = "::1" }.Problems().ShouldBeEmpty();
        new GatewayOptions { Development = true, BindAddress = "nope" }.Problems().ShouldContain(p => p.Contains("not an IP"));
    }

    [Fact]
    public void Server_options_collect_net_options_and_the_environment()
    {
        var options = ServerOptions.Parse(["--environment", "Development", "--net-port", "1234"], out var error)!;
        error.ShouldBeNull();
        options.Environment.ShouldBe("Development");
        options.NetArgs.ShouldBe(["--net-port", "1234"]);
        ServerOptions.Parse([], out _)!.EnvironmentName(null).ShouldBe("Production");
        ServerOptions.Parse([], out _)!.EnvironmentName("Development").ShouldBe("Development");
        options.EnvironmentName("Production").ShouldBe("Development");
    }

    [Theory]
    [InlineData(true, "Ada", "Ada")]
    [InlineData(true, "  Ada  ", "Ada")]
    [InlineData(true, "", null)]
    [InlineData(true, "bell\u0007", null)]
    [InlineData(true, "abcdefghij", "abcdefghij")]
    [InlineData(true, "abcdefghijk", null)]
    [InlineData(false, "Ada", null)]
    public void Dev_login_accepts_short_names_in_development_only(bool development, string name, string? accepted)
    {
        var result = new DevLogin(development, maxNameLength: 10, new ResumeTokens()).Login(new HelloMessage(ProtocolVersion.Current, name));
        result.DisplayName.ShouldBe(accepted);
        result.Refusal.ShouldBe(accepted is null ? ByeReasons.LoginRefused : null);
    }

    [Fact]
    public void New_characters_come_from_balance_new_character_and_the_stat_pipeline()
    {
        var content = TestGrids.SampleContent;
        var start = new NewCharacters(content);
        var def = content.Balance.NewCharacter;
        start.MapId.ShouldBe(def.StartMapId);
        start.Cell.ShouldBe(new Game.Shared.World.GridCell(def.StartX, def.StartY));
        start.Facing.ShouldBe(def.StartFacing);
        start.JobId.ShouldBe(def.StartJobId);

        var sheet = start.CreateStats();
        sheet.Base.BaseLevel.ShouldBe(1);
        sheet.Base.JobLevel.ShouldBe(1);
        foreach (var stat in PrimaryStats.All.Ordered) sheet.Base[stat].ShouldBe(content.Balance.Progression.StartingStatValue);
        var expected = new Game.Content.Simulation.BalanceSimulator(content).Character(def.StartJobId, 1, exact: PrimaryStats.All.Ordered
            .ToDictionary(s => s, _ => content.Balance.Progression.StartingStatValue)).Sheet;
        sheet.Get(DerivedStats.MoveSpeed).ShouldBe(expected.Get(DerivedStats.MoveSpeed));
        sheet.Get(DerivedStats.MoveSpeed).ShouldBeGreaterThan(0);
        start.CreateStats().ShouldNotBeSameAs(sheet);
    }

    [Fact]
    public void Protocol_vocabulary_matches_the_doc()
    {
        MessageTypes.All.Ordered.ShouldBe(["hello", "move", "stop", "attack", "stop_attack", "respawn", "ping", "logout",
            "welcome", "snapshot", "delta", "rejected", "attack_stopped", "exp", "level_up", "you_died", "respawned", "pong", "bye"]);
        ByeReasons.All.Ordered.ShouldBe(["timeout", "bad_version", "malformed", "unknown_type", "oversize", "before_hello", "duplicate_hello",
            "rate_limit", "login_refused", "server_full", "too_slow", "shutdown", "server_error", "update_required", "logout", "replaced", "resume_failed"]);
        EntityKinds.All.Ordered.ShouldBe(["player", "monster", "npc", "item"]);
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "VOCABULARY.md"));
        foreach (var term in MessageTypes.All.Concat(ByeReasons.All).Concat(EntityKinds.All)) doc.ShouldContain($"`{term}`");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found");
    }
}
