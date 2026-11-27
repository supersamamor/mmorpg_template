using System.Collections.Concurrent;
using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Chat;

/// <summary>
/// Chat (T-901, ARCHITECTURE §10 "Chat"): the server decides every line. A client's <c>chat</c> is an intent; the service checks it in this order and sends the line, with the sender's
/// character name and entity id, to everyone it reaches, the sender included, or answers <c>chat_refused</c>:
/// <list type="number">
/// <item><b>Mute</b>: <see cref="ModerationChecks.SpeakAsync"/> (cached 5 s per account, so a burst of lines costs one database read; it fails closed). A muted sender is told how long is left.</item>
/// <item><b>Rate</b>: <see cref="ChatLimits"/>, per account.</item>
/// <item><b>Text</b>: <see cref="ChatText"/>.</item>
/// <item><b>Channel</b>: <c>map</c> (every playing character on the sender's map instance), <c>whisper</c> (one playing character by name; "not online" when there is none, or when it blocks
/// the sender), or a channel registered with <see cref="Register"/> (party, guild: the seam). <c>system</c> is the server's.</item>
/// <item><b>Blocks</b>: a line never reaches an account that blocks its sender's character.</item>
/// </list>
/// Block lists are read from the database when an account signs in (<see cref="LoadBlocksAsync"/>) and kept while it plays, so sending a line reads nothing but the mute's cache. Nothing a
/// player wrote is logged; a report stores the line it names (<see cref="RecentLines"/>). Runs on the sessions' receive loops and their tasks, never on a tick thread. Thread-safe.
/// </summary>
public sealed class ChatService : IAsyncDisposable
{
    /// <summary>How often lists of accounts that left are forgotten and old reports erased.</summary>
    public const int SweepIntervalMs = 60_000;

    /// <summary>A block list loaded this recently is kept even if its account is not (yet) playing: it was loaded for a login on its way in.</summary>
    public const int BlockListGraceMs = 300_000;

    private readonly ChatOptions _options;
    private readonly IChatDirectory _directory;
    private readonly IChatStore? _store;
    private readonly IChatReportStore? _reports;
    private readonly Func<Guid, CancellationToken, ValueTask<SpeakAnswer>>? _speak;
    private readonly TimeProvider _time;
    private readonly IServerLog _log;
    private readonly ChatLimits _limits;
    private readonly ConcurrentDictionary<string, IChannelMembers> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, BlockList> _blocks = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _sweeper;
    private DateTime _lastPurge = DateTime.MinValue;

    /// <summary>
    /// Creates the service. <paramref name="speak"/> is <see cref="ModerationChecks.SpeakAsync"/> (null without a database: nobody can be muted, as nobody has an account);
    /// <paramref name="store"/> holds blocks and reports (null without a database: blocks and reports are <c>unavailable</c>).
    /// </summary>
    public ChatService(ChatOptions options, IChatDirectory directory, IChatStore? store, Func<Guid, CancellationToken, ValueTask<SpeakAnswer>>? speak, TimeProvider time, IServerLog log,
        IChatReportStore? reports = null)
    {
        _options = options;
        _directory = directory;
        _store = store;
        _reports = reports;
        _speak = speak;
        _time = time;
        _log = log;
        _limits = new ChatLimits(options, time);
        Recent = new RecentLines(options, time);
    }

    /// <summary>The settings.</summary>
    public ChatOptions Options => _options;

    /// <summary>The lines kept for reports.</summary>
    public RecentLines Recent { get; }

    /// <summary>
    /// The seam (T-902 party, T-904 guild): lines on <paramref name="channel"/> go to the characters <paramref name="members"/> names. The kind must be a new "Chat channels" term, not one
    /// chat routes itself. Register before players arrive.
    /// </summary>
    public void Register(string channel, IChannelMembers members)
    {
        if (string.IsNullOrEmpty(channel) || channel is ChatChannels.Map or ChatChannels.Whisper or ChatChannels.System)
            throw new ArgumentException($"'{channel}' is routed by chat itself; register a channel of your own.", nameof(channel));
        if (!_channels.TryAdd(channel, members)) throw new ArgumentException($"Channel '{channel}' is registered already.", nameof(channel));
    }

