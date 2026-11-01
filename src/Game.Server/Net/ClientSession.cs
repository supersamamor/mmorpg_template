using System.Diagnostics;
using System.Net.WebSockets;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Combat;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Net;

/// <summary>Where a session is in its lifecycle (ARCHITECTURE §10).</summary>
public enum SessionState
{
    /// <summary>Connected; waiting for <c>hello</c>.</summary>
    AwaitingHello,

    /// <summary>Logged in; the character is queued to join its map on the instance's next tick.</summary>
    Joining,

    /// <summary>The character is in the world; <c>welcome</c> was queued and the AOI sends its views.</summary>
    Joined,

    /// <summary><c>bye</c> was queued (or the client left); nothing more is accepted.</summary>
    Closing,
}

/// <summary>
/// One client connection: its socket, its receive loop (parse, validate, rate-limit, map to intents), its send loop (drains the
/// <see cref="SendQueue"/>), and the <see cref="IViewSink"/> the AOI calls on the tick thread. Created and run by <see cref="Gateway"/>.
/// </summary>
/// <remarks>
/// Threads: the receive loop and the send loop are the session's own (thread pool); <see cref="OnSnapshot"/>, <see cref="OnDelta"/> and the
/// join initializer run on the tick thread of the instance the character is on; <see cref="Sweep"/> runs on the gateway's sweeper. Shared
/// fields are volatile or interlocked, and everything those threads hand to the send loop goes through the thread-safe queue.
/// </remarks>
public sealed class ClientSession : IViewSink
{
    private readonly Gateway _gateway;
    private readonly WebSocket _socket;
    private readonly GatewayOptions _options;
    private readonly TokenBucket _bucket;
    private readonly ViewMessageWriter _views = new();
    private readonly Dictionary<long, EntityKind> _visible = new();   // what the client was last told it sees; lock it (tick thread writes, receive loop reads)
    private readonly CancellationTokenSource _abort = new();
    private readonly long _connectedMs;
    private int _state = (int)SessionState.AwaitingHello;
    private long _entity;            // 0 until joined
    private long _lastInboundMs;
    private long _sendStartedMs = -1;
    private long _closingSinceMs = -1;
    private long _bytesSent;
    private long _messagesSent;
    private string? _byeReason;
    private volatile string? _mapId;
    private volatile bool _welcomed;

    internal ClientSession(Gateway gateway, WebSocket socket, string peer, long nowMs)
    {
        _gateway = gateway;
        _socket = socket;
        _options = gateway.Options;
        Peer = peer;
        Address = AddressOf(peer);
        _connectedMs = nowMs;
        _lastInboundMs = nowMs;
        _bucket = new TokenBucket(_options.RateLimitPerSecond, _options.RateLimitBurst, nowMs);
        Queue = new SendQueue(_options.SendQueueMessages, _options.ResyncBelowMessages);
    }

    /// <summary>Remote address and port, for logs.</summary>
    public string Peer { get; }

    /// <summary>Remote address without the port (failed resumes are counted per address).</summary>
    public string Address { get; }

    /// <summary>True once <c>welcome</c> was queued: the client has (or is about to have) a resume token for its character.</summary>
    public bool Welcomed => _welcomed;

    /// <summary>The lifecycle state.</summary>
    public SessionState State => (SessionState)Volatile.Read(ref _state);

    /// <summary>The character's entity, once the login was accepted.</summary>
    public EntityId? Entity
    {
        get
        {
            var value = Interlocked.Read(ref _entity);
            return value == 0 ? null : new EntityId(value);
        }
    }

    /// <summary>The map of the last snapshot sent (follows warps).</summary>
    public string? MapId => _mapId;

    /// <summary>The dev display name, once logged in.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>The reason of the <c>bye</c> sent, or null (none yet, or the client closed first).</summary>
    public string? ByeReason => Volatile.Read(ref _byeReason);

    /// <summary>Bytes handed to the socket (message payloads).</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Messages handed to the socket.</summary>
    public long MessagesSent => Interlocked.Read(ref _messagesSent);

    /// <summary>Messages refused by the rate limit.</summary>
    public long RateLimited => _bucket.Drops;

    /// <summary>The outbound queue.</summary>
    public SendQueue Queue { get; }

    /// <summary>Test hook: while set, the send loop sends nothing (a client that stopped reading, without filling real TCP buffers).</summary>
    internal ManualResetEventSlim SendGate { get; } = new(true);

    private bool Closing => State == SessionState.Closing;

    // ---- the AOI's sink: tick thread ----

