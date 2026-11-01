namespace Game.Server.World;

/// <summary>
/// Fixed-size ring of the most recent tick durations (microseconds) of one instance. The tick thread is the only writer and allocates
/// nothing; <see cref="Snapshot"/> may be called from any thread and costs one small array (it is for the admin panel, not the tick).
/// A reader can see a sample or two that are one tick stale; it never sees a torn value (64-bit stores) or an out-of-range index.
/// </summary>
public sealed class TickTimings
{
    /// <summary>Samples kept: one minute at 10 Hz.</summary>
    public const int Capacity = 600;

    private readonly long[] _micros = new long[Capacity];
    private long _written;   // samples ever recorded; slot (n % Capacity) holds sample n
    private long _lifetimeMax;

    /// <summary>Records one tick's duration. Tick thread only. No allocation.</summary>
    public void Record(long micros)
    {
        var n = Volatile.Read(ref _written);
        Volatile.Write(ref _micros[(int)(n % Capacity)], micros);
        if (micros > Volatile.Read(ref _lifetimeMax)) Volatile.Write(ref _lifetimeMax, micros);
        Volatile.Write(ref _written, n + 1);
    }

    /// <summary>The percentiles of the samples in the ring. Safe from any thread.</summary>
    public TickTimingSnapshot Snapshot()
    {
        var written = Volatile.Read(ref _written);
        var count = (int)Math.Min(written, Capacity);
        if (count == 0) return new TickTimingSnapshot(written, 0, 0, 0, 0, Volatile.Read(ref _lifetimeMax));

        var copy = new long[count];
        for (var i = 0; i < count; i++) copy[i] = Volatile.Read(ref _micros[i]);
        Array.Sort(copy);
        return new TickTimingSnapshot(written, count, Percentile(copy, 50), Percentile(copy, 99), copy[count - 1], Volatile.Read(ref _lifetimeMax));
    }

    /// <summary>Nearest-rank percentile of a sorted, non-empty array.</summary>
    internal static long Percentile(long[] sorted, int percent) =>
        sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percent / 100.0) - 1)];
}

/// <summary>Tick duration figures over the recent window, in microseconds.</summary>
/// <param name="Ticks">Ticks recorded since the instance started.</param>
/// <param name="Samples">Ticks the percentiles are computed from (at most <see cref="TickTimings.Capacity"/>).</param>
/// <param name="P50Micros">Median tick duration of the window.</param>
/// <param name="P99Micros">99th percentile tick duration of the window.</param>
/// <param name="MaxMicros">Slowest tick of the window.</param>
/// <param name="LifetimeMaxMicros">Slowest tick since the instance started.</param>
public readonly record struct TickTimingSnapshot(long Ticks, int Samples, long P50Micros, long P99Micros, long MaxMicros, long LifetimeMaxMicros);

/// <summary>A copy of an instance's <see cref="InstanceCounters"/>.</summary>
public readonly record struct CounterSnapshot(
    long IntentsRouted, long IntentsDroppedUnknownEntity, long IntentsDroppedDeadEntity, long IntentsDroppedUnrouted,
    long SystemErrors, long HandlerErrors, long StructuralIgnored);

/// <summary>What the admin panel reads for one instance: map, tick number, tick timings and counters. Immutable.</summary>
public sealed record InstanceStats(string MapId, long TickNumber, TickTimingSnapshot Timings, CounterSnapshot Counters);
