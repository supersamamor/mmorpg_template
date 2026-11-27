using Game.Server.Accounts;
using Game.Server.Chat;
using Game.Server.Net;
using Game.Server.Party;
using Game.Server.World;
using Game.Server.World.Guild;
using Game.Shared.Accounts;
using Game.Shared.Definitions;
using Game.Shared.Guild;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Guild;

/// <summary>Who plays where, as the guild service needs it (T-904): the party directory's answers and the players on a map instance (for emblem tags).</summary>
public interface IGuildDirectory : IPartyDirectory
{
    /// <summary>Every playing character on the map instance <paramref name="mapId"/>.</summary>
    IReadOnlyList<ChatPeer> OnMap(string mapId);
}

/// <summary>The directory of a running server: the gateway's characters and chat's block lists.</summary>
public sealed class GatewayGuildDirectory(Gateway gateway, ChatService? chat) : IGuildDirectory
{
    private readonly GatewayPartyDirectory _party = new(gateway, chat);
    private readonly IChatDirectory _chat = gateway;

    /// <inheritdoc />
    public ChatPeer? Playing(EntityId character) => _party.Playing(character);

    /// <inheritdoc />
    public ChatPeer? Online(string nameKey) => _party.Online(nameKey);

    /// <inheritdoc />
    public bool Holds(EntityId character) => _party.Holds(character);

    /// <inheritdoc />
    public bool Blocks(ChatPeer listener, ChatPeer speaker) => _party.Blocks(listener, speaker);

    /// <inheritdoc />
    public IReadOnlyList<ChatPeer> OnMap(string mapId) => _chat.OnMap(mapId);
}

/// <summary>
/// Guilds (T-904, ARCHITECTURE §6.11). The database holds them (<see cref="IGuildStore"/>) and decides every change in a transaction that re-checks the actor's rank; this service turns
/// players' guild messages into those calls, keeps a copy of the guilds its players are in, and tells the members who play here.
/// <list type="bullet">
/// <item><b>Founding</b> (<c>guild_found</c>): the name by the naming rules, an emblem of the theme's set; the guild is reserved in the database (the name unique), then the tick takes the
/// cost (<see cref="GuildFoundingPayment"/>) and the founder's next save founds it in the same transaction. A cost refused or a save that never lands drops the reservation.</item>
/// <item><b>Invites</b> (<c>guild_invite</c>, <c>guild_answer</c>): by name or by tapping a player on the same map, like a party's (a block reads as not online), held in this process's
/// memory for <c>guild.invite_timeout_ms</c>; the accept re-checks the inviter's permission in the database.</item>
/// <item><b>Changes</b> (<c>guild_leave</c>, <c>guild_kick</c>, <c>guild_rank</c>, <c>guild_ranks</c>, <c>guild_lead</c>, <c>guild_notice</c>): straight to the store; after one, the guild is read
/// again and every member playing here gets it.</item>
/// <item><b>Other processes</b>: every <see cref="GuildOptions.RefreshMs"/> the versions of the guilds held here are read; a changed one is read again and sent. Online means playing on this
/// process: members on another one show offline here (v1).</item>
/// <item><b>Guild chat</b>: this is the <c>guild</c> channel's <see cref="IChannelMembers"/>: the members who play on this process (chat does mute, rate, text, blocks and reports).</item>
/// <item><b>Tags</b>: every pulse, each player gets the emblems of the guild members on its map instance when that list changed for it (<c>guild_tags</c>).</item>
/// </list>
/// Messages are handled off the tick and off the receive loop; state is under one lock, never held across a database call. Thread-safe.
/// </summary>
public sealed class GuildService : IChannelMembers, IAsyncDisposable
{
    private readonly GuildBalanceDef _balance;
    private readonly GuildOptions _options;
    private readonly IGuildStore _store;
    private readonly IGuildDirectory _directory;
    private readonly Func<IIntent, bool> _enqueue;
    private readonly TimeProvider _time;
    private readonly IServerLog _log;
    private readonly CharacterNameRules _names;
    private readonly int _marksPerLetter;
    private readonly ChatLimits _limits;
    private readonly HashSet<string> _emblems;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, EntityId> _peers = new();          // characters welcomed here, while the server holds them
    private readonly Dictionary<Guid, long> _guildOf = new();            // of those, who is in which founded guild
    private readonly Dictionary<long, Cached> _guilds = new();           // the guilds of players here, as last read
    private readonly Dictionary<Guid, Invite> _invites = new();          // by the invited character
    private readonly Dictionary<Guid, long> _foundings = new();          // founders whose founding is under way: the reserved guild (0 before it is reserved)
    private readonly Dictionary<EntityId, string> _tagsSent = new();     // the tag list each player was last sent, as a key
    private readonly CancellationTokenSource _stop = new();
    private long _lastRefreshMs;
    private Task? _loop;
    private int _disposed;

