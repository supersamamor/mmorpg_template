using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Game.Server.Hosting;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.Time;
using Game.Shared.World;

namespace Game.Server.Net;

/// <summary>Gateway-wide counters. Thread-safe; read them any time.</summary>
public sealed class GatewayStats
{
    private readonly ConcurrentDictionary<string, long> _byes = new(StringComparer.Ordinal);
    private long _accepted, _bytesIn, _messagesIn, _bytesOut, _messagesOut, _rateLimited, _intentsRefused, _sinkTicks, _sinkCalls;

    /// <summary>Connections accepted.</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>Client message bytes received.</summary>
    public long BytesIn => Interlocked.Read(ref _bytesIn);

    /// <summary>Client messages received.</summary>
    public long MessagesIn => Interlocked.Read(ref _messagesIn);

    /// <summary>Message bytes handed to sockets.</summary>
    public long BytesOut => Interlocked.Read(ref _bytesOut);

    /// <summary>Messages handed to sockets.</summary>
    public long MessagesOut => Interlocked.Read(ref _messagesOut);

    /// <summary>Client messages dropped by the rate limit.</summary>
    public long RateLimited => Interlocked.Read(ref _rateLimited);

    /// <summary>Intents the world refused (instance queue full, or the entity is gone).</summary>
    public long IntentsRefused => Interlocked.Read(ref _intentsRefused);

    /// <summary>Calls of the sessions' view sinks (snapshots and deltas).</summary>
    public long SinkCalls => Interlocked.Read(ref _sinkCalls);

    /// <summary>Total time spent in the view sinks on tick threads.</summary>
    public TimeSpan SinkTime => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _sinkTicks));

    /// <summary>Byes sent, by reason.</summary>
    public IReadOnlyDictionary<string, long> Byes => _byes;

    internal void CountAccepted() => Interlocked.Increment(ref _accepted);

    internal void CountInbound(int bytes)
    {
        Interlocked.Increment(ref _messagesIn);
        Interlocked.Add(ref _bytesIn, bytes);
    }

    internal void CountOutbound(int bytes)
    {
        Interlocked.Increment(ref _messagesOut);
        Interlocked.Add(ref _bytesOut, bytes);
    }

    internal void CountRateLimited() => Interlocked.Increment(ref _rateLimited);

    internal void CountIntentRefused() => Interlocked.Increment(ref _intentsRefused);

    internal void CountBye(string reason) => _byes.AddOrUpdate(reason, 1, (_, n) => n + 1);

    internal void AddSinkTime(long stopwatchTicks)
    {
        Interlocked.Increment(ref _sinkCalls);
        Interlocked.Add(ref _sinkTicks, stopwatchTicks);
    }
}

/// <summary>
/// The network gateway (ARCHITECTURE §3, §10): turns WebSocket connections into <see cref="ClientSession"/>s, logs them in, puts their
/// characters into the world through the thread-safe <see cref="GameWorld.Spawn(EntityKind, string, GridCell, string, Action{Entity})"/>,
/// forwards their intents to the instance that holds the character, and removes the character when the connection ends. Owns no
/// socket listener: <see cref="GatewayHost"/> accepts connections and calls <see cref="RunSessionAsync"/>.
/// </summary>
/// <remarks>
/// Create it before the instances start ticking: the constructor subscribes to every instance's event bus (<c>MoveRejected</c> →
/// <c>rejected</c>; <c>EntityEnteredMap</c> / <c>EntityLeftMap</c> to track characters). Those handlers run on tick threads and only look
/// up a session and queue bytes.
/// </remarks>
public sealed class Gateway
{
    private readonly GameWorld _world;
    private readonly NewCharacters _start;
    private readonly ConcurrentDictionary<ClientSession, byte> _sessions = new();
    private readonly ConcurrentDictionary<EntityId, ClientSession> _byEntity = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _sweeper;
    private volatile bool _shuttingDown;

    /// <summary>Creates the gateway for <paramref name="world"/> and subscribes to its instances' events.</summary>
    public Gateway(GameWorld world, NewCharacters start, GatewayOptions options, IClock clock, IServerLog log, string contentHash, ILoginService? login = null)
    {
        _world = world;
        _start = start;
        Options = options;
        Clock = clock;
        Log = log;
        ContentHash = contentHash;
        Login = login ?? new DevLogin(options.Development, options.MaxNameLength);
        if (!world.TryGetInstance(start.MapId, out _)) throw new ArgumentException($"No instance for the start map '{start.MapId}'.", nameof(start));
        foreach (var instance in world.Instances)
        {
            instance.Events.Subscribe<MoveRejected>(OnMoveRejected);
            instance.Events.Subscribe<EntityEnteredMap>(OnEnteredMap);
            instance.Events.Subscribe<EntityLeftMap>(OnLeftMap);
        }
    }

    /// <summary>The options it runs with.</summary>
    public GatewayOptions Options { get; }

    /// <summary>The clock for timeouts and rate limits.</summary>
    public IClock Clock { get; }

    /// <summary>The log.</summary>
    public IServerLog Log { get; }

    /// <summary>The server's content hash (ARCHITECTURE §5.4): sent in <c>welcome</c>; a client with another hash is told to update.</summary>
    public string ContentHash { get; }

    /// <summary>The login in use (the dev stub unless one was given).</summary>
    public ILoginService Login { get; }

    /// <summary>Counters.</summary>
    public GatewayStats Stats { get; } = new();

