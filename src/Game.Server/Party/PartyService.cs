using Game.Server.Chat;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Party;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Party;

/// <summary>
/// Parties (T-902, ARCHITECTURE §6.9): the server decides every change. A party lives in this server process's memory only: it is never stored, and it ends with the process. Its
/// members are characters this gateway holds, so all of them are on this process (a character on another process can neither be invited nor found: v1 has one game process per world).
/// <list type="bullet">
/// <item><b>Invite</b> (<c>party_invite</c>), by name or by tapping a player on the same map: rate-limited per account, then refused when nobody of that name plays (also when the
/// invited blocks the inviter: a block does not show), it is oneself, the inviter is in a party it does not lead, the party is full, the invited is in a party or has an unanswered
/// invite. The invited gets <c>party_invited</c>; it expires after <c>party.invite_timeout_ms</c>.</item>
/// <item><b>Answer</b> (<c>party_answer</c>): decline tells the inviter; accept joins the inviter's party, or makes one led by the inviter.</item>
/// <item><b>Leave, kick, hand over</b> (<c>party_leave</c>, <c>party_kick</c>, <c>party_lead</c>; the last two the leader's). When the leader goes, the earliest-joined member who plays
/// now leads (else the earliest-joined). A party of one ends.</item>
/// <item><b>Disconnects</b>: a member in its grace period stays a member (shown away), so a dropped phone comes back to its party; when the grace period ends it leaves as if it had left.</item>
/// <item><b>Members' state</b>: every <see cref="PartyOptions.StatusIntervalMs"/>, the members' HP, SP, map and cell that the instances reported (<see cref="PartyRoster"/>) go to the
/// other members, and only to them, when they changed.</item>
/// <item><b>Party chat</b>: this is the <c>party</c> channel's <see cref="IChannelMembers"/> (chat does the mute, rate, text, blocks and reports).</item>
/// </list>
/// All of it runs off the tick, under one lock; messages are posted after the lock is let go. Thread-safe.
/// </summary>
public sealed class PartyService : IChannelMembers, IAsyncDisposable
{
    private readonly PartyBalanceDef _balance;
    private readonly PartyOptions _options;
    private readonly PartyRoster _roster;
    private readonly IPartyDirectory _directory;
    private readonly TimeProvider _time;
    private readonly IServerLog _log;
    private readonly ChatLimits _limits;
    private readonly object _lock = new();
    private readonly Dictionary<long, PartyState> _parties = new();
    private readonly Dictionary<EntityId, PartyState> _of = new();
    private readonly Dictionary<EntityId, Invite> _invites = new();               // by the invited
    private readonly Dictionary<EntityId, PartyMemberStatusRecord[]> _sent = new(); // what each member was last told of the others
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    private Task? _loop;
    private int _disposed;

    /// <summary>Creates the service over the shared <paramref name="roster"/> (combat and the instances read it).</summary>
    public PartyService(PartyBalanceDef balance, PartyOptions options, PartyRoster roster, IPartyDirectory directory, TimeProvider time, IServerLog log)
    {
        _balance = balance;
        _options = options;
        _roster = roster;
        _directory = directory;
        _time = time;
        _log = log;
        _limits = new ChatLimits(new ChatOptions { LinesPerMinute = options.InvitesPerMinute, Burst = options.InviteBurst }, time);
    }

    /// <summary>Parties now.</summary>
    public int PartyCount
    {
        get
        {
            lock (_lock) return _parties.Count;
        }
    }

    /// <summary>Starts the pulse: invites expire, members whose grace period ended leave, members' state goes out.</summary>
    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    // ---- what the gateway calls --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Handles one party message of <paramref name="peer"/> (a character that plays now): <c>party_invite</c>, <c>party_answer</c>, <c>party_leave</c>, <c>party_kick</c> or
    /// <c>party_lead</c>. Never throws for what a client sent.
    /// </summary>
    public void Handle(ChatPeer peer, ClientMessage message)
    {
        var outbox = new Outbox();
        try
        {
            lock (_lock)
            {
                switch (message)
                {
                    case PartyInviteMessage invite: SendInvite(peer, invite, outbox); break;
                    case PartyAnswerMessage answer: Answer(peer, answer.Accept, outbox); break;
                    case PartyLeaveMessage: Leave(peer, outbox); break;
                    case PartyKickMessage kick: Kick(peer, new EntityId(kick.Target), outbox); break;
                    case PartyLeadMessage lead: Lead(peer, new EntityId(lead.Target), outbox); break;
                }
            }
        }
        catch (Exception e)
        {
            _log.Error($"[party] {message.Type} of {peer.Character} failed.", e);
        }
        Send(outbox);
    }