    /// <summary>Creates the service. <paramref name="enqueue"/> queues a tick intent for a character (the gateway's); <paramref name="marksPerLetter"/> is chat's text rule.</summary>
    public GuildService(GuildBalanceDef balance, CharactersBalanceDef characters, GuildOptions options, IGuildStore store, IGuildDirectory directory, Func<IIntent, bool> enqueue,
        int marksPerLetter, TimeProvider time, IServerLog log)
    {
        _balance = balance;
        _options = options;
        _store = store;
        _directory = directory;
        _enqueue = enqueue;
        _time = time;
        _log = log;
        _names = GuildRules.NameRules(characters, balance);
        _marksPerLetter = marksPerLetter;
        _limits = new ChatLimits(new ChatOptions { LinesPerMinute = options.IntentsPerMinute, Burst = options.IntentBurst }, time);
        _emblems = new HashSet<string>(balance.Emblems, StringComparer.Ordinal);
    }

    /// <summary>Starts the pulse.</summary>
    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>Guilds held here now (tests, status).</summary>
    public int GuildCount
    {
        get
        {
            lock (_lock) return _guilds.Count;
        }
    }

    // ---- what the gateway calls --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Handles one guild message of <paramref name="peer"/> (a character that plays now). Never throws for what a client sent; a database that does not answer refuses it as
    /// <c>unavailable</c>.
    /// </summary>
    public async Task HandleAsync(ChatPeer peer, ClientMessage message, CancellationToken cancellation)
    {
        var outbox = new Outbox();
        try
        {
            if (peer.CharacterId is not { } me)
            {
                outbox.Refuse(peer.Character, message.Type, GuildReasons.Unavailable);   // a dev login has no stored character
                return;
            }
            if (!_limits.TryTake(peer.AccountId is { } account ? ChatLimits.Of(account) : ChatLimits.Of(peer.Character), out var wait))
            {
                outbox.Refuse(peer.Character, message.Type, GuildReasons.RateLimited, wait);
                return;
            }
            lock (_lock) _peers[me] = peer.Character;
            switch (message)
            {
                case GuildFoundMessage found: await FoundAsync(peer, me, found, outbox, cancellation).ConfigureAwait(false); break;
                case GuildInviteMessage invite: await InviteAsync(peer, me, invite, outbox, cancellation).ConfigureAwait(false); break;
                case GuildAnswerMessage answer: await AnswerAsync(peer, me, answer.Accept, outbox, cancellation).ConfigureAwait(false); break;
                case GuildLeaveMessage: await ApplyAsync(peer, message.Type, await _store.LeaveAsync(me, cancellation).ConfigureAwait(false), outbox, cancellation).ConfigureAwait(false); break;
                case GuildKickMessage kick:
                    await ApplyAsync(peer, message.Type, await _store.KickAsync(me, kick.Member, cancellation).ConfigureAwait(false), outbox, cancellation).ConfigureAwait(false);
                    break;
                case GuildRankMessage rank:
                    await ApplyAsync(peer, message.Type, await _store.SetRankAsync(me, rank.Member, rank.Rank, cancellation).ConfigureAwait(false), outbox, cancellation).ConfigureAwait(false);
                    break;
                case GuildRanksMessage ranks:
                    if (GuildRules.CheckRanks(_balance, new GuildRankRecord("", []), ranks.Ranks, RankName, out var checkedRanks) is { } badRanks)
                        outbox.Refuse(peer.Character, message.Type, badRanks);
                    else
                        await ApplyAsync(peer, message.Type, await _store.SetRanksAsync(me, checkedRanks.Skip(1).ToList(), cancellation).ConfigureAwait(false), outbox, cancellation)
                            .ConfigureAwait(false);
                    break;
                case GuildLeadMessage lead:
                    await ApplyAsync(peer, message.Type, await _store.LeadAsync(me, lead.Member, cancellation).ConfigureAwait(false), outbox, cancellation).ConfigureAwait(false);
                    break;
                case GuildNoticeMessage notice:
                    if (Notice(notice.Text) is { Refusal: { } badNotice })
                        outbox.Refuse(peer.Character, message.Type, badNotice);
                    else
                        await ApplyAsync(peer, message.Type, await _store.SetNoticeAsync(me, Notice(notice.Text).Text!, cancellation).ConfigureAwait(false), outbox, cancellation)
                            .ConfigureAwait(false);
                    break;
            }
        }
        catch (StoreUnavailableException e)
        {
            _log.Warn($"[guild] {message.Type} of {peer.Character}: {e.Message}.");
            outbox.Refuse(peer.Character, message.Type, GuildReasons.Unavailable);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log.Error($"[guild] {message.Type} of {peer.Character} failed.", e);
        }
        finally
        {
            Send(outbox);
        }
    }