    /// <inheritdoc />
    public void OnSnapshot(ViewSnapshot snapshot)
    {
        if (Closing) return;
        var started = Stopwatch.GetTimestamp();
        _mapId = snapshot.MapId;
        lock (_visible)
        {
            _visible.Clear();
            var entities = snapshot.Entities;
            for (var i = 0; i < entities.Count; i++) _visible[entities[i].Id.Value] = entities[i].Kind;
        }
        if (!Queue.PostSnapshot(_views.Write(snapshot))) Bye(ByeReasons.TooSlow);
        _gateway.PostExp(snapshot.Observer); // a fresh view (join, resume, warp, resync) comes with the own levels and EXP
        _gateway.Stats.AddSinkTime(Stopwatch.GetTimestamp() - started);
    }

    /// <inheritdoc />
    public void OnDelta(ViewDelta delta)
    {
        if (Closing) return;
        var started = Stopwatch.GetTimestamp();
        if (delta.Appeared.Count > 0 || delta.Disappeared.Count > 0)
        {
            lock (_visible)
            {
                var appeared = delta.Appeared;
                for (var i = 0; i < appeared.Count; i++) _visible[appeared[i].Id.Value] = appeared[i].Kind;
                var gone = delta.Disappeared;
                for (var i = 0; i < gone.Count; i++) _visible.Remove(gone[i].Value);
            }
        }
        if (Queue.Resyncing) Queue.PostDelta([], 0);  // dropped and counted without serializing: the client is behind anyway
        else Queue.PostDelta(_views.Write(delta), _gateway.Clock.NowMilliseconds);
        _gateway.Stats.AddSinkTime(Stopwatch.GetTimestamp() - started);
    }

    /// <summary>Called on the tick thread when the character is added to its map, before the AOI first sees it.</summary>
    /// <param name="entity">The new character.</param>
    /// <param name="start">Where and how new characters start.</param>
    /// <param name="token">Its first resume token, or null when the session ended before the gateway linked the character.</param>
    internal void OnJoinedOnTick(Entity entity, NewCharacters start, string? token)
    {
        Interlocked.Exchange(ref _entity, entity.Id.Value);
        entity.Stats = start.CreateStats();
        entity.DefinitionId = start.JobId;
        if (Closing || token is null) return; // the gateway has released it; it leaves on the next tick
        _mapId = entity.MapId;
        Post(new WelcomeMessage(ProtocolVersion.Current, entity.Id.Value, entity.MapId, TickRate.Hz, _gateway.ContentHash, token));
        _welcomed = true;
        entity.View = this; // the AOI sends the first snapshot in this tick's phase 5, after the welcome
        Interlocked.CompareExchange(ref _state, (int)SessionState.Joined, (int)SessionState.Joining);
    }

    /// <summary>
    /// Called by the gateway, under its link lock, when this session has taken over <paramref name="id"/> with a resume token: queues
    /// <c>welcome</c> (with the new <paramref name="token"/>); the gateway then attaches the view, so a full snapshot follows.
    /// </summary>
    internal void OnResumed(EntityId id, string? displayName, string mapId, string token)
    {
        DisplayName = displayName;
        _mapId = mapId;
        Interlocked.Exchange(ref _entity, id.Value);
        _welcomed = true; // from here a drop starts a new grace period rather than removing the character
        Post(new WelcomeMessage(ProtocolVersion.Current, id.Value, mapId, TickRate.Hz, _gateway.ContentHash, token));
        Interlocked.CompareExchange(ref _state, (int)SessionState.Joined, (int)SessionState.AwaitingHello);
    }

    /// <summary>Queues a control message (welcome, rejected, pong); a hopelessly full queue ends the session.</summary>
    internal void Post(ServerMessage message)
    {
        if (Closing) return;
        if (!Queue.PostControl(ProtocolJson.ToBytes(message))) Bye(ByeReasons.TooSlow);
    }

    // ---- lifecycle ----

    /// <summary>
    /// Queues <c>bye</c> and starts closing; the send loop sends it and the close frame. Only the first call counts. Thread-safe.
    /// </summary>
    public void Bye(string reason)
    {
        if (Interlocked.CompareExchange(ref _byeReason, reason, null) is not null) return;
        BeginClosing();
        Queue.PostFinal(ProtocolJson.ToBytes(new ByeMessage(reason)));
        _gateway.Stats.CountBye(reason);
    }

    private void BeginClosing()
    {
        Volatile.Write(ref _state, (int)SessionState.Closing);
        Interlocked.CompareExchange(ref _closingSinceMs, _gateway.Clock.NowMilliseconds, -1);
    }

