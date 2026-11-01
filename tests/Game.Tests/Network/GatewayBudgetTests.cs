using System.Diagnostics;
using Game.Shared.Protocol;
using Game.Shared.World;
using Xunit.Abstractions;

namespace Game.Tests.Network;

/// <summary>
/// T-205 measurement (ARCHITECTURE §9, ≤ 10 KB/s per client with 50 visible entities): 50 real loopback clients join the start map and
/// keep walking around the start cell, so each sees the other 49 walk. Logs the bytes per second each client receives on the wire and the
/// tick-thread cost of the gateway's view sinks. Logs; does not assert wall time. Runs on its own (not in parallel with other tests): 50 sockets and
/// their threads would skew both its numbers and other tests' timings.
/// </summary>
[Collection(nameof(GatewayBudgetTests))]
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
        await Poll.UntilAsync(() => clients.Min(c => c.SeenIds.Count), n => n == Clients, "everyone sees everyone");

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
            visible.Add(clients[0].SeenIds.Count);
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
        output.WriteLine($"visible entities per client: avg {visible.Average():0.0}, min {visible.Min()}");
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
}

[CollectionDefinition(nameof(GatewayBudgetTests), DisableParallelization = true)]
public sealed class GatewayBudgetCollection;
