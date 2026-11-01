using System.Diagnostics;
using Game.Server.World;
using Game.Server.World.Monsters;
using Game.Shared.Random;
using Game.Shared.World;
using Game.Tests.World;
using Xunit.Abstractions;

namespace Game.Tests.Server;

/// <summary>
/// T-301 cost: the spawner + AI on the sample meadow with its full spawn list, with ~20 players walking around and with every monster
/// dormant (no players). Logs wall time per tick; asserts only that allocation per tick stays bounded after warm-up.
/// </summary>
public class MonsterBudgetTests(ITestOutputHelper output)
{
    /// <summary>Times and counts allocations of one system's tick.</summary>
    private sealed class Timed(ITickSystem inner) : ITickSystem, IWrappedSystem
    {
        public bool Measure;
        public double Micros;
        public long Bytes;
        public long MaxBytes;
        public double MaxMicros;

        public ITickSystem Inner => inner;

        public string Name => inner.Name;

        public IReadOnlyCollection<Type> IntentTypes => inner.IntentTypes;

        public void Tick(TickContext context)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            inner.Tick(context);
            var micros = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (!Measure) return;
            Micros += micros;
            Bytes += bytes;
            MaxBytes = Math.Max(MaxBytes, bytes);
            MaxMicros = Math.Max(MaxMicros, micros);
        }
    }

    private const int Warmup = 300;
    private const int Measured = 1000;

    [Fact]
    public void Meadow_with_twenty_walking_players()
    {
        var (timed, rig) = Rig();
        var grid = rig.Instance.Grid;
        IRandom route = new Pcg32Random(3, 9);
        var players = Enumerable.Range(0, 20).Select(_ => rig.AddPlayer(RandomCell(grid, route))).ToList();
        rig.Ticks(2);

        void Walk(int t)
        {
            // Each player picks a new random destination every 4 s (staggered), so they cross the monster areas all the time.
            for (var i = 0; i < players.Count; i++)
                if ((t + i * 2) % 40 == 0) rig.Send(new MoveTo(players[i], RandomCell(grid, route)));
        }

        for (var t = 0; t < Warmup; t++)
        {
            Walk(t);
            rig.Ticks();
        }
        var awake = 0L;
        var aggro = rig.Of<AggroGained>().Count;
        var attacks = rig.Of<AttackRequested>().Count;
        foreach (var s in timed) s.Measure = true;
        for (var t = Warmup; t < Warmup + Measured; t++)
        {
            Walk(t);
            rig.Ticks();
            awake += rig.Ai.AwakeCount;
        }

        Report("20 players walking", timed, rig);
        output.WriteLine($"  awake monsters per tick: {(double)awake / Measured:0.0} of {rig.Ai.Count}; aggro gained: {rig.Of<AggroGained>().Count - aggro}, " +
                         $"attack requests: {rig.Of<AttackRequested>().Count - attacks} in {Measured / 10} s");
        rig.MonsterEntities.Count.ShouldBe(grid.Spawns.Sum(s => s.Count));
        (awake / Measured).ShouldBeGreaterThan(0);
        // After warm-up only the intents and events the AI raises allocate (records of a few dozen bytes): bounded by the monster count.
        foreach (var s in timed) s.MaxBytes.ShouldBeLessThan(rig.Ai.Count * 512L, s.Name);
    }

    [Fact]
    public void Meadow_with_every_monster_dormant()
    {
        var (timed, rig) = Rig();
        rig.Ticks(Warmup);
        foreach (var s in timed) s.Measure = true;
        rig.Ticks(Measured);
        Report("no players (all dormant)", timed, rig);
        rig.Ai.AwakeCount.ShouldBe(0);
        rig.Ai.DormantCount.ShouldBe(rig.Ai.Count);
        foreach (var s in timed) s.Bytes.ShouldBe(0, s.Name);   // a dormant map allocates nothing
    }

    private static (List<Timed> Timed, MonsterRig Rig) Rig()
    {
        var timed = new List<Timed>();
        var content = TestGrids.SampleContent;
        var rig = new MonsterRig(content.GetGrid("map.starter_meadow"), content.Monsters.All, content.Balance.Ai, content.Balance.Aoi.BucketSize,
            content.Balance.Aoi.ViewRadius, 11, s =>
            {
                var t = new Timed(s);
                timed.Add(t);
                return t;
            });
        rig.Ticks();
        return (timed, rig);
    }

    private void Report(string label, List<Timed> timed, MonsterRig rig)
    {
        output.WriteLine($"{label}: {rig.Ai.Count} monsters, {rig.Instance.Players.Count} players, {Measured} ticks");
        foreach (var s in timed)
            output.WriteLine($"  {s.Name}: {s.Micros / Measured:0.0} us/tick (max {s.MaxMicros:0} us), {(double)s.Bytes / Measured:0} B/tick (max {s.MaxBytes} B)");
    }

    private static GridCell RandomCell(MapGrid grid, IRandom random)
    {
        while (true)
        {
            var cell = new GridCell(random.Next(grid.Width), random.Next(grid.Height));
            if (grid.IsWalkable(cell.X, cell.Y)) return cell;
        }
    }
}
