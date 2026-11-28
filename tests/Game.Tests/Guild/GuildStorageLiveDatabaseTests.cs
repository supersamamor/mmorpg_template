using Game.Client.Core;
using Game.Client.Core.Guild;
using Game.Server.Chat;
using Game.Server.Guild;
using Game.Server.Net;
using Game.Server.Persistence;
using Game.Server.World;
using Game.Server.World.Guild;
using Game.Server.World.Saving;
using Game.Shared.Accounts;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Chat;
using Game.Tests.Party;
using Game.Tests.Saving;
using Game.Tests.Server;

namespace Game.Tests.Guild;

/// <summary>
/// T-810 against a real PostgreSQL, through the service as the gateway calls it: members with the storage open get another's moves at the next flush (once, whatever the number of moves),
/// not once their lease ran out, the log only with <c>storage_log</c> as the database has it then, nothing after they left; and the rank editor's message is accepted or refused in words.
/// Flushes are run by the test: the service is not started.
/// </summary>
[Trait("Category", "Database")]
public sealed class GuildStorageLiveDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Draught = "item.healing_draught";
    private static readonly DateTime Now = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
    private static long _lastItem = 11_000_000_000;
    private static long _lastEntity = 5_000;

    private static readonly IReadOnlyList<GuildRankRecord> Ranks =
    [
        new("Leader", []),
        new("Officer", [GuildPermissions.StorageDeposit, GuildPermissions.StorageItems, GuildPermissions.StorageCurrency, GuildPermissions.StorageLog]),
        new("Member", [GuildPermissions.StorageDeposit]),
    ];

    private readonly FakePartyDirectory _players = new();
    private readonly FakeTime _time = new() { Now = new DateTimeOffset(Now) };
    private readonly ListLog _log = new();
    private string? _tickRefuses;

    private GameDatabase Database => fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set.");

    private PostgresGuildStore Store => new(Database);

    private PostgresAccountStore Accounts => new(Database);

    private static string Tag() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));

    private async Task<ChatPeer> PlayerAsync()
    {
        var account = Guid.NewGuid();
        (await Accounts.TryCreateAccountAsync(account, "w" + Tag(), "hash", Now, default)).ShouldBeTrue();
        var name = "W" + Tag();
        var (_, made) = await Accounts.CreateCharacterAsync(account, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.wanderer", 3, null, Now, default);
        var peer = new ChatPeer(new EntityId(Interlocked.Increment(ref _lastEntity)), name, account, made!.Id, "map.meadow", new ListSink());
        _players.Playing.Add(peer);
        return peer;
    }

    /// <summary>A founded guild: the first leads, then <paramref name="officer"/> at rank 1 and the others at rank 2.</summary>
    private async Task<long> GuildAsync(ChatPeer leader, ChatPeer? officer, params ChatPeer[] members)
    {
        var lead = leader.CharacterId!.Value;
        var (refused, guild) = await Store.ReserveAsync(lead, "Live " + char.ToUpperInvariant(Tag()[0]) + Tag()[1..8], Tag(), "icon.emblem.oak", Ranks, [], Now, default);
        refused.ShouldBeNull();
        await using (var db = Database.Open()) await PostgresGuildStore.FoundAsync(db, guild, lead, default);
        foreach (var other in members.Concat(officer is null ? [] : [officer]))
            (await Store.JoinAsync(guild, lead, other.CharacterId!.Value, 30, Now, default)).Refusal.ShouldBeNull();
        if (officer is not null)
        {
            var number = (await Store.LoadAsync(guild, default))!.Members.Single(m => m.Character == officer.CharacterId).Member;
            (await Store.SetRankAsync(lead, number, 1, default)).Refusal.ShouldBeNull();
        }
        return guild;
    }

    /// <summary>The service over the real stores. The tick moves the bag's side at once, and the save that has it lands the storage's side (or the tick refuses with <see cref="_tickRefuses"/>).</summary>
    private GuildService Service(int intentBurst = 100)
    {
        var guilds = new GuildService(
            new GuildBalanceDef
            {
                NameMinLength = 3, NameMaxLength = 20, NameSeparators = [" "], MaxMembers = 10, MaxRanks = 4, RankNameMaxLength = 12, InviteTimeoutMs = 30_000,
                DefaultRanks = [new() { Name = "Leader" }, new() { Name = "Member" }], Emblems = ["icon.emblem.oak"],
            },
            new CharactersBalanceDef { NameMinLength = 3, NameMaxLength = 16, NameLetters = ["a-z", "A-Z"], NameSeparators = ["-", "'"] },
            new GuildOptions { IntentBurst = intentBurst, IntentsPerMinute = intentBurst }, Store, new FakeGuildDirectory(_players), _ => false, 3, _time, _log);
        guilds.Storage = new GuildStorage(new GuildStorageDef { MaxEntries = 10, MaxCurrency = 1_000, LogDays = 30, LogShown = 20 }, new PostgresGuildStorageStore(Database),
            () => Interlocked.Increment(ref _lastItem), intent =>
            {
                var transfer = (GuildStorageTransfer)intent;
                _ = Task.Run(async () =>
                {
                    if (!transfer.TryStart()) return;
                    if (_tickRefuses is { } refused)
                    {
                        transfer.Refuse(refused);
                        return;
                    }
                    var entry = transfer.Entry ?? (transfer.EntryId is { } id ? new InventoryEntry(id, Draught, transfer.Quantity) : null);
                    var note = StaffNote.ForStorage(transfer.ToMove(entry));
                    transfer.Moved(note);
                    var character = _players.Playing.First(p => p.Character == transfer.Entity).CharacterId!.Value;
                    await using var db = Database.Open();
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    await PostgresGuildStorageStore.LandAsync(db, note.Storage!, character, Now, default);
                    await transaction.CommitAsync();
                    note.Settle(true);
                });
                return true;
            }, 5_000, _time, _log);
        return guilds;
    }

    private static ListSink Sink(ChatPeer peer) => (ListSink)peer.Sink;

    private static IReadOnlyList<GuildStorageMessage> Storages(ChatPeer peer) => Sink(peer).Messages.OfType<GuildStorageMessage>().ToList();

    private static void Forget(params ChatPeer[] peers)
    {
        foreach (var peer in peers) Sink(peer).Clear();
    }

    [DatabaseFact]
    public async Task A_deposit_reaches_those_with_the_storage_open_once_with_the_log_only_for_its_permission_and_not_one_who_closed_it()
    {
        var (ada, officer, watcher, closed, never) = (await PlayerAsync(), await PlayerAsync(), await PlayerAsync(), await PlayerAsync(), await PlayerAsync());
        await GuildAsync(ada, officer, watcher, closed, never);
        await using var guilds = Service();
        // "never" is known to the service (it did something) but never opened the storage.
        await guilds.HandleAsync(never, new GuildNoticeMessage("mine"), default);
        await guilds.HandleAsync(closed, new GuildStorageOpenMessage(), default);
        _time.Now = _time.Now.AddSeconds(10);
        foreach (var member in new[] { officer, watcher }) await guilds.HandleAsync(member, new GuildStorageOpenMessage(), default);
        Storages(watcher).ShouldHaveSingleItem().Currency.ShouldBe(0, "opening answers at once");
        _time.Now = _time.Now.AddSeconds(6);   // "closed" stopped renewing 16 s ago: its lease (15 s) ran out; the others' runs
        Forget(ada, officer, watcher, closed, never);
        await guilds.FlushStorageAsync();
        new[] { officer, watcher, closed }.ShouldAllBe(p => Storages(p).Count == 0, "nothing changed, nothing sent");

        // Two moves before one flush: one message each, with both.
        await guilds.HandleAsync(ada, new GuildDepositMessage(null, 0, 40), default);
        await guilds.HandleAsync(ada, new GuildDepositMessage(Interlocked.Increment(ref _lastItem), 2, 0), default);
        Sink(ada).Messages.OfType<GuildRefusedMessage>().ShouldBeEmpty();
        Storages(watcher).ShouldBeEmpty("only the flush, once a tick, sends changes");
        await guilds.FlushStorageAsync();

        var seen = Storages(watcher).ShouldHaveSingleItem();
        (seen.Currency, seen.Entries.ShouldHaveSingleItem().Quantity).ShouldBe((40L, 2));
        seen.Log.ShouldBeNull("a Member's rank has no storage_log");
        var told = Storages(officer).ShouldHaveSingleItem();
        told.Log!.Count.ShouldBe(2);
        Storages(ada).ShouldHaveSingleItem().Log!.Count.ShouldBe(2, "the mover is answered once, though it never opened the window");
        Storages(closed).ShouldBeEmpty("it closed the window and its lease ran out");
        Storages(never).ShouldBeEmpty();
        await guilds.FlushStorageAsync();
        Storages(watcher).Count.ShouldBe(1);

        // A renewal keeps the lease running past the first one's end; the permission is the database's at the time of the push.
        _time.Now = _time.Now.AddSeconds(8);
        await guilds.HandleAsync(watcher, new GuildStorageOpenMessage(), default);
        _time.Now = _time.Now.AddSeconds(8);
        await guilds.PulseAsync();
        (await Store.SetRanksAsync(ada.CharacterId!.Value, [Ranks[1], new GuildRankRecord("Member", [GuildPermissions.StorageDeposit, GuildPermissions.StorageLog])], default))
            .Refusal.ShouldBeNull();
        Forget(ada, officer, watcher);
        await guilds.HandleAsync(ada, new GuildWithdrawMessage(null, 0, 15), default);
        await guilds.FlushStorageAsync();
        var after = Storages(watcher).ShouldHaveSingleItem();
        (after.Currency, after.Log!.Count).ShouldBe((25L, 3), "its rank reads the log now");
        Storages(officer).ShouldBeEmpty("it did not renew for 16 s");
    }

    [DatabaseFact]
    public async Task An_undone_withdrawal_is_shown_again_and_whoever_left_or_is_in_another_guild_gets_nothing()
    {
        var (ada, watcher, kicked) = (await PlayerAsync(), await PlayerAsync(), await PlayerAsync());
        var guild = await GuildAsync(ada, null, watcher, kicked);
        var other = await PlayerAsync();
        await GuildAsync(other, null);
        await using var guilds = Service();
        await guilds.HandleAsync(ada, new GuildDepositMessage(null, 0, 40), default);
        await guilds.FlushStorageAsync();
        foreach (var member in new[] { watcher, kicked, other }) await guilds.HandleAsync(member, new GuildStorageOpenMessage(), default);
        var number = (await Store.LoadAsync(guild, default))!.Members.Single(m => m.Character == kicked.CharacterId).Member;
        await guilds.HandleAsync(ada, new GuildKickMessage(number), default);
        Forget(ada, watcher, kicked, other);

        // The tick refuses the bag's side: the hold had taken the coins, so those watching are sent them back.
        _tickRefuses = GuildReasons.CurrencyFull;
        await guilds.HandleAsync(ada, new GuildWithdrawMessage(null, 0, 40), default);
        Sink(ada).Messages.OfType<GuildRefusedMessage>().ShouldHaveSingleItem().Reason.ShouldBe(GuildReasons.CurrencyFull);
        await guilds.FlushStorageAsync();
        Storages(watcher).ShouldHaveSingleItem().Currency.ShouldBe(40);
        Storages(kicked).ShouldBeEmpty("no longer in the guild");
        Storages(other).ShouldBeEmpty("another guild's storage did not change");
        Storages(ada).ShouldBeEmpty("a refused move is answered by its refusal");

        // A member who left the world is sent nothing.
        _tickRefuses = null;
        _players.Gone(watcher);
        Forget(watcher);
        await guilds.HandleAsync(ada, new GuildDepositMessage(null, 0, 5), default);
        await guilds.FlushStorageAsync();
        Storages(watcher).ShouldBeEmpty();
    }

    [DatabaseFact]
    public async Task The_started_service_flushes_by_itself_and_opening_has_a_rate_bucket_of_its_own()
    {
        var (ada, watcher) = (await PlayerAsync(), await PlayerAsync());
        await GuildAsync(ada, null, watcher);
        await using var guilds = Service(intentBurst: 2);
        guilds.Start();
        await guilds.HandleAsync(watcher, new GuildStorageOpenMessage(), default);
        await guilds.HandleAsync(watcher, new GuildStorageOpenMessage(), default);
        await guilds.HandleAsync(watcher, new GuildStorageOpenMessage(), default);
        Sink(watcher).Messages.OfType<GuildRefusedMessage>().ShouldHaveSingleItem().Reason.ShouldBe(GuildReasons.RateLimited, "opening is limited too");
        Forget(watcher);
        await guilds.HandleAsync(watcher, new GuildDepositMessage(null, 0, 3), default);
        await guilds.HandleAsync(ada, new GuildDepositMessage(null, 0, 4), default);
        Sink(watcher).Messages.OfType<GuildRefusedMessage>().ShouldBeEmpty("renewals do not spend what the other guild messages may");
        for (var i = 0; i < 100 && Storages(watcher).LastOrDefault()?.Currency != 7; i++) await Task.Delay(50);
        Storages(watcher).Last().Currency.ShouldBe(7);
    }

    [DatabaseFact]
    public async Task The_rank_editors_message_is_taken_by_the_server_and_its_refusal_is_shown_in_words()
    {
        var (ada, bo) = (await PlayerAsync(), await PlayerAsync());
        var guild = await GuildAsync(ada, null, bo);
        await using var guilds = Service();
        await guilds.HandleAsync(bo, new GuildStorageOpenMessage(), default);
        await guilds.HandleAsync(ada, new GuildNoticeMessage("hello"), default);
        var book = new GuildBook();
        book.Apply(Sink(ada).Messages.OfType<GuildMessage>().Last());

        // As the client sends it: through the wire format.
        async Task SendAsync(RankDraft draft)
        {
            Forget(ada);
            book.AwaitRanks();
            var message = ProtocolJson.ReadClient(ProtocolJson.ToBytes(new GuildRanksMessage(draft.ToRecords()))).Message!;
            await guilds.HandleAsync(ada, message, default);
            foreach (var answer in Sink(ada).Messages)
                if (answer is GuildMessage state) book.Apply(state);
                else if (answer is GuildNewsMessage news) book.Apply(news);
                else if (answer is GuildRefusedMessage refused) book.Apply(refused);
        }

        var draft = RankDraft.From(book.State);
        draft.Ranks[0].Name = new string('x', 13);   // guild.rank_name_max_length is 12 here
        await SendAsync(draft);
        book.RankAnswer.ShouldBe(new GuildRankAnswer(false, UiText.GuildRefused(GuildReasons.InvalidRanks, null, null)));
        draft.Ranks[0].Name = "  ";
        await SendAsync(draft);
        book.RankAnswer!.Saved.ShouldBeFalse();
        draft.Ranks[0].Name = "Fine";
        draft.Add("Three");
        draft.Add("Four");
        await SendAsync(draft);
        book.RankAnswer!.Saved.ShouldBeFalse("more ranks than guild.max_ranks");
        (await Store.LoadAsync(guild, default))!.Ranks.Select(r => r.Name).ShouldBe(["Leader", "Officer", "Member"]);

        draft = RankDraft.From(book.State);
        draft.Ranks[1].Name = "  Quarter   master ";
        draft.Ranks[1].Set(GuildPermissions.StorageDeposit, true);
        draft.Ranks[1].Set(GuildPermissions.StorageCurrency, true);
        draft.Remove(0);
        await SendAsync(draft);
        book.RankAnswer.ShouldBe(new GuildRankAnswer(false, UiText.GuildRefused(GuildReasons.InvalidRanks, null, null)), "13 characters once cleaned");
        draft.Ranks[0].Name = "  Quarter   man ";
        await SendAsync(draft);
        book.RankAnswer.ShouldBe(new GuildRankAnswer(true, UiText.GuildRanksSaved));
        var stored = (await Store.LoadAsync(guild, default))!;
        stored.Ranks.Select(r => r.Name).ShouldBe(["Leader", "Quarter man"]);
        stored.Ranks[1].Permissions.ShouldBe([GuildPermissions.StorageDeposit, GuildPermissions.StorageCurrency]);
        stored.Members.Single(m => m.Character == bo.CharacterId).Rank.ShouldBe(1, "past the end: the last rank");
        RankDraft.From(book.State).Ranks.ShouldHaveSingleItem().Name.ShouldBe("Quarter man", "the editor reads the names back as the server cleaned them");
    }
}
