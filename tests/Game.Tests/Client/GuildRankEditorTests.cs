using Game.Client.Core;
using Game.Client.Core.Guild;
using Game.Server.Net;
using Game.Shared.Definitions;
using Game.Shared.Guild;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Client;

/// <summary>
/// T-810 on the client: the rank editor's draft and the message it sends (what the server's parser and rules accept), who a row would hold, the server's answer in words, and when an open
/// storage window renews. The same message against the real service and database is in <c>GuildStorageLiveDatabaseTests</c>.
/// </summary>
public sealed class GuildRankEditorTests
{
    private static readonly IReadOnlyList<GuildRankRecord> Ranks =
        [new("Leader", []), new("Officer", [GuildPermissions.Invite, GuildPermissions.Kick]), new("Member", [GuildPermissions.StorageDeposit])];

    private static GuildMessage State(long own = 1) => new(3, "Iron Oaks", "icon.emblem.oak", "", own, Ranks,
        [new GuildMemberRecord(1, "Ada", 0, 9, true), new GuildMemberRecord(2, "Bo", 1, 5, true), new GuildMemberRecord(3, "Cy", 2, 2, false), new GuildMemberRecord(4, "Dee", 2, 4, false)]);

    private static (string Name, string Permissions)[] Rows(IEnumerable<GuildRankRecord> ranks) => ranks.Select(r => (r.Name, string.Join(",", r.Permissions))).ToArray();

    [Fact]
    public void The_draft_renames_adds_moves_ticks_and_removes_all_but_the_last_rank()
    {
        var draft = RankDraft.From(State());
        Rows(draft.ToRecords()).ShouldBe([("Officer", "invite,kick"), ("Member", "storage_deposit")], "the ranks below the leader's");

        draft.Ranks[1].Name = "Recruit";
        draft.Add("New rank");
        draft.Ranks[2].Set(GuildPermissions.StorageLog, true);
        draft.Ranks[2].Set(GuildPermissions.Invite, true);
        draft.Ranks[0].Set(GuildPermissions.Kick, false);
        draft.Move(2, -1);
        Rows(draft.ToRecords()).ShouldBe([("Officer", "invite"), ("New rank", "invite,storage_log"), ("Recruit", "storage_deposit")], "permissions in the vocabulary's order");
        draft.Move(0, -1);
        draft.Move(2, 1);
        draft.Remove(7);
        draft.Ranks.Count.ShouldBe(3, "out of range changes nothing");

        draft.Remove(1);
        draft.Remove(0);
        draft.CanRemove.ShouldBeFalse();
        draft.Remove(0);
        draft.Ranks.ShouldHaveSingleItem().Name.ShouldBe("Recruit");
        State().Ranks[2].Name.ShouldBe("Member", "the guild as sent is not touched");
    }

    [Fact]
    public void A_rows_member_count_is_who_the_server_would_put_at_its_place()
    {
        var draft = RankDraft.From(State());
        (draft.MembersAt(State(), 0), draft.MembersAt(State(), 1)).ShouldBe((1, 2));
        draft.Move(0, 1);
        (draft.MembersAt(State(), 0), draft.MembersAt(State(), 1)).ShouldBe((1, 2), "members stay at their place, whatever rank moves there");
        draft.Add("Third");
        draft.MembersAt(State(), 2).ShouldBe(0);
        draft.Remove(2);
        draft.Remove(1);
        draft.MembersAt(State(), 0).ShouldBe(3, "those past the end go to the last rank");
    }

    [Fact]
    public void What_the_editor_sends_is_what_the_server_reads_and_its_rules_accept()
    {
        var draft = RankDraft.From(State());
        draft.Ranks[1].Name = "Recruit";
        foreach (var permission in GuildPermissions.All) draft.Ranks[0].Set(permission, true);
        draft.Add(UiText.GuildRankNew);

        var read = ProtocolJson.ReadClient(ProtocolJson.ToBytes(new GuildRanksMessage(draft.ToRecords()))).Message.ShouldBeOfType<GuildRanksMessage>();
        Rows(read.Ranks).ShouldBe(Rows(draft.ToRecords()));
        var balance = new GuildBalanceDef { MaxRanks = 6 };
        GuildRules.CheckRanks(balance, new GuildRankRecord("Leader", []), read.Ranks, n => n.Trim() is { Length: > 0 and <= 16 } t ? t : null, out var accepted).ShouldBeNull();
        accepted.Count.ShouldBe(4);
    }