    /// <summary>Sessions currently connected (any state).</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>The connected sessions right now.</summary>
    public IReadOnlyCollection<ClientSession> Sessions => _sessions.Keys.ToList();

    /// <summary>Starts the timeout sweeper (and the periodic stats log).</summary>
    public void Start()
    {
        if (_sweeper is not null) throw new InvalidOperationException("Already started.");
        _sweeper = SweepLoopAsync(_stop.Token);
    }

    /// <summary>
    /// Runs one connection to its end: the session's receive and send loops, then removal of its character. The caller owns accepting
    /// the socket and disposes nothing; the session closes and disposes it.
    /// </summary>
    public async Task RunSessionAsync(WebSocket socket, string peer)
    {
        Stats.CountAccepted();
        var session = new ClientSession(this, socket, peer, Clock.NowMilliseconds);
        _sessions[session] = 0;
        if (_shuttingDown) session.Bye(ByeReasons.Shutdown);
        else if (_sessions.Count > Options.MaxSessions) session.Bye(ByeReasons.ServerFull);
        try
        {
            await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(session, out _);
            OnSessionEnded(session);
        }
    }

    /// <summary>Sends <c>bye</c> <c>shutdown</c> to every session and waits (up to the close timeout) for them to end.</summary>
    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        foreach (var session in _sessions.Keys) session.Bye(ByeReasons.Shutdown);
        var deadline = Stopwatch.GetTimestamp();
        while (!_sessions.IsEmpty && Stopwatch.GetElapsedTime(deadline).TotalMilliseconds < Options.CloseTimeoutMs * 2)
            await Task.Delay(20).ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_sweeper is not null) await _sweeper.ConfigureAwait(false);
    }

    /// <summary>Queues an intent for the session's character on whatever instance holds it now (warps included); counts refusals.</summary>
    internal EnqueueResult Enqueue(IIntent intent)
    {
        var result = _world.TryEnqueue(intent);
        if (result != EnqueueResult.Accepted) Stats.CountIntentRefused();
        return result;
    }

    /// <summary>The grid of the map the entity is on (or warping to), or null when it is not in the world.</summary>
    internal MapGrid? GridOf(EntityId id) =>
        _world.LocationOf(id) is { } map && _world.TryGetInstance(map, out var instance) ? instance.Grid : null;

    /// <summary>Queues the session's new character on the start map; it joins on that instance's next tick.</summary>
    internal EntityId SpawnCharacter(ClientSession session)
    {
        var id = _world.Spawn(EntityKind.Player, _start.MapId, _start.Cell, _start.Facing, entity =>
        {
            _byEntity[entity.Id] = session;
            session.OnJoinedOnTick(entity, _start);
        });
        Log.Info($"[net] {session.Peer} joined as '{session.DisplayName}' ({id} on {_start.MapId})");
        return id;
    }

    /// <summary>
    /// The connection ended: the character leaves the world on its instance's next tick. <b>Seam for T-210:</b> instead of despawning at
    /// once, keep the character for the grace period and let a resume token re-attach a new session to it (<c>AttachView</c> + a snapshot).
    /// </summary>
    private void OnSessionEnded(ClientSession session)
    {
        if (session.Entity is { } id) _world.Despawn(id);
        Log.Info($"[net] {session.Peer} left ({session.ByeReason ?? "closed by client"}; sent {session.MessagesSent} messages, {session.BytesSent} bytes)");
    }

    private void OnMoveRejected(MoveRejected e)
    {
        if (_byEntity.TryGetValue(e.Entity, out var session))
            session.Post(new RejectedMessage(MessageTypes.Move, e.Reason, e.Target));
    }

    private void OnEnteredMap(EntityEnteredMap e)
    {
        // A character whose session ended while it was joining or warping is removed now that it has arrived.
        if (_byEntity.TryGetValue(e.Entity, out var session) && session.State == SessionState.Closing && !_sessions.ContainsKey(session))
            _world.Despawn(e.Entity);
    }

    private void OnLeftMap(EntityLeftMap e)
    {
        if (e.Reason == MapChangeReason.Spawn) _byEntity.TryRemove(e.Entity, out _);
    }

    private async Task SweepLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Options.SweepIntervalMs));
        var reportStarted = Stopwatch.GetTimestamp();
        var lastOut = 0L;
        var lastSink = TimeSpan.Zero;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var now = Clock.NowMilliseconds;
                foreach (var session in _sessions.Keys) session.Sweep(now);

                var elapsed = Stopwatch.GetElapsedTime(reportStarted);
                if (elapsed.TotalMilliseconds < InstanceRunner.ReportIntervalMs) continue;
                var joined = _sessions.Keys.Count(s => s.State == SessionState.Joined);
                var bytes = Stats.BytesOut - lastOut;
                var sink = Stats.SinkTime - lastSink;
                if (joined > 0 || bytes > 0)
                {
                    var ticks = elapsed.TotalMilliseconds / TickRate.IntervalMs;
                    Log.Info($"[net] {joined} player(s); out {bytes / elapsed.TotalSeconds / 1024:0.0} KB/s total, " +
                             $"{(joined == 0 ? 0 : bytes / elapsed.TotalSeconds / joined / 1024):0.00} KB/s per player; " +
                             $"view sinks {sink.TotalMicroseconds / ticks:0.0} us per tick (all instances); rate-limited {Stats.RateLimited}");
                }
                reportStarted = Stopwatch.GetTimestamp();
                lastOut = Stats.BytesOut;
                lastSink = Stats.SinkTime;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
