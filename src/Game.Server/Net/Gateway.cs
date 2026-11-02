using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Game.Server.Hosting;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Combat;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Time;
using Game.Shared.World;

namespace Game.Server.Net;

/// <summary>Gateway-wide counters. Thread-safe; read them any time.</summary>
public sealed class GatewayStats
{
    private readonly ConcurrentDictionary<string, long> _byes = new(StringComparer.Ordinal);
    private long _accepted, _bytesIn, _messagesIn, _bytesOut, _messagesOut, _rateLimited, _intentsRefused, _sinkTicks, _sinkCalls, _wireOut;

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

    /// <summary>
    /// Bytes written to client sockets: TLS records or WebSocket frames (compressed when permessage-deflate is on), HTTP upgrade responses
    /// included. What a phone actually downloads; compare with <see cref="BytesOut"/> (JSON payloads).
    /// </summary>
    public long WireBytesOut => Interlocked.Read(ref _wireOut);

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

    internal void CountWireOut(int bytes) => Interlocked.Add(ref _wireOut, bytes);

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
/// forwards their intents to the instance that holds the character, keeps a character whose connection dropped for the grace period so a
/// resume token can hand it to a new session, and removes it on <c>logout</c>, at the end of the grace period, or at shutdown. Owns no
/// socket listener: <see cref="GatewayHost"/> accepts connections and calls <see cref="RunSessionAsync"/>.
/// </summary>
/// <remarks>
/// Create it before the instances start ticking: the constructor subscribes to every instance's event bus (<c>MoveRejected</c> and a
/// player's <c>AttackRefused</c> → <c>rejected</c>; the owner-only combat messages from <c>AttackStopped</c>, <c>EntityDied</c>,
/// <c>PlayerRespawned</c>, <c>ExpGained</c>, <c>ExpLost</c> and <c>PlayerProgressed</c>; <c>EntityEnteredMap</c> / <c>EntityLeftMap</c> to track
/// characters). Those handlers run on tick threads and only look up a session, read its character's progression, queue bytes or queue intents.
/// <para>Every change to which session plays a character (join, resume and takeover, drop, logout, end of the grace period, shutdown)
/// happens under one lock, so a character never has two sessions and a resume never races its own expiry.</para>
/// </remarks>
public sealed class Gateway
{
    private readonly GameWorld _world;
    private readonly NewCharacters _start;
    private readonly ConcurrentDictionary<ClientSession, byte> _sessions = new();
    private readonly object _linkLock = new();
    private readonly ConcurrentDictionary<EntityId, CharacterLink> _links = new();
    private int _joining;   // logins accepted whose character is not linked yet (each holds a slot); under the link lock
    private readonly ResumeGuard _resumeGuard;
    private readonly CancellationTokenSource _stop = new();
    private Task? _sweeper;
    private volatile bool _shuttingDown;

    /// <summary>Creates the gateway for <paramref name="world"/> and subscribes to its instances' events.</summary>
    public Gateway(GameWorld world, NewCharacters start, GatewayOptions options, IClock clock, IServerLog log, string contentHash, ILoginService? login = null,
        ResumeTokens? tokens = null, OwnProgress? progress = null)
    {
        _world = world;
        _start = start;
        Options = options;
        Clock = clock;
        Log = log;
        ContentHash = contentHash;
        Tokens = tokens ?? new ResumeTokens();
        Login = login ?? new DevLogin(options.Development, options.MaxNameLength, Tokens);
        Progress = progress;
        _resumeGuard = new ResumeGuard(options);
        if (!world.TryGetInstance(start.MapId, out _)) throw new ArgumentException($"No instance for the start map '{start.MapId}'.", nameof(start));
        foreach (var instance in world.Instances)
        {
            var events = instance.Events;
            events.Subscribe<MoveRejected>(OnMoveRejected);
            events.Subscribe<EntityEnteredMap>(OnEnteredMap);
            events.Subscribe<EntityLeftMap>(OnLeftMap);
            // Combat, owner only (ARCHITECTURE §10): what happened to the player's own character.
            events.Subscribe<AttackRefused>(e => Owner(e.Attacker)?.Post(new RejectedMessage(MessageTypes.Attack, e.Reason, null, e.Target.Value)));
            events.Subscribe<AttackStopped>(e => Owner(e.Attacker)?.Post(new AttackStoppedMessage(e.Target.Value, e.Reason)));
            events.Subscribe<EntityDied>(e =>
            {
                if (e.Kind == EntityKind.Player) Owner(e.Entity)?.Post(new YouDiedMessage(e.Killer?.Value));
            });
            events.Subscribe<PlayerRespawned>(e => Owner(e.Player)?.Post(new RespawnedMessage(e.MapId, e.Cell, e.Hp)));
            events.Subscribe<ExpGained>(e => PostExp(e.Player));
            events.Subscribe<ExpLost>(e => PostExp(e.Player));
            events.Subscribe<PlayerProgressed>(OnProgressed);
        }
    }