    /// <summary>Starts the sweep (block lists of accounts that left, closed reports past their time).</summary>
    public void Start() => _sweeper ??= Task.Run(() => SweepLoopAsync(_stop.Token));

    // ---- what the gateway calls ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the account's block list, before its character enters (the session's receive loop, at its <c>hello</c>). When the database does not answer, the account plays with an empty
    /// list for now and a warning is logged; the next block or unblock reads it again.
    /// </summary>
    public async Task LoadBlocksAsync(Guid account, CancellationToken cancellation)
    {
        if (_store is null) return;
        try
        {
            var list = await _store.BlocksAsync(account, cancellation).ConfigureAwait(false);
            _blocks[account] = new BlockList(list, _time.GetUtcNow().UtcDateTime, loaded: true);
        }
        catch (StoreUnavailableException e)
        {
            _blocks[account] = new BlockList([], _time.GetUtcNow().UtcDateTime, loaded: false);
            _log.Warn($"[chat] account {account}: its block list could not be read ({e.Message}); it plays without it for now.");
        }
    }

    /// <summary>What <c>chat_state</c> says to the account (null: a dev login, which blocks nobody).</summary>
    public ChatStateMessage StateFor(Guid? account) =>
        new(_options.MaxLength, account is { } a && _blocks.TryGetValue(a, out var list) ? list.Names : Array.Empty<string>());

    /// <summary>
    /// Handles one chat message of <paramref name="speaker"/>: <c>chat</c>, <c>block</c>, <c>unblock</c> or <c>report</c>. Never throws for what a client sent; a failure is logged by its type
    /// (never what was said) and answered <c>unavailable</c>.
    /// </summary>
    public async Task HandleAsync(ChatPeer speaker, ClientMessage message, CancellationToken cancellation = default)
    {
        var intent = message.Type;
        try
        {
            switch (message)
            {
                case ChatMessage chat:
                    await SayAsync(speaker, chat, cancellation).ConfigureAwait(false);
                    break;
                case BlockMessage block:
                    await BlockAsync(speaker, block.Name, cancellation).ConfigureAwait(false);
                    break;
                case UnblockMessage unblock:
                    await UnblockAsync(speaker, unblock.Name, cancellation).ConfigureAwait(false);
                    break;
                case ReportMessage report:
                    await ReportAsync(speaker, report.Line, cancellation).ConfigureAwait(false);
                    break;
            }
        }
        catch (StoreUnavailableException e)
        {
            _log.Warn($"[chat] {intent} of {speaker.Character} not done: {e.Message}.");
            Refuse(speaker, intent, ChatReasons.Unavailable);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // the session ended meanwhile
        }
        catch (Exception e)
        {
            _log.Error($"[chat] {intent} of {speaker.Character} failed: {e.GetType().FullName}\n{e.StackTrace}");   // the type only: a message could quote the line
            Refuse(speaker, intent, ChatReasons.Unavailable);
        }
    }

    // ---- lines -----------------------------------------------------------------------------------------------------------------------------

