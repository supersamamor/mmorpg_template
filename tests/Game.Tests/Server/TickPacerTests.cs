using Game.Server.Hosting;
using Game.Server.World;

namespace Game.Tests.Server;

/// <summary>Scheduler logic against hand-advanced time; nothing here sleeps.</summary>
public class TickPacerTests
{
    [Fact]
    public void Interval_is_the_locked_10_hz()
    {
        TickRate.Hz.ShouldBe(10);
        TickRate.IntervalMs.ShouldBe(100);
        new TickPacer(0).IntervalMs.ShouldBe(TickRate.IntervalMs);
    }

    [Fact]
    public void Steady_state_runs_one_tick_per_slot_and_waits_for_the_next()
    {
        var pacer = new TickPacer(startMs: 1000);
        pacer.Next(990).ShouldBe(new PacerStep(0, 0, 10, 0));
        pacer.Next(1000).ShouldBe(new PacerStep(1, 0, 0, 0));
        pacer.Next(1000).ShouldBe(new PacerStep(0, 0, 100, 0));
        pacer.Next(1100).ShouldBe(new PacerStep(1, 0, 0, 0));
        pacer.SlotsConsumed.ShouldBe(2);
    }

    [Fact]
    public void A_late_wake_does_not_push_later_ticks_back()
    {
        var pacer = new TickPacer(startMs: 0);
        pacer.Next(0);
        pacer.Next(117).ShouldBe(new PacerStep(1, 0, 0, 17));
        pacer.Next(117).WaitMs.ShouldBe(83); // next slot still at 200, not 217
        pacer.Next(200).ShouldBe(new PacerStep(1, 0, 0, 0));
    }

    [Fact]
    public void Behind_by_a_few_slots_runs_them_back_to_back()
    {
        var pacer = new TickPacer(startMs: 0);
        pacer.Next(0);
        pacer.Next(350).ShouldBe(new PacerStep(3, 0, 0, 250)); // slots 1..3; slot 1 was due at 100
        pacer.Next(350).WaitMs.ShouldBe(50);
    }

    [Fact]
    public void Far_behind_catches_up_at_most_the_limit_and_skips_the_rest()
    {
        var pacer = new TickPacer(startMs: 0, maxCatchUpTicks: 5);
        pacer.Next(0);
        var step = pacer.Next(2000); // slots 1..20 are due
        step.Run.ShouldBe(5);
        step.Skipped.ShouldBe(15);
        pacer.SlotsSkipped.ShouldBe(15);
        pacer.Next(2000).WaitMs.ShouldBe(100); // back on the grid: slot 21 at 2100
    }

    /// <summary>Drives a pacer like the runner does, with a fake clock: each tick costs <paramref name="tickCostMs"/>, each wake is late by <paramref name="wakeLateMs"/>.</summary>
    private static (long Ticks, long Skipped, int MaxBurst) Simulate(long tickCostMs, long wakeLateMs, long durationMs)
    {
        var clock = new ManualClock();
        var pacer = new TickPacer(clock.NowMilliseconds);
        long ticks = 0;
        var maxBurst = 0;
        while (clock.NowMilliseconds < durationMs)
        {
            var step = pacer.Next(clock.NowMilliseconds);
            if (step.Run == 0)
            {
                clock.Advance(step.WaitMs + wakeLateMs);
                continue;
            }
            maxBurst = Math.Max(maxBurst, step.Run);
            ticks += step.Run;
            clock.Advance(step.Run * tickCostMs);
        }
        return (ticks, pacer.SlotsSkipped, maxBurst);
    }

    [Fact]
    public void Jittery_wakes_keep_the_long_run_rate_exact()
    {
        var (ticks, skipped, _) = Simulate(tickCostMs: 3, wakeLateMs: 15, durationMs: 60_000);
        ticks.ShouldBeInRange(600, 601); // 10 Hz over 60 s, no drift from 15 ms late wakes
        skipped.ShouldBe(0);
    }

    [Fact]
    public void An_overloaded_instance_skips_instead_of_spiralling()
    {
        var (ticks, skipped, maxBurst) = Simulate(tickCostMs: 150, wakeLateMs: 0, durationMs: 60_000);
        maxBurst.ShouldBeLessThanOrEqualTo(TickPacer.DefaultMaxCatchUpTicks);
        skipped.ShouldBeGreaterThan(0);
        ticks.ShouldBeInRange(399, 401); // as many as fit: 60 s / 150 ms
    }

    [Fact]
    public void Stats_window_reports_min_avg_max_and_resets()
    {
        var window = new TickStatsWindow();
        window.Snapshot().ShouldBe(new TickStats(0, 0, 0, 0, 0, 0));
        window.AddTick(10);
        window.AddTick(30);
        window.AddTick(20);
        window.AddStep(new PacerStep(1, 2, 0, 7));
        window.AddStep(new PacerStep(1, 0, 0, 3));
        window.Snapshot().ShouldBe(new TickStats(3, 10, 20, 30, 7, 2));
        window.Reset();
        window.Snapshot().ShouldBe(new TickStats(0, 0, 0, 0, 0, 0));
    }
}
