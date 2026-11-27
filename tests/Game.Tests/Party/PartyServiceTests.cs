using Game.Server.Admin;
using Game.Server.Chat;
using Game.Server.Party;
using Game.Server.World;
using Game.Server.World.Party;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Accounts;
using Game.Tests.Chat;
using Game.Tests.Server;

namespace Game.Tests.Party;

/// <summary>Who plays where for the party service, set by the test: playing peers, characters in their grace period, and blocks.</summary>
public sealed class FakePartyDirectory : IPartyDirectory, IChatDirectory
{
    public List<ChatPeer> Playing { get; } = new();

    public HashSet<EntityId> InGrace { get; } = new();

    public HashSet<(EntityId Listener, EntityId Speaker)> BlockList { get; } = new();

    public ChatPeer Add(long entity, string name, string map = "map.meadow")
    {
        var peer = new ChatPeer(new EntityId(entity), name, Guid.NewGuid(), Guid.NewGuid(), map, new ListSink());
        Playing.Add(peer);
        return peer;
    }

    /// <summary>The connection dropped: the character waits in its grace period.</summary>
    public void Drop(ChatPeer peer)
    {
        Playing.Remove(peer);
        InGrace.Add(peer.Character);
    }

    /// <summary>The grace period ended, or a logout: the character left the world.</summary>
    public void Gone(ChatPeer peer)
    {
        Playing.Remove(peer);
        InGrace.Remove(peer.Character);
    }

    public void Back(ChatPeer peer)
    {
        InGrace.Remove(peer.Character);
        Playing.Add(peer);
    }

    ChatPeer? IPartyDirectory.Playing(EntityId character) => Playing.FirstOrDefault(p => p.Character == character);

    public ChatPeer? Online(string nameKey) => Playing.FirstOrDefault(p => ChatNames.Key(p.Name) == nameKey);

    public bool Holds(EntityId character) => InGrace.Contains(character) || Playing.Any(p => p.Character == character);

    public bool Blocks(ChatPeer listener, ChatPeer speaker) => BlockList.Contains((listener.Character, speaker.Character));

    public IReadOnlyList<ChatPeer> OnMap(string mapId) => Playing.Where(p => p.MapId == mapId).ToList();

    public ChatPeer? Find(EntityId character) => Playing.FirstOrDefault(p => p.Character == character);

    public IReadOnlyCollection<Guid> PlayingAccounts() => Playing.Select(p => p.AccountId!.Value).ToList();
}