    private async Task SayAsync(ChatPeer speaker, ChatMessage chat, CancellationToken cancellation)
    {
        // 1. Mute: before anything else, so a muted line goes nowhere whatever it is.
        if (speaker.AccountId is { } account && _speak is not null)
        {
            var answer = await _speak(account, cancellation).ConfigureAwait(false);
            if (!answer.May)
            {
                if (answer.Unavailable) Refuse(speaker, MessageTypes.Chat, ChatReasons.Unavailable, channel: chat.Channel);
                else Refuse(speaker, MessageTypes.Chat, ChatReasons.Muted, SecondsUntil(answer.MutedUntil), chat.Channel);
                return;
            }
        }
        // 2. Rate, per account.
        if (!_limits.TryTake(LimitKey(speaker), out var wait))
        {
            Refuse(speaker, MessageTypes.Chat, ChatReasons.RateLimited, wait, chat.Channel);
            return;
        }
        // 3. Text.
        var clean = ChatText.Clean(chat.Text, _options.MaxLength, _options.MarksPerLetter);
        if (clean.Text is not { } text)
        {
            Refuse(speaker, MessageTypes.Chat, clean.Refusal!, channel: chat.Channel);
            return;
        }
        // 4. Who hears it.
        switch (chat.Channel)
        {
            case ChatChannels.Map:
                if (speaker.MapId is not { } map)
                {
                    Refuse(speaker, MessageTypes.Chat, ChatReasons.Unavailable, channel: chat.Channel);   // between maps
                    return;
                }
                Deliver(speaker, ChatChannels.Map, text, null, _directory.OnMap(map));
                return;
            case ChatChannels.Whisper:
                Whisper(speaker, chat.To, text);
                return;
        }
        if (_channels.TryGetValue(chat.Channel, out var members))
        {
            var ids = members.MembersFor(speaker);
            if (!ids.Contains(speaker.Character))
            {
                Refuse(speaker, MessageTypes.Chat, ChatReasons.InvalidChannel, channel: chat.Channel);
                return;
            }
            var peers = new List<ChatPeer>(ids.Count);
            foreach (var id in ids)
                if (_directory.Find(id) is { } peer) peers.Add(peer);
            Deliver(speaker, chat.Channel, text, null, peers);
            return;
        }
        Refuse(speaker, MessageTypes.Chat, ChatReasons.InvalidChannel, channel: chat.Channel);   // system, or a kind nobody registered
    }

    private void Whisper(ChatPeer speaker, string? to, string text)
    {
        var key = ChatNames.Key(to);
        var target = key is null ? null : _directory.Online(key);
        if (target is not null && target.Character == speaker.Character)
        {
            Refuse(speaker, MessageTypes.Chat, ChatReasons.InvalidTarget, channel: ChatChannels.Whisper, to: to);
            return;
        }
        // A block reads as "not online": the sender learns nothing it would not learn from a player who left.
        if (target is null || Blocks(target, speaker))
        {
            Refuse(speaker, MessageTypes.Chat, ChatReasons.NotOnline, channel: ChatChannels.Whisper, to: to);
            return;
        }
        Deliver(speaker, ChatChannels.Whisper, text, target.Name, [speaker, target]);
    }

    /// <summary>One serialization, then a copy to each peer that does not block the speaker; the speaker gets it too (the echo comes only now, after the server took the line).</summary>
    private void Deliver(ChatPeer speaker, string channel, string text, string? to, IReadOnlyList<ChatPeer> peers)
    {
        var line = new ChatLineMessage(Recent.NextId(), channel, speaker.Character.Value, speaker.Name, text, to);
        var bytes = ProtocolJson.ToBytes(line);
        var reached = new List<Guid>(peers.Count);
        var spoke = false;
        foreach (var peer in peers)
        {
            if (peer.Character == speaker.Character)
            {
                if (spoke) continue;
                spoke = true;
            }
            else if (Blocks(peer, speaker)) continue;
            peer.Sink.Post(line, bytes);
            if (peer.AccountId is { } account && peer.Character != speaker.Character) reached.Add(account);
        }
        if (!spoke) speaker.Sink.Post(line, bytes);
        if (reached.Count > 0)
            Recent.Keep(new SaidLine(line.Line, channel, speaker.AccountId, speaker.CharacterId, text, _time.GetUtcNow().UtcDateTime), reached);
    }

    /// <summary>True when <paramref name="listener"/>'s account blocks <paramref name="speaker"/>'s character. Any thread; reads only the lists in memory (T-902: a party invite asks it too).</summary>
    public bool Blocks(ChatPeer listener, ChatPeer speaker) =>
        speaker.CharacterId is { } character && listener.AccountId is { } account && _blocks.TryGetValue(account, out var list) && list.Has(character);

    // ---- blocks ----------------------------------------------------------------------------------------------------------------------------