    [Fact]
    public void The_servers_answer_to_Save_comes_back_in_words()
    {
        var book = new GuildBook();
        book.Apply(State());
        book.Apply(new GuildNewsMessage(GuildNews.Ranks));
        book.RankAnswer.ShouldBeNull("no Save waits");

        book.AwaitRanks();
        book.Apply(new GuildRefusedMessage(MessageTypes.GuildNotice, GuildReasons.TooLong));
        book.RankAnswer.ShouldBeNull("another intent's refusal");
        book.Apply(new GuildRefusedMessage(MessageTypes.GuildRanks, GuildReasons.InvalidRanks));
        book.RankAnswer.ShouldBe(new GuildRankAnswer(false, UiText.GuildRefused(GuildReasons.InvalidRanks, null, null)));

        book.AwaitRanks();
        book.RankAnswer.ShouldBeNull();
        book.Apply(new GuildNewsMessage(GuildNews.Ranks));
        book.RankAnswer.ShouldBe(new GuildRankAnswer(true, UiText.GuildRanksSaved));
        book.Clear();
        book.RankAnswer.ShouldBeNull();
    }

    [Fact]
    public void Every_permission_has_its_own_box_words()
    {
        var words = GuildPermissions.All.Select(UiText.GuildPermission).ToList();
        words.Distinct().Count().ShouldBe(words.Count);
        foreach (var permission in GuildPermissions.All) UiText.GuildPermission(permission).ShouldNotBe(permission);
    }

    [Fact]
    public void An_open_storage_window_renews_until_it_closes()
    {
        var book = new GuildBook();
        book.Apply(State());
        book.StorageRenewalDue(100).ShouldBeFalse("not open");
        book.WatchStorage(true);
        book.StorageRenewalDue(100).ShouldBeFalse("it was just opened");
        book.StorageRenewalDue(104.9).ShouldBeFalse();
        book.StorageRenewalDue(105).ShouldBeTrue();
        book.StorageRenewalDue(105.1).ShouldBeFalse();
        book.StorageRenewalDue(110.2).ShouldBeTrue();
        book.WatchStorage(false);
        book.StorageRenewalDue(200).ShouldBeFalse("closed: the server's lease runs out");

        book.WatchStorage(true);
        book.Apply(GuildMessage.None);
        book.StorageRenewalDue(300).ShouldBeFalse();
        book.Apply(State());
        book.StorageRenewalDue(400).ShouldBeFalse("out of the guild closed it");

        new Game.Server.Guild.GuildOptions().StorageLeaseMs.ShouldBeGreaterThanOrEqualTo((int)(GuildBook.StorageRenewSeconds * 2000), "a lost renewal does not end the lease");
        new Game.Server.Guild.GuildOptions { StorageLeaseMs = 5_000 }.Problems(4096).ShouldContain(p => p.StartsWith("storage-lease-ms", StringComparison.Ordinal));
    }

    [Fact]
    public void An_open_storage_window_stays_within_the_network_budget()
    {
        // The largest guild_storage the placeholder rules allow (50 entries with four cards each and 50 log lines, long ids and names), once per renewal: ARCHITECTURE §9, 10 KB/s.
        var item = "item." + new string('w', 24);
        var entries = Enumerable.Range(0, 50).Select(i => new InventoryEntryRecord(long.MaxValue - i, item, 99, null, [item, item, item, item], 10)).ToList();
        var log = Enumerable.Repeat(new GuildStorageLogRecord(1_900_000_000, new string('W', 16), GuildStorageMoves.Withdraw, item, long.MaxValue, 99, 0), 50).ToList();
        var largest = ProtocolJson.ToBytes(new GuildStorageMessage(50, 1_000_000_000, entries, log)).Length;
        (largest / GuildBook.StorageRenewSeconds).ShouldBeLessThan(10 * 1024 / 2, $"{largest} bytes every {GuildBook.StorageRenewSeconds} s: under half the budget");
    }

    [Fact]
    public void A_storage_that_arrives_moves_only_the_storage_version()
    {
        var book = new GuildBook();
        book.Apply(State());
        var (version, storage) = (book.Version, book.StorageVersion);
        book.Apply(new GuildStorageMessage(50, 7, [], null));
        (book.Version, book.StorageVersion > storage, book.Storage!.Currency).ShouldBe((version, true, 7L), "the guild panel is not redrawn over a notice being typed");
    }
}
