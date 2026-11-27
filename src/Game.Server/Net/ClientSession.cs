using System.Diagnostics;
using System.Net.WebSockets;
using Game.Server.Chat;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Autopilot;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Quests;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Server.World.Outfits;

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
public sealed class ClientSession : IViewSink, IChatSink
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
    private string? _closeReason;   // T-806: the bye in the close frame, with its retry_after
    private volatile string? _mapId;
    private volatile bool _welcomed;
    private bool _autopilotTold;             // tick thread: the autopilot's status went out with this session's first snapshot (T-604)
    private string? _sessionKey;
    private volatile bool _unacknowledged;   // resumed, and the client has not spoken since: the redeemed token still works
    private Task _guild = Task.CompletedTask; // T-904: the last guild message's handling, chained like chat's (receive loop only)
    private Task _chat = Task.CompletedTask;  // T-901: the last chat message's handling; the next waits for it, so a player's lines keep their order (receive loop only)

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

    /// <summary>True while this session's login holds a joining slot (the gateway's link lock guards it).</summary>
    internal bool HoldsSlot { get; set; }

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

    /// <summary>The account session its <c>hello</c> named (T-710), from before it is checked: one that ends meanwhile ends this login. Null for a dev login and a resume.</summary>
    internal string? SessionKey => Volatile.Read(ref _sessionKey);

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
        _gateway.PostInventory(snapshot.Observer); // and the whole own inventory
        _gateway.PostSkills(snapshot.MapId, snapshot.Observer); // and the own skills, points and cooldowns
        _gateway.PostStats(snapshot.MapId, snapshot.Observer); // and the own stats, stat points and derived stats (T-503)
        _gateway.PostQuests(snapshot.Observer); // and the own quest log and NPC markers (T-602)
        _gateway.PostWardrobe(snapshot.MapId, snapshot.Observer); // and the own outfits (T-708)
        if (!_autopilotTold)
        {
            _autopilotTold = true;
            _gateway.PostAutopilot(snapshot.MapId, snapshot.Observer); // a session that took a character over learns what became of its autopilot run (T-604)
        }
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
    /// <param name="entity">The character, its state set up.</param>
    /// <param name="token">Its first resume token, or null when the session ended before the gateway linked the character.</param>
    internal void OnJoinedOnTick(Entity entity, string? token)
    {
        Interlocked.Exchange(ref _entity, entity.Id.Value);
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
        _unacknowledged = true;
        Post(new WelcomeMessage(ProtocolVersion.Current, id.Value, mapId, TickRate.Hz, _gateway.ContentHash, token));
        Interlocked.CompareExchange(ref _state, (int)SessionState.Joined, (int)SessionState.AwaitingHello);
    }

    /// <summary>A chat line or answer (T-901), serialized once by chat for everyone it goes to; queued like a control message.</summary>
    void IChatSink.Post(ServerMessage message, byte[] bytes)
    {
        if (Closing) return;
        if (!Queue.PostControl(bytes)) Bye(ByeReasons.TooSlow);
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
    public void Bye(string reason) => Bye(reason, null);

    /// <summary>The same, with how long is left for a <c>banned</c> bye (T-806: seconds, or null when the ban never ends).</summary>
    public void Bye(string reason, long? retryAfterSeconds)
    {
        if (Interlocked.CompareExchange(ref _byeReason, reason, null) is not null) return;
        BeginClosing();
        var bye = new ByeMessage(reason, retryAfterSeconds);
        Volatile.Write(ref _closeReason, ProtocolJson.CloseReasonOf(bye));
        Queue.PostFinal(ProtocolJson.ToBytes(bye));
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
        // The login a hello started (T-701: it may read the database). Reading goes on while it runs, so a connection that drops ends it at once; but the next
        // message waits for it, so nothing is handled before the login is decided.
        Task login = Task.CompletedTask;
        using var loginStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
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
                await login.ConfigureAwait(false);
                if (Closing) continue; // after bye, input is ignored until the client answers the close
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Bye(ByeReasons.Malformed);
                    continue;
                }
                if (Handle(buffer.AsSpan(0, length)) is { } hello) login = JoinAsync(hello, loginStop.Token);
            }
        }
        finally
        {
            // The session must not end while its login still runs: what the login did (a joining slot, a character) is cleaned up after this.
            await loginStop.CancelAsync().ConfigureAwait(false);
            await login.ConfigureAwait(false);
        }
    }

    /// <summary>Handles one message, except a <c>hello</c> that may log in: that one is returned.</summary>
    private HelloMessage? Handle(ReadOnlySpan<byte> utf8)
    {
        var now = _gateway.Clock.NowMilliseconds;
        Interlocked.Exchange(ref _lastInboundMs, now);
        _gateway.Stats.CountInbound(utf8.Length);
        if (!_bucket.TryTake(now))
        {
            _gateway.Stats.CountRateLimited();
            if (_bucket.DropsSinceFull >= _options.RateLimitDropsBeforeBye) Bye(ByeReasons.RateLimit);
            return null;
        }

        var read = ProtocolJson.ReadClient(utf8);
        if (read.Message is not { } message)
        {
            Bye(read.Error ?? ByeReasons.Malformed);
            return null;
        }

        if (State == SessionState.AwaitingHello)
        {
            if (message is HelloMessage hello) return hello;
            Bye(ByeReasons.BeforeHello);
            return null;
        }

        if (_unacknowledged)
        {
            // The first message on a resumed session: the client has the welcome, so the token it resumed with may stop working.
            _unacknowledged = false;
            _gateway.Acknowledge(this);
        }

        // T-604: the player's own orders end an autopilot run, whatever becomes of the order itself. Queued first, so both reach the same tick in this order.
        if (Entity is { } self && IsManualInput(message, out var isOrder)) _gateway.Enqueue(new ManualInput(self, isOrder));

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
            case PickUpMessage pick:
                RequestPickUp(pick);
                break;
            case UseItemMessage use:
                if (Entity is { } user) _gateway.Enqueue(new UseItem(user, use.Entry));
                break;
            case EquipMessage equip:
                if (Entity is { } wearer) _gateway.Enqueue(new Equip(wearer, equip.Entry, equip.Slot));
                break;
            case UnequipMessage unequip:
                if (Entity is { } owner) _gateway.Enqueue(new Unequip(owner, unequip.Slot));
                break;
            case DropItemMessage drop:
                if (Entity is { } dropper) _gateway.Enqueue(new DropItem(dropper, drop.Entry, drop.Quantity));
                break;
            case InsertCardMessage insert:
                if (Entity is { } socketer) _gateway.Enqueue(new InsertCard(socketer, insert.Entry, insert.Card));
                break;
            case RefineMessage refine:
                if (Entity is { } refiner) _gateway.Enqueue(new RefineItem(refiner, refine.Entry));
                break;
            case TalkMessage talk:
                RequestTalk(talk);
                break;
            case DialogueChoiceMessage choice:
                if (Entity is { } chooser) _gateway.Enqueue(new DialogueChoose(chooser, choice.Index));
                break;
            case DialogueCloseMessage:
                if (Entity is { } closer) _gateway.Enqueue(new DialogueClose(closer));
                break;
            case ShopBuyMessage buy:
                if (Entity is { } buyer) _gateway.Enqueue(new ShopBuy(buyer, buy.Item, buy.Quantity));
                break;
            case ShopSellMessage sell:
                if (Entity is { } seller) _gateway.Enqueue(new ShopSell(seller, sell.Entry, sell.Quantity));
                break;
            case StartAutopilotMessage start:
                if (Entity is { } starter) _gateway.Enqueue(new StartAutopilot(starter, start.Quests, start.Skills));
                break;
            case StopAutopilotMessage:
                if (Entity is { } stopper) _gateway.Enqueue(new StopAutopilot(stopper, AutopilotStops.Stopped));
                break;
            case AbandonQuestMessage abandon:
                if (Entity is { } quitter) _gateway.Enqueue(new AbandonQuest(quitter, abandon.Quest));
                break;
            case UseSkillMessage cast:
                RequestSkill(cast);
                break;
            case LearnSkillMessage learn:
                if (Entity is { } learner) _gateway.Enqueue(new LearnSkill(learner, learn.Skill));
                break;
            case RaiseStatMessage raise:
                if (Entity is { } raiser) _gateway.Enqueue(new RaiseStat(raiser, raise.Stat, raise.Amount));
                break;
            case SetOutfitMessage { Hidden: { } hidden }:
                if (Entity is { } hider) _gateway.Enqueue(new HideOutfits(hider, hidden));
                break;
            case SetOutfitMessage { Slot: { } outfitSlot } outfit:
                if (Entity is { } dresser) _gateway.Enqueue(new SetOutfit(dresser, outfitSlot, outfit.Outfit));
                break;
            case ChangeJobMessage job:
                if (Entity is { } changer) _gateway.Enqueue(new ChangeJob(changer, job.Job));
                break;
            case ChatMessage or BlockMessage or UnblockMessage or ReportMessage:
                Chat(message);
                break;
            case GuildFoundMessage or GuildInviteMessage or GuildAnswerMessage or GuildLeaveMessage or GuildKickMessage or GuildRankMessage or GuildRanksMessage or GuildLeadMessage
                or GuildNoticeMessage:
                Guild(message);
                break;
            case PartyInviteMessage or PartyAnswerMessage or PartyLeaveMessage or PartyKickMessage or PartyLeadMessage:
                // T-902: decided at once, in memory, off the tick; ignored before the welcome and on a server without parties
                if (_gateway.Party is { } party && State == SessionState.Joined && _gateway.ChatPeerOf(this) is { } member) party.Handle(member, message);
                break;
            // T-903: intents only; the trade system decides on the tick. An ask goes through the gate (rate limit, blocks) first.
            case TradeAskMessage ask:
                if (Entity is { } asker && State == SessionState.Joined)
                {
                    if (_gateway.Trade is null) _gateway.Enqueue(new Game.Server.World.Trade.TradeAsk(asker, new EntityId(ask.Target)));
                    else if (_gateway.ChatPeerOf(this) is { } peer) _gateway.Trade.Ask(peer, ask.Target);
                }
                break;
            case TradeAnswerMessage answer:
                if (Entity is { } answerer) _gateway.Enqueue(new Game.Server.World.Trade.TradeAnswer(answerer, answer.Accept));
                break;
            case TradeOfferMessage offer:
                if (Entity is { } offerer) _gateway.Enqueue(new Game.Server.World.Trade.TradeOfferItem(offerer, offer.Entry, offer.Quantity));
                break;
            case TradeCurrencyMessage coins:
                if (Entity is { } payer) _gateway.Enqueue(new Game.Server.World.Trade.TradeOfferCurrency(payer, coins.Currency));
                break;
            case TradeLockMessage:
                if (Entity is { } locker) _gateway.Enqueue(new Game.Server.World.Trade.TradeLock(locker, true));
                break;
            case TradeUnlockMessage:
                if (Entity is { } unlocker) _gateway.Enqueue(new Game.Server.World.Trade.TradeLock(unlocker, false));
                break;
            case TradeConfirmMessage confirm:
                if (Entity is { } confirmer) _gateway.Enqueue(new Game.Server.World.Trade.TradeConfirm(confirmer, confirm.Version));
                break;
            case TradeCancelMessage:
                if (Entity is { } canceller) _gateway.Enqueue(new Game.Server.World.Trade.TradeCancel(canceller, Game.Shared.Vocabulary.TradeEnds.Cancelled));
                break;
        }
        return null;
    }

    /// <summary>
    /// A chat message (T-901): handed to the chat service after this session's previous one, so lines keep their order; the receive loop goes on meanwhile (the mute check may read the
    /// database). Ignored before the welcome and on a server without chat.
    /// </summary>
    private void Chat(ClientMessage message)
    {
        if (_gateway.Chat is not { } chat || State != SessionState.Joined) return;
        var token = _abort.Token;
        var previous = _chat;
        _chat = RunAsync();

        async Task RunAsync()
        {
            await previous.ConfigureAwait(false);
            if (Closing || _gateway.ChatPeerOf(this) is not { } speaker) return;   // the session ended or its character left meanwhile
            await chat.HandleAsync(speaker, message, token).ConfigureAwait(false);   // never throws for what the client sent
        }
    }

    /// <summary>
    /// A guild message (T-904): handed to the guild service after this session's previous one (each reads the database), off the receive loop. Ignored before the welcome and on a server
    /// without guilds (no database).
    /// </summary>
    private void Guild(ClientMessage message)
    {
        if (_gateway.Guild is not { } guild || State != SessionState.Joined) return;
        var token = _abort.Token;
        var previous = _guild;
        _guild = RunAsync();

        async Task RunAsync()
        {
            await previous.ConfigureAwait(false);
            if (Closing || _gateway.ChatPeerOf(this) is not { } member) return;
            await guild.HandleAsync(member, message, token).ConfigureAwait(false);   // never throws for what the client sent
        }
    }

    /// <summary>
    /// True for the messages that take the character out of the autopilot's hands (T-604): move, stop, attack, skill, item, dialogue, shop and respawn. <paramref name="order"/>: it is
    /// itself an order where to go or what to fight, so the walk in progress is its to end.
    /// </summary>
    private static bool IsManualInput(ClientMessage message, out bool order)
    {
        order = message is MoveMessage or StopMessage or AttackMessage or StopAttackMessage or RespawnMessage or UseSkillMessage or PickUpMessage or TalkMessage;
        return order || message is UseItemMessage or EquipMessage or UnequipMessage or DropItemMessage or InsertCardMessage or RefineMessage
            or DialogueChoiceMessage or DialogueCloseMessage or ShopBuyMessage or ShopSellMessage;
    }

    /// <summary>Logs in and joins; never throws. While the login is pending the session is still awaiting its hello (<see cref="SessionState.AwaitingHello"/>).</summary>
    private async Task JoinAsync(HelloMessage hello, CancellationToken stop)
    {
        try
        {
            await LogInAsync(hello, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // the connection ended first
        }
        catch (Exception e)
        {
            _gateway.Log.Error($"[net] session {Peer}: login failed.", e);
            Bye(ByeReasons.ServerError);
        }
    }

    private async Task LogInAsync(HelloMessage hello, CancellationToken stop)
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
        if (_gateway.InMaintenance)
        {
            Bye(ByeReasons.Maintenance);   // T-806: no new game while the server is in maintenance; a resume still gets back in until the games end
            return;
        }
        Volatile.Write(ref _sessionKey, Accounts.SessionTokens.KeyOf(hello.Session));
        var login = await _gateway.Login.LoginAsync(hello, Address, stop).ConfigureAwait(false);
        stop.ThrowIfCancellationRequested();   // dropped while it was pending: nothing is claimed or spawned for it
        if (login.Refusal is { } refusal)
        {
            Bye(refusal, login.RetryAfterSeconds);
            return;
        }
        if (Closing) return; // timed out, or the server is stopping
        DisplayName = login.DisplayName;
        // T-901: the account's block list is read now, off the tick and before its character can hear a line; while it plays, chat reads nothing per line.
        if (login.AccountId is { } chatAccount && _gateway.Chat is { } chat) await chat.LoadBlocksAsync(chatAccount, stop).ConfigureAwait(false);
        if (Closing) return;
        if (_gateway.ClaimAccount(this, login)) return;
        if (Closing) return;   // its account session ended while the login was pending (T-710)
        // T-702: a saved character is loaded before anything of it enters the world. Whatever goes wrong refuses the login and changes nothing.
        var entered = await _gateway.LoadCharacterAsync(login, stop).ConfigureAwait(false);
        if (entered?.Refusal is { } refused)
        {
            Bye(refused);
            return;
        }
        var joined = false;
        try
        {
            joined = TryJoin(login, entered);
        }
        finally
        {
            if (!joined) _gateway.GiveUp(entered);
        }
    }

    /// <summary>Queues the character's spawn; false when the session has ended meanwhile or the server is full, and the character never enters.</summary>
    private bool TryJoin(LoginResult login, World.Saving.EnterResult? entered)
    {
        if (Interlocked.CompareExchange(ref _state, (int)SessionState.Joining, (int)SessionState.AwaitingHello) != (int)SessionState.AwaitingHello) return false;
        if (_gateway.SpawnCharacter(this, login, entered) is not { } id)
        {
            Bye(ByeReasons.ServerFull);
            return false;
        }
        Interlocked.CompareExchange(ref _entity, id.Value, 0);
        return true;
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
    /// <summary>A <c>use_skill</c>: a given entity target must be in the client's view (else <c>target_gone</c>); combat checks the rest on the tick.</summary>
    private void RequestSkill(UseSkillMessage cast)
    {
        if (Entity is not { } id) return;
        if (cast.Target is { } target && target != id.Value)
        {
            bool seen;
            lock (_visible) seen = _visible.ContainsKey(target);
            if (!seen)
            {
                Post(new RejectedMessage(MessageTypes.UseSkill, SkillReasons.TargetGone, null, target, Skill: cast.Skill));
                return;
            }
        }
        _gateway.Enqueue(new UseSkill(id, cast.Skill, cast.Level ?? 0, cast.Target is { } t ? new EntityId(t) : null, cast.Cell));
    }

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

    /// <summary>
    /// The target must be an NPC in the client's view (else <c>target_gone</c> for one it does not see, <c>invalid_target</c> for anything but an NPC). The auto-attack
    /// stops first, so combat's walk does not cancel the walk to the NPC; the dialogue system checks the rest (range, a dialogue to run) on the tick.
    /// </summary>
    private void RequestTalk(TalkMessage talk)
    {
        if (Entity is not { } id) return;
        EntityKind kind;
        bool seen;
        lock (_visible) seen = _visible.TryGetValue(talk.Target, out kind);
        string? reason = !seen ? DialogueReasons.TargetGone : kind != EntityKind.Npc ? DialogueReasons.InvalidTarget : null;
        if (reason is not null)
        {
            Post(new RejectedMessage(MessageTypes.Talk, reason, null, talk.Target));
            return;
        }
        _gateway.Enqueue(new StopAttack(id));
        _gateway.Enqueue(new TalkTo(id, new EntityId(talk.Target)));
    }

    /// <summary>
    /// The target must be a ground item in the client's view (else <c>item_gone</c>). The auto-attack stops first, so combat's walk does not
    /// cancel the walk to the item; the inventory checks the rest on the tick.
    /// </summary>
    private void RequestPickUp(PickUpMessage pick)
    {
        if (Entity is not { } id) return;
        EntityKind kind;
        bool seen;
        lock (_visible) seen = _visible.TryGetValue(pick.Target, out kind);
        if (!seen || kind != EntityKind.Item)
        {
            Post(new RejectedMessage(MessageTypes.PickUp, ItemReasons.ItemGone, null, pick.Target));
            return;
        }
        _gateway.Enqueue(new StopAttack(id));
        _gateway.Enqueue(new PickUp(id, new EntityId(pick.Target)));
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
                await _socket.CloseOutputAsync(CloseStatusFor(reason), Volatile.Read(ref _closeReason) ?? reason, token).ConfigureAwait(false);
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
