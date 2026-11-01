using System.Diagnostics;
using Game.Server.World;
using Game.Shared.Time;

namespace Game.Server.Hosting;

/// <summary>
/// Ticks one <see cref="MapInstance"/> at <see cref="TickRate.Hz"/> on its own dedicated thread, paced by a
/// <see cref="TickPacer"/>. Waiting uses the stop signal's wait handle, so <see cref="Stop"/> wakes it at once; a tick
/// in progress is always finished before the thread exits. Tick cost is measured with <see cref="Stopwatch"/>
/// timestamps (µs resolution) and logged every <see cref="ReportIntervalMs"/>.
/// </summary>
public sealed class InstanceRunner
{
    /// <summary>How often each runner logs its tick stats.</summary>
    public const int ReportIntervalMs = 10_000;

    private readonly IClock _clock;
    private readonly IServerLog _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly TickStatsWindow _window = new();
    private Thread? _thread;

    /// <summary>Start lateness is logged as p50 / p99 over this many ticks.</summary>
    public const int LatenessWindowTicks = 600;

    private readonly bool _preciseTimer;

    /// <summary>
    /// Creates a runner; call <see cref="Start"/> to begin ticking. <paramref name="preciseTimer"/> false keeps the ordinary wait on every
    /// platform (used to measure what the high-resolution timer buys).
    /// </summary>
    public InstanceRunner(MapInstance instance, IClock clock, IServerLog log, bool preciseTimer = true)
    {
        _preciseTimer = preciseTimer;
        Instance = instance;
        _clock = clock;
        _log = log;
    }

    /// <summary>The instance this runner ticks.</summary>
    public MapInstance Instance { get; }

    /// <summary>Stats of every tick since start (not reset by reports).</summary>
    public TickStats Lifetime { get; private set; }

    /// <summary>Starts the tick thread.</summary>
    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("Already started.");
        _thread = new Thread(Run) { Name = "tick:" + Instance.MapId, IsBackground = true };
        _thread.Start();
    }

    /// <summary>Asks the thread to stop after the current tick and waits for it.</summary>
    public void Stop()
    {
        _stop.Cancel();
        _thread?.Join();
    }

    private void Run()
    {
        var token = _stop.Token;
        var pacer = new TickPacer(_clock.NowMilliseconds);
        var lifetime = new TickStatsWindow();
        using var waiter = TickWaiter.Create(_preciseTimer);
        var lateness = new LatenessWindow(LatenessWindowTicks);
        var timerName = waiter.Precise ? "high-resolution timer" : "ordinary wait";
        var startTimestamp = Stopwatch.GetTimestamp();   // the pacer's slot 0, on the high-resolution counter
        var intervalTimestamps = Stopwatch.Frequency * pacer.IntervalMs / 1000;
        var lastReport = _clock.NowMilliseconds;
        while (!token.IsCancellationRequested)
        {
            var consumedBefore = pacer.SlotsConsumed;
            var step = pacer.Next(_clock.NowMilliseconds);
            if (step.Run == 0)
            {
                var due = startTimestamp + consumedBefore * intervalTimestamps;
                if (waiter.Precise && due > Stopwatch.GetTimestamp()) waiter.WaitUntil(due, token);
                else token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Max(1, step.WaitMs)));
                continue;
            }

            var firstDue = startTimestamp + (consumedBefore + step.Skipped) * intervalTimestamps;
            var lateMicros = Math.Max(0, (long)Stopwatch.GetElapsedTime(firstDue).TotalMicroseconds);
            if (lateness.Add(lateMicros))
            {
                var (n, p50, p99, max) = lateness.TakeAndReset();
                _log.Info($"[{Instance.MapId}] tick start lateness over {n} ticks ({timerName}): p50 {p50 / 1000.0:0.00} ms, p99 {p99 / 1000.0:0.00} ms, max {max / 1000.0:0.00} ms");
            }

            _window.AddStep(step);
            lifetime.AddStep(step);
            if (step.Skipped > 0)
                _log.Warn($"[{Instance.MapId}] fell behind: skipped {step.Skipped} tick(s), catching up {step.Run}.");

            for (var i = 0; i < step.Run && !token.IsCancellationRequested; i++)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    Instance.Tick();
                }
                catch (Exception ex)
                {
                    // Systems and handlers are already isolated inside Tick; this is a bug in the instance itself.
                    _log.Error($"[{Instance.MapId}] tick {Instance.TickNumber} failed outside any system.", ex);
                }
                var micros = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                _window.AddTick(micros);
                lifetime.AddTick(micros);
            }

            var now = _clock.NowMilliseconds;
            if (now - lastReport >= ReportIntervalMs)
            {
                _log.Info($"[{Instance.MapId}] {_window.Snapshot()} (last {(now - lastReport) / 1000.0:0.#} s; " +
                          $"{Instance.Entities.Count} entities, {Instance.Systems.Count} systems)");
                _window.Reset();
                lastReport = now;
            }
            Lifetime = lifetime.Snapshot();
        }
        Lifetime = lifetime.Snapshot();
    }
}