    /// <summary>Where players' <c>exp</c> messages come from, or null when the gateway sends none (tests without combat).</summary>
    public OwnProgress? Progress { get; }

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

    /// <summary>The live resume tokens (in memory: a restart invalidates them all).</summary>
    public ResumeTokens Tokens { get; }

    /// <summary>Characters the gateway holds: played by a session or waiting in their grace period.</summary>
    public int CharacterCount => _links.Count;

    /// <summary>
    /// Slots in use for <see cref="GatewayOptions.MaxSessions"/> (ADR-002 #56): characters held (played or in their grace period) plus logins
    /// still joining. A new login on a full server is refused; a resume is always let in (it reuses its character's slot).
    /// </summary>
    public int SlotsInUse
    {
        get { lock (_linkLock) return _links.Count + _joining; }
    }

    /// <summary>Characters waiting in their grace period.</summary>
    public int CharactersInGrace => _links.Values.Count(l => l.InGrace);

    /// <summary>The gateway's link for <paramref name="character"/>, or null when it does not hold it.</summary>
    public CharacterLink? LinkOf(EntityId character) => _links.TryGetValue(character, out var link) ? link : null;

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
        // Only a flood of connections that have not said hello is refused here; whether a login fits is decided at its hello (a resume must
        // always get in, and characters in their grace period hold slots).
        else if (_sessions.Keys.Count(s => s.State == SessionState.AwaitingHello) > Options.MaxSessions) session.Bye(ByeReasons.ServerFull);
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

    /// <summary>
    /// Sends <c>bye</c> <c>shutdown</c> to every session, waits (up to the close timeout) for them to end, then removes every character
    /// it still holds, those in their grace period included (T-702 saves them first). No resume is accepted from here on.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        foreach (var session in _sessions.Keys) session.Bye(ByeReasons.Shutdown);
        var deadline = Stopwatch.GetTimestamp();
        while (!_sessions.IsEmpty && Stopwatch.GetElapsedTime(deadline).TotalMilliseconds < Options.CloseTimeoutMs * 2)
            await Task.Delay(20).ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_sweeper is not null) await _sweeper.ConfigureAwait(false);
        lock (_linkLock)
            foreach (var link in _links.Values.ToList())
                Release(link, "server shutdown");
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

    /// <summary>
    /// Queues the session's new character on the start map, or returns null when the server is full (<see cref="SlotsInUse"/>): nobody is
    /// evicted to make room. The character joins on that instance's next tick. There, unless the session has already ended, the gateway links
    /// the character to the session, issues its first resume token, and the session queues <c>welcome</c>.
    /// </summary>
    internal EntityId? SpawnCharacter(ClientSession session)
    {
        lock (_linkLock)
        {
            if (_links.Count + _joining >= Options.MaxSessions)
            {
                Log.Warn($"[net] {session.Peer}: login refused, the server is full ({_links.Count} characters held, " +
                         $"{_links.Values.Count(l => l.InGrace)} of them in their grace period; {_joining} joining).");
                return null;
            }
            _joining++;
            session.HoldsSlot = true;
        }
        var id = _world.Spawn(EntityKind.Player, _start.MapId, _start.Cell, _start.Facing, entity =>
        {
            string? token = null;
            lock (_linkLock)
            {
                FreeJoiningSlot(session); // from here the link holds the slot, or the character leaves
                if (session.State != SessionState.Closing)
                {
                    _links[entity.Id] = new CharacterLink(entity.Id, session.DisplayName, session);
                    token = Tokens.Issue(entity.Id);
                }
            }
            session.OnJoinedOnTick(entity, _start, token);
        });
        Log.Info($"[net] {session.Peer} joined as '{session.DisplayName}' ({id} on {_start.MapId})");
        return id;
    }