/// <summary>
/// T-902's party service on its own: the invite flow and every refusal, expiry, the leader's hand-over, the grace period, the size limit, a party of one ending, members' state to
/// members only, and party chat through T-901's seam with the mute.
/// </summary>
public sealed class PartyServiceTests
{
    private readonly FakePartyDirectory _directory = new();
    private readonly FakeTime _time = new() { Now = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero) };
    private readonly ListLog _log = new();
    private readonly PartyRoster _roster = new();
    private readonly PartyBalanceDef _balance = new() { MaxMembers = 3, InviteTimeoutMs = 30_000 };

    private PartyService New(PartyOptions? options = null) => new(_balance, options ?? new PartyOptions { InviteBurst = 100, InvitesPerMinute = 100 }, _roster, _directory, _time, _log);

    private static ListSink Sink(ChatPeer peer) => (ListSink)peer.Sink;

    private static T Last<T>(ChatPeer peer) where T : ServerMessage => Sink(peer).Messages.OfType<T>().Last();

    private static PartyRefusedMessage Refusal(ChatPeer peer) => Last<PartyRefusedMessage>(peer);

    private static void Invite(PartyService party, ChatPeer from, ChatPeer to) => party.Handle(from, new PartyInviteMessage(to.Name, null));

    private static void Accept(PartyService party, ChatPeer who) => party.Handle(who, new PartyAnswerMessage(true));

    /// <summary>Ada invites, Bo accepts: a party of two led by Ada.</summary>
    private (PartyService Party, ChatPeer Ada, ChatPeer Bo) Two()
    {
        var party = New();
        var ada = _directory.Add(1, "Ada");
        var bo = _directory.Add(2, "Bo");
        Invite(party, ada, bo);
        Accept(party, bo);
        return (party, ada, bo);
    }

    // ---- the invite flow ----

    [Fact]
    public void An_invite_by_name_is_seen_by_the_invited_and_accepting_forms_a_party_led_by_the_inviter()
    {
        var party = New();
        var ada = _directory.Add(1, "Ada");
        var bo = _directory.Add(2, "Bo");

        party.Handle(ada, new PartyInviteMessage("  bO ", null));   // names compare as names do (T-701)
        var invited = Last<PartyInvitedMessage>(bo);
        (invited.From, invited.Name, invited.ExpiresInSeconds).ShouldBe((1L, "Ada", 30L));
        Last<PartyNoticeMessage>(ada).ShouldBe(new PartyNoticeMessage(PartyNotices.InviteSent, "Bo"));
        party.PartyCount.ShouldBe(0);   // nothing before the answer

        Accept(party, bo);
        party.PartyCount.ShouldBe(1);
        foreach (var member in new[] { ada, bo })
        {
            var state = Last<PartyMessage>(member);
            state.Leader.ShouldBe(1);
            state.Members.ShouldBe([new PartyMemberRecord(1, "Ada"), new PartyMemberRecord(2, "Bo")]);
        }
        Last<PartyNoticeMessage>(ada).ShouldBe(new PartyNoticeMessage(PartyNotices.Joined, "Bo"));
        _roster.Of(ada.Character)!.Members.ShouldBe([ada.Character, bo.Character]);
        _roster.Of(bo.Character).ShouldBeSameAs(_roster.Of(ada.Character));
    }

    [Fact]
    public void Tapping_a_player_invites_it_only_on_the_same_map()
    {
        var party = New();
        var ada = _directory.Add(1, "Ada");
        var bo = _directory.Add(2, "Bo");
        var far = _directory.Add(3, "Far", "map.cave");

        party.Handle(ada, new PartyInviteMessage(null, far.Character.Value));
        Refusal(ada).Reason.ShouldBe(PartyReasons.NotOnline);
        Sink(far).Messages.OfType<PartyInvitedMessage>().ShouldBeEmpty();

        party.Handle(ada, new PartyInviteMessage(null, bo.Character.Value));
        Last<PartyInvitedMessage>(bo).From.ShouldBe(1);
    }

    [Fact]
    public void Declining_tells_the_inviter_and_forms_nothing()
    {
        var party = New();
        var ada = _directory.Add(1, "Ada");
        var bo = _directory.Add(2, "Bo");
        Invite(party, ada, bo);
        party.Handle(bo, new PartyAnswerMessage(false));
        Last<PartyNoticeMessage>(ada).ShouldBe(new PartyNoticeMessage(PartyNotices.Declined, "Bo"));
        party.PartyCount.ShouldBe(0);
        party.Handle(bo, new PartyAnswerMessage(true));
        Refusal(bo).Reason.ShouldBe(PartyReasons.NoInvite);   // the invite is used up
    }

    [Fact]
    public void An_invite_expires_after_the_content_timeout_and_both_are_told()
    {
        var party = New();
        var ada = _directory.Add(1, "Ada");
        var bo = _directory.Add(2, "Bo");
        Invite(party, ada, bo);

        _time.Now += TimeSpan.FromMilliseconds(_balance.InviteTimeoutMs - 1);
        party.Pulse();
        Sink(ada).Messages.OfType<PartyNoticeMessage>().ShouldNotContain(n => n.Notice == PartyNotices.Expired);

        _time.Now += TimeSpan.FromMilliseconds(1);
        party.Pulse();
        Last<PartyNoticeMessage>(ada).ShouldBe(new PartyNoticeMessage(PartyNotices.Expired, "Bo"));
        Last<PartyNoticeMessage>(bo).ShouldBe(new PartyNoticeMessage(PartyNotices.Expired, "Ada"));
        Accept(party, bo);
        Refusal(bo).Reason.ShouldBe(PartyReasons.NoInvite);
        party.PartyCount.ShouldBe(0);
    }

    [Fact]
    public void An_answer_after_the_timeout_but_before_the_pulse_is_too_late()
    {
        var party = New();
        var ada = _directory.Add(1, "Ada");
        var bo = _directory.Add(2, "Bo");
        Invite(party, ada, bo);
        _time.Now += TimeSpan.FromMilliseconds(_balance.InviteTimeoutMs);
        Accept(party, bo);
        Refusal(bo).Reason.ShouldBe(PartyReasons.NoInvite);
    }

    // ---- refusals ----

    [Fact]
    public void Nobody_of_that_name_oneself_and_a_block_are_refused_and_a_block_reads_as_not_online()
    {
        var party = New();
        var ada = _directory.Add(1, "Ada");
        var bo = _directory.Add(2, "Bo");

        party.Handle(ada, new PartyInviteMessage("Ghost", null));
        Refusal(ada).ShouldBe(new PartyRefusedMessage(MessageTypes.PartyInvite, PartyReasons.NotOnline, null, "Ghost"));
        party.Handle(ada, new PartyInviteMessage("Ada", null));
        Refusal(ada).Reason.ShouldBe(PartyReasons.InvalidTarget);

        _directory.BlockList.Add((bo.Character, ada.Character));   // Bo blocks Ada
        party.Handle(ada, new PartyInviteMessage("Bo", null));
        Refusal(ada).ShouldBe(new PartyRefusedMessage(MessageTypes.PartyInvite, PartyReasons.NotOnline, null, "Bo"), "exactly what a name nobody has answers");
        Sink(bo).Messages.OfType<PartyInvitedMessage>().ShouldBeEmpty();
    }

    [Fact]
    public void Someone_in_a_party_or_with_an_unanswered_invite_cannot_be_invited()
    {
        var (party, ada, bo) = Two();
        var cy = _directory.Add(3, "Cy");
        var dee = _directory.Add(4, "Dee");

        Invite(party, cy, bo);
        Refusal(cy).ShouldBe(new PartyRefusedMessage(MessageTypes.PartyInvite, PartyReasons.AlreadyInParty, null, "Bo"));

        Invite(party, ada, dee);
        Invite(party, cy, dee);
        Refusal(cy).Reason.ShouldBe(PartyReasons.InvitePending);
        Sink(dee).Messages.OfType<PartyInvitedMessage>().Count().ShouldBe(1);
    }

    [Fact]
    public void Only_the_leader_invites_kicks_and_hands_over()
    {
        var (party, ada, bo) = Two();
        var cy = _directory.Add(3, "Cy");

        Invite(party, bo, cy);
        Refusal(bo).Reason.ShouldBe(PartyReasons.NotLeader);
        party.Handle(bo, new PartyKickMessage(ada.Character.Value));
        Refusal(bo).Reason.ShouldBe(PartyReasons.NotLeader);
        party.Handle(bo, new PartyLeadMessage(bo.Character.Value));
        Refusal(bo).Reason.ShouldBe(PartyReasons.NotLeader);
        party.Handle(ada, new PartyKickMessage(ada.Character.Value));
        Refusal(ada).Reason.ShouldBe(PartyReasons.InvalidTarget);
        party.Handle(ada, new PartyKickMessage(cy.Character.Value));
        Refusal(ada).Reason.ShouldBe(PartyReasons.NotInParty);
        party.Handle(cy, new PartyLeaveMessage());
        Refusal(cy).Reason.ShouldBe(PartyReasons.NotInParty);
    }

    [Fact]
    public void The_size_limit_from_content_refuses_the_invite_and_an_accept_that_comes_too_late()
    {
        var (party, ada, bo) = Two();
        var cy = _directory.Add(3, "Cy");
        var dee = _directory.Add(4, "Dee");
        Invite(party, ada, cy);
        Invite(party, ada, dee);   // two invites out for the last place
        Accept(party, cy);
        Last<PartyMessage>(ada).Members.Count.ShouldBe(_balance.MaxMembers);

        Accept(party, dee);
        Refusal(dee).ShouldBe(new PartyRefusedMessage(MessageTypes.PartyAnswer, PartyReasons.Full));
        var eve = _directory.Add(5, "Eve");
        Invite(party, ada, eve);
        Refusal(ada).ShouldBe(new PartyRefusedMessage(MessageTypes.PartyInvite, PartyReasons.Full, null, "Eve"));
    }

    [Fact]
    public void Invites_are_rate_limited_per_account()
    {
        var party = New(new PartyOptions { InviteBurst = 2, InvitesPerMinute = 6 });
        var ada = _directory.Add(1, "Ada");
        party.Handle(ada, new PartyInviteMessage("Ghost1", null));
        party.Handle(ada, new PartyInviteMessage("Ghost2", null));
        party.Handle(ada, new PartyInviteMessage("Ghost3", null));
        Refusal(ada).ShouldBe(new PartyRefusedMessage(MessageTypes.PartyInvite, PartyReasons.RateLimited, 10, "Ghost3"));   // 6 a minute: one back every 10 s
        _time.Now += TimeSpan.FromSeconds(10);
        party.Handle(ada, new PartyInviteMessage("Ghost4", null));
        Refusal(ada).Reason.ShouldBe(PartyReasons.NotOnline);
    }

    // ---- leaving, kicking, the leader ----

    [Fact]
    public void The_leader_leaving_hands_the_leader_to_the_earliest_joined_member_who_plays()
    {
        var (party, ada, bo) = Two();
        var cy = _directory.Add(3, "Cy");
        Invite(party, ada, cy);
        Accept(party, cy);
        _directory.Drop(bo);   // Bo joined first, but is away

        party.Handle(ada, new PartyLeaveMessage());
        var state = Last<PartyMessage>(cy);
        state.Leader.ShouldBe(cy.Character.Value);
        state.Members.Select(m => m.Name).ShouldBe(["Bo", "Cy"]);
        Last<PartyMessage>(ada).Members.ShouldBeEmpty();
        Sink(cy).Messages.OfType<PartyNoticeMessage>().TakeLast(2).ShouldBe([new PartyNoticeMessage(PartyNotices.Left, "Ada"), new PartyNoticeMessage(PartyNotices.Leader, "Cy")]);
        _roster.Of(ada.Character).ShouldBeNull();
        _roster.Of(cy.Character)!.Leader.ShouldBe(cy.Character);
    }

    [Fact]
    public void With_nobody_playing_the_earliest_joined_member_leads()
    {
        var (party, ada, bo) = Two();
        var cy = _directory.Add(3, "Cy");
        Invite(party, ada, cy);
        Accept(party, cy);
        _directory.Drop(bo);
        _directory.Drop(cy);
        party.Handle(ada, new PartyLeaveMessage());
        _roster.Of(bo.Character)!.Leader.ShouldBe(bo.Character);
    }

    [Fact]
    public void The_leader_hands_over_and_kicks_and_a_party_of_one_ends()
    {
        var (party, ada, bo) = Two();
        var cy = _directory.Add(3, "Cy");
        Invite(party, ada, cy);
        Accept(party, cy);

        party.Handle(ada, new PartyLeadMessage(bo.Character.Value));
        Last<PartyMessage>(cy).Leader.ShouldBe(bo.Character.Value);
        Last<PartyNoticeMessage>(cy).ShouldBe(new PartyNoticeMessage(PartyNotices.Leader, "Bo"));

        party.Handle(bo, new PartyKickMessage(cy.Character.Value));
        Last<PartyMessage>(cy).Members.ShouldBeEmpty();
        Last<PartyNoticeMessage>(cy).ShouldBe(new PartyNoticeMessage(PartyNotices.Kicked, "Cy"));
        Last<PartyNoticeMessage>(ada).ShouldBe(new PartyNoticeMessage(PartyNotices.Kicked, "Cy"));

        party.Handle(bo, new PartyKickMessage(ada.Character.Value));
        party.PartyCount.ShouldBe(0);
        Last<PartyMessage>(bo).Members.ShouldBeEmpty();
        Last<PartyNoticeMessage>(bo).ShouldBe(new PartyNoticeMessage(PartyNotices.Disbanded));
        _roster.Any.ShouldBeFalse();
    }

    [Fact]
    public void The_leader_cannot_be_handed_to_a_member_who_is_away()
    {
        var (party, ada, bo) = Two();
        _directory.Drop(bo);
        party.Handle(ada, new PartyLeadMessage(bo.Character.Value));
        Refusal(ada).Reason.ShouldBe(PartyReasons.NotOnline);
        _roster.Of(ada.Character)!.Leader.ShouldBe(ada.Character);
    }

    // ---- the grace period ----

    [Fact]
    public void A_member_who_drops_stays_through_its_grace_period_shown_away_and_leaves_when_it_ends()
    {
        var (party, ada, bo) = Two();
        var cy = _directory.Add(3, "Cy");
        Invite(party, ada, cy);
        Accept(party, cy);
        _directory.Drop(bo);

        party.Pulse();
        party.PartyCount.ShouldBe(1);
        _roster.Of(bo.Character).ShouldNotBeNull();
        Last<PartyMembersMessage>(ada).Status.Single(s => s.Id == bo.Character.Value).Away.ShouldBeTrue();

        // Back within the grace period: still a member, and it gets its party again.
        _directory.Back(bo);
        party.Welcomed(bo.Character);
        Last<PartyMessage>(bo).Members.Count.ShouldBe(3);

        _directory.Drop(bo);
        _directory.Gone(bo);   // the grace period ended
        party.Pulse();
        Last<PartyMessage>(ada).Members.Select(m => m.Name).ShouldBe(["Ada", "Cy"]);
        Last<PartyNoticeMessage>(ada).ShouldBe(new PartyNoticeMessage(PartyNotices.Left, "Bo"));
        _roster.Of(bo.Character).ShouldBeNull();
    }

    [Fact]
    public void An_invite_dies_with_its_inviter()
    {
        var party = New();
        var ada = _directory.Add(1, "Ada");
        var bo = _directory.Add(2, "Bo");
        Invite(party, ada, bo);
        _directory.Gone(ada);
        Accept(party, bo);
        Refusal(bo).Reason.ShouldBe(PartyReasons.NoInvite);
        party.PartyCount.ShouldBe(0);
    }

    // ---- members' state ----

    [Fact]
    public void Members_state_goes_to_the_other_members_only_and_only_when_it_changed()
    {
        var (party, ada, bo) = Two();
        var stranger = _directory.Add(9, "Stranger");
        _roster.Report(ada.Character, new MemberStatus("map.meadow", new GridCell(3, 4), 80, 100, false));
        _roster.Report(bo.Character, new MemberStatus("map.cave", new GridCell(5, 6), 1, 0, false));
        _roster.Report(stranger.Character, new MemberStatus("map.meadow", new GridCell(1, 1), 50, 50, false));   // in no party: ignored

        party.Pulse();
        Last<PartyMembersMessage>(ada).Status.ShouldBe([new PartyMemberStatusRecord(2, 1, 0, "map.cave", new GridCell(5, 6), false, false)]);
        Last<PartyMembersMessage>(bo).Status.ShouldBe([new PartyMemberStatusRecord(1, 80, 100, "map.meadow", new GridCell(3, 4), false, false)]);
        Sink(stranger).Messages.OfType<PartyMembersMessage>().ShouldBeEmpty();
        _roster.StatusOf(stranger.Character).ShouldBeNull();

        var sent = Sink(ada).Messages.OfType<PartyMembersMessage>().Count();
        party.Pulse();
        Sink(ada).Messages.OfType<PartyMembersMessage>().Count().ShouldBe(sent, "nothing changed, nothing sent");
        _roster.Report(bo.Character, new MemberStatus("map.cave", new GridCell(5, 7), 1, 0, false));
        party.Pulse();
        Last<PartyMembersMessage>(ada).Status.Single().Cell.ShouldBe(new GridCell(5, 7));
    }

    [Fact]
    public void An_instance_reports_hp_and_sp_in_whole_percent_never_zero_while_alive()
    {
        var entity = new Entity(new EntityId(1), EntityKind.Player, "map.meadow", new GridCell(2, 3), Directions.S) { Stats = TestCombat.PlayerSheet(maxHp: 300, maxSp: 40) };
        entity.Vitals = new Game.Server.World.Combat.Vitals(entity.Stats);
        entity.Vitals.Hp = 1;
        entity.Vitals.Sp = entity.Vitals.MaxSp;
        PartySystem.StatusOf("map.meadow", entity).ShouldBe(new MemberStatus("map.meadow", new GridCell(2, 3), 1, 100, false));
        entity.Vitals.Hp = 150;
        PartySystem.StatusOf("map.meadow", entity).HpPercent.ShouldBe(50);
        entity.Dead = true;
        PartySystem.StatusOf("map.meadow", entity).ShouldBe(new MemberStatus("map.meadow", new GridCell(2, 3), 0, 0, true));
    }

    // ---- party chat through T-901's seam ----

    [Fact]
    public async Task Party_chat_reaches_the_members_only_and_a_muted_member_is_refused_like_on_every_channel()
    {
        var (party, ada, bo) = Two();
        var stranger = _directory.Add(9, "Stranger");
        var moderation = new FakeModeration();
        var checks = new ModerationChecks(moderation, _time);
        var chat = new ChatService(new ChatOptions(), _directory, null, checks.SpeakAsync, _time, _log);
        chat.Register(ChatChannels.Party, party);

        await chat.HandleAsync(ada, new ChatMessage(ChatChannels.Party, null, "pull the slime"));
        Sink(bo).Lines.ShouldHaveSingleItem().ShouldSatisfyAllConditions(l => l.Channel.ShouldBe(ChatChannels.Party), l => l.Text.ShouldBe("pull the slime"));
        Sink(ada).Lines.ShouldHaveSingleItem();   // the echo
        Sink(stranger).Lines.ShouldBeEmpty();

        await chat.HandleAsync(stranger, new ChatMessage(ChatChannels.Party, null, "let me in"));
        Sink(stranger).Refusals.Last().Reason.ShouldBe(ChatReasons.InvalidChannel);

        moderation.Mutes[bo.AccountId!.Value] = _time.Now.UtcDateTime.AddMinutes(5);
        await chat.HandleAsync(bo, new ChatMessage(ChatChannels.Party, null, "muted words"));
        Sink(bo).Refusals.Last().Reason.ShouldBe(ChatReasons.Muted);
        Sink(ada).Lines.Count.ShouldBe(1);
    }
}