    /// <summary>
    /// A session (re)took <paramref name="character"/> and had its welcome (a resume, a takeover): it gets its party and any invite again, and the members' state at the next pulse.
    /// Any thread; called under the gateway's link lock, so it only takes the party lock and posts.
    /// </summary>
    public void Welcomed(EntityId character)
    {
        var outbox = new Outbox();
        lock (_lock)
        {
            _sent.Remove(character);
            outbox.Add(character, MessageOf(_of.GetValueOrDefault(character)));
            if (_invites.TryGetValue(character, out var invite)) outbox.Add(character, InvitedOf(invite));
        }
        Send(outbox);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<EntityId> MembersFor(ChatPeer speaker)
    {
        lock (_lock) return _of.TryGetValue(speaker.Character, out var party) ? party.Members.Select(m => m.Id).ToArray() : [];
    }

    // ---- intents -----------------------------------------------------------------------------------------------------------------------------

    private void SendInvite(ChatPeer inviter, PartyInviteMessage message, Outbox outbox)
    {
        var intent = MessageTypes.PartyInvite;
        if (!_limits.TryTake(LimitKey(inviter), out var wait))
        {
            outbox.Refuse(inviter.Character, intent, PartyReasons.RateLimited, wait, message.Name);
            return;
        }
        ChatPeer? target;
        if (message.Target is { } id)
        {
            target = _directory.Playing(new EntityId(id));
            if (target is not null && (target.MapId is null || target.MapId != inviter.MapId)) target = null;   // a tap: only a player on the same map
        }
        else target = ChatNames.Key(message.Name) is { } key ? _directory.Online(key) : null;
        if (target is not null && target.Character == inviter.Character)
        {
            outbox.Refuse(inviter.Character, intent, PartyReasons.InvalidTarget, name: message.Name);
            return;
        }
        // A block reads as "not online": the inviter learns nothing it would not learn from a player who left.
        if (target is null || _directory.Blocks(target, inviter))
        {
            outbox.Refuse(inviter.Character, intent, PartyReasons.NotOnline, name: message.Name);
            return;
        }
        var name = target.Name;
        if (_of.TryGetValue(inviter.Character, out var party))
        {
            if (party.Leader != inviter.Character)
            {
                outbox.Refuse(inviter.Character, intent, PartyReasons.NotLeader, name: name);
                return;
            }
            if (party.Members.Count >= _balance.MaxMembers)
            {
                outbox.Refuse(inviter.Character, intent, PartyReasons.Full, name: name);
                return;
            }
        }
        if (_of.ContainsKey(target.Character))
        {
            outbox.Refuse(inviter.Character, intent, PartyReasons.AlreadyInParty, name: name);
            return;
        }
        if (_invites.TryGetValue(target.Character, out var pending) && pending.ExpiresAt > Now)
        {
            outbox.Refuse(inviter.Character, intent, PartyReasons.InvitePending, name: name);
            return;
        }
        var invite = new Invite(inviter.Character, inviter.Name, inviter.AccountId, target.Character, name, Now.AddMilliseconds(_balance.InviteTimeoutMs));
        _invites[target.Character] = invite;
        outbox.Add(target.Character, InvitedOf(invite));
        outbox.Add(inviter.Character, new PartyNoticeMessage(PartyNotices.InviteSent, name));
    }

    private void Answer(ChatPeer peer, bool accept, Outbox outbox)
    {
        var intent = MessageTypes.PartyAnswer;
        if (!_invites.Remove(peer.Character, out var invite) || invite.ExpiresAt <= Now || !_directory.Holds(invite.From))
        {
            outbox.Refuse(peer.Character, intent, PartyReasons.NoInvite);
            return;
        }
        if (!accept)
        {
            outbox.Add(invite.From, new PartyNoticeMessage(PartyNotices.Declined, peer.Name));
            return;
        }
        if (_of.ContainsKey(peer.Character))
        {
            outbox.Refuse(peer.Character, intent, PartyReasons.AlreadyInParty);
            return;
        }
        if (!_of.TryGetValue(invite.From, out var party))
        {
            party = new PartyState(++_nextId, invite.From);
            party.Members.Add(new Member(invite.From, invite.FromName));
            _parties[party.Id] = party;
            _of[invite.From] = party;
            _log.Info($"[party] party {party.Id} formed, led by {invite.From}.");
        }
        else if (party.Members.Count >= _balance.MaxMembers)
        {
            outbox.Refuse(peer.Character, intent, PartyReasons.Full);
            return;
        }
        party.Members.Add(new Member(peer.Character, peer.Name));
        _of[peer.Character] = party;
        Changed(party, outbox);
        foreach (var member in party.Members)
            if (member.Id != peer.Character) outbox.Add(member.Id, new PartyNoticeMessage(PartyNotices.Joined, peer.Name));
    }

    private void Leave(ChatPeer peer, Outbox outbox)
    {
        if (!_of.TryGetValue(peer.Character, out var party))
        {
            outbox.Refuse(peer.Character, MessageTypes.PartyLeave, PartyReasons.NotInParty);
            return;
        }
        Remove(party, peer.Character, PartyNotices.Left, outbox);
    }

    private void Kick(ChatPeer peer, EntityId target, Outbox outbox)
    {
        if (Leading(peer, target, MessageTypes.PartyKick, outbox) is not { } party) return;
        Remove(party, target, PartyNotices.Kicked, outbox);
    }

    private void Lead(ChatPeer peer, EntityId target, Outbox outbox)
    {
        if (Leading(peer, target, MessageTypes.PartyLead, outbox) is not { } party) return;
        if (_directory.Playing(target) is null)
        {
            outbox.Refuse(peer.Character, MessageTypes.PartyLead, PartyReasons.NotOnline);   // away in its grace period: it could not lead
            return;
        }
        party.Leader = target;
        Changed(party, outbox);
        var name = party.Members.First(m => m.Id == target).Name;
        foreach (var member in party.Members) outbox.Add(member.Id, new PartyNoticeMessage(PartyNotices.Leader, name));
    }

    /// <summary>The party <paramref name="peer"/> leads and <paramref name="target"/> (not the leader) is in, or null after a refusal.</summary>
    private PartyState? Leading(ChatPeer peer, EntityId target, string intent, Outbox outbox)
    {
        if (!_of.TryGetValue(peer.Character, out var party))
        {
            outbox.Refuse(peer.Character, intent, PartyReasons.NotInParty);
            return null;
        }
        if (party.Leader != peer.Character)
        {
            outbox.Refuse(peer.Character, intent, PartyReasons.NotLeader);
            return null;
        }
        if (target == peer.Character)
        {
            outbox.Refuse(peer.Character, intent, PartyReasons.InvalidTarget);
            return null;
        }
        if (!party.Members.Any(m => m.Id == target))
        {
            outbox.Refuse(peer.Character, intent, PartyReasons.NotInParty);
            return null;
        }
        return party;
    }

    // ---- changes -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Takes <paramref name="who"/> out of <paramref name="party"/>; a new leader when it led; the party ends when one member is left.</summary>
    private void Remove(PartyState party, EntityId who, string notice, Outbox outbox)
    {
        var index = party.Members.FindIndex(m => m.Id == who);
        if (index < 0) return;
        var name = party.Members[index].Name;
        party.Members.RemoveAt(index);
        _of.Remove(who);
        _sent.Remove(who);
        _roster.Remove(who);
        outbox.Add(who, MessageOf(null));
        if (notice == PartyNotices.Kicked) outbox.Add(who, new PartyNoticeMessage(PartyNotices.Kicked, name));
        if (party.Members.Count <= 1)
        {
            _parties.Remove(party.Id);
            foreach (var last in party.Members)
            {
                _of.Remove(last.Id);
                _sent.Remove(last.Id);
                _roster.Remove(last.Id);
                outbox.Add(last.Id, MessageOf(null));
                outbox.Add(last.Id, new PartyNoticeMessage(notice, name));
                outbox.Add(last.Id, new PartyNoticeMessage(PartyNotices.Disbanded));
            }
            _log.Info($"[party] party {party.Id} ended.");
            return;
        }
        var newLeader = party.Leader == who;
        if (newLeader) party.Leader = (party.Members.FirstOrDefault(m => _directory.Playing(m.Id) is not null) ?? party.Members[0]).Id;
        Changed(party, outbox);
        var leaderName = party.Members.First(m => m.Id == party.Leader).Name;
        foreach (var member in party.Members)
        {
            outbox.Add(member.Id, new PartyNoticeMessage(notice, name));
            if (newLeader) outbox.Add(member.Id, new PartyNoticeMessage(PartyNotices.Leader, leaderName));
        }
    }

    /// <summary>The roster and every member learn the party as it is now; members' state goes out again in full at the next pulse.</summary>
    private void Changed(PartyState party, Outbox outbox)
    {
        var view = new PartyView(party.Id, party.Leader, party.Members.Select(m => m.Id).ToArray());
        _roster.Set(view);
        var message = MessageOf(party);
        foreach (var member in party.Members)
        {
            _sent.Remove(member.Id);
            outbox.Add(member.Id, message);
        }
    }

    // ---- the pulse ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One pulse (every <see cref="PartyOptions.StatusIntervalMs"/>): expired invites are told and dropped, as are those whose inviter or invited left; members who left the world
    /// (their grace period ended, a logout, a kick by staff) leave their party; each playing member gets the others' state if it changed.
    /// </summary>
    public void Pulse()
    {
        var outbox = new Outbox();
        lock (_lock)
        {
            var now = Now;
            foreach (var (to, invite) in _invites.ToList())
            {
                if (!_directory.Holds(to) || !_directory.Holds(invite.From))
                {
                    _invites.Remove(to);
                    continue;
                }
                if (invite.ExpiresAt > now) continue;
                _invites.Remove(to);
                outbox.Add(to, new PartyNoticeMessage(PartyNotices.Expired, invite.FromName));
                outbox.Add(invite.From, new PartyNoticeMessage(PartyNotices.Expired, invite.ToName));
            }
            foreach (var party in _parties.Values.ToList())
                foreach (var member in party.Members.ToList())
                    if (!_directory.Holds(member.Id) && _of.ContainsKey(member.Id)) Remove(party, member.Id, PartyNotices.Left, outbox);
            foreach (var party in _parties.Values)
            {
                var status = party.Members.Select(m => StatusOf(m.Id)).Where(s => s is not null).Select(s => s!).ToArray();
                foreach (var member in party.Members)
                {
                    var others = status.Where(s => s.Id != member.Id.Value).ToArray();
                    if (_sent.TryGetValue(member.Id, out var last) && last.SequenceEqual(others)) continue;
                    if (_directory.Playing(member.Id) is null) continue;   // away: it gets them in full when it is back
                    _sent[member.Id] = others;
                    outbox.Add(member.Id, new PartyMembersMessage(others));
                }
            }
        }
        Send(outbox);
    }

    /// <summary>A member's state for the others: away in its grace period, else what its instance last reported (none yet: left out).</summary>
    private PartyMemberStatusRecord? StatusOf(EntityId member)
    {
        if (_directory.Playing(member) is null) return new PartyMemberStatusRecord(member.Value, 0, 0, null, null, false, Away: true);
        return _roster.StatusOf(member) is { } s ? new PartyMemberStatusRecord(member.Value, s.HpPercent, s.SpPercent, s.MapId, s.Cell, s.Dead, false) : null;
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_options.StatusIntervalMs), _time, stop).ConfigureAwait(false);
                Pulse();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.Error("[party] the pulse failed.", e);
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