    /// <summary>
    /// A session (re)took <paramref name="character"/> and had its welcome (a sign-in, a resume, a takeover): its guild (with the notice) and any invite go to it, and the other members see it
    /// online. Any thread: the database is read in the background.
    /// </summary>
    public void Welcomed(EntityId character) => _ = Task.Run(() => WelcomeAsync(character, _stop.Token));

    /// <inheritdoc />
    public IReadOnlyCollection<EntityId> MembersFor(ChatPeer speaker)
    {
        lock (_lock)
        {
            if (speaker.CharacterId is not { } me || !_guildOf.TryGetValue(me, out var guild)) return [];
            var members = new List<EntityId>();
            foreach (var (character, of) in _guildOf)
                if (of == guild && _peers.TryGetValue(character, out var entity)) members.Add(entity);
            return members;
        }
    }

    // ---- intents -----------------------------------------------------------------------------------------------------------------------------

    private async Task FoundAsync(ChatPeer peer, Guid me, GuildFoundMessage found, Outbox outbox, CancellationToken cancellation)
    {
        var intent = MessageTypes.GuildFound;
        var name = _names.Normalize(found.Name);
        if (name is null)
        {
            outbox.Refuse(peer.Character, intent, GuildReasons.InvalidName);
            return;
        }
        if (!_emblems.Contains(found.Emblem))
        {
            outbox.Refuse(peer.Character, intent, GuildReasons.InvalidEmblem);
            return;
        }
        long[] keep;
        lock (_lock)
        {
            if (_guildOf.ContainsKey(me))
            {
                outbox.Refuse(peer.Character, intent, GuildReasons.AlreadyInGuild);
                return;
            }
            if (!_foundings.TryAdd(me, 0))
            {
                outbox.Refuse(peer.Character, intent, GuildReasons.Busy);
                return;
            }
            keep = _foundings.Values.Where(g => g > 0).ToArray();
        }
        var guild = 0L;
        try
        {
            var ranks = _balance.DefaultRanks.Select(r => new GuildRankRecord(r.Name, r.Permissions)).ToList();
            var (refusal, reserved) = await _store.ReserveAsync(me, name, CharacterNameRules.Key(name), found.Emblem, ranks, keep, Now, cancellation).ConfigureAwait(false);
            if (refusal is not null)
            {
                outbox.Refuse(peer.Character, intent, refusal, name: refusal == GuildReasons.NameTaken ? name : null);   // a name in a refusal is a character's, except the one taken
                return;
            }
            guild = reserved;
            lock (_lock) _foundings[me] = guild;
            var pay = new GuildFoundingPayment(peer.Character, guild, _balance.FoundCost);
            var reason = _enqueue(pay) ? await PaidAsync(pay).ConfigureAwait(false) : GuildReasons.Unavailable;
            if (reason is null && !await pay.Note!.Landed.ConfigureAwait(false)) reason = GuildReasons.Unavailable;   // the stay ended before the save: neither cost nor guild exists
            if (reason is not null)
            {
                await _store.DropReservationAsync(guild, CancellationToken.None).ConfigureAwait(false);
                outbox.Refuse(peer.Character, intent, reason);
                return;
            }
            _log.Info($"[guild] guild {guild} founded by {me}.");
            lock (_lock) _guildOf[me] = guild;
            await RefreshAsync(guild, CancellationToken.None).ConfigureAwait(false);
            outbox.Add(peer.Character, new GuildNewsMessage(GuildNews.Founded, name));
        }
        finally
        {
            lock (_lock) _foundings.Remove(me);
        }
    }