    /// <summary>
    /// <c>hello</c> with <c>resume</c> (version and content hash already checked): if the address is not locked out and the login service
    /// knows the token, the token is redeemed and rotated, the session takes the character over wherever it is now (a session still playing
    /// it gets <c>bye</c> <c>replaced</c>), and gets <c>welcome</c> with the new token, then a full snapshot, then deltas. Otherwise
    /// <c>bye</c> <c>resume_failed</c>, or <c>rate_limit</c> once the address has failed too often. Runs on the session's receive loop.
    /// </summary>
    internal void Resume(ClientSession session, HelloMessage hello)
    {
        var now = Clock.NowMilliseconds;
        if (_resumeGuard.IsLockedOut(session.Address, now))
        {
            Log.Warn($"[net] {session.Peer}: resume refused, the address is locked out after failed resumes.");
            session.Bye(ByeReasons.RateLimit);
            return;
        }

        var check = Login.Resume(hello);
        CharacterLink? taken = null;
        ClientSession? replaced = null;
        string token = string.Empty, map = string.Empty;
        if (check.Character is { } id && !_shuttingDown)
        {
            lock (_linkLock)
            {
                if (_links.TryGetValue(id, out var link) && _world.LocationOf(id) is { } where && Tokens.TryRotate(hello.Resume!, id, out token))
                {
                    replaced = link.Session;
                    link.Session = session;
                    taken = link;
                    map = where;
                    // Welcome first, then the view: the AOI snapshots a new sink in the tick that attaches it. Inside the lock so that a drop
                    // (grace: detach) or a warp arrival (re-attach) cannot queue its view change between these.
                    session.OnResumed(id, link.DisplayName, map, token);
                    Enqueue(new AttachView(id, session));
                    Enqueue(new RequestSnapshot(id));
                }
            }
        }

        if (taken is null)
        {
            var lockedOut = _resumeGuard.Fail(session.Address, now);
            Log.Warn($"[net] {session.Peer}: resume failed ({(check.Character is null ? "unknown, used or expired token" : "token already used or character gone")})" +
                     (lockedOut ? $"; address locked out for {Options.ResumeLockoutMs} ms." : "."));
            session.Bye(lockedOut ? ByeReasons.RateLimit : ByeReasons.ResumeFailed);
            return;
        }
        replaced?.Bye(ByeReasons.Replaced);
        Log.Info($"[net] {session.Peer} resumed '{taken.DisplayName}' ({taken.Character} on {map})" +
                 $"{(replaced is null ? "" : $", replacing {replaced.Peer}")}; new token {ResumeTokens.Fingerprint(token)}");
    }

    /// <summary>The session's join is over (linked, or the session ended first): its reserved slot is given back once. Under the link lock.</summary>
    private void FreeJoiningSlot(ClientSession session)
    {
        if (!session.HoldsSlot) return;
        session.HoldsSlot = false;
        _joining--;
    }

    /// <summary>
    /// A resumed session sent its first message, so its client holds the new token: the token the resume redeemed stops working (ADR-002 #55).
    /// Only while the session still plays the character (a later resume may already have taken it over).
    /// </summary>
    internal void Acknowledge(ClientSession session)
    {
        if (session.Entity is not { } id) return;
        lock (_linkLock)
            if (_links.TryGetValue(id, out var link) && link.Session == session && Tokens.Acknowledge(id))
                Log.Info($"[net] {session.Peer} acknowledged its new token for '{link.DisplayName}' ({id}); the previous one is revoked.");
    }

