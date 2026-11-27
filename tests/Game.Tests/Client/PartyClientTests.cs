using Game.Client.Core;
using Game.Client.Core.Chat;
using Game.Client.Core.Party;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Client;

/// <summary>T-902's client side without the engine: the party book holds only what the server said, in party order, and forgets it; the words of the party; the party channel.</summary>
public sealed class PartyClientTests
{
    [Fact]
    public void The_book_shows_the_party_the_server_sent_with_the_others_state_and_forgets_who_left()
    {
        var party = new PartyBook { OwnId = 42 };
        party.InParty.ShouldBeFalse();
        party.Apply(new PartyMessage(3, 42, [new PartyMemberRecord(42, "Ada"), new PartyMemberRecord(43, "Bo")]));
        (party.InParty, party.Leading).ShouldBe((true, true));
        party.Members.Select(m => (m.Name, m.Leader, m.Mine, m.Known)).ShouldBe([("Ada", true, true, false), ("Bo", false, false, false)]);

        party.Apply(new PartyMembersMessage([new PartyMemberStatusRecord(43, 80, 5, "map.meadow", new GridCell(3, 4), false, false)]));
        var bo = party.Members[1];
        (bo.Hp, bo.Sp, bo.Map, bo.Cell, bo.Known).ShouldBe((80, 5, "map.meadow", (GridCell?)new GridCell(3, 4), true));

        party.Apply(new PartyMessage(3, 43, [new PartyMemberRecord(43, "Bo")]));
        party.Leading.ShouldBeFalse();
        party.Apply(new PartyMessage(3, 43, [new PartyMemberRecord(42, "Ada"), new PartyMemberRecord(43, "Bo")]));
        party.Members[1].Known.ShouldBeTrue("Bo stayed: its state stays");
        party.Apply(new PartyMessage(null, 0, []));
        (party.InParty, party.Members.Count, party.Leader).ShouldBe((false, 0, 0L));
    }

    [Fact]
    public void An_invite_is_shown_until_answered_refused_or_past_its_end()
    {
        var party = new PartyBook { OwnId = 43 };
        party.Apply(new PartyInvitedMessage(42, "Ada", 30), 100);
        party.Invite.ShouldBe(new PartyInviteView(42, "Ada", 130));
        party.Expire(129.9);
        party.Invite.ShouldNotBeNull();
        party.Expire(130);
        party.Invite.ShouldBeNull();

        party.Apply(new PartyInvitedMessage(42, "Ada", 30), 200);
        party.Apply(new PartyNoticeMessage(PartyNotices.Expired, "Ada"));
        party.Invite.ShouldBeNull();
        party.Notice.ShouldBe(UiText.PartyNotice(PartyNotices.Expired, "Ada"));

        party.Apply(new PartyInvitedMessage(42, "Ada", 30), 300);
        party.Apply(new PartyRefusedMessage(MessageTypes.PartyAnswer, PartyReasons.NoInvite));
        party.Invite.ShouldBeNull();
        party.Notice.ShouldBe(UiText.PartyRefused(PartyReasons.NoInvite, null, null));

        party.Apply(new PartyInvitedMessage(42, "Ada", 30), 400);
        party.Tap(9);
        party.Clear();
        (party.Invite, party.Tapped, party.InParty).ShouldBe((null, 0L, false));
    }

    [Fact]
    public void A_tap_on_oneself_is_no_invite_and_a_joined_tapped_player_is_forgotten()
    {
        var party = new PartyBook { OwnId = 42 };
        party.Tap(42);
        party.Tapped.ShouldBe(0);
        party.Tap(43);
        party.Tapped.ShouldBe(43);
        party.Apply(new PartyMessage(1, 42, [new PartyMemberRecord(42, "Ada"), new PartyMemberRecord(43, "Bo")]));
        party.Tapped.ShouldBe(0);
    }

    [Fact]
    public void Every_party_term_has_its_own_words()
    {
        foreach (var notice in PartyNotices.All) UiText.PartyNotice(notice, "Bo").ShouldNotBe(UiText.PartyNotice("something_new", "Bo"), notice);
        foreach (var reason in PartyReasons.All) UiText.PartyRefused(reason, 10, "Bo").ShouldNotBe(UiText.PartyRefused("something_new", 10, "Bo"), reason);
        UiText.PartyRefused(PartyReasons.NotOnline, null, "Bo").ShouldBe(UiText.ChatRefused(ChatReasons.NotOnline, null, "Bo"), "a block reads as not online, in the same words as chat");
        UiText.ChatChannel(ChatChannels.Party).ShouldBe("Party");
        UiText.PartyInvited("Ada", 25).ShouldBe("Ada invites you to a party (25 s)");
    }

    [Fact]
    public void The_chat_can_go_to_the_party_channel()
    {
        var chat = new ChatBook();
        chat.SetChannel(ChatChannels.Party);
        chat.Channel.ShouldBe(ChatChannels.Party);
        chat.SetChannel(ChatChannels.System);
        chat.Channel.ShouldBe(ChatChannels.Party, "nobody sends on system");
    }
}
