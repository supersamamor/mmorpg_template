using System.Diagnostics;
using Game.Server.Hosting;
using Game.Server.World;
using Game.Shared.Vocabulary;

namespace Game.Tests.Server;

/// <summary>T-213: per-instance tick timings (ring buffer, p50 / p99 / max) and the counters snapshot.</summary>
[Collection(TimingCollection.Name)]
public class TickTimingsTests
{
    [Fact]
    public void An_empty_ring_reports_zeros()
    {
        new TickTimings().Snapshot().ShouldBe(new TickTimingSnapshot(0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void Percentiles_use_the_nearest_rank_over_the_recorded_samples()
    {
        var timings = new TickTimings();
        for (var i = 1; i <= 100; i++) timings.Record(i * 10);   // 10 .. 1000 µs

        var snapshot = timings.Snapshot();

        snapshot.Ticks.ShouldBe(100);
        snapshot.Samples.ShouldBe(100);
        snapshot.P50Micros.ShouldBe(500);
        snapshot.P99Micros.ShouldBe(990);
        snapshot.MaxMicros.ShouldBe(1000);
        snapshot.LifetimeMaxMicros.ShouldBe(1000);
    }

    [Fact]
    public void The_ring_keeps_only_the_most_recent_window_but_remembers_the_lifetime_max()
    {
        var timings = new TickTimings();
        timings.Record(9_000);   // one slow tick that falls out of the window
        for (var i = 0; i < TickTimings.Capacity; i++) timings.Record(100);

        var snapshot = timings.Snapshot();

        snapshot.Ticks.ShouldBe(TickTimings.Capacity + 1);
        snapshot.Samples.ShouldBe(TickTimings.Capacity);
        snapshot.MaxMicros.ShouldBe(100);
        snapshot.P99Micros.ShouldBe(100);
        snapshot.LifetimeMaxMicros.ShouldBe(9_000);
    }

    [Fact]
    public void Recording_allocates_nothing()
    {
        var timings = new TickTimings();
        AllocationProbe.MinAllocatedBytes(() =>
        {
            for (var i = 0; i < 5_000; i++) timings.Record(i);
        }).ShouldBe(0);
    }

    [Fact]
    public async Task A_snapshot_can_be_taken_while_another_thread_records()
    {
        var timings = new TickTimings();
        using var done = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            long n = 0;
            while (!done.IsCancellationRequested) timings.Record(100 + n++ % 50);
        });
        try
        {
            for (var i = 0; i < 200; i++)
            {
                var s = timings.Snapshot();
                if (s.Samples == 0) continue;
                s.P50Micros.ShouldBeInRange(100, 149);
                s.P99Micros.ShouldBeInRange(s.P50Micros, 149);
                s.MaxMicros.ShouldBeInRange(s.P99Micros, 149);
            }
        }
        finally
        {
            await done.CancelAsync();
            await writer;
        }
    }

    [Fact]
    public void An_instance_records_every_tick_and_exposes_counters_as_a_snapshot()
    {
        var rig = new AoiRig();
        var instance = rig.A;
        var entity = rig.Add(new Game.Shared.World.GridCell(1, 1), kind: EntityKind.Monster);
        instance.TryEnqueue(new MoveTo(new EntityId(9_999), new Game.Shared.World.GridCell(2, 2)));   // an unknown entity: dropped, counted
        rig.Ticks(3);

        var stats = instance.GetStats();

        stats.MapId.ShouldBe("map.a");
        stats.TickNumber.ShouldBe(3);
        stats.Timings.Ticks.ShouldBe(3);
        stats.Timings.Samples.ShouldBe(3);
        stats.Timings.MaxMicros.ShouldBeGreaterThanOrEqualTo(stats.Timings.P50Micros);
        stats.Counters.IntentsDroppedUnknownEntity.ShouldBe(1);
        stats.Counters.IntentsRouted.ShouldBe(instance.Counters.IntentsRouted);
        entity.Value.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Ticking_with_the_timings_allocates_nothing()
    {
        var rig = new AoiRig();
        rig.Add(new Game.Shared.World.GridCell(1, 1));
        rig.Ticks(5);
        var instance = rig.A;

        AllocationProbe.MinAllocatedBytes(() =>
        {
            for (var i = 0; i < 200; i++) instance.Tick();
        }).ShouldBe(0);
    }
}

/// <summary>T-213: the high-resolution tick waiter and the lateness window.</summary>
[Collection(TimingCollection.Name)]
public class TickWaiterTests
{
    [Fact]
    public void The_lateness_window_reports_p50_p99_and_max_when_full_and_starts_over()
    {
        var window = new LatenessWindow(100);
        for (var i = 1; i <= 99; i++) window.Add(i).ShouldBeFalse();
        window.Add(100).ShouldBeTrue();

        window.TakeAndReset().ShouldBe((100, 50, 99, 100));
        window.TakeAndReset().ShouldBe((0, 0, 0, 0));
    }

    [Fact]
    public void The_precise_waiter_never_returns_early_and_stays_close_to_the_due_time()
    {
        using var waiter = TickWaiter.Create(allowPrecise: true);
        if (!waiter.Precise) return;   // not Windows (or no high-resolution timer): the runner keeps the ordinary wait

        var late = new List<double>();
        var due = Stopwatch.GetTimestamp();
        for (var i = 0; i < 30; i++)
        {
            due += Stopwatch.Frequency / 10;   // 100 ms apart, like ticks
            waiter.WaitUntil(due, CancellationToken.None).ShouldBeTrue();
            var lateMs = Stopwatch.GetElapsedTime(due).TotalMilliseconds;
            lateMs.ShouldBeGreaterThanOrEqualTo(0);
            late.Add(lateMs);
        }

        late.Sort();
        late[late.Count / 2].ShouldBeLessThan(2.0);   // the ordinary wait is up to ~15 ms late on Windows
    }

    [Fact]
    public void A_stop_signal_ends_the_wait_at_once()
    {
        using var waiter = TickWaiter.Create(allowPrecise: true);
        if (!waiter.Precise) return;
        using var stop = new CancellationTokenSource();
        stop.CancelAfter(50);

        var started = Stopwatch.GetTimestamp();
        waiter.WaitUntil(started + Stopwatch.Frequency * 30, stop.Token).ShouldBeFalse();

        Stopwatch.GetElapsedTime(started).TotalSeconds.ShouldBeLessThan(5);
    }

    [Fact]
    public void Forcing_the_ordinary_wait_gives_a_waiter_that_is_not_precise()
    {
        using var waiter = TickWaiter.Create(allowPrecise: false);
        waiter.Precise.ShouldBeFalse();
    }
}