    /// <summary>Waits for the tick to take the cost; gives it up when the tick has not started it in time (nothing is taken then). Null when paid.</summary>
    private async Task<string?> PaidAsync(GuildFoundingPayment pay)
    {
        var done = pay.Done;
        if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromMilliseconds(_options.PaymentWaitMs), _time)).ConfigureAwait(false) != done && pay.TryAbandon())
            return GuildReasons.Unavailable;
        return await done.ConfigureAwait(false);
    }

    private async Task InviteAsync(ChatPeer inviter, Guid me, GuildInviteMessage message, Outbox outbox, CancellationToken cancellation)
    {
        var intent = MessageTypes.GuildInvite;
        ChatPeer? target;
        if (message.Target is { } id)
        {
            target = _directory.Playing(new EntityId(id));
            if (target is not null && (target.MapId is null || target.MapId != inviter.MapId)) target = null;   // a tap: only a player on the same map
        }
        else target = ChatNames.Key(message.Name) is { } key ? _directory.Online(key) : null;
        if (target is not null && target.Character == inviter.Character)
        {
            outbox.Refuse(inviter.Character, intent, GuildReasons.InvalidTarget, name: message.Name);
            return;
        }
        // A block reads as "not online", as for a party; so does a character with no stored id (a dev login cannot be in a guild).
        if (target?.CharacterId is not { } invited || _directory.Blocks(target, inviter))
        {
            outbox.Refuse(inviter.Character, intent, GuildReasons.NotOnline, name: message.Name);
            return;
        }
        lock (_lock)
            if (_invites.TryGetValue(invited, out var pending) && pending.ExpiresAt > Now)
            {
                outbox.Refuse(inviter.Character, intent, GuildReasons.InvitePending, name: target.Name);
                return;
            }
        var check = await _store.CheckInviteAsync(me, invited, _balance.MaxMembers, cancellation).ConfigureAwait(false);
        if (check.Refusal is { } refused)
        {
            outbox.Refuse(inviter.Character, intent, refused, name: target.Name);
            return;
        }
        var invite = new Invite(me, inviter.Character, inviter.Name, check.Guild, check.Title, check.Emblem, invited, target.Character, target.Name, Now.AddMilliseconds(_balance.InviteTimeoutMs));
        lock (_lock)
        {
            _invites[invited] = invite;
            _peers[invited] = target.Character;
        }
        outbox.Add(target.Character, InvitedOf(invite));
        outbox.Add(inviter.Character, new GuildNewsMessage(GuildNews.InviteSent, target.Name));
    }

    private async Task AnswerAsync(ChatPeer peer, Guid me, bool accept, Outbox outbox, CancellationToken cancellation)
    {
        Invite? invite;
        lock (_lock)
            if (!_invites.Remove(me, out invite) || invite.ExpiresAt <= Now) invite = null;
        if (invite is null)
        {
            outbox.Refuse(peer.Character, MessageTypes.GuildAnswer, GuildReasons.NoInvite);
            return;
        }
        if (!accept)
        {
            outbox.Add(invite.FromEntity, new GuildNewsMessage(GuildNews.Declined, peer.Name));
            return;
        }
        var outcome = await _store.JoinAsync(invite.Guild, invite.From, me, _balance.MaxMembers, Now, cancellation).ConfigureAwait(false);
        if (outcome.Refusal is null) lock (_lock) _guildOf[me] = invite.Guild;
        await ApplyAsync(peer, MessageTypes.GuildAnswer, outcome, outbox, cancellation).ConfigureAwait(false);
    }

    /// <summary>After a change: a refusal goes to the actor; otherwise the guild is read again, every member here gets it with the news, and whoever was taken out is told.</summary>
    private async Task ApplyAsync(ChatPeer peer, string intent, GuildOutcome outcome, Outbox outbox, CancellationToken cancellation)
    {
        if (outcome.Refusal is { } refused)
        {
            outbox.Refuse(peer.Character, intent, refused);
            return;
        }
        if (outcome.Removed is { } removed)
            lock (_lock)
                if (_guildOf.TryGetValue(removed, out var of) && of == outcome.Guild) _guildOf.Remove(removed);
        var members = await RefreshAsync(outcome.Guild, cancellation).ConfigureAwait(false);
        if (outcome.Removed is { } gone && EntityOf(gone) is { } goneEntity)
        {
            outbox.Add(goneEntity, GuildMessage.None);
            if (outcome.RemovedNews is { } told) outbox.Add(goneEntity, told);
        }
        foreach (var news in outcome.News ?? [])
            foreach (var member in members)
                outbox.Add(member, news);
        if (outcome.Disbanded) _log.Info($"[guild] guild {outcome.Guild} disbanded.");
    }

    // ---- the copy of guilds ------------------------------------------------------------------------------------------------------------------

    /// <summary>Reads <paramref name="guild"/> again and sends it to its members here (those no longer in it get "no guild"). Returns the members here.</summary>
    private async Task<IReadOnlyList<EntityId>> RefreshAsync(long guild, CancellationToken cancellation)
    {
        var snapshot = await _store.LoadAsync(guild, cancellation).ConfigureAwait(false);
        var outbox = new Outbox();
        var here = new List<EntityId>();
        lock (_lock)
        {
            var inIt = snapshot?.Members.Select(m => m.Character).ToHashSet() ?? [];
            foreach (var (character, of) in _guildOf.ToList())
                if (of == guild && !inIt.Contains(character))
                {
                    _guildOf.Remove(character);
                    if (_peers.TryGetValue(character, out var entity)) outbox.Add(entity, GuildMessage.None);
                }
            if (snapshot is null) _guilds.Remove(guild);
            else
            {
                foreach (var m in snapshot.Members)
                    if (_peers.ContainsKey(m.Character)) _guildOf[m.Character] = guild;
                var cached = new Cached(snapshot);
                _guilds[guild] = cached;
                SendGuild(cached, outbox, here);
            }
        }
        Send(outbox);
        return here;
    }

    /// <summary>The guild to every member here (under the lock). Remembers whom it showed online.</summary>
    private void SendGuild(Cached cached, Outbox outbox, List<EntityId>? here = null)
    {
        var s = cached.Snapshot;
        var online = s.Members.Select(m => _peers.TryGetValue(m.Character, out var e) && _directory.Playing(e) is not null).ToArray();
        cached.Online = OnlineKey(online);
        var members = s.Members.Select((m, i) => new GuildMemberRecord(m.Member, m.Name, m.Rank, m.Level, online[i])).ToArray();
        foreach (var m in s.Members)
        {
            if (!_peers.TryGetValue(m.Character, out var entity)) continue;
            here?.Add(entity);
            outbox.Add(entity, new GuildMessage(s.Id, s.Name, s.Emblem, s.Notice, m.Member, s.Ranks, members));
        }
    }

    private async Task WelcomeAsync(EntityId entity, CancellationToken stop)
    {
        try
        {
            ChatPeer? peer = null;
            for (var i = 0; i < 50 && (peer = _directory.Playing(entity)) is null; i++) await Task.Delay(TimeSpan.FromMilliseconds(100), _time, stop).ConfigureAwait(false);
            if (peer?.CharacterId is not { } me) return;
            Invite? invite;
            long[] keep;
            bool founding;
            lock (_lock)
            {
                _peers[me] = entity;
                _invites.TryGetValue(me, out invite);
                keep = _foundings.Values.Where(g => g > 0).ToArray();
                founding = _foundings.ContainsKey(me);
            }
            if (invite is not null && invite.ExpiresAt > Now) Post(entity, InvitedOf(invite));
            if (!founding) await _store.DropStaleReservationsAsync(me, keep, stop).ConfigureAwait(false);   // one a crash left
            if (await _store.GuildOfAsync(me, stop).ConfigureAwait(false) is { } guild)
            {
                lock (_lock) _guildOf[me] = guild;
                await RefreshAsync(guild, stop).ConfigureAwait(false);
            }
            else Post(entity, GuildMessage.None);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (StoreUnavailableException e)
        {
            _log.Warn($"[guild] the guild of {entity} could not be read: {e.Message}.");
        }
        catch (Exception e)
        {
            _log.Error($"[guild] welcoming {entity} failed.", e);
        }
    }

    // ---- the pulse ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One pulse (every <see cref="GuildOptions.PulseMs"/>): characters that left the world are forgotten, expired invites told and dropped, members whose online state changed shown again, and
    /// each player sent the emblem tags of its map when they changed. Every <see cref="GuildOptions.RefreshMs"/>, the guilds held here are compared with the database's versions.
    /// </summary>
    public async Task PulseAsync(CancellationToken cancellation = default)
    {
        var outbox = new Outbox();
        long[] guilds;
        Dictionary<long, long> versions;
        lock (_lock)
        {
            var now = Now;
            foreach (var (character, entity) in _peers.ToList())
                if (!_directory.Holds(entity))
                {
                    _peers.Remove(character);
                    _guildOf.Remove(character);
                }
            foreach (var (to, invite) in _invites.ToList())
            {
                if (!_directory.Holds(invite.ToEntity) || !_directory.Holds(invite.FromEntity))
                {
                    _invites.Remove(to);
                    continue;
                }
                if (invite.ExpiresAt > now) continue;
                _invites.Remove(to);
                outbox.Add(invite.ToEntity, new GuildNewsMessage(GuildNews.Expired, invite.FromName));
                outbox.Add(invite.FromEntity, new GuildNewsMessage(GuildNews.Expired, invite.ToName));
            }
            foreach (var (id, cached) in _guilds.ToList())
            {
                if (!_guildOf.ContainsValue(id))
                {
                    _guilds.Remove(id);   // nobody here is in it any more
                    continue;
                }
                var online = cached.Snapshot.Members.Select(m => _peers.TryGetValue(m.Character, out var e) && _directory.Playing(e) is not null).ToArray();
                if (OnlineKey(online) != cached.Online) SendGuild(cached, outbox);
            }
            Tags(outbox);
            var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
            if (nowMs - _lastRefreshMs >= _options.RefreshMs && _guilds.Count > 0)
            {
                _lastRefreshMs = nowMs;
                guilds = _guilds.Keys.ToArray();
                versions = _guilds.ToDictionary(g => g.Key, g => g.Value.Snapshot.Version);
            }
            else
            {
                guilds = [];
                versions = [];
            }
        }
        Send(outbox);
        if (guilds.Length == 0) return;
        var stored = await _store.VersionsAsync(guilds, cancellation).ConfigureAwait(false);
        foreach (var guild in guilds)
            if (!stored.TryGetValue(guild, out var version) || version != versions[guild])
                await RefreshAsync(guild, cancellation).ConfigureAwait(false);   // changed on another process, or gone
    }

    /// <summary>The emblems of the members on each map instance, to every player there whose list changed (under the lock).</summary>
    private void Tags(Outbox outbox)
    {
        var byMap = new Dictionary<string, List<GuildTagRecord>>(StringComparer.Ordinal);
        foreach (var (character, guild) in _guildOf)
        {
            if (!_guilds.TryGetValue(guild, out var cached) || !_peers.TryGetValue(character, out var entity) || _directory.Playing(entity) is not { MapId: { } map }) continue;
            if (!byMap.TryGetValue(map, out var list)) byMap[map] = list = new List<GuildTagRecord>();
            list.Add(new GuildTagRecord(entity.Value, cached.Snapshot.Emblem));
        }
        var seen = new HashSet<EntityId>();
        foreach (var (map, list) in byMap)
        {
            list.Sort((a, b) => a.Entity.CompareTo(b.Entity));
            var key = string.Join(';', list.Select(t => t.Entity + "=" + t.Emblem));
            foreach (var player in _directory.OnMap(map))
            {
                seen.Add(player.Character);
                if (_tagsSent.TryGetValue(player.Character, out var sent) && sent == key) continue;
                _tagsSent[player.Character] = key;
                outbox.Add(player.Character, new GuildTagsMessage(list.ToArray()));
            }
        }
        foreach (var (player, sent) in _tagsSent.ToList())
        {
            if (seen.Contains(player)) continue;
            if (!_directory.Holds(player)) _tagsSent.Remove(player);
            else if (sent.Length > 0 && _directory.Playing(player) is not null)
            {
                _tagsSent[player] = string.Empty;   // on a map with no members now
                outbox.Add(player, new GuildTagsMessage([]));
            }
        }
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_options.PulseMs), _time, stop).ConfigureAwait(false);
                await PulseAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (StoreUnavailableException e)
            {
                _log.Warn($"[guild] the refresh could not read the database: {e.Message}.");
            }
            catch (Exception e)
            {
                _log.Error("[guild] the pulse failed.", e);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------------------

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static string OnlineKey(bool[] online) => string.Concat(online.Select(o => o ? '1' : '0'));

    private EntityId? EntityOf(Guid character)
    {
        lock (_lock) return _peers.TryGetValue(character, out var entity) ? entity : null;
    }

    /// <summary>A rank name cleaned like a chat line (and no longer than <c>guild.rank_name_max_length</c>), or null.</summary>
    private string? RankName(string raw) => ChatText.Clean(raw, _balance.RankNameMaxLength, _marksPerLetter).Text;

    /// <summary>The notice cleaned like a chat line: empty clears it; over-long is refused.</summary>
    private CleanText Notice(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new CleanText(string.Empty, null);
        var clean = ChatText.Clean(raw, _options.NoticeLength, _marksPerLetter);
        if (clean.Refusal == ChatReasons.TooLong) return new CleanText(null, GuildReasons.TooLong);
        return clean.Text is null ? new CleanText(string.Empty, null) : clean;
    }

    private GuildInvitedMessage InvitedOf(Invite invite) =>
        new(invite.FromName, invite.Title, invite.Emblem, Math.Max(1, (long)Math.Ceiling((invite.ExpiresAt - Now).TotalSeconds)));

    private void Post(EntityId to, ServerMessage message)
    {
        if (_directory.Playing(to) is { } peer) peer.Sink.Post(message, ProtocolJson.ToBytes(message));
    }

    /// <summary>Posts what was decided, outside the lock: to characters that play now (one away gets it all again at its welcome).</summary>
    private void Send(Outbox outbox)
    {
        foreach (var (to, message) in outbox.Items) Post(to, message);
    }

    private sealed class Cached(GuildSnapshot snapshot)
    {
        public GuildSnapshot Snapshot { get; } = snapshot;

        /// <summary>Who was last shown online, one character per member.</summary>
        public string Online { get; set; } = string.Empty;
    }

    private sealed record Invite(Guid From, EntityId FromEntity, string FromName, long Guild, string Title, string Emblem, Guid To, EntityId ToEntity, string ToName, DateTime ExpiresAt);

    private sealed class Outbox
    {
        public List<(EntityId To, ServerMessage Message)> Items { get; } = new();

        public void Add(EntityId to, ServerMessage message) => Items.Add((to, message));

        public void Refuse(EntityId to, string intent, string reason, long? retryAfter = null, string? name = null) =>
            Items.Add((to, new GuildRefusedMessage(intent, reason, retryAfter, name)));
    }
}
