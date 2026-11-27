using Game.Client.Core;
using Game.Client.Core.Guild;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Client;

/// <summary>T-904 on the client: words for every term, what the book hides by rank (the server decides), the notice shown once, invites and tags.</summary>
public sealed class GuildClientTests
{
    private static readonly IReadOnlyList<GuildRankRecord> Ranks = [new("Leader", []), new("Officer", [GuildPermissions.Kick, GuildPermissions.SetRank]), new("Member", [])];

    private static GuildMessage State(long own, string notice = "") => new(3, "Iron Oaks", "icon.emblem.oak", notice, own, Ranks,
        [new GuildMemberRecord(1, "Ada", 0, 9, true), new GuildMemberRecord(2, "Bo", 1, 5, true), new GuildMemberRecord(3, "Cy", 2, 2, false), new GuildMemberRecord(4, "Dee", 1, 4, false)]);

    [Fact]
    public void Every_news_and_reason_term_has_its_own_words()
    {
        foreach (var news in GuildNews.All) UiText.GuildNews(news, "Bo").ShouldNotBe(UiText.GuildNews("something_new", "Bo"), news);
        foreach (var reason in GuildReasons.All.Where(r => r != GuildReasons.Unavailable))
            UiText.GuildRefused(reason, 10, "Bo").ShouldNotBe(UiText.GuildRefused("something_new", 10, "Bo"), reason);
        UiText.ChatChannel(ChatChannels.Guild).ShouldBe("Guild");
    }

    [Fact]
    public void The_book_hides_only_what_the_rank_cannot_do()
    {
        var book = new GuildBook();
        book.Apply(State(own: 2));
        (book.InGuild, book.OwnRank, book.Leading).ShouldBe((true, 1, false));
        book.May(GuildPermissions.Kick).ShouldBeTrue();
        book.May(GuildPermissions.Invite).ShouldBeFalse();
        book.MayActOn(book.State.Members[2], GuildPermissions.Kick).ShouldBeTrue("a Member, below");
        book.MayActOn(book.State.Members[3], GuildPermissions.Kick).ShouldBeFalse("another officer");
        book.MayActOn(book.State.Members[0], GuildPermissions.Kick).ShouldBeFalse("the leader");
        book.MayActOn(book.State.Members[1], GuildPermissions.Kick).ShouldBeFalse("oneself");
        book.Apply(State(own: 1));
        book.Leading.ShouldBeTrue();
        book.May(GuildPermissions.Invite).ShouldBeTrue("the leader holds every permission");
        book.Apply(GuildMessage.None);
        (book.InGuild, book.May(GuildPermissions.Kick)).ShouldBe((false, false));
    }

    [Fact]
    public void The_notice_shows_once_at_sign_in_and_when_it_changes()
    {
        var book = new GuildBook();
        book.Apply(State(1, "Meet at the well")).ShouldBe(UiText.GuildNoticeShown("Iron Oaks", "Meet at the well"));
        book.Apply(State(1, "Meet at the well")).ShouldBeNull("the same notice again (a member came online)");
        book.Apply(State(1, "Raid at noon")).ShouldNotBeNull();
        book.Apply(State(1)).ShouldBeNull();
        book.Clear();
        book.Apply(State(1, "Raid at noon")).ShouldNotBeNull("a new sign-in shows it again");
    }

    [Fact]
    public void Invites_expire_and_tags_name_the_emblem_above_a_player()
    {
        var book = new GuildBook();
        book.Apply(new GuildInvitedMessage("Ada", "Iron Oaks", "icon.emblem.oak", 30), 100);
        book.Invite!.Title.ShouldBe("Iron Oaks");
        book.Expire(129);
        book.Invite.ShouldNotBeNull();
        book.Expire(130);
        book.Invite.ShouldBeNull();
        book.Apply(new GuildInvitedMessage("Ada", "Iron Oaks", "icon.emblem.oak", 30), 200);
        book.Apply(new GuildRefusedMessage(MessageTypes.GuildAnswer, GuildReasons.NoInvite)).ShouldBe(UiText.GuildRefused(GuildReasons.NoInvite, null, null));
        book.Invite.ShouldBeNull();

        var version = book.TagsVersion;
        book.Apply(new GuildTagsMessage([new GuildTagRecord(42, "icon.emblem.star")]));
        (book.TagOf(42), book.TagOf(43), book.TagsVersion > version).ShouldBe(("icon.emblem.star", (string?)null, true));
        book.Clear();
        book.TagOf(42).ShouldBeNull();
    }
}