    private static string LimitKey(ChatPeer peer) => peer.AccountId is { } account ? ChatLimits.Of(account) : ChatLimits.Of(peer.Character);

    private PartyInvitedMessage InvitedOf(Invite invite) =>
        new(invite.From.Value, invite.FromName, Math.Max(1, (long)Math.Ceiling((invite.ExpiresAt - Now).TotalSeconds)));

    private static PartyMessage MessageOf(PartyState? party) =>
        party is null ? new PartyMessage(null, 0, []) : new PartyMessage(party.Id, party.Leader.Value, party.Members.Select(m => new PartyMemberRecord(m.Id.Value, m.Name)).ToArray());

    /// <summary>Posts what was decided, after the lock is let go: to members that play now (one away gets it all again when it is back).</summary>
    private void Send(Outbox outbox)
    {
        foreach (var (to, message) in outbox.Items)
            if (_directory.Playing(to) is { } peer) peer.Sink.Post(message, ProtocolJson.ToBytes(message));
    }

    private sealed class PartyState(long id, EntityId leader)
    {
        public long Id { get; } = id;
        public EntityId Leader { get; set; } = leader;
        public List<Member> Members { get; } = new();
    }

    private sealed record Member(EntityId Id, string Name);

    private sealed record Invite(EntityId From, string FromName, Guid? FromAccount, EntityId To, string ToName, DateTime ExpiresAt);

    private sealed class Outbox
    {
        public List<(EntityId To, ServerMessage Message)> Items { get; } = new();

        public void Add(EntityId to, ServerMessage message) => Items.Add((to, message));

        public void Refuse(EntityId to, string intent, string reason, long? retryAfter = null, string? name = null) =>
            Items.Add((to, new PartyRefusedMessage(intent, reason, retryAfter, name)));
    }
}