    /// <summary>Checks this session's timeouts at <paramref name="nowMs"/> (gateway sweeper).</summary>
    internal void Sweep(long nowMs)
    {
        var state = State;
        if (state == SessionState.Closing)
        {
            if (nowMs - Interlocked.Read(ref _closingSinceMs) >= _options.CloseTimeoutMs) _abort.Cancel();
            return;
        }
        if (state == SessionState.AwaitingHello && nowMs - _connectedMs >= _options.HelloTimeoutMs) Bye(ByeReasons.Timeout);
        else if (nowMs - Interlocked.Read(ref _lastInboundMs) >= _options.IdleTimeoutMs) Bye(ByeReasons.Timeout);
        else if (Queue.BehindSinceMs is { } behind && nowMs - behind >= _options.SlowClientTimeoutMs) Bye(ByeReasons.TooSlow);
        else if (Interlocked.Read(ref _sendStartedMs) is var sending && sending >= 0 && nowMs - sending >= _options.SlowClientTimeoutMs)
        {
            Bye(ByeReasons.TooSlow);
            _abort.Cancel(); // the socket is not taking data: the bye could never be sent
        }
    }

    /// <summary>Runs the session until the connection ends; then the gateway removes the character.</summary>
    internal async Task RunAsync()
    {
        var send = SendLoopAsync();
        try
        {
            await ReceiveLoopAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // dropped connection or aborted socket: normal on mobile
        }
        catch (Exception e)
        {
            _gateway.Log.Error($"[net] session {Peer}: receive loop failed.", e);
            Bye(ByeReasons.ServerError);
        }

        BeginClosing();
        Queue.PostFinal([]); // a client that left gets no bye; this only ends the send loop
        try
        {
            await send.WaitAsync(TimeSpan.FromMilliseconds(_options.CloseTimeoutMs)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // timed out or failed: the socket is aborted below either way
        }
        if (_socket.State == WebSocketState.CloseReceived)
        {
            // the client closed first: answer its close frame so it sees a clean close
            using var timeout = new CancellationTokenSource(_options.CloseTimeoutMs);
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
            {
                // gone already
            }
        }
        await _abort.CancelAsync().ConfigureAwait(false);
        _socket.Abort();
        _socket.Dispose();
        SendGate.Set();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[_options.MaxMessageBytes];
        var count = 0;
        var token = _abort.Token;
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            count += result.Count;
            if (!result.EndOfMessage)
            {
                if (count < buffer.Length) continue;
                // Too long: say so, then discard the rest of this and any later message until the close handshake ends.
                Bye(ByeReasons.Oversize);
                count = 0;
                continue;
            }
            var length = count;
            count = 0;
            if (Closing) continue; // after bye, input is ignored until the client answers the close
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                Bye(ByeReasons.Malformed);
                continue;
            }
            Handle(buffer.AsSpan(0, length));
        }
    }

    private void Handle(ReadOnlySpan<byte> utf8)
    {
        var now = _gateway.Clock.NowMilliseconds;
        Interlocked.Exchange(ref _lastInboundMs, now);
        _gateway.Stats.CountInbound(utf8.Length);
        if (!_bucket.TryTake(now))
        {
            _gateway.Stats.CountRateLimited();
            if (_bucket.DropsSinceFull >= _options.RateLimitDropsBeforeBye) Bye(ByeReasons.RateLimit);
            return;
        }

        var read = ProtocolJson.ReadClient(utf8);
        if (read.Message is not { } message)
        {
            Bye(read.Error ?? ByeReasons.Malformed);
            return;
        }

        if (State == SessionState.AwaitingHello)
        {
            if (message is HelloMessage hello) Join(hello);
            else Bye(ByeReasons.BeforeHello);
            return;
        }

        switch (message)
        {
            case HelloMessage:
                Bye(ByeReasons.DuplicateHello);
                break;
            case MoveMessage move:
                RequestMove(move);
                break;
            case StopMessage:
                if (Entity is { } id)
                {
                    // Both: a stop while standing still produces no movement event, so combat would never hear of it (ADR-002 #80).
                    _gateway.Enqueue(new StopMove(id));
                    _gateway.Enqueue(new StopAttack(id));
                }
                break;
            case AttackMessage attack:
                RequestAttack(attack);
                break;
            case StopAttackMessage:
                if (Entity is { } attacker) _gateway.Enqueue(new StopAttack(attacker));
                break;
            case RespawnMessage:
                if (Entity is { } dead) _gateway.Enqueue(new Respawn(dead)); // combat ignores a living character
                break;
            case PingMessage ping:
                Post(new PongMessage(ping.Seq));
                break;
            case LogoutMessage:
                _gateway.Logout(this);
                break;
        }
    }

    private void Join(HelloMessage hello)
    {
        if (hello.Version != ProtocolVersion.Current)
        {
            Bye(ByeReasons.BadVersion);
            return;
        }
        if (hello.Content is null)
        {
            if (!_gateway.Options.Development)
            {
                Bye(ByeReasons.UpdateRequired);
                return;
            }
            _gateway.Log.Warn($"[net] session {Peer}: hello without a content hash accepted (development only).");
        }
        else if (!string.Equals(hello.Content, _gateway.ContentHash, StringComparison.Ordinal))
        {
            Bye(ByeReasons.UpdateRequired);
            return;
        }
        if (hello.Resume is not null)
        {
            _gateway.Resume(this, hello);
            return;
        }
        var login = _gateway.Login.Login(hello);
        if (login.Refusal is { } refusal)
        {
            Bye(refusal);
            return;
        }
        DisplayName = login.DisplayName;
        Volatile.Write(ref _state, (int)SessionState.Joining);
        var id = _gateway.SpawnCharacter(this);
        Interlocked.CompareExchange(ref _entity, id.Value, 0);
    }

    /// <summary>Checks the target against the current map's bounds, then queues the intent; the movement system decides the rest.</summary>
    private void RequestMove(MoveMessage move)
    {
        if (Entity is not { } id) return;
        var grid = _gateway.GridOf(id);
        if (grid is null) return; // between maps or leaving: nothing to walk on
        if (!grid.InBounds(move.Cell.X, move.Cell.Y))
        {
            Post(new RejectedMessage(MessageTypes.Move, MovementReasons.OutOfBounds, move.Cell));
            return;
        }
        _gateway.Enqueue(new MoveTo(id, move.Cell));
    }

    /// <summary>
    /// The target must be in the client's view (else <c>target_gone</c>: it does not exist for this client) and a monster other than itself
    /// (else <c>invalid_target</c>: no PvP in v1). Combat checks the rest on the tick (dead, safe zone) and may still refuse.
    /// </summary>
    private void RequestAttack(AttackMessage attack)
    {
        if (Entity is not { } id) return;
        EntityKind kind;
        bool seen;
        lock (_visible) seen = _visible.TryGetValue(attack.Target, out kind);
        string? reason = !seen ? AttackReasons.TargetGone : attack.Target == id.Value || kind != EntityKind.Monster ? AttackReasons.InvalidTarget : null;
        if (reason is not null)
        {
            Post(new RejectedMessage(MessageTypes.Attack, reason, null, attack.Target));
            return;
        }
        _gateway.Enqueue(new AttackTarget(id, new EntityId(attack.Target)));
    }

    private async Task SendLoopAsync()
    {
        await Task.Yield();
        var token = _abort.Token;
        try
        {
            while (true)
            {
                await Queue.WaitAsync(token).ConfigureAwait(false);
                while (true)
                {
                    SendGate.Wait(token);
                    var took = Queue.TryTake(out var bytes, out var requestSnapshot);
                    if (requestSnapshot && Entity is { } id && _gateway.Enqueue(new RequestSnapshot(id)) != EnqueueResult.Accepted)
                        Queue.ResyncRequestFailed();
                    if (!took) break;
                    if (bytes.Length == 0) continue; // the end marker of a client that left
                    Interlocked.Exchange(ref _sendStartedMs, _gateway.Clock.NowMilliseconds);
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _sendStartedMs, -1);
                    Interlocked.Add(ref _bytesSent, bytes.Length);
                    Interlocked.Increment(ref _messagesSent);
                    _gateway.Stats.CountOutbound(bytes.Length);
                }
                if (Queue.Completed && Queue.Count == 0) break;
            }

            if (ByeReason is { } reason && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(CloseStatusFor(reason), reason, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // the connection went away under us
        }
    }

    /// <summary>The address part of <c>address:port</c> (IPv6 addresses contain colons too, so the last one splits).</summary>
    internal static string AddressOf(string peer)
    {
        var colon = peer.LastIndexOf(':');
        return colon > 0 ? peer[..colon] : peer;
    }

    /// <summary>The WebSocket close status that goes with a bye reason.</summary>
    public static WebSocketCloseStatus CloseStatusFor(string reason) => reason switch
    {
        ByeReasons.Oversize => WebSocketCloseStatus.MessageTooBig,
        ByeReasons.Shutdown or ByeReasons.ServerFull => WebSocketCloseStatus.EndpointUnavailable,
        ByeReasons.ServerError => WebSocketCloseStatus.InternalServerError,
        ByeReasons.Timeout or ByeReasons.TooSlow or ByeReasons.Logout or ByeReasons.Replaced => WebSocketCloseStatus.NormalClosure,
        _ => WebSocketCloseStatus.PolicyViolation,
    };
}