    private async Task BlockAsync(ChatPeer speaker, string name, CancellationToken cancellation)
    {
        if (speaker.AccountId is not { } account || _store is null)
        {
            Refuse(speaker, MessageTypes.Block, ChatReasons.Unavailable, to: name);
            return;
        }
        if (!_limits.TryTake(LimitKey(speaker), out var wait))
        {
            Refuse(speaker, MessageTypes.Block, ChatReasons.RateLimited, wait, to: name);
            return;
        }
        var key = ChatNames.Key(name);
        var target = key is null ? null : _directory.Online(key) is { CharacterId: { } id, AccountId: { } owner } online
            ? new ChatCharacter(id, owner, online.Name)
            : await _store.FindCharacterAsync(key, cancellation).ConfigureAwait(false);
        if (target is null)
        {
            Refuse(speaker, MessageTypes.Block, ChatReasons.UnknownCharacter, to: name);
            return;
        }
        if (target.AccountId == account)
        {
            Refuse(speaker, MessageTypes.Block, ChatReasons.InvalidTarget, to: name);
            return;
        }
        var list = await ListAsync(account, cancellation).ConfigureAwait(false);
        if (list.Has(target.Id))
        {
            Refuse(speaker, MessageTypes.Block, ChatReasons.AlreadyBlocked, to: name);
            return;
        }
        switch (await _store.AddBlockAsync(account, target.Id, _options.BlockLimit, _time.GetUtcNow().UtcDateTime, cancellation).ConfigureAwait(false))
        {
            case BlockOutcome.Limit:
                Refuse(speaker, MessageTypes.Block, ChatReasons.BlockLimit, to: name);
                return;
            case BlockOutcome.Gone:
                Refuse(speaker, MessageTypes.Block, ChatReasons.UnknownCharacter, to: name);
                return;
            case BlockOutcome.AlreadyBlocked:   // made through another server meanwhile: the list is what counts
            case BlockOutcome.Added:
                _blocks[account] = list.With(target.Id, target.Name, _time.GetUtcNow().UtcDateTime);
                Post(speaker, StateFor(account));
                return;
        }
    }

    private async Task UnblockAsync(ChatPeer speaker, string name, CancellationToken cancellation)
    {
        if (speaker.AccountId is not { } account || _store is null)
        {
            Refuse(speaker, MessageTypes.Unblock, ChatReasons.Unavailable, to: name);
            return;
        }
        if (!_limits.TryTake(LimitKey(speaker), out var wait))
        {
            Refuse(speaker, MessageTypes.Unblock, ChatReasons.RateLimited, wait, to: name);
            return;
        }
        var list = await ListAsync(account, cancellation).ConfigureAwait(false);
        if (list.Find(ChatNames.Key(name)) is not { } blocked)
        {
            Refuse(speaker, MessageTypes.Unblock, ChatReasons.NotBlocked, to: name);
            return;
        }
        await _store.RemoveBlockAsync(account, blocked, cancellation).ConfigureAwait(false);
        _blocks[account] = list.Without(blocked, _time.GetUtcNow().UtcDateTime);
        Post(speaker, StateFor(account));
    }

    /// <summary>The account's list, read again when it was not read at sign-in (the database was away then, or it is not playing here).</summary>
    private async Task<BlockList> ListAsync(Guid account, CancellationToken cancellation)
    {
        if (_blocks.TryGetValue(account, out var list) && list.Loaded) return list;
        var read = new BlockList(await _store!.BlocksAsync(account, cancellation).ConfigureAwait(false), _time.GetUtcNow().UtcDateTime, loaded: true);
        _blocks[account] = read;
        return read;
    }

    // ---- reports ---------------------------------------------------------------------------------------------------------------------------

