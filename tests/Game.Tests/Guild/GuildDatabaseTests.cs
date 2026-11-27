using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Server.Chat;
using Game.Server.Guild;
using Game.Server.Party;
using Game.Server.Persistence;
using Game.Server.World;
using Game.Server.World.Guild;
using Game.Server.World.Saving;
using Game.Shared.Accounts;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Chat;
using Game.Tests.Party;
using Game.Tests.Saving;
using Game.Tests.Server;
using Microsoft.EntityFrameworkCore;

namespace Game.Tests.Guild;

/// <summary>The guild directory over T-902's fake: the same players, blocks and grace periods.</summary>
public sealed class FakeGuildDirectory(FakePartyDirectory party) : IGuildDirectory
{
    public ChatPeer? Playing(EntityId character) => ((IPartyDirectory)party).Playing(character);

    public ChatPeer? Online(string nameKey) => party.Online(nameKey);

    public bool Holds(EntityId character) => party.Holds(character);

    public bool Blocks(ChatPeer listener, ChatPeer speaker) => party.Blocks(listener, speaker);

    public IReadOnlyList<ChatPeer> OnMap(string mapId) => party.OnMap(mapId);
}

/// <summary>
/// T-904 against a real PostgreSQL: founding with its cost in one transaction, names racing, the invite flow, ranks re-checked in the database by two officers on two stores at once, the
/// leader's hand-over and the end of a guild, the notice's text rules, guild chat with the mute, emblems, and what erasing an account and deleting a character do.
/// </summary>
[Trait("Category", "Database")]
public sealed class GuildDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
    private static long _lastItem = 7_000_000;
    private static long _lastEntity = 100;

    private static readonly IReadOnlyList<GuildRankRecord> Ranks =
        [new("Leader", []), new("Officer", [GuildPermissions.Invite, GuildPermissions.Kick, GuildPermissions.EditNotice, GuildPermissions.SetRank]), new("Member", [])];

    private readonly FakePartyDirectory _players = new();
    private readonly FakeTime _time = new() { Now = new DateTimeOffset(Now) };
    private readonly ListLog _log = new();

    private GameDatabase Database => fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set.");

    private PostgresGuildStore Store => new(Database);

    private PostgresAccountStore Accounts => new(Database);

    private static string Tag() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));

    private static string GuildName() => "Oak " + char.ToUpperInvariant(Tag()[0]) + Tag()[1..8];

    private async Task<(Guid Account, Guid Character, string Name)> CharacterAsync(Guid? account = null)
    {
        var owner = account ?? Guid.NewGuid();
        if (account is null) (await Accounts.TryCreateAccountAsync(owner, "g" + Tag(), "hash", Now, default)).ShouldBeTrue();
        var name = "G" + Tag();
        var (_, made) = await Accounts.CreateCharacterAsync(owner, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.wanderer", 3, null, Now, default);
        return (owner, made!.Id, name);
    }

    /// <summary>A guild founded by <paramref name="leader"/> (the founding save's step is done directly), with the others joined at the last rank.</summary>
    private async Task<long> GuildAsync(Guid leader, params Guid[] others)
    {
        var (refused, guild) = await Store.ReserveAsync(leader, GuildName(), Tag(), "icon.emblem.oak", Ranks, [], Now, default);
        refused.ShouldBeNull();
        await FoundAsync(guild, leader);
        foreach (var other in others) (await Store.JoinAsync(guild, leader, other, 30, Now, default)).Refusal.ShouldBeNull();
        return guild;
    }

    private async Task FoundAsync(long guild, Guid founder)
    {
        await using var db = Database.Open();
        await PostgresGuildStore.FoundAsync(db, guild, founder, default);
    }

    private async Task<GuildMember> MemberAsync(long guild, Guid character) => (await Store.LoadAsync(guild, default))!.Members.Single(m => m.Character == character);

    // ---- founding --------------------------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Founding_and_its_cost_land_in_one_transaction_or_not_at_all()
    {
        var (_, founder, _) = await CharacterAsync();
        var characters = new PostgresCharacterStore(Database);
        var run = await characters.StartRunAsync(default);
        var claim = await characters.ClaimAsync(founder, run, default);
        var ticket = new CharacterTicket(founder) { Claim = claim.Claim, TakenVersion = claim.Version, LandedVersion = claim.Version };
        CharacterSave Copy(long currency, params StaffNote[] notes) =>
            new(ticket, ++ticket.TakenVersion, SavedContent.Full(() => Interlocked.Increment(ref _lastItem), Now) with { Currency = currency }, false, [], notes);
        (await characters.SaveAsync([Copy(5_000)], default)).ShouldBeOfType<SaveResult.Saved>();

        var name = GuildName();
        var (refused, guild) = await Store.ReserveAsync(founder, name, CharacterNameRules.Key(name), "icon.emblem.oak", Ranks, [], Now, default);
        refused.ShouldBeNull();
        (await Store.GuildOfAsync(founder, default)).ShouldBeNull("reserved, not founded");
        (await Store.ReserveAsync((await CharacterAsync()).Character, name.ToUpperInvariant(), CharacterNameRules.Key(name), "icon.emblem.oak", Ranks, [], Now, default)).Refusal
            .ShouldBe(GuildReasons.NameTaken, "the name is held while the founding is under way");
        (await Store.ReserveAsync(founder, GuildName(), Tag(), "icon.emblem.oak", Ranks, [guild], Now, default)).Refusal.ShouldBe(GuildReasons.Busy);

        // The save that has the cost taken dies after it wrote everything: neither the cost nor the guild.
        var note = StaffNote.ForGuild(guild);
        note.Version = ticket.TakenVersion + 1;
        characters.OnStep = step => step == "checked" ? throw new IOException("the process dies here (test)") : Task.CompletedTask;
        await Should.ThrowAsync<IOException>(() => characters.SaveAsync([Copy(4_000, note)], default));
        ticket.TakenVersion--;
        characters.OnStep = null;
        (await Store.GuildOfAsync(founder, default)).ShouldBeNull();
        await using (var db = Database.Open()) (await db.CharacterStates.SingleAsync(s => s.CharacterId == founder)).Currency.ShouldBe(5_000);

        (await characters.SaveAsync([Copy(4_000, note)], default)).ShouldBeOfType<SaveResult.Saved>();
        (await Store.GuildOfAsync(founder, default)).ShouldBe(guild);
        await using (var db = Database.Open()) (await db.CharacterStates.SingleAsync(s => s.CharacterId == founder)).Currency.ShouldBe(4_000);
        var founded = (await Store.LoadAsync(guild, default))!;
        (founded.Name, founded.Emblem, founded.Members.Single().Rank).ShouldBe((name, "icon.emblem.oak", 0));
        founded.Ranks.Select(r => r.Name).ShouldBe(["Leader", "Officer", "Member"]);
        (await Store.ReserveAsync(founder, GuildName(), Tag(), "icon.emblem.oak", Ranks, [], Now, default)).Refusal.ShouldBe(GuildReasons.AlreadyInGuild, "one guild per character");
        await characters.EndRunAsync(run, default);
    }

    [DatabaseFact]
    public async Task Of_eight_who_found_the_same_name_at_once_one_gets_it()
    {
        var founders = new List<Guid>();
        for (var i = 0; i < 8; i++) founders.Add((await CharacterAsync()).Character);
        var name = GuildName();
        var results = await Task.WhenAll(founders.Select((f, i) =>
            new PostgresGuildStore(Database).ReserveAsync(f, i % 2 == 0 ? name : name.ToLowerInvariant(), CharacterNameRules.Key(name), "icon.emblem.oak", Ranks, [], Now, default)));
        results.Count(r => r.Refusal is null).ShouldBe(1);
        results.Count(r => r.Refusal == GuildReasons.NameTaken).ShouldBe(7);
    }

    [DatabaseFact]
    public async Task A_reservation_left_by_a_stay_that_ended_is_dropped_and_the_name_is_free_again()
    {
        var (_, founder, _) = await CharacterAsync();
        var name = GuildName();
        var (_, guild) = await Store.ReserveAsync(founder, name, CharacterNameRules.Key(name), "icon.emblem.oak", Ranks, [], Now, default);
        (await Store.DropStaleReservationsAsync(founder, [guild], default)).ShouldBe(0, "one under way is kept");
        (await Store.DropStaleReservationsAsync(founder, [], default)).ShouldBe(1);
        (await Store.ReserveAsync((await CharacterAsync()).Character, name, CharacterNameRules.Key(name), "icon.emblem.oak", Ranks, [], Now, default)).Refusal.ShouldBeNull();
    }

    // ---- members and ranks -----------------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Joining_needs_an_inviter_who_may_still_invite_and_room()
    {
        var (_, leader, _) = await CharacterAsync();
        var (_, member, _) = await CharacterAsync();
        var guild = await GuildAsync(leader, member);
        var (_, newcomer, newcomerName) = await CharacterAsync();
        (await Store.CheckInviteAsync(member, newcomer, 30, default)).Refusal.ShouldBe(GuildReasons.NotAllowed, "a Member may not invite");
        (await Store.JoinAsync(guild, member, newcomer, 30, Now, default)).Refusal.ShouldBe(GuildReasons.NoInvite, "the accept re-checks the inviter");
        (await Store.CheckInviteAsync(leader, member, 30, default)).Refusal.ShouldBe(GuildReasons.AlreadyInGuild);
        (await Store.CheckInviteAsync(leader, newcomer, 2, default)).Refusal.ShouldBe(GuildReasons.Full);
        (await Store.JoinAsync(guild, leader, newcomer, 2, Now, default)).Refusal.ShouldBe(GuildReasons.Full);
        var joined = await Store.JoinAsync(guild, leader, newcomer, 30, Now, default);
        joined.News!.Single().ShouldBe(new GuildNewsMessage(GuildNews.Joined, newcomerName));
        (await MemberAsync(guild, newcomer)).Rank.ShouldBe(2, "new members join at the last rank");
    }

    [DatabaseFact]
    public async Task Nobody_acts_on_the_same_or_a_higher_rank_in_the_database()
    {
        var (_, leader, _) = await CharacterAsync();
        var (_, officer, _) = await CharacterAsync();
        var (_, other, _) = await CharacterAsync();
        var (_, member, _) = await CharacterAsync();
        var guild = await GuildAsync(leader, officer, other, member);
        (await Store.SetRankAsync(leader, (await MemberAsync(guild, officer)).Member, 1, default)).Refusal.ShouldBeNull();
        (await Store.SetRankAsync(leader, (await MemberAsync(guild, other)).Member, 1, default)).Refusal.ShouldBeNull();

        (await Store.KickAsync(officer, (await MemberAsync(guild, other)).Member, default)).Refusal.ShouldBe(GuildReasons.RankTooHigh);
        (await Store.KickAsync(officer, (await MemberAsync(guild, leader)).Member, default)).Refusal.ShouldBe(GuildReasons.RankTooHigh);
        (await Store.SetRankAsync(officer, (await MemberAsync(guild, member)).Member, 1, default)).Refusal.ShouldBe(GuildReasons.RankTooHigh, "not to its own rank");
        (await Store.KickAsync(member, (await MemberAsync(guild, officer)).Member, default)).Refusal.ShouldBe(GuildReasons.NotAllowed);
        (await Store.SetRanksAsync(officer, [new("Member", [])], default)).Refusal.ShouldBe(GuildReasons.NotAllowed, "only the leader names the ranks");
        (await Store.LeadAsync(officer, (await MemberAsync(guild, member)).Member, default)).Refusal.ShouldBe(GuildReasons.NotAllowed);
        (await Store.KickAsync(officer, (await MemberAsync(guild, officer)).Member, default)).Refusal.ShouldBe(GuildReasons.InvalidTarget, "oneself");
        (await Store.KickAsync(officer, -1, default)).Refusal.ShouldBe(GuildReasons.InvalidTarget);

        var kicked = await Store.KickAsync(officer, (await MemberAsync(guild, member)).Member, default);
        (kicked.Refusal, kicked.Removed).ShouldBe((null, (Guid?)member));
        (await Store.GuildOfAsync(member, default)).ShouldBeNull();

        // The leader names three ranks below it, then two: whoever sat on the third goes to the last.
        (await Store.SetRanksAsync(leader, [new("Captain", [GuildPermissions.Kick]), new("Sergeant", []), new("Recruit", [])], default)).Refusal.ShouldBeNull();
        (await Store.SetRankAsync(leader, (await MemberAsync(guild, other)).Member, 3, default)).Refusal.ShouldBeNull();
        (await Store.SetRanksAsync(leader, [new("Captain", [GuildPermissions.Kick]), new("Recruit", [])], default)).Refusal.ShouldBeNull();
        (await MemberAsync(guild, other)).Rank.ShouldBe(2);
        (await Store.LoadAsync(guild, default))!.Ranks.Select(r => r.Name).ShouldBe(["Leader", "Captain", "Recruit"]);
    }

    [DatabaseFact]
    public async Task Two_officers_on_two_stores_at_once_never_break_the_rules()
    {
        // Two GameDatabase objects: two connection pools, as two server processes have.
        await using var first = new GameDatabase(fixture.ConnectionString);
        await using var second = new GameDatabase(fixture.ConnectionString);
        var (one, two) = (new PostgresGuildStore(first), new PostgresGuildStore(second));
        for (var round = 0; round < 6; round++)
        {
            var (_, leader, _) = await CharacterAsync();
            var (_, a, _) = await CharacterAsync();
            var (_, b, _) = await CharacterAsync();
            var (_, m, _) = await CharacterAsync();
            var guild = await GuildAsync(leader, a, b, m);
            foreach (var officer in new[] { a, b }) (await Store.SetRankAsync(leader, (await MemberAsync(guild, officer)).Member, 1, default)).Refusal.ShouldBeNull();
            var target = (await MemberAsync(guild, m)).Member;

            // The leader makes m an officer while officer b kicks m: whichever comes second sees the first's change.
            var results = await Task.WhenAll(one.SetRankAsync(leader, target, 1, default), two.KickAsync(b, target, default));
            var promoted = results[0].Refusal is null;
            var kicked = results[1].Refusal is null;
            (promoted && kicked).ShouldBeFalse("m cannot be both an officer and kicked by an equal");
            (promoted || kicked).ShouldBeTrue("one of them happened");
            if (promoted) results[1].Refusal.ShouldBe(GuildReasons.RankTooHigh);
            else results[0].Refusal.ShouldBe(GuildReasons.InvalidTarget);

            // Both officers kick each other at once: equals, so neither.
            var duel = await Task.WhenAll(one.KickAsync(a, (await MemberAsync(guild, b)).Member, default), two.KickAsync(b, (await MemberAsync(guild, a)).Member, default));
            duel.ShouldAllBe(r => r.Refusal == GuildReasons.RankTooHigh);

            // The leader demotes a while a kicks: after the demotion a may not kick any more.
            var (_, n, _) = await CharacterAsync();
            (await Store.JoinAsync(guild, leader, n, 30, Now, default)).Refusal.ShouldBeNull();
            var nm = (await MemberAsync(guild, n)).Member;
            var race = await Task.WhenAll(one.SetRankAsync(leader, (await MemberAsync(guild, a)).Member, 2, default), two.KickAsync(a, nm, default));
            race[0].Refusal.ShouldBeNull();
            var members = (await Store.LoadAsync(guild, default))!.Members;
            if (race[1].Refusal is null) members.ShouldNotContain(x => x.Character == n);
            else (race[1].Refusal, members.Any(x => x.Character == n)).ShouldBe((GuildReasons.NotAllowed, true), "a Member may not kick");
            members.Count(x => x.Rank == 0).ShouldBe(1, "one leader, always");
        }
    }

    [DatabaseFact]
    public async Task The_leader_hands_over_and_the_last_member_ends_the_guild_its_name_free()
    {
        var (_, leader, leaderName) = await CharacterAsync();
        var (_, early, earlyName) = await CharacterAsync();
        var (_, late, _) = await CharacterAsync();
        var guild = await GuildAsync(leader, early, late);
        var name = (await Store.LoadAsync(guild, default))!.Name;

        var handed = await Store.LeadAsync(leader, (await MemberAsync(guild, early)).Member, default);
        handed.News!.Single().ShouldBe(new GuildNewsMessage(GuildNews.Leader, earlyName));
        ((await MemberAsync(guild, early)).Rank, (await MemberAsync(guild, leader)).Rank).ShouldBe((0, 2), "the old leader takes the new one's old rank");
        (await Store.LeadAsync(leader, (await MemberAsync(guild, late)).Member, default)).Refusal.ShouldBe(GuildReasons.NotAllowed);

        // The leader leaves: the highest rank takes over, the earliest to join among equals (leader before late here).
        var left = await Store.LeaveAsync(early, default);
        left.News!.ShouldBe([new GuildNewsMessage(GuildNews.Left, earlyName), new GuildNewsMessage(GuildNews.Leader, leaderName)]);
        (await MemberAsync(guild, leader)).Rank.ShouldBe(0);
        (await Store.LeaveAsync(late, default)).Disbanded.ShouldBeFalse();
        var last = await Store.LeaveAsync(leader, default);
        (last.Disbanded, last.RemovedNews).ShouldBe((true, new GuildNewsMessage(GuildNews.Disbanded)));
        (await Store.LoadAsync(guild, default)).ShouldBeNull();
        (await Store.LeaveAsync(leader, default)).Refusal.ShouldBe(GuildReasons.NotInGuild);
        (await Store.ReserveAsync(leader, name, CharacterNameRules.Key(name), "icon.emblem.oak", Ranks, [], Now, default)).Refusal.ShouldBeNull("the name is free");
        await using var db = Database.Open();
        (await db.GuildRanks.CountAsync(r => r.GuildId == guild)).ShouldBe(0);
    }

    // ---- erasing (T-703) and deleting --------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Erasing_an_account_takes_its_characters_out_and_a_guild_left_empty_goes()
    {
        var (account, leads, _) = await CharacterAsync();
        var (_, alone, _) = await CharacterAsync(account);
        var (_, stays, _) = await CharacterAsync();
        var shared = await GuildAsync(leads, stays);
        var (_, solo) = await Store.ReserveAsync(alone, GuildName(), Tag(), "icon.emblem.star", Ranks, [], Now, default);
        await FoundAsync(solo, alone);
        var soloName = (await Store.LoadAsync(solo, default))!.Name;
        var export = (await new PostgresModerationStore(Database).ExportAsync(account, Now, default))!["guild_memberships"]!.AsArray();
        export.Count.ShouldBe(2, "the export holds the account's guild memberships");
        export.Select(m => (string?)m!["rank"]).ShouldAllBe(r => r == "Leader");

        (await Accounts.TryRequestErasureAsync(account, "hash", Now, Now, default)).ShouldNotBeNull();
        (await Accounts.EraseAsync(account, Now, default)).ShouldBe(EraseOutcome.Erased);

        var left = (await Store.LoadAsync(shared, default))!;
        left.Members.ShouldHaveSingleItem().ShouldSatisfyAllConditions(m => m.Character.ShouldBe(stays), m => m.Rank.ShouldBe(0, "the leader handed over by the leaving rule"));
        (await Store.LoadAsync(solo, default)).ShouldBeNull("left empty, it is deleted");
        (await Store.ReserveAsync(stays, soloName, CharacterNameRules.Key(soloName), "icon.emblem.oak", Ranks, [], Now, default)).Refusal.ShouldBe(GuildReasons.AlreadyInGuild);
        var (_, fresh, _) = await CharacterAsync();
        (await Store.ReserveAsync(fresh, soloName, CharacterNameRules.Key(soloName), "icon.emblem.oak", Ranks, [], Now, default)).Refusal.ShouldBeNull("its name is free");
    }

    [DatabaseFact]
    public async Task A_deleted_character_leaves_its_guild()
    {
        var (account, gone, goneName) = await CharacterAsync();
        var (_, stays, _) = await CharacterAsync();
        var guild = await GuildAsync(stays, gone);
        (await Accounts.DeleteCharacterAsync(account, gone, Now, default)).ShouldBe(DeleteCharacterOutcome.Deleted);
        (await Store.LoadAsync(guild, default))!.Members.Select(m => m.Character).ShouldBe([stays]);
        (await Store.GuildOfAsync(gone, default)).ShouldBeNull();
        _ = goneName;
    }

    // ---- the service: invites, notice, chat, founding through the tick ------------------------------------------------------------------------

    private ChatPeer Peer(Guid account, Guid character, string name, string map = "map.meadow")
    {
        var peer = new ChatPeer(new EntityId(Interlocked.Increment(ref _lastEntity)), name, account, character, map, new ListSink());
        _players.Playing.Add(peer);
        return peer;
    }

    private static ListSink Sink(ChatPeer peer) => (ListSink)peer.Sink;

    private static T Last<T>(ChatPeer peer) where T : ServerMessage => Sink(peer).Messages.OfType<T>().Last();

    /// <summary>The service over the real store; the tick takes the cost at once and the founding save lands (both done here as the inventory and the save would).</summary>
    private GuildService Service(GuildCostDef? cost = null, Func<GuildFoundingPayment, string?>? tick = null) => new(
        new GuildBalanceDef
        {
            NameMinLength = 3, NameMaxLength = 20, NameSeparators = [" "], MaxMembers = 3, MaxRanks = 4, RankNameMaxLength = 12, InviteTimeoutMs = 30_000, FoundCost = cost,
            DefaultRanks = [new() { Name = "Leader" }, new() { Name = "Officer", Permissions = [GuildPermissions.Invite] }, new() { Name = "Member" }],
            Emblems = ["icon.emblem.oak", "icon.emblem.star"],
        },
        new CharactersBalanceDef { NameMinLength = 3, NameMaxLength = 16, NameLetters = ["a-z", "A-Z"], NameSeparators = ["-", "'"] },
        new GuildOptions { NoticeLength = 40, IntentBurst = 100, IntentsPerMinute = 100, PaymentWaitMs = 2_000 }, new PostgresGuildStore(Database), new FakeGuildDirectory(_players),
        intent =>
        {
            var pay = (GuildFoundingPayment)intent;
            _ = Task.Run(async () =>
            {
                if (!pay.TryStart()) return;
                if (tick?.Invoke(pay) is { } refused)
                {
                    pay.Refuse(refused);
                    return;
                }
                var note = StaffNote.ForGuild(pay.Guild);
                pay.Paid(note);
                await FoundAsync(pay.Guild, FounderOf(pay));
                note.Settle(true);
            });
            return true;
        }, 3, _time, _log);

    private Guid FounderOf(GuildFoundingPayment pay) => _players.Playing.First(p => p.Character == pay.Entity).CharacterId!.Value;

    private async Task<(ChatPeer Peer, Guid Account)> PlayerAsync()
    {
        var (account, character, name) = await CharacterAsync();
        return (Peer(account, character, name), account);
    }

    [DatabaseFact]
    public async Task Founding_through_the_service_takes_the_cost_on_the_tick_and_refuses_what_the_rules_refuse()
    {
        await using var guilds = Service(new GuildCostDef { Currency = 100 }, pay => _players.Playing.First(p => p.Character == pay.Entity).Name.StartsWith("Gpoor", StringComparison.Ordinal) ? GuildReasons.CannotAfford : null);
        var (ada, _) = await PlayerAsync();
        await guilds.HandleAsync(ada, new GuildFoundMessage("Oak 7", "icon.emblem.oak"), default);
        Last<GuildRefusedMessage>(ada).Reason.ShouldBe(GuildReasons.InvalidName);
        await guilds.HandleAsync(ada, new GuildFoundMessage(GuildName(), "icon.emblem.uploaded"), default);
        Last<GuildRefusedMessage>(ada).Reason.ShouldBe(GuildReasons.InvalidEmblem, "only the theme's emblems");

        var name = GuildName();
        await guilds.HandleAsync(ada, new GuildFoundMessage(name, "icon.emblem.star"), default);
        Last<GuildNewsMessage>(ada).ShouldBe(new GuildNewsMessage(GuildNews.Founded, name));
        var state = Last<GuildMessage>(ada);
        (state.Name, state.Emblem, state.Members.Single().Online, state.Members.Single().Rank).ShouldBe((name, "icon.emblem.star", true, 0));
        await guilds.HandleAsync(ada, new GuildFoundMessage(GuildName(), "icon.emblem.star"), default);
        Last<GuildRefusedMessage>(ada).Reason.ShouldBe(GuildReasons.AlreadyInGuild);

        // The tick refuses the cost: nothing is founded and the name is free again.
        var (account, poorCharacter, _) = await CharacterAsync();
        var poor = Peer(account, poorCharacter, "Gpoor" + Tag());
        var dear = GuildName();
        await guilds.HandleAsync(poor, new GuildFoundMessage(dear, "icon.emblem.oak"), default);
        Last<GuildRefusedMessage>(poor).Reason.ShouldBe(GuildReasons.CannotAfford);
        (await Store.GuildOfAsync(poorCharacter, default)).ShouldBeNull();
        (await Store.ReserveAsync((await CharacterAsync()).Character, dear, CharacterNameRules.Key(dear), "icon.emblem.oak", Ranks, [], Now, default)).Refusal.ShouldBeNull();

        // A dev login has no stored character: no guilds.
        var dev = new ChatPeer(new EntityId(Interlocked.Increment(ref _lastEntity)), "Dev", null, null, "map.meadow", new ListSink());
        _players.Playing.Add(dev);
        await guilds.HandleAsync(dev, new GuildFoundMessage(GuildName(), "icon.emblem.oak"), default);
        Last<GuildRefusedMessage>(dev).Reason.ShouldBe(GuildReasons.Unavailable);
    }

    [DatabaseFact]
    public async Task The_invite_flow_with_blocks_expiry_and_room()
    {
        await using var guilds = Service();
        var (ada, _) = await PlayerAsync();
        var (bo, _) = await PlayerAsync();
        var (cy, _) = await PlayerAsync();
        var (dee, _) = await PlayerAsync();
        var title = GuildName();
        await guilds.HandleAsync(ada, new GuildFoundMessage(title, "icon.emblem.oak"), default);

        await guilds.HandleAsync(ada, new GuildInviteMessage("  " + bo.Name.ToLowerInvariant() + " ", null), default);
        Last<GuildInvitedMessage>(bo).ShouldSatisfyAllConditions(i => i.Name.ShouldBe(ada.Name), i => i.Title.ShouldBe(title), i => i.Emblem.ShouldBe("icon.emblem.oak"));
        Last<GuildNewsMessage>(ada).ShouldBe(new GuildNewsMessage(GuildNews.InviteSent, bo.Name));
        await guilds.HandleAsync(ada, new GuildInviteMessage(bo.Name, null), default);
        Last<GuildRefusedMessage>(ada).Reason.ShouldBe(GuildReasons.InvitePending);
        await guilds.HandleAsync(bo, new GuildAnswerMessage(true), default);
        foreach (var member in new[] { ada, bo })
            Last<GuildMessage>(member).Members.Select(m => (m.Name, m.Online)).ShouldBe([(ada.Name, true), (bo.Name, true)]);
        Last<GuildMessage>(bo).Own.ShouldBe(Last<GuildMessage>(bo).Members[1].Member);
        Last<GuildNewsMessage>(ada).ShouldBe(new GuildNewsMessage(GuildNews.Joined, bo.Name));
        await guilds.HandleAsync(bo, new GuildAnswerMessage(true), default);
        Last<GuildRefusedMessage>(bo).Reason.ShouldBe(GuildReasons.NoInvite);
        await guilds.HandleAsync(bo, new GuildInviteMessage(cy.Name, null), default);
        Last<GuildRefusedMessage>(bo).Reason.ShouldBe(GuildReasons.NotAllowed, "a Member may not invite");

        // A block reads as "not online"; so do a stranger's tap on another map and a name nobody has.
        _players.BlockList.Add((cy.Character, ada.Character));
        await guilds.HandleAsync(ada, new GuildInviteMessage(cy.Name, null), default);
        Last<GuildRefusedMessage>(ada).ShouldBe(new GuildRefusedMessage(MessageTypes.GuildInvite, GuildReasons.NotOnline, null, cy.Name));
        Sink(cy).Messages.OfType<GuildInvitedMessage>().ShouldBeEmpty();
        await guilds.HandleAsync(ada, new GuildInviteMessage("Nobodyhere", null), default);
        Last<GuildRefusedMessage>(ada).Reason.ShouldBe(GuildReasons.NotOnline);
        await guilds.HandleAsync(ada, new GuildInviteMessage(null, ada.Character.Value), default);
        Last<GuildRefusedMessage>(ada).Reason.ShouldBe(GuildReasons.InvalidTarget);

        // Expiry: the pulse tells both.
        await guilds.HandleAsync(ada, new GuildInviteMessage(null, dee.Character.Value), default);
        Last<GuildInvitedMessage>(dee).ExpiresInSeconds.ShouldBe(30);
        _time.Now = _time.Now.AddSeconds(31);
        await guilds.PulseAsync();
        Last<GuildNewsMessage>(dee).ShouldBe(new GuildNewsMessage(GuildNews.Expired, ada.Name));
        Last<GuildNewsMessage>(ada).ShouldBe(new GuildNewsMessage(GuildNews.Expired, dee.Name));
        await guilds.HandleAsync(dee, new GuildAnswerMessage(true), default);
        Last<GuildRefusedMessage>(dee).Reason.ShouldBe(GuildReasons.NoInvite);

        // A member who leaves the world shows offline to the others at the next pulse; tags show the emblem of the members on the map.
        _players.Drop(bo);
        await guilds.PulseAsync();
        Last<GuildMessage>(ada).Members.Single(m => m.Name == bo.Name).Online.ShouldBeFalse();
        Last<GuildTagsMessage>(dee).Tags.ShouldBe([new GuildTagRecord(ada.Character.Value, "icon.emblem.oak")]);
    }

    [DatabaseFact]
    public async Task The_notice_follows_the_chat_text_rules_and_the_rank_permission()
    {
        await using var guilds = Service();
        var (ada, _) = await PlayerAsync();
        var (bo, _) = await PlayerAsync();
        await guilds.HandleAsync(ada, new GuildFoundMessage(GuildName(), "icon.emblem.oak"), default);
        await guilds.HandleAsync(ada, new GuildInviteMessage(bo.Name, null), default);
        await guilds.HandleAsync(bo, new GuildAnswerMessage(true), default);

        await guilds.HandleAsync(ada, new GuildNoticeMessage("  Meet​ at   the‮ well  "), default);
        foreach (var member in new[] { ada, bo }) Last<GuildMessage>(member).Notice.ShouldBe("Meet at the well", "format characters out, spaces folded, as chat does");
        Last<GuildNewsMessage>(bo).ShouldBe(new GuildNewsMessage(GuildNews.Notice));
        await guilds.HandleAsync(ada, new GuildNoticeMessage(new string('a', 41)), default);
        Last<GuildRefusedMessage>(ada).Reason.ShouldBe(GuildReasons.TooLong);
        await guilds.HandleAsync(bo, new GuildNoticeMessage("mine now"), default);
        Last<GuildRefusedMessage>(bo).Reason.ShouldBe(GuildReasons.NotAllowed);
        await guilds.HandleAsync(ada, new GuildNoticeMessage("   "), default);
        Last<GuildMessage>(bo).Notice.ShouldBe(string.Empty, "an empty notice clears it");

        // A member signing in again gets the guild with its notice.
        await guilds.HandleAsync(ada, new GuildNoticeMessage("Back soon"), default);
        Sink(bo).Clear();
        guilds.Welcomed(bo.Character);
        await Until(() => Sink(bo).Messages.OfType<GuildMessage>().Any());
        Last<GuildMessage>(bo).Notice.ShouldBe("Back soon");
    }

    [DatabaseFact]
    public async Task Guild_chat_reaches_the_members_here_and_a_muted_member_is_refused_like_on_every_channel()
    {
        await using var guilds = Service();
        var (ada, _) = await PlayerAsync();
        var (bo, boAccount) = await PlayerAsync();
        var (stranger, _) = await PlayerAsync();
        await guilds.HandleAsync(ada, new GuildFoundMessage(GuildName(), "icon.emblem.oak"), default);
        await guilds.HandleAsync(ada, new GuildInviteMessage(bo.Name, null), default);
        await guilds.HandleAsync(bo, new GuildAnswerMessage(true), default);
        var moderation = new FakeModeration();
        var chat = new ChatService(new ChatOptions(), _players, null, new ModerationChecks(moderation, _time).SpeakAsync, _time, _log);
        chat.Register(ChatChannels.Guild, guilds);

        await chat.HandleAsync(ada, new ChatMessage(ChatChannels.Guild, null, "guild night"));
        Sink(bo).Lines.ShouldHaveSingleItem().ShouldSatisfyAllConditions(l => l.Channel.ShouldBe(ChatChannels.Guild), l => l.Text.ShouldBe("guild night"));
        Sink(ada).Lines.ShouldHaveSingleItem();
        Sink(stranger).Lines.ShouldBeEmpty();
        await chat.HandleAsync(stranger, new ChatMessage(ChatChannels.Guild, null, "let me in"));
        Sink(stranger).Refusals.Last().Reason.ShouldBe(ChatReasons.InvalidChannel);

        moderation.Mutes[boAccount] = _time.Now.UtcDateTime.AddMinutes(5);
        await chat.HandleAsync(bo, new ChatMessage(ChatChannels.Guild, null, "muted words"));
        Sink(bo).Refusals.Last().Reason.ShouldBe(ChatReasons.Muted);
        Sink(ada).Lines.Count.ShouldBe(1);

        // Kicked, and the channel no longer has it.
        await guilds.HandleAsync(ada, new GuildKickMessage(Last<GuildMessage>(ada).Members.Single(m => m.Name == bo.Name).Member), default);
        Last<GuildMessage>(bo).Guild.ShouldBeNull();
        Last<GuildNewsMessage>(bo).ShouldBe(new GuildNewsMessage(GuildNews.Kicked, bo.Name));
        guilds.MembersFor(bo).ShouldBeEmpty();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        condition().ShouldBeTrue();
    }
}