    /// <summary><c>logout</c>: the character leaves on its instance's next tick, with no grace period; then <c>bye</c> <c>logout</c>.</summary>
    internal void Logout(ClientSession session)
    {
        if (session.Entity is { } id)
        {
            lock (_linkLock)
            {
                if (!_links.TryGetValue(id, out var link)) ReleaseUnlinked(id); // still joining: the link does not exist yet
                else if (link.Session == session) Release(link, "logout");
            }
        }
        session.Bye(ByeReasons.Logout);
    }

    /// <summary>
    /// The connection ended. A character whose player got its <c>welcome</c> stays in the world for the grace period (stopped, its view
    /// detached, still visible to others) and can be resumed. That holds for every ending (the client closed or vanished, idle timeout,
    /// <c>too_slow</c>, a protocol error, a server error) except <c>logout</c> (already gone), <c>replaced</c> (another session plays it)
    /// and shutdown. A character still joining (no welcome, so no token) is removed at once.
    /// </summary>
    private void OnSessionEnded(ClientSession session)
    {
        lock (_linkLock) FreeJoiningSlot(session);
        if (session.Entity is { } id)
        {
            lock (_linkLock)
            {
                if (!_links.TryGetValue(id, out var link)) ReleaseUnlinked(id);
                else if (link.Session != session) { } // replaced by a resume: the character is the new session's
                else if (_shuttingDown) Release(link, "server shutdown");
                else if (!session.Welcomed) Release(link, "left before its welcome");
                else StartGrace(link);
            }
        }
        Log.Info($"[net] {session.Peer} left ({session.ByeReason ?? "closed by client"}; sent {session.MessagesSent} messages, {session.BytesSent} bytes)");
    }

    /// <summary>Starts the grace period of a character whose session ended. Under the link lock.</summary>
    private void StartGrace(CharacterLink link)
    {
        link.Session = null;
        link.GraceEndsMs = Clock.NowMilliseconds + Options.GracePeriodMs;
        Enqueue(new StopMove(link.Character));
        Enqueue(new StopAttack(link.Character));   // a dropped player does not farm on: stopped means stopped (it can still be attacked)
        Enqueue(new AttachView(link.Character, null));
        Log.Info($"[net] '{link.DisplayName}' ({link.Character}) waits {Options.GracePeriodMs} ms for a resume.");
    }

    /// <summary>
    /// The character leaves the world on its instance's next tick and its token stops working. Under the link lock.
    /// <b>Seam for T-702:</b> save the character here, before it leaves (logout, end of the grace period, shutdown).
    /// </summary>
    private void Release(CharacterLink link, string why)
    {
        _links.TryRemove(link.Character, out _);
        ReleaseUnlinked(link.Character);
        Log.Info($"[net] '{link.DisplayName}' ({link.Character}) leaves the world: {why}.");
    }

    /// <summary>
    /// Despawns a character the gateway holds no link for (or no longer). The world keeps the removal even if the character is between maps
    /// (still joining, or warping), so it never turns up again.
    /// </summary>
    private void ReleaseUnlinked(EntityId id)
    {
        Tokens.Revoke(id);
        _world.Despawn(id);
    }

    /// <summary>Removes characters whose grace period is over (gateway sweeper).</summary>
    private void ExpireGrace(long nowMs)
    {
        foreach (var link in _links.Values)
        {
            if (!link.InGrace || nowMs < link.GraceEndsMs) continue;
            lock (_linkLock)
                if (link.InGrace && nowMs >= link.GraceEndsMs && _links.TryGetValue(link.Character, out var current) && current == link)
                    Release(link, "grace period over");
        }
    }

    private void OnMoveRejected(MoveRejected e) => Owner(e.Entity)?.Post(new RejectedMessage(MessageTypes.Move, e.Reason, e.Target));

    /// <summary>The session playing <paramref name="character"/> right now, or null (not a player, or in its grace period).</summary>
    private ClientSession? Owner(EntityId character) => _links.TryGetValue(character, out var link) ? link.Session : null;

    /// <summary>Tick thread of the player's instance (the only thread that touches its progression).</summary>
    internal void PostExp(EntityId player)
    {
        if (Progress?.For(player) is { } exp) Owner(player)?.Post(exp);
    }

