using Game.Server.World;

namespace Game.Server.Hosting;

/// <summary>What the runner should do now: run some ticks, or wait.</summary>
/// <param name="Run">Ticks to run back to back now (0 means wait).</param>
/// <param name="Skipped">Tick slots dropped because the instance fell too far behind (warn when above 0).</param>
/// <param name="WaitMs">When <see cref="Run"/> is 0: milliseconds until the next slot is due.</param>
/// <param name="LateMs">When <see cref="Run"/> is above 0: how far past its due time the first tick to run is.</param>
public readonly record struct PacerStep(int Run, long Skipped, long WaitMs, long LateMs);

/// <summary>
/// Pure fixed-rate scheduling logic, separate from threads and sleeping so it can be tested with a hand-advanced clock.
/// Slot <c>k</c> is due at <c>start + k × interval</c>: the schedule is anchored to the start time, so a late wake-up
/// does not push later ticks back (no drift). When behind, up to <see cref="MaxCatchUpTicks"/> ticks run back to back;
/// slots beyond that are skipped (not run at all) so an overloaded instance degrades to a slower simulation instead
/// of spiralling. Skipped slots do not consume tick numbers: simulated time then runs behind wall time.
/// </summary>
public sealed class TickPacer
{
    /// <summary>Most ticks run in one burst to catch up (half a second of simulation at 10 Hz).</summary>
    public const int DefaultMaxCatchUpTicks = 5;

    private readonly long _startMs;
    private long _slotsConsumed;

    /// <summary>Creates a pacer whose slot 0 is due at <paramref name="startMs"/>.</summary>
    public TickPacer(long startMs, int intervalMs = TickRate.IntervalMs, int maxCatchUpTicks = DefaultMaxCatchUpTicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCatchUpTicks, 1);
        _startMs = startMs;
        IntervalMs = intervalMs;
        MaxCatchUpTicks = maxCatchUpTicks;
    }

    /// <summary>Milliseconds between slots.</summary>
    public int IntervalMs { get; }

    /// <summary>Most ticks one <see cref="Next"/> call asks for.</summary>
    public int MaxCatchUpTicks { get; }

    /// <summary>Slots run or skipped so far.</summary>
    public long SlotsConsumed => _slotsConsumed;

    /// <summary>Total slots skipped so far.</summary>
    public long SlotsSkipped { get; private set; }

    /// <summary>
    /// Decides what to do at <paramref name="nowMs"/>. Ticks it asks for count as done: run them all before calling again.
    /// </summary>
    public PacerStep Next(long nowMs)
    {
        var dueSlots = nowMs < _startMs ? 0 : (nowMs - _startMs) / IntervalMs + 1;
        var behind = dueSlots - _slotsConsumed;
        if (behind <= 0) return new PacerStep(0, 0, _startMs + _slotsConsumed * IntervalMs - nowMs, 0);

        var skipped = Math.Max(0, behind - MaxCatchUpTicks);
        _slotsConsumed += skipped;
        SlotsSkipped += skipped;
        var late = nowMs - (_startMs + _slotsConsumed * IntervalMs);
        var run = (int)(behind - skipped);
        _slotsConsumed += run;
        return new PacerStep(run, skipped, 0, late);
    }
}

/// <summary>Min / average / max of tick durations over a reporting window. Not thread-safe (one per runner).</summary>
public sealed class TickStatsWindow
{
    private long _count;
    private double _sum;
    private double _min = double.MaxValue;
    private double _max;
    private long _maxLateMs;
    private long _skipped;

    /// <summary>Ticks recorded in this window.</summary>
    public long Count => _count;

    /// <summary>Records one tick's duration in microseconds.</summary>
    public void AddTick(double micros)
    {
        _count++;
        _sum += micros;
        if (micros < _min) _min = micros;
        if (micros > _max) _max = micros;
    }

    /// <summary>Records a pacer step's lateness and skipped slots.</summary>
    public void AddStep(PacerStep step)
    {
        if (step.LateMs > _maxLateMs) _maxLateMs = step.LateMs;
        _skipped += step.Skipped;
    }

    /// <summary>The window's figures so far.</summary>
    public TickStats Snapshot() =>
        new(_count, _count == 0 ? 0 : _min, _count == 0 ? 0 : _sum / _count, _max, _maxLateMs, _skipped);

    /// <summary>Starts a new window.</summary>
    public void Reset()
    {
        _count = 0;
        _sum = 0;
        _min = double.MaxValue;
        _max = 0;
        _maxLateMs = 0;
        _skipped = 0;
    }
}

/// <summary>One reporting window: tick count, min/avg/max tick duration (µs), worst start lateness (ms), skipped slots.</summary>
public readonly record struct TickStats(long Ticks, double MinMicros, double AvgMicros, double MaxMicros, long MaxLateMs, long Skipped)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"{Ticks} ticks, tick cost min/avg/max {MinMicros:0.0}/{AvgMicros:0.0}/{MaxMicros:0.0} us, max start lateness {MaxLateMs} ms, skipped {Skipped}";
}
