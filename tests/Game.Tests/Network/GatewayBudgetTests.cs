using System.Diagnostics;
using Game.Shared.Protocol;
using Game.Shared.World;
using Xunit.Abstractions;

namespace Game.Tests.Network;

/// <summary>
/// T-205 measurement (ARCHITECTURE §9, ≤ 10 KB/s per client with 50 visible entities): 50 real loopback clients join the start map and
/// keep walking around the start cell, so each sees the other 49 walk. Logs the bytes per second each client receives on the wire and the
/// tick-thread cost of the gateway's view sinks. Logs; does not assert wall time. Runs in the TimingCollection (not in parallel with other tests): 50 sockets and
/// their threads would skew both its numbers and other tests' timings.
/// </summary>
[Collection(TimingCollection.Name)]
public class GatewayBudgetTests(ITestOutputHelper output)
{
    private const int Clients = 50;
    private const int WarmupMs = 1_500;
    private const int MeasureMs = 6_000;

    [Fact]
    public async Task Fifty_walking_clients_bytes_per_second_and_sink_cost()
    {
        await using var rig = await GatewayRig.StartAsync(o => o with { MaxSessions = Clients + 1 });
        var grid = rig.Content.GetGrid(rig.Start.MapId);
        var area = new List<GridCell>();
        for (var dy = -5; dy <= 5; dy++)
        for (var dx = -5; dx <= 5; dx++)
        {
            var c = new GridCell(rig.Start.Cell.X + dx, rig.Start.Cell.Y + dy);
            if (grid.IsWalkable(c.X, c.Y)) area.Add(c);
        }

        var clients = await Task.WhenAll(Enumerable.Range(0, Clients).Select(i => rig.JoinAsync("walker" + i)));
        await Poll.UntilAsync(() => clients.Min(c => c.SeenPlayers), n => n == Clients, "everyone sees everyone");

        using var stop = new CancellationTokenSource();
        var walking = Task.Run(async () =>
        {
            var random = new Random(205);
            while (!stop.IsCancellationRequested)
            {
                foreach (var client in clients)
                    if (client.Seen(client.Id) is { Next: null })
                        await client.SendAsync(new MoveMessage(area[random.Next(area.Count)]));
                await Task.Delay(50);
            }
        });

        await Task.Delay(WarmupMs);
        var instance = rig.World.GetInstance(rig.Start.MapId);
        var bytes0 = clients.Select(c => c.WireBytesReceived).ToArray();
        var payload0 = clients.Sum(c => c.BytesReceived);
        var frames0 = clients.Sum(c => c.FramesReceived);
        var sink0 = rig.Gateway.Stats.SinkTime;
        var calls0 = rig.Gateway.Stats.SinkCalls;
        var tick0 = instance.TickNumber;
        var started = Stopwatch.GetTimestamp();
        var visible = new List<int>();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < MeasureMs)
        {
            visible.Add(clients[0].SeenPlayers);
            await Task.Delay(250);
        }
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var ticks = instance.TickNumber - tick0;
        var perClient = clients.Select((c, i) => (c.WireBytesReceived - bytes0[i]) / seconds / 1024).OrderBy(v => v).ToList();
        var payload = clients.Sum(c => c.BytesReceived) - payload0;
        var frames = clients.Sum(c => c.FramesReceived) - frames0;
        var sink = rig.Gateway.Stats.SinkTime - sink0;
        var calls = rig.Gateway.Stats.SinkCalls - calls0;
        await stop.CancelAsync();
        await walking;

        var resyncs = rig.Gateway.Sessions.Sum(s => s.Queue.Resyncs);
        output.WriteLine($"{Clients} clients walking within 5 cells of {rig.Start.Cell} on {rig.Start.MapId}; measured {seconds:0.0} s, {ticks} ticks");
        output.WriteLine($"visible players per client: avg {visible.Average():0.0}, min {visible.Min()}");
        output.WriteLine($"wire bytes/s per client (payload + WebSocket header): avg {perClient.Average():0.00} KB/s, " +
                         $"min {perClient[0]:0.00}, median {perClient[perClient.Count / 2]:0.00}, max {perClient[^1]:0.00} KB/s (budget 10 KB/s)");
        output.WriteLine($"messages per client: {frames / (double)Clients / seconds:0.0}/s, {payload / (double)frames:0} bytes average");
        output.WriteLine($"view sinks on the tick thread: {sink.TotalMicroseconds / ticks:0.0} us per tick for {Clients} observers " +
                         $"({calls / (double)ticks:0.0} calls per tick, {sink.TotalMicroseconds / calls:0.00} us per call)");
        output.WriteLine($"resyncs: {resyncs}; rate-limited: {rig.Gateway.Stats.RateLimited}; intents refused: {rig.Gateway.Stats.IntentsRefused}");

