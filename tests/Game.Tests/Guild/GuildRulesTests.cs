using Game.Shared.Definitions;
using Game.Shared.Guild;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Guild;

/// <summary>T-904's pure rules: the rank permissions table, nobody acts on the same or a higher rank, the leader's acts, the rank list, the successor and the naming rules.</summary>
public sealed class GuildRulesTests
{
    // The leader (0), an officer who may invite and kick (1), a clerk who may only write the notice (2), a member who may do nothing (3).
    private static readonly IReadOnlyList<GuildRankRecord> Ranks =
    [
        new("Leader", []), new("Officer", [GuildPermissions.Invite, GuildPermissions.Kick, GuildPermissions.SetRank]), new("Clerk", [GuildPermissions.EditNotice]), new("Member", []),
    ];

    public static TheoryData<int, string, bool> Table => new()
    {
        { 0, GuildPermissions.Invite, true }, { 0, GuildPermissions.Kick, true }, { 0, GuildPermissions.EditNotice, true }, { 0, GuildPermissions.SetRank, true },
        { 1, GuildPermissions.Invite, true }, { 1, GuildPermissions.Kick, true }, { 1, GuildPermissions.EditNotice, false }, { 1, GuildPermissions.SetRank, true },
        { 2, GuildPermissions.Invite, false }, { 2, GuildPermissions.Kick, false }, { 2, GuildPermissions.EditNotice, true }, { 2, GuildPermissions.SetRank, false },
        { 3, GuildPermissions.Invite, false }, { 3, GuildPermissions.Kick, false }, { 3, GuildPermissions.EditNotice, false }, { 3, GuildPermissions.SetRank, false },
        { 4, GuildPermissions.Invite, false }, { -1, GuildPermissions.Invite, false },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void A_rank_holds_what_it_lists_and_the_leader_holds_everything(int rank, string permission, bool holds)
    {
        GuildRules.Has(Ranks, rank, permission).ShouldBe(holds);
        if (permission == GuildPermissions.Invite) (GuildRules.MayInvite(Ranks, rank) is null).ShouldBe(holds);
        if (permission == GuildPermissions.EditNotice) (GuildRules.MayEditNotice(Ranks, rank) is null).ShouldBe(holds);
    }

    [Fact]
    public void Nobody_kicks_a_member_of_the_same_or_a_higher_rank()
    {
        GuildRules.MayKick(Ranks, 1, 3).ShouldBeNull();
        GuildRules.MayKick(Ranks, 1, 2).ShouldBeNull();
        GuildRules.MayKick(Ranks, 1, 1).ShouldBe(GuildReasons.RankTooHigh, "another officer");
        GuildRules.MayKick(Ranks, 1, 0).ShouldBe(GuildReasons.RankTooHigh, "the leader");
        GuildRules.MayKick(Ranks, 0, 1).ShouldBeNull();
        GuildRules.MayKick(Ranks, 2, 3).ShouldBe(GuildReasons.NotAllowed, "a clerk may not kick, whoever it is");
    }

    [Fact]
    public void A_rank_is_given_only_below_ones_own_and_the_leader_rank_only_by_a_hand_over()
    {
        GuildRules.MaySetRank(Ranks, 1, 3, 2).ShouldBeNull();
        GuildRules.MaySetRank(Ranks, 1, 2, 3).ShouldBeNull("down is fine");
        GuildRules.MaySetRank(Ranks, 1, 3, 1).ShouldBe(GuildReasons.RankTooHigh, "not to one's own rank");
        GuildRules.MaySetRank(Ranks, 1, 1, 3).ShouldBe(GuildReasons.RankTooHigh, "not an equal");
        GuildRules.MaySetRank(Ranks, 0, 3, 1).ShouldBeNull("the leader makes officers");
        GuildRules.MaySetRank(Ranks, 0, 3, 0).ShouldBe(GuildReasons.InvalidRanks, "the leader rank moves by guild_lead");
        GuildRules.MaySetRank(Ranks, 0, 3, 4).ShouldBe(GuildReasons.InvalidRanks, "no such rank");
        GuildRules.MaySetRank(Ranks, 2, 3, 3).ShouldBe(GuildReasons.NotAllowed);
    }

    [Fact]
    public void Only_the_leader_names_the_ranks_and_hands_over()
    {
        GuildRules.MayEditRanks(0).ShouldBeNull();
        GuildRules.MayEditRanks(1).ShouldBe(GuildReasons.NotAllowed);
        GuildRules.MayLead(0).ShouldBeNull();
        GuildRules.MayLead(1).ShouldBe(GuildReasons.NotAllowed);
    }

    [Fact]
    public void A_rank_list_needs_one_to_max_minus_one_ranks_with_names_and_known_permissions()
    {
        var def = new GuildBalanceDef { MaxRanks = 3 };
        var leader = new GuildRankRecord("Leader", []);
        string? Clean(string name) => name.Trim().Length is > 0 and <= 8 ? name.Trim() : null;
        GuildRules.CheckRanks(def, leader, [new("Officer", ["invite", "invite"]), new(" Member ", [])], Clean, out var ranks).ShouldBeNull();
        ranks.Select(r => r.Name).ShouldBe(["Leader", "Officer", "Member"]);
        ranks[1].Permissions.ShouldBe(["invite"], "a permission once");
        GuildRules.CheckRanks(def, leader, [], Clean, out _).ShouldBe(GuildReasons.InvalidRanks);
        GuildRules.CheckRanks(def, leader, [new("A", []), new("B", []), new("C", [])], Clean, out _).ShouldBe(GuildReasons.InvalidRanks, "the leader's and three is four");
        GuildRules.CheckRanks(def, leader, [new("  ", [])], Clean, out _).ShouldBe(GuildReasons.InvalidRanks);
        GuildRules.CheckRanks(def, leader, [new("Far too long a name", [])], Clean, out _).ShouldBe(GuildReasons.InvalidRanks);
        GuildRules.CheckRanks(def, leader, [new("Officer", ["ban"])], Clean, out _).ShouldBe(GuildReasons.InvalidRanks, "not a permission term");
        GuildRules.CheckRanks(def, leader, null, Clean, out _).ShouldBe(GuildReasons.InvalidRanks);
    }

    [Fact]
    public void The_successor_is_the_highest_rank_then_the_earliest_to_join()
    {
        GuildRules.Successor([(3, 5), (1, 9), (1, 7), (2, 1)]).ShouldBe(2);
        GuildRules.Successor([(3, 5)]).ShouldBe(0);
        GuildRules.Successor([]).ShouldBe(-1, "nobody to take over: the guild ends");
    }

    [Fact]
    public void Guild_names_follow_character_naming_with_their_own_lengths_and_a_space()
    {
        var characters = new CharactersBalanceDef { NameMinLength = 3, NameMaxLength = 16, NameLetters = ["a-z", "A-Z"], NameSeparators = ["-", "'"] };
        var rules = GuildRules.NameRules(characters, new GuildBalanceDef { NameMinLength = 3, NameMaxLength = 20, NameSeparators = [" "] });
        rules.Normalize("  Iron Oaks ").ShouldBe("Iron Oaks");
        rules.Normalize("O'Dell Riders").ShouldBe("O'Dell Riders");
        rules.Normalize("Iron  Oaks").ShouldBeNull("two separators in a row");
        rules.Normalize("Oaks 7").ShouldBeNull("letters of character names only");
        rules.Normalize("Ox").ShouldBeNull();
        rules.Normalize("Twenty One Letters Ab").ShouldBeNull();
        rules.Normalize("Ｉｒｏｎ Ｏａｋｓ").ShouldBe("Iron Oaks", "NFKC first, as for characters");
        Game.Shared.Accounts.CharacterNameRules.Key("Iron Oaks").ShouldBe(Game.Shared.Accounts.CharacterNameRules.Key("iron oaks"), "letter case does not make a new name");
    }
}