    private async Task ReportAsync(ChatPeer speaker, long line, CancellationToken cancellation)
    {
        if (speaker.AccountId is not { } account || speaker.CharacterId is not { } character || _store is null)
        {
            Refuse(speaker, MessageTypes.Report, ChatReasons.Unavailable, line: line);
            return;
        }
        if (!_limits.TryTake(LimitKey(speaker), out var wait))
        {
            Refuse(speaker, MessageTypes.Report, ChatReasons.RateLimited, wait, line: line);
            return;
        }
        // Only a line this account received, still kept, from somebody else; the text is the server's copy.
        if (Recent.Reportable(line, account, out var already) is not { } said || said.SenderCharacterId == character)
        {
            Refuse(speaker, MessageTypes.Report, ChatReasons.UnknownLine, line: line);
            return;
        }
        if (already)
        {
            Refuse(speaker, MessageTypes.Report, ChatReasons.AlreadyReported, line: line);
            return;
        }
        if (said.SenderAccountId is not { } senderAccount || said.SenderCharacterId is not { } senderCharacter)
        {
            Refuse(speaker, MessageTypes.Report, ChatReasons.Unavailable, line: line);   // a dev login said it: there is nobody to report
            return;
        }
        var id = await _store.AddReportAsync(new ChatReportDraft(account, character, senderAccount, senderCharacter, said.Channel, said.Text, said.SaidAt, _time.GetUtcNow().UtcDateTime),
            cancellation).ConfigureAwait(false);
        Recent.MarkReported(line, account);
        _log.Info($"[chat] account {account} reported line {line} of account {senderAccount}: report {id}.");
        Post(speaker, new ChatReportedMessage(line));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------------

    private static string LimitKey(ChatPeer speaker) => speaker.AccountId is { } account ? ChatLimits.Of(account) : ChatLimits.Of(speaker.Character);

    private long? SecondsUntil(DateTime? until) => until is { } u ? Math.Max(1, (long)Math.Ceiling((u - _time.GetUtcNow().UtcDateTime).TotalSeconds)) : null;

    private static void Refuse(ChatPeer speaker, string intent, string reason, long? retryAfter = null, string? channel = null, string? to = null, long? line = null) =>
        Post(speaker, new ChatRefusedMessage(intent, reason, retryAfter, channel, to, line));

    private static void Post(ChatPeer peer, ServerMessage message) => peer.Sink.Post(message, ProtocolJson.ToBytes(message));

    /// <summary>One sweep: forgets block lists and reportable lines of accounts that left, and once an hour erases closed reports past <see cref="ChatOptions.ClosedReportDays"/>.</summary>
    public async Task SweepAsync(CancellationToken cancellation)
    {
        var playing = _directory.PlayingAccounts().ToHashSet();
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (account, list) in _blocks)
            if (!playing.Contains(account) && (now - list.At).TotalMilliseconds > BlockListGraceMs) _blocks.TryRemove(account, out _);
        Recent.Prune(playing);
        if (_reports is not null && now - _lastPurge >= TimeSpan.FromHours(1))
        {
            _lastPurge = now;
            var erased = await _reports.PurgeClosedAsync(now.AddDays(-_options.ClosedReportDays), cancellation).ConfigureAwait(false);
            if (erased > 0) _log.Info($"[chat] erased {erased} closed report(s) older than {_options.ClosedReportDays} days.");
        }
    }

    private async Task SweepLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(SweepIntervalMs), _time, stop).ConfigureAwait(false);
                await SweepAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (StoreUnavailableException e)
            {
                _log.Warn($"[chat] the sweep could not erase old reports: {e.Message}.");
            }
            catch (Exception e)
            {
                _log.Error("[chat] the sweep failed.", e);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_sweeper is not null) await _sweeper.ConfigureAwait(false);
        _stop.Dispose();
    }

    private int _disposed;

    /// <summary>One account's blocks, immutable: a change makes a new one.</summary>
    private sealed class BlockList
    {
        private readonly Dictionary<Guid, string> _byId;

        public BlockList(IEnumerable<BlockedCharacter> blocked, DateTime at, bool loaded)
        {
            _byId = blocked.ToDictionary(b => b.CharacterId, b => b.Name);
            At = at;
            Loaded = loaded;
            Names = _byId.Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>When it was read or changed.</summary>
        public DateTime At { get; }

        /// <summary>False when the database could not give it at sign-in: empty for now.</summary>
        public bool Loaded { get; }

        public IReadOnlyList<string> Names { get; }

        public bool Has(Guid character) => _byId.ContainsKey(character);

        public Guid? Find(string? nameKey) => nameKey is null ? null : _byId.Where(b => ChatNames.Key(b.Value) == nameKey).Select(b => (Guid?)b.Key).FirstOrDefault();

        public BlockList With(Guid character, string name, DateTime at) =>
            new(_byId.Select(b => new BlockedCharacter(b.Key, b.Value)).Append(new BlockedCharacter(character, name)).DistinctBy(b => b.CharacterId), at, Loaded);

        public BlockList Without(Guid character, DateTime at) => new(_byId.Where(b => b.Key != character).Select(b => new BlockedCharacter(b.Key, b.Value)), at, Loaded);
    }
}
