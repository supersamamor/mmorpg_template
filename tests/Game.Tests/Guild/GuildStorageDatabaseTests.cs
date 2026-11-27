using Game.Server.Accounts;
using Game.Server.Guild;
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
using Game.Tests.Saving;
using Game.Tests.Server;
using Microsoft.EntityFrameworkCore;

namespace Game.Tests.Guild;

/// <summary>
/// T-905 against a real PostgreSQL: a deposit and a withdrawal land with the character's save or not at all (a crash on either side, the next claim undoing a hold), two withdrawals of the
/// last unit on two stores, a rank change racing a withdrawal, permissions, room and the log, a disconnect before the save, the last member who cannot leave a storage behind, erasure, and
/// random sequences counting every item id and coin across characters and the storage.
/// </summary>
[Trait("Category", "Database")]
public sealed class GuildStorageDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Draught = "item.healing_draught";
    private static readonly DateTime Now = new(2026, 11, 28, 9, 0, 0, DateTimeKind.Utc);
    private static long _lastItem = 9_000_000_000;
    private static long _lastEntity = 900;

    private static readonly string[] All = [GuildPermissions.StorageDeposit, GuildPermissions.StorageItems, GuildPermissions.StorageCurrency, GuildPermissions.StorageLog];

    private static readonly IReadOnlyList<GuildRankRecord> Ranks =
        [new("Leader", []), new("Officer", [GuildPermissions.SetRank, .. All]), new("Member", [GuildPermissions.StorageDeposit])];

    private readonly FakeTime _time = new() { Now = new DateTimeOffset(Now) };
    private readonly ListLog _log = new();

    private GameDatabase Database => fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set.");

    private PostgresGuildStore Guilds => new(Database);

    private PostgresGuildStorageStore Store => new(Database);

    private PostgresAccountStore Accounts => new(Database);

    private static long NewId() => Interlocked.Increment(ref _lastItem);

    private static string Tag() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));

    private async Task<(Guid Account, Guid Character, string Name)> CharacterAsync()
    {
        var owner = Guid.NewGuid();
        (await Accounts.TryCreateAccountAsync(owner, "s" + Tag(), "hash", Now, default)).ShouldBeTrue();
        var name = "S" + Tag();
        var (_, made) = await Accounts.CreateCharacterAsync(owner, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.wanderer", 3, null, Now, default);
        return (owner, made!.Id, name);
    }

    /// <summary>A founded guild led by <paramref name="leader"/>; <paramref name="officers"/> at rank 1, <paramref name="members"/> at rank 2.</summary>
    private async Task<long> GuildAsync(Guid leader, Guid[]? officers = null, Guid[]? members = null)
    {
        var (refused, guild) = await Guilds.ReserveAsync(leader, "Vault " + char.ToUpperInvariant(Tag()[0]) + Tag()[1..8], Tag(), "icon.emblem.oak", Ranks, [], Now, default);
        refused.ShouldBeNull();
        await using (var db = Database.Open()) await PostgresGuildStore.FoundAsync(db, guild, leader, default);
        foreach (var other in (officers ?? []).Concat(members ?? [])) (await Guilds.JoinAsync(guild, leader, other, 30, Now, default)).Refusal.ShouldBeNull();
        foreach (var officer in officers ?? [])
            (await Guilds.SetRankAsync(leader, (await Guilds.LoadAsync(guild, default))!.Members.Single(m => m.Character == officer).Member, 1, default)).Refusal.ShouldBeNull();
        return guild;
    }

    /// <summary>A save as the store gets it: <paramref name="items"/>, <paramref name="currency"/> and the notes.</summary>
    private sealed class Saver(PostgresCharacterStore store, CharacterTicket ticket)
    {
        public PostgresCharacterStore Store { get; } = store;

        public Task<SaveResult> SaveAsync(IReadOnlyList<InventoryEntry> items, long currency, params StaffNote[] notes)
        {
            foreach (var note in notes) note.Version = ticket.TakenVersion + 1;
            return Store.SaveAsync([new CharacterSave(ticket, ++ticket.TakenVersion, SavedContent.Full(NewId, Now) with { Items = items.ToList(), AsideItems = [], Currency = currency }, false, [],
                notes)], default);
        }

        public void Failed() => ticket.TakenVersion--;
    }

    private async Task<Saver> ClaimAsync(Guid character, IReadOnlyList<InventoryEntry> items, long currency)
    {
        var store = new PostgresCharacterStore(Database);
        var run = await store.StartRunAsync(default);
        var claim = await store.ClaimAsync(character, run, default);
        var saver = new Saver(store, new CharacterTicket(character) { Claim = claim.Claim, TakenVersion = claim.Version, LandedVersion = claim.Version });
        (await saver.SaveAsync(items, currency)).ShouldBeOfType<SaveResult.Saved>();
        return saver;
    }

    /// <summary>A move landed as a save would land it, in a transaction of its own (what is in the bag is the test's).</summary>
    private async Task LandAsync(StorageMove move, Guid character)
    {
        await using var db = Database.Open();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await PostgresGuildStorageStore.LandAsync(db, move, character, Now, default);
        await transaction.CommitAsync();
    }

    /// <summary>Puts <paramref name="entry"/> (and currency) into the storage as a member's deposit would.</summary>
    private async Task DepositAsync(Guid character, InventoryEntry? entry, long currency = 0)
    {
        var hold = await Store.HoldDepositAsync(character, entry is not null, currency, 50, 1_000_000, Now, default);
        hold.Refusal.ShouldBeNull();
        await LandAsync(new StorageMove(hold.Hold, hold.Guild, GuildStorageMoves.Deposit, entry, currency), character);
    }

    private async Task<StorageView> ViewAsync(Guid character) => (await Store.ReadAsync(character, 50, Now.AddDays(-30), default))!;

    private async Task<int> HoldsAsync(long guild)
    {
        await using var db = Database.Open();
        return await db.GuildStorageHolds.CountAsync(h => h.GuildId == guild);
    }

    private async Task<long[]> CarriedAsync(Guid character)
    {
        await using var db = Database.Open();
        return await db.Items.Where(i => i.CharacterId == character).OrderBy(i => i.Id).Select(i => i.Id).ToArrayAsync();
    }

    // ---- landing with the save -----------------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task A_deposit_and_a_withdrawal_land_with_the_save_or_not_at_all_and_a_crash_is_undone_at_the_next_claim()
    {
        var (_, leader, leaderName) = await CharacterAsync();
        var guild = await GuildAsync(leader);
        var draughts = new InventoryEntry(NewId(), Draught, 5);
        var saver = await ClaimAsync(leader, [draughts], 300);

        // A deposit whose save dies after it wrote everything: neither side moved, and the hold waits.
        var hold = await Store.HoldDepositAsync(leader, true, 100, 50, 1_000_000, Now, default);
        hold.Refusal.ShouldBeNull();
        saver.Store.OnStep = step => step == "checked" ? throw new IOException("the process dies here (test)") : Task.CompletedTask;
        await Should.ThrowAsync<IOException>(() => saver.SaveAsync([], 200, StaffNote.ForStorage(new StorageMove(hold.Hold, guild, GuildStorageMoves.Deposit, draughts, 100))));
        saver.Failed();
        saver.Store.OnStep = null;
        (await ViewAsync(leader)).ShouldSatisfyAllConditions(v => v.Entries.ShouldBeEmpty(), v => v.Currency.ShouldBe(0));
        (await CarriedAsync(leader)).ShouldBe([draughts.Id]);
        (await HoldsAsync(guild)).ShouldBe(1);

        // The server is gone; the character is claimed again (by any server): the hold is undone, as no save of the old stay can land now.
        saver = await ClaimAsync(leader, [draughts], 300);
        (await HoldsAsync(guild)).ShouldBe(0);

        // Again, and the save lands: the entry with its id is in the storage, the bag lacks it, one log line.
        hold = await Store.HoldDepositAsync(leader, true, 100, 50, 1_000_000, Now, default);
        (await saver.SaveAsync([], 200, StaffNote.ForStorage(new StorageMove(hold.Hold, guild, GuildStorageMoves.Deposit, draughts, 100)))).ShouldBeOfType<SaveResult.Saved>();
        var view = await ViewAsync(leader);
        (view.Entries.ShouldHaveSingleItem(), view.Currency).ShouldBe((draughts, 100L));
        (await CarriedAsync(leader)).ShouldBeEmpty();
        (await HoldsAsync(guild)).ShouldBe(0);

        // Part of the stack comes out as a new entry; the storage keeps the rest under the old id.
        var withdrawal = await Store.HoldWithdrawAsync(leader, draughts.Id, 2, 0, NewId, Now, default);
        withdrawal.Refusal.ShouldBeNull();
        var part = withdrawal.Entry!;
        (part.Id != draughts.Id, part.ItemId, part.Quantity).ShouldBe((true, Draught, 2));
        (await ViewAsync(leader)).Entries.ShouldHaveSingleItem().Quantity.ShouldBe(3, "out of the storage from the hold on");
        // Its save dies before the commit: undone by the release, the units go back onto the stack.
        saver.Store.OnStep = step => step == "items_written" ? throw new IOException("dies (test)") : Task.CompletedTask;
        await Should.ThrowAsync<IOException>(() => saver.SaveAsync([part], 200, StaffNote.ForStorage(new StorageMove(withdrawal.Hold, guild, GuildStorageMoves.Withdraw, part, 0))));
        saver.Failed();
        saver.Store.OnStep = null;
        await Store.ReleaseAsync(withdrawal.Hold, default);
        await Store.ReleaseAsync(withdrawal.Hold, default);   // twice changes nothing
        (await ViewAsync(leader)).Entries.ShouldHaveSingleItem().Quantity.ShouldBe(5);
        (await CarriedAsync(leader)).ShouldBeEmpty();

        withdrawal = await Store.HoldWithdrawAsync(leader, draughts.Id, 2, 0, NewId, Now, default);
        part = withdrawal.Entry!;
        (await saver.SaveAsync([part], 200, StaffNote.ForStorage(new StorageMove(withdrawal.Hold, guild, GuildStorageMoves.Withdraw, part, 0)))).ShouldBeOfType<SaveResult.Saved>();
        await Store.ReleaseAsync(withdrawal.Hold, default);   // a release after the landing changes nothing
        view = await ViewAsync(leader);
        view.Entries.ShouldHaveSingleItem().ShouldBe(draughts with { Quantity = 3 });
        (await CarriedAsync(leader)).ShouldBe([part.Id]);
        view.Log!.Select(l => (l.Name, l.Move, l.ItemId, l.EntryId, l.Quantity, l.Currency)).ShouldBe(
            [(leaderName, GuildStorageMoves.Withdraw, Draught, part.Id, 2, 0L), (leaderName, GuildStorageMoves.Deposit, Draught, draughts.Id, 5, 100L)]);
    }

    // ---- races ------------------------------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Two_members_withdrawing_the_last_unit_on_two_stores_at_once_never_both_get_it()
    {
        var (_, leader, _) = await CharacterAsync();
        var (_, officer, _) = await CharacterAsync();
        await GuildAsync(leader, [officer]);
        for (var round = 0; round < 8; round++)
        {
            var last = new InventoryEntry(NewId(), Draught, 1);
            await DepositAsync(leader, last, round == 0 ? 10 : 0);
            var one = new PostgresGuildStorageStore(Database);
            var two = new PostgresGuildStorageStore(Database);
            var results = await Task.WhenAll(
                Task.Run(() => one.HoldWithdrawAsync(leader, last.Id, 1, 0, NewId, Now, default)), Task.Run(() => two.HoldWithdrawAsync(officer, last.Id, 1, 0, NewId, Now, default)),
                Task.Run(() => one.HoldWithdrawAsync(leader, null, 0, 10, NewId, Now, default)), Task.Run(() => two.HoldWithdrawAsync(officer, null, 0, 10, NewId, Now, default)));
            results.Take(2).Count(r => r.Refusal is null).ShouldBe(1, "one of the two gets the last unit");
            results.Take(2).Single(r => r.Refusal is not null).Refusal.ShouldBe(GuildReasons.ItemGone);
            results.Skip(2).Count(r => r.Refusal is null).ShouldBe(round == 0 ? 1 : 0, "the last coins too");
        }
    }

    [DatabaseFact]
    public async Task A_rank_change_racing_a_withdrawal_is_before_it_or_after_it_never_in_between()
    {
        var (_, leader, _) = await CharacterAsync();
        var (_, officer, _) = await CharacterAsync();
        var guild = await GuildAsync(leader, [officer]);
        var entry = new InventoryEntry(NewId(), Draught, 4);
        await DepositAsync(leader, entry);
        var member = (await Guilds.LoadAsync(guild, default))!.Members.Single(m => m.Character == officer).Member;

        // The leader's rank change holds the guild's row and has not committed when the officer's withdrawal comes: the withdrawal waits, then sees the new rank.
        await using (var db = Database.Open())
        {
            await using var change = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync($"SELECT id FROM guilds WHERE id = {guild} FOR UPDATE");
            await db.Database.ExecuteSqlAsync($"UPDATE guild_members SET rank = 2 WHERE id = {member}");
            var withdrawal = Task.Run(() => Store.HoldWithdrawAsync(officer, entry.Id, 1, 0, NewId, Now, default));
            await Task.Delay(400);
            withdrawal.IsCompleted.ShouldBeFalse("it waits for the guild's row");
            await change.CommitAsync();
            (await withdrawal).Refusal.ShouldBe(GuildReasons.NotAllowed);
        }
        (await ViewAsync(leader)).Entries.ShouldHaveSingleItem().Quantity.ShouldBe(4);

        // And the other way round: a withdrawal that holds the row first goes through; the rank change after it does not undo it.
        (await Guilds.SetRankAsync(leader, member, 1, default)).Refusal.ShouldBeNull();
        var results = await Task.WhenAll(Task.Run(async () => (await Store.HoldWithdrawAsync(officer, entry.Id, 1, 0, NewId, Now, default)).Refusal),
            Task.Run(async () => (await Guilds.SetRankAsync(leader, member, 2, default)).Refusal));
        results[1].ShouldBeNull();
        (await ViewAsync(leader)).Entries.Single().Quantity.ShouldBe(results[0] is null ? 3 : 4);
        (await Store.HoldWithdrawAsync(officer, entry.Id, 1, 0, NewId, Now, default)).Refusal.ShouldBe(GuildReasons.NotAllowed, "after the change, never");
    }

    // ---- permissions, room, log -------------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Ranks_room_and_the_log_are_checked_by_the_database()
    {
        var (_, leader, _) = await CharacterAsync();
        var (_, member, _) = await CharacterAsync();
        var (_, outsider, _) = await CharacterAsync();
        var guild = await GuildAsync(leader, members: [member]);
        var entry = new InventoryEntry(NewId(), Draught, 2);
        await DepositAsync(member, entry, 40);   // a member may deposit

        (await Store.HoldWithdrawAsync(member, entry.Id, 1, 0, NewId, Now, default)).Refusal.ShouldBe(GuildReasons.NotAllowed);
        (await Store.HoldWithdrawAsync(member, null, 0, 1, NewId, Now, default)).Refusal.ShouldBe(GuildReasons.NotAllowed);
        (await ViewAsync(member)).ShouldSatisfyAllConditions(v => v.Log.ShouldBeNull("no storage_log"), v => v.Entries.Count.ShouldBe(1), v => v.Currency.ShouldBe(40));
        (await Store.ReadAsync(outsider, 50, Now.AddDays(-30), default)).ShouldBeNull();
        (await Store.HoldDepositAsync(outsider, true, 0, 50, 100, Now, default)).Refusal.ShouldBe(GuildReasons.NotInGuild);

        (await Store.HoldWithdrawAsync(leader, entry.Id, 3, 0, NewId, Now, default)).Refusal.ShouldBe(GuildReasons.ItemGone, "more units than there are");
        (await Store.HoldWithdrawAsync(leader, 424_242, 1, 0, NewId, Now, default)).Refusal.ShouldBe(GuildReasons.ItemGone);
        (await Store.HoldWithdrawAsync(leader, null, 0, 41, NewId, Now, default)).Refusal.ShouldBe(GuildReasons.ItemGone);

        // Room: a hold keeps its place, so two deposits for the last place cannot both have it; currency counts what is on its way.
        var first = await Store.HoldDepositAsync(member, true, 0, 2, 100, Now, default);
        first.Refusal.ShouldBeNull();
        (await Store.HoldDepositAsync(leader, true, 0, 2, 100, Now, default)).Refusal.ShouldBe(GuildReasons.StorageFull);
        var coins = await Store.HoldDepositAsync(member, false, 50, 2, 100, Now, default);
        coins.Refusal.ShouldBeNull();
        (await Store.HoldDepositAsync(leader, false, 11, 2, 100, Now, default)).Refusal.ShouldBe(GuildReasons.StorageFull);
        await Store.ReleaseAsync(first.Hold, default);
        await Store.ReleaseAsync(coins.Hold, default);
        (await HoldsAsync(guild)).ShouldBe(0);

        // The log keeps a move guild.storage.log_days: an older line goes when the storage is read.
        await using (var db = Database.Open())
        {
            db.GuildStorageLog.Add(new GuildStorageLogRow { GuildId = guild, CharacterId = leader, Move = GuildStorageMoves.Deposit, Currency = 1, At = Now.AddDays(-31) });
            await db.SaveChangesAsync();
        }
        (await ViewAsync(leader)).Log!.Count.ShouldBe(1);
        await using (var db = Database.Open()) (await db.GuildStorageLog.CountAsync(l => l.GuildId == guild)).ShouldBe(1);
    }

    // ---- the service: tick refusals and a disconnect before the save ---------------------------------------------------------------------------

    private GuildStorage Service(Func<GuildStorageTransfer, (string? Refusal, bool Lands)> tick) => new(new GuildStorageDef { Size = 10, MaxCurrency = 1_000 }, Store, NewId, intent =>
    {
        var transfer = (GuildStorageTransfer)intent;
        _ = Task.Run(() =>
        {
            if (!transfer.TryStart()) return;
            var (refusal, lands) = tick(transfer);
            if (refusal is not null)
            {
                transfer.Refuse(refusal);
                return;
            }
            var note = StaffNote.ForStorage(transfer.ToMove(transfer.Entry ?? (transfer.EntryId is { } id ? new InventoryEntry(id, Draught, transfer.Quantity) : null)));
            transfer.Moved(note);
            note.Settle(lands);   // a stay that ended before its save: never landed
        });
        return true;
    }, 2_000, _time, _log);

    [DatabaseFact]
    public async Task A_refusal_on_the_tick_or_a_disconnect_before_the_save_undoes_the_hold()
    {
        var (_, leader, _) = await CharacterAsync();
        var guild = await GuildAsync(leader);
        var entry = new InventoryEntry(NewId(), Draught, 3);
        await DepositAsync(leader, entry, 30);
        var entity = new EntityId(Interlocked.Increment(ref _lastEntity));

        var full = Service(_ => (GuildReasons.InventoryFull, true));
        (await full.WithdrawAsync(leader, entity, new GuildWithdrawMessage(entry.Id, 3, 0), default)).ShouldBe(GuildReasons.InventoryFull);
        var gone = Service(_ => (null, false));
        (await gone.WithdrawAsync(leader, entity, new GuildWithdrawMessage(entry.Id, 1, 0), default)).ShouldBe(GuildReasons.Unavailable);
        (await gone.WithdrawAsync(leader, entity, new GuildWithdrawMessage(null, 0, 30), default)).ShouldBe(GuildReasons.Unavailable);
        (await gone.DepositAsync(leader, entity, new GuildDepositMessage(NewId(), 1, 0), default)).ShouldBe(GuildReasons.Unavailable);
        (await full.DepositAsync(leader, entity, new GuildDepositMessage(1, 0, 0), default)).ShouldBe(GuildReasons.ItemGone, "no unit is no deposit");
        (await full.DepositAsync(leader, entity, new GuildDepositMessage(1, 1, 5), default)).ShouldBe(GuildReasons.ItemGone, "an entry or currency, not both");

        var view = await ViewAsync(leader);
        (view.Entries.ShouldHaveSingleItem(), view.Currency).ShouldBe((entry, 30L));
        (await HoldsAsync(guild)).ShouldBe(0);
        view.Log!.Count.ShouldBe(1, "only the landed deposit");

        var works = Service(_ => (null, true));
        (await works.WithdrawAsync(leader, entity, new GuildWithdrawMessage(null, 0, 30), default)).ShouldBeNull();
        (await ViewAsync(leader)).Currency.ShouldBe(0, "the hold took it; the note's landing (the save's) is the test's fake");
    }

    // ---- disbanding and erasure -----------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task The_last_member_cannot_leave_or_be_deleted_while_the_storage_holds_anything_and_erasure_keeps_only_the_moves()
    {
        var (account, leader, _) = await CharacterAsync();
        var guild = await GuildAsync(leader);
        var entry = new InventoryEntry(NewId(), Draught, 1);
        await DepositAsync(leader, entry);
        (await Guilds.LeaveAsync(leader, default)).Refusal.ShouldBe(GuildReasons.StorageNotEmpty);
        (await Accounts.DeleteCharacterAsync(account, leader, Now, default)).ShouldBe(DeleteCharacterOutcome.GuildStorageNotEmpty);
        (await Guilds.GuildOfAsync(leader, default)).ShouldBe(guild, "nothing changed");
        (await Accounts.ListCharactersAsync(account, default)).ShouldHaveSingleItem();

        var hold = await Store.HoldWithdrawAsync(leader, entry.Id, 1, 0, NewId, Now, default);
        (await Guilds.LeaveAsync(leader, default)).Refusal.ShouldBe(GuildReasons.StorageNotEmpty, "a move under way counts");
        await LandAsync(new StorageMove(hold.Hold, guild, GuildStorageMoves.Withdraw, hold.Entry, 0), leader);
        (await Guilds.LeaveAsync(leader, default)).Disbanded.ShouldBeTrue("empty: the guild ends");

        // Erasure: the log forgets who moved (the guild keeps the moves), holds are undone, and a guild left empty goes with its storage.
        var (gone, mover, _) = await CharacterAsync();
        var (_, stays, _) = await CharacterAsync();
        var shared = await GuildAsync(stays, [mover]);
        await DepositAsync(mover, new InventoryEntry(NewId(), Draught, 2), 5);
        var pending = await Store.HoldWithdrawAsync(mover, null, 0, 5, NewId, Now, default);
        pending.Refusal.ShouldBeNull();
        var (solo, alone, _) = await CharacterAsync();
        var lonely = await GuildAsync(alone);
        await DepositAsync(alone, new InventoryEntry(NewId(), Draught, 1));
        (await Accounts.TryRequestErasureAsync(gone, "hash", Now, Now, default)).ShouldNotBeNull();
        (await Accounts.EraseAsync(gone, Now, default)).ShouldBe(EraseOutcome.Erased);
        (await Accounts.TryRequestErasureAsync(solo, "hash", Now, Now, default)).ShouldNotBeNull();
        (await Accounts.EraseAsync(solo, Now, default)).ShouldBe(EraseOutcome.Erased);

        var view = await ViewAsync(stays);
        view.Currency.ShouldBe(5, "the erased member's withdrawal under way was undone");
        view.Log!.ShouldHaveSingleItem().ShouldSatisfyAllConditions(l => l.Name.ShouldBe(string.Empty), l => l.Quantity.ShouldBe(2));
        await using var db = Database.Open();
        (await db.GuildStorageLog.CountAsync(l => l.CharacterId == mover)).ShouldBe(0);
        (await db.GuildStorageLog.CountAsync(l => l.GuildId == shared && l.CharacterId == null)).ShouldBe(1);
        (await db.GuildStorageItems.CountAsync(i => i.GuildId == lonely)).ShouldBe(0, "erasing the last member takes the guild and its storage");
        (await db.Guilds.AnyAsync(g => g.Id == lonely)).ShouldBeFalse();
    }

    // ---- property: random sequences on the real store ----------------------------------------------------------------------------------------

    /// <summary>
    /// Random deposits and withdrawals of three members on two stores, each landing or never landing (then undone by a release or by a claim), with the bags as the saves left them: after every
    /// step every item id is in exactly one place (a bag, the storage, or a hold under way) and units and coins add up.
    /// </summary>
    [DatabaseFact]
    public async Task Random_sequences_on_the_real_store_count_every_item_and_coin()
    {
        var (_, a, _) = await CharacterAsync();
        var (_, b, _) = await CharacterAsync();
        var (_, c, _) = await CharacterAsync();
        var guild = await GuildAsync(a, [b, c]);
        var rules = new InventoryRules(new ItemLookup(Game.Tests.Items.InventoryRulesTests.Items()), Game.Tests.Items.InventoryRulesTests.Balance(maxEntries: 8));
        var bags = new Dictionary<Guid, InventoryState>();
        foreach (var who in new[] { a, b, c })
        {
            var bag = new InventoryState { Currency = 300, WeightLimit = 10_000 };
            bag.SetEntries([new InventoryEntry(NewId(), Draught, 6), new InventoryEntry(NewId(), "item.iron_blade", 1) { Refine = 3 }]);
            rules.Refresh(bag);
            bags[who] = bag;
        }
        var stores = new[] { new PostgresGuildStorageStore(Database), new PostgresGuildStorageStore(Database) };
        var random = new Random(905);
        var start = await TotalsAsync();
        for (var step = 0; step < 60; step++)
        {
            var who = new[] { a, b, c }[random.Next(3)];
            var bag = bags[who];
            var store = stores[random.Next(2)];
            var lands = random.Next(3) != 0;
            if (random.Next(2) == 0 && bag.Entries.Count > 0)
            {
                var entry = bag.Entries[random.Next(bag.Entries.Count)];
                var quantity = random.Next(1, entry.Quantity + 1);
                var hold = await store.HoldDepositAsync(who, true, 0, 50, 1_000_000, Now, default);
                if (hold.Refusal is not null) continue;
                var copy = (bag.Entries.ToList(), bag.Currency);
                GuildStorageRules_TryTake(rules, bag, entry.Id, quantity, out var moved).ShouldBeNull();
                if (lands) await LandAsync(new StorageMove(hold.Hold, guild, GuildStorageMoves.Deposit, moved, 0), who);
                else
                {
                    bag.SetEntries(copy.Item1);   // the save never landed: the bag as last saved
                    await store.ReleaseAsync(hold.Hold, default);
                }
            }
            else
            {
                var view = await ViewAsync(a);
                var take = view.Entries.Count > 0 && random.Next(3) != 0 ? view.Entries[random.Next(view.Entries.Count)] : null;
                var hold = take is not null
                    ? await store.HoldWithdrawAsync(who, take.Id, random.Next(1, take.Quantity + 1), 0, NewId, Now, default)
                    : await store.HoldWithdrawAsync(who, null, 0, random.Next(1, 40), NewId, Now, default);
                if (hold.Refusal is not null) continue;
                var currency = hold.Entry is null ? await HeldCurrencyAsync(hold.Hold) : 0;
                if (!lands)
                {
                    await TotalsAsync().ContinueWith(t => t.Result.ShouldBe(start, $"with a hold under way at step {step}"));
                    await store.ReleaseAsync(hold.Hold, default);
                    continue;
                }
                if (Game.Shared.Guild.GuildStorageRules.TryGive(rules, bag, hold.Entry, currency, bag.WeightLimit, new InventoryDelta()) is not null)
                {
                    await store.ReleaseAsync(hold.Hold, default);   // refused on the tick (a full bag): undone
                    continue;
                }
                await LandAsync(new StorageMove(hold.Hold, guild, GuildStorageMoves.Withdraw, hold.Entry, currency), who);
            }
            (await TotalsAsync()).ShouldBe(start, $"after step {step}");
        }
        (await HoldsAsync(guild)).ShouldBe(0);

        async Task<string> TotalsAsync()
        {
            await using var db = Database.Open();
            var stored = await db.GuildStorageItems.AsNoTracking().Where(i => i.GuildId == guild).ToListAsync();
            var held = await db.GuildStorageHolds.AsNoTracking().Where(h => h.GuildId == guild).ToListAsync();
            var currency = (await db.Guilds.SingleAsync(g => g.Id == guild)).StorageCurrency;
            var all = bags.Values.SelectMany(x => x.Entries.Select(e => (e.Id, e.ItemId, e.Quantity)))
                .Concat(stored.Select(i => (i.Id, i.ItemId, i.Quantity)))
                .Concat(held.Where(h => h.EntryId is not null).Select(h => (h.EntryId!.Value, h.ItemId!, h.Quantity))).ToList();
            all.Select(e => e.Item1).Distinct().Count().ShouldBe(all.Count, "an item id in two places");
            var coins = bags.Values.Sum(x => x.Currency) + currency + held.Where(h => h.Move == GuildStorageMoves.Withdraw).Sum(h => h.Currency);
            return string.Join(",", all.GroupBy(e => e.Item2).OrderBy(g => g.Key).Select(g => g.Key + "=" + g.Sum(e => e.Item3))) + "|" + coins;
        }
    }

    private static string? GuildStorageRules_TryTake(InventoryRules rules, InventoryState bag, long entry, int quantity, out InventoryEntry? moved) =>
        Game.Shared.Guild.GuildStorageRules.TryTake(rules, bag, entry, quantity, 0, NewId, new InventoryDelta(), out moved);

    private async Task<long> HeldCurrencyAsync(Guid hold)
    {
        await using var db = Database.Open();
        return await db.GuildStorageHolds.Where(h => h.Id == hold).Select(h => h.Currency).SingleAsync();
    }
}
