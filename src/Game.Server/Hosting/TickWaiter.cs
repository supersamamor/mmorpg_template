using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Game.Server.Hosting;

/// <summary>
/// Waits until a <see cref="Stopwatch"/> timestamp. On Windows a plain <c>WaitOne(ms)</c> wakes on the system timer tick (about 15.6 ms
/// by default), so a 10 Hz tick could start a whole timer period late. The precise waiter sleeps on a high-resolution waitable timer
/// (<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, Windows 10 1803+) until <see cref="SpinMicros"/> before the due time and spins for the rest.
/// Where the timer is not available (other operating systems, older Windows) <see cref="Precise"/> is false and the runner keeps its
/// ordinary wait. One waiter per tick thread; not thread-safe.
/// </summary>
internal sealed class TickWaiter : IDisposable
{
    /// <summary>The last stretch before the due time that is spun, not slept (the timer is accurate to a few hundred µs).</summary>
    public const long SpinMicros = 1000;

    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;

    private readonly ManualResetEvent? _timer;
    private readonly WaitHandle[] _handles = new WaitHandle[2];

    private TickWaiter(ManualResetEvent? timer)
    {
        _timer = timer;
    }

    /// <summary>True when <see cref="WaitUntil"/> is precise; false when the caller should use its ordinary wait.</summary>
    public bool Precise => _timer is not null;

    /// <summary>Creates a waiter; <paramref name="allowPrecise"/> false forces the ordinary wait (to compare the two).</summary>
    public static TickWaiter Create(bool allowPrecise)
    {
        if (allowPrecise && OperatingSystem.IsWindows())
        {
            try
            {
                var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
                if (!handle.IsInvalid) return new TickWaiter(new ManualResetEvent(false) { SafeWaitHandle = handle });
                handle.Dispose();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // fall through: no high-resolution timer
            }
        }
        return new TickWaiter(null);
    }

    /// <summary>
    /// Blocks until <see cref="Stopwatch.GetTimestamp"/> reaches <paramref name="dueTimestamp"/> or <paramref name="stop"/> is signalled.
    /// Returns false when stopped. Only valid when <see cref="Precise"/>.
    /// </summary>
    public bool WaitUntil(long dueTimestamp, CancellationToken stop)
    {
        var timer = _timer ?? throw new InvalidOperationException("No high-resolution timer.");
        var sleepUntil = dueTimestamp - Stopwatch.Frequency * SpinMicros / 1_000_000;
        var remaining = sleepUntil - Stopwatch.GetTimestamp();
        if (remaining > 0)
        {
            var relative100Ns = -(long)(remaining * 10_000_000.0 / Stopwatch.Frequency);
            if (SetWaitableTimer(timer.SafeWaitHandle, ref relative100Ns, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                _handles[0] = timer;
                _handles[1] = stop.WaitHandle;
                if (WaitHandle.WaitAny(_handles) == 1) return false;
            }
            else if (stop.WaitHandle.WaitOne(TimeSpan.FromTicks(Math.Max(1, remaining * TimeSpan.TicksPerSecond / Stopwatch.Frequency))))
            {
                return false;
            }
        }
        while (Stopwatch.GetTimestamp() < dueTimestamp)
        {
            if (stop.IsCancellationRequested) return false;
            Thread.SpinWait(20);
        }
        return !stop.IsCancellationRequested;
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
}

/// <summary>Fixed-size window of start lateness samples (microseconds); reports p50 / p99 / max when full. No allocation after construction.</summary>
internal sealed class LatenessWindow
{
    private readonly long[] _samples;
    private int _count;

    public LatenessWindow(int size) => _samples = new long[size];

    /// <summary>Adds a sample; returns true when the window is now full.</summary>
    public bool Add(long micros)
    {
        _samples[_count++] = micros;
        return _count == _samples.Length;
    }

    /// <summary>Percentiles of the samples so far; clears the window. The sample order is lost.</summary>
    public (int Samples, long P50, long P99, long Max) TakeAndReset()
    {
        var n = _count;
        _count = 0;
        if (n == 0) return (0, 0, 0, 0);
        Array.Sort(_samples, 0, n);
        long Rank(int percent) => _samples[Math.Max(0, (int)Math.Ceiling(n * percent / 100.0) - 1)];
        return (n, Rank(50), Rank(99), _samples[n - 1]);
    }
}