        visible.Min().ShouldBe(Clients);
        perClient[0].ShouldBeGreaterThan(0);
        resyncs.ShouldBe(0);
    }

    /// <summary>
    /// T-307 measurement: the same 50 clients, but 20 of them fight the meadow's gel slimes nonstop (walk out of the safe zone, auto-attack the
    /// nearest slime, pick the next one when it dies, respawn when they die) while the other 30 keep walking near the start. Every client then
    /// gets HP bars, hits, deaths, drops and its own vitals and EXP on top of the walking. Real character stats: a boost that kills slimes faster
    /// only empties the meadow and sends the fighters after slimes out of everyone's view, which lowers the traffic. Asserts the
    /// §9 budget (≤ 10 KB/s average per client) because this is the worst case the task promises to stay under.
    /// </summary>
    [Fact]
    public async Task Fifty_clients_with_twenty_fighting_bytes_per_second()
    {
        const int Fighters = 20;
        await using var rig = await GatewayRig.StartAsync(o => o with { MaxSessions = Clients + 1 });
        var grid = rig.Content.GetGrid(rig.Start.MapId);
        var area = new List<GridCell>();
        for (var dy = -5; dy <= 5; dy++)
        for (var dx = -5; dx <= 5; dx++)
        {
            var c = new GridCell(rig.Start.Cell.X + dx, rig.Start.Cell.Y + dy);
            if (grid.IsWalkable(c.X, c.Y)) area.Add(c);
        }
        // Just outside the safe zone (x 8-17, y 7-13), between the start and the slimes (x 16-25, y 16-23).
        var outside = Enumerable.Range(14, 8).Select(x => new GridCell(x, 15)).Where(c => grid.IsWalkable(c.X, c.Y)).ToList();

        var clients = await Task.WhenAll(Enumerable.Range(0, Clients).Select(i => rig.JoinAsync((i < Fighters ? "fighter" : "walker") + i)));
        await Poll.UntilAsync(() => clients.Min(c => c.SeenPlayers), n => n == Clients, "everyone sees everyone");
        var fighters = clients.Take(Fighters).Select(c => new Fighter(c)).ToList();

        using var stop = new CancellationTokenSource();
        var acting = Task.Run(async () =>
        {
            var random = new Random(307);
            while (!stop.IsCancellationRequested)
            {
                foreach (var client in clients.Skip(Fighters))
                    if (client.Seen(client.Id) is { Next: null })
                        await client.SendAsync(new MoveMessage(area[random.Next(area.Count)]));
                foreach (var fighter in fighters) await fighter.ActAsync(outside[random.Next(outside.Count)]);
                await Task.Delay(100);
            }
        });

        await Task.Delay(WarmupMs * 4);   // let the fighters walk out and start swinging
        var instance = rig.World.GetInstance(rig.Start.MapId);
        var bytes0 = clients.Select(c => c.WireBytesReceived).ToArray();
        var payload0 = clients.Sum(c => c.BytesReceived);
        var frames0 = clients.Sum(c => c.FramesReceived);
        var hits0 = clients.Select(c => c.Hits.Count).ToArray();
        var deaths0 = clients[0].Messages.OfType<DeltaMessage>().SelectMany(d => d.Changed).Count(c => c.Dead);
        var tick0 = instance.TickNumber;
        var sink0 = rig.Gateway.Stats.SinkTime;
        var started = Stopwatch.GetTimestamp();
        var visible = new List<int>();
        var visiblePlayers = new List<int>();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < MeasureMs)
        {
            visible.Add(clients.Min(c => c.SeenIds.Count));
            visiblePlayers.Add(clients.Min(c => c.SeenPlayers));
            await Task.Delay(250);
        }
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var ticks = instance.TickNumber - tick0;
        var perClient = clients.Select((c, i) => (c.WireBytesReceived - bytes0[i]) / seconds / 1024).OrderBy(v => v).ToList();
        var payload = clients.Sum(c => c.BytesReceived) - payload0;
        var frames = clients.Sum(c => c.FramesReceived) - frames0;
        var hitsSeen = clients.Select((c, i) => (c.Hits.Count - hits0[i]) / seconds).ToList();
        var sink = rig.Gateway.Stats.SinkTime - sink0;
        var deaths = clients[0].Messages.OfType<DeltaMessage>().SelectMany(d => d.Changed).Count(c => c.Dead) - deaths0;
        await stop.CancelAsync();
        await acting;

        var resyncs = rig.Gateway.Sessions.Sum(s => s.Queue.Resyncs);
        output.WriteLine($"{Clients} clients ({Fighters} fighting gel slimes, {Clients - Fighters} walking) on {rig.Start.MapId}; measured {seconds:0.0} s, {ticks} ticks");
        output.WriteLine($"visible entities per client: min avg {visible.Average():0.0} (lowest {visible.Min()}); players min {visiblePlayers.Min()}");
        output.WriteLine($"wire bytes/s per client (payload + WebSocket header): avg {perClient.Average():0.00} KB/s, " +
                         $"min {perClient[0]:0.00}, median {perClient[perClient.Count / 2]:0.00}, max {perClient[^1]:0.00} KB/s (budget 10 KB/s)");
        output.WriteLine($"messages per client: {frames / (double)Clients / seconds:0.0}/s, {payload / (double)frames:0} bytes average");
        output.WriteLine($"hits seen per client: avg {hitsSeen.Average():0.0}/s; deaths seen by client 0: {deaths}; " +
                         $"attacks sent: {fighters.Sum(f => f.Attacks)}, respawns: {fighters.Sum(f => f.Respawns)}");
        output.WriteLine($"view sinks on the tick thread: {sink.TotalMicroseconds / ticks:0.0} us per tick for {Clients} observers");
        output.WriteLine($"resyncs: {resyncs}; rate-limited: {rig.Gateway.Stats.RateLimited}; intents refused: {rig.Gateway.Stats.IntentsRefused}");

        visible.Min().ShouldBeGreaterThanOrEqualTo(Clients);
        hitsSeen.Min().ShouldBeGreaterThan(1);
        perClient.Average().ShouldBeLessThanOrEqualTo(10);
        resyncs.ShouldBe(0);
    }

    /// <summary>A scripted fighter: out of the safe zone, attack the nearest living gel slime, again when the attack ends, respawn when dead.</summary>
    private sealed class Fighter(TestClient client)
    {
        private long? _target;
        private int _endedAt;

        public int Attacks { get; private set; }

        public int Respawns { get; private set; }

        public async Task ActAsync(GridCell outside)
        {
            if (client.Seen(client.Id) is not { } me) return;
            if (me.Dead)
            {
                if (_target is not null) Respawns++;
                _target = null;
                await client.SendAsync(new RespawnMessage());
                return;
            }
            var ended = client.Messages.Count(m => m is AttackStoppedMessage || m is RejectedMessage { Intent: MessageTypes.Attack });
            if (_target is not null && ended == _endedAt) return;   // still attacking
            _target = null;
            if (me.Cell.Y < 15)
            {
                if (me.Next is null) await client.SendAsync(new MoveMessage(outside));
                return;
            }
            var slime = client.View.Where(e => e is { Kind: EntityKinds.Monster, DisplayKey: "monster.gel_slime", Dead: false })
                .OrderBy(e => Math.Max(Math.Abs(e.Cell.X - me.Cell.X), Math.Abs(e.Cell.Y - me.Cell.Y))).FirstOrDefault();
            if (slime is null) return;
            _target = slime.Id;
            _endedAt = ended;
            Attacks++;
            await client.SendAsync(new AttackMessage(slime.Id));
        }
    }
}