    private void OnProgressed(PlayerProgressed e)
    {
        if (Owner(e.Player) is not { } session) return;
        if (e.Change is LevelUp up) session.Post(new LevelUpMessage(up.NewLevel, null));
        else if (e.Change is JobLevelUp jobUp) session.Post(new LevelUpMessage(null, jobUp.NewLevel));
    }

    private void OnEnteredMap(EntityEnteredMap e)
    {
        // A view change queued while the character was between maps was dropped (it was on neither instance): say it again. The AOI ignores
        // a sink that is already set, so this costs nothing when nothing was lost.
        if (e.Reason == MapChangeReason.Warp && _links.ContainsKey(e.Entity))
            lock (_linkLock)
                if (_links.TryGetValue(e.Entity, out var link))
                    Enqueue(new AttachView(e.Entity, link.Session));
    }

    private void OnLeftMap(EntityLeftMap e)
    {
        if (e.Reason != MapChangeReason.Spawn) return;
        if (!_links.ContainsKey(e.Entity)) return;
        lock (_linkLock)
        {
            // Removed by something other than the gateway: forget it, so its token stops working.
            if (_links.TryRemove(e.Entity, out var link))
            {
                Tokens.Revoke(e.Entity);
                Log.Warn($"[net] '{link.DisplayName}' ({e.Entity}) left the world without the gateway; its token is revoked.");
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Options.SweepIntervalMs));
        var reportStarted = Stopwatch.GetTimestamp();
        var lastOut = 0L;
        var lastWire = 0L;
        var lastSink = TimeSpan.Zero;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var now = Clock.NowMilliseconds;
                foreach (var session in _sessions.Keys) session.Sweep(now);
                ExpireGrace(now);
                _resumeGuard.Prune(now);

                var elapsed = Stopwatch.GetElapsedTime(reportStarted);
                if (elapsed.TotalMilliseconds < InstanceRunner.ReportIntervalMs) continue;
                var joined = _sessions.Keys.Count(s => s.State == SessionState.Joined);
                var bytes = Stats.BytesOut - lastOut;
                var wire = Stats.WireBytesOut - lastWire;
                var sink = Stats.SinkTime - lastSink;
                if (joined > 0 || bytes > 0 || !_links.IsEmpty)
                {
                    var ticks = elapsed.TotalMilliseconds / TickRate.IntervalMs;
                    var perPlayer = joined == 0 ? 0 : 1.0 / elapsed.TotalSeconds / joined / 1024;
                    Log.Info($"[net] {joined} player(s), {CharactersInGrace} in their grace period, {SlotsInUse}/{Options.MaxSessions} slots; " +
                             $"out {bytes / elapsed.TotalSeconds / 1024:0.0} KB/s total, {bytes * perPlayer:0.00} KB/s per player, " +
                             $"wire {wire * perPlayer:0.00} KB/s per player{(Options.Compression ? " (permessage-deflate on)" : "")}; " +
                             $"view sinks {sink.TotalMicroseconds / ticks:0.0} us per tick (all instances); rate-limited {Stats.RateLimited}");
                    // Tick cost of the last minute (TickTimings window) and the process's memory, for the load test (T-214) and operators.
                    var stats = _world.Instances.Select(i => i.GetStats());
                    Log.Info($"[net] ticks (last {TickTimings.Capacity / TickRate.Hz} s): " +
                             string.Join("; ", stats.Select(t => $"{t.MapId} p50 {t.Timings.P50Micros / 1000.0:0.00} ms, p99 {t.Timings.P99Micros / 1000.0:0.00} ms, " +
                                                                 $"max {t.Timings.MaxMicros / 1000.0:0.00} ms (since start {t.Timings.LifetimeMaxMicros / 1000.0:0.00} ms)")) +
                             $"; memory: GC heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MB, working set {Environment.WorkingSet / 1048576.0:0.0} MB");
                }
                reportStarted = Stopwatch.GetTimestamp();
                lastOut = Stats.BytesOut;
                lastWire = Stats.WireBytesOut;
                lastSink = Stats.SinkTime;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
