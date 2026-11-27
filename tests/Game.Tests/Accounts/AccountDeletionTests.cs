using Game.Content.Registry;
using Game.Server.Accounts;
using Game.Server.Hosting;
using Game.Server.Persistence;
using Game.Server.World.Saving;
using Game.Shared.Accounts;
using Game.Shared.Items;
using Game.Shared.Vocabulary;
using Game.Tests.Network;
using Game.Tests.Saving;
using Microsoft.EntityFrameworkCore;

namespace Game.Tests.Accounts;

/// <summary>T-703: deleting an account, taking it back, and what the erasing does, above the database.</summary>
public class AccountDeletionTests
{
    private static Task<AccountResult<ErasureAnswer>> DeleteAsync(ServiceRig rig, string token, string password = ServiceRig.Password) =>
        rig.Service.DeleteAccountAsync(token, password, ServiceRig.Address, default);

    private static AccountEraser Eraser(ServiceRig rig) => new(rig.Memory, rig.Time, rig.Log);

    [Fact]
    public async Task Deleting_ends_every_session_and_nobody_signs_in_until_it_is_kept()
    {
        var rig = new ServiceRig();
        var ended = new List<EndedSessions>();
        rig.Service.SessionsEnded = ended.Add;
        var first = await rig.RegisterAsync();
        var second = (await rig.LoginAsync()).Value!.Token;
        var answer = await DeleteAsync(rig, first);
        answer.Value!.In.ShouldBe(TimeSpan.FromDays(14));
        ended.Single().All.ShouldBeTrue();
        ended.Single().Keys.Count.ShouldBe(2);
        foreach (var token in new[] { first, second }) (await rig.Service.ListAsync(token, default)).Error.ShouldBe(AccountErrors.InvalidSession);

        rig.Time.Now += TimeSpan.FromDays(4);
        var refused = await rig.LoginAsync();
        (refused.Error, refused.RetryAfterMs).ShouldBe((AccountErrors.AccountDeleting, (long)TimeSpan.FromDays(10).TotalMilliseconds));
        (await rig.LoginAsync(password: "not the password")).Error.ShouldBe(AccountErrors.InvalidCredentials, "only someone who has the password is told");
        (await rig.Service.RegisterAsync("ada", ServiceRig.Password, ServiceRig.Address, default)).Error.ShouldBe(AccountErrors.UsernameTaken);

        var kept = (await rig.Service.KeepAsync("ada", ServiceRig.Password, ServiceRig.Address, default)).Value!.Token;
        (await rig.Service.ListAsync(kept, default)).Ok.ShouldBeTrue();
        rig.Memory.Deletions.ShouldBeEmpty("a request that is taken back leaves no record");
        (await rig.LoginAsync()).Ok.ShouldBeTrue();
        rig.Time.Now += TimeSpan.FromDays(60);
        (await Eraser(rig).PassAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_wrong_password_deletes_and_keeps_nothing_and_counts_like_a_failed_sign_in()
    {
        var limits = new AccountLimits();
        var rig = new ServiceRig(limits: limits);
        var token = await rig.RegisterAsync();
        for (var i = 0; i <= limits.AccountFreeFailures; i++) (await DeleteAsync(rig, token, "wrong password " + i)).Error.ShouldBe(AccountErrors.InvalidCredentials);
        (await DeleteAsync(rig, token)).Error.ShouldBe(AccountErrors.RateLimited);
        (await rig.Service.ListAsync(token, default)).Ok.ShouldBeTrue("nothing was deleted");
        (await rig.LoginAsync()).Ok.ShouldBeTrue("the owner's sign-in is not locked");
        rig.Clock.Advance(limits.LockBaseMs);
        (await DeleteAsync(rig, token)).Ok.ShouldBeTrue();
        (await rig.Service.KeepAsync("ada", "wrong password", ServiceRig.Address, default)).Error.ShouldBe(AccountErrors.InvalidCredentials);
        (await rig.LoginAsync()).Error.ShouldBe(AccountErrors.AccountDeleting);
    }

    [Fact]
    public async Task Asking_to_keep_an_account_that_is_not_being_deleted_is_a_sign_in()
    {
        var rig = new ServiceRig();
        await rig.RegisterAsync();
        (await rig.Service.KeepAsync("ada", ServiceRig.Password, ServiceRig.Address, default)).Ok.ShouldBeTrue();
        (await rig.Service.KeepAsync("nobody", ServiceRig.Password, ServiceRig.Address, default)).Error.ShouldBe(AccountErrors.InvalidCredentials);
    }

    [Fact]
    public async Task Once_the_time_is_over_the_account_is_erased_and_it_is_as_if_it_had_never_been()
    {
        var rig = new ServiceRig();
        var ada = await rig.RegisterAsync();
        await rig.CreateAsync(ada, "Lyra");
        var bob = await rig.RegisterAsync("bobby");
        await DeleteAsync(rig, ada);
        rig.Time.Now += TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1);
        (await Eraser(rig).PassAsync()).ShouldBe(0);
        rig.Time.Now += TimeSpan.FromSeconds(1);
        (await Eraser(rig).PassAsync()).ShouldBe(1);
        (await Eraser(rig).PassAsync()).ShouldBe(0);
        rig.Memory.AccountCount.ShouldBe(1);
        (await rig.LoginAsync()).Error.ShouldBe(AccountErrors.InvalidCredentials);
        (await rig.Service.KeepAsync("ada", ServiceRig.Password, ServiceRig.Address, default)).Error.ShouldBe(AccountErrors.InvalidCredentials);
        (await rig.Service.CreateAsync(bob, "Lyra", default)).Error.ShouldBe(AccountErrors.InvalidSession, "a day-old session, so sign in again");
        bob = (await rig.LoginAsync("bobby")).Value!.Token;
        (await rig.Service.CreateAsync(bob, "Lyra", default)).Ok.ShouldBeTrue("the name is free again");
        rig.Memory.Deletions.Single().Value.Erased.ShouldBe(rig.Time.Now.UtcDateTime);
        (await rig.Service.RegisterAsync("ada", ServiceRig.Password, ServiceRig.Address, default)).Ok.ShouldBeTrue("the username is free again");
    }

    [Fact]
    public async Task A_waiting_time_of_nothing_erases_at_the_next_pass()
    {
        var rig = new ServiceRig(limits: new AccountLimits { EraseWait = TimeSpan.Zero });
        await DeleteAsync(rig, await rig.RegisterAsync());
        (await Eraser(rig).PassAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task An_account_whose_character_a_server_holds_waits_and_does_not_hold_up_the_others()
    {
        var rig = new ServiceRig(limits: new AccountLimits { EraseWait = TimeSpan.Zero });
        var ada = await rig.RegisterAsync();
        var id = Guid.Parse((await rig.CreateAsync(ada, "Lyra")).Id);
        var bob = await rig.RegisterAsync("bobby");
        rig.Memory.HeldElsewhere.Add(id);
        await DeleteAsync(rig, ada);
        await DeleteAsync(rig, bob);
        (await Eraser(rig).PassAsync()).ShouldBe(1);
        rig.Memory.HeldElsewhere.Clear();
        (await Eraser(rig).PassAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_database_that_is_away_is_said_once_and_tried_again()
    {
        var rig = new ServiceRig(limits: new AccountLimits { EraseWait = TimeSpan.Zero });
        await DeleteAsync(rig, await rig.RegisterAsync());
        rig.Memory.Away = true;
        var eraser = Eraser(rig);
        for (var i = 0; i < 3; i++) (await eraser.PassAsync()).ShouldBe(0);
        rig.Log.Lines.Count(l => l.Contains("erasing deleted accounts waits")).ShouldBe(1);
        rig.Memory.Away = false;
        (await eraser.PassAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_eraser_runs_by_itself_and_stops_when_asked()
    {
        var rig = new ServiceRig(limits: new AccountLimits { EraseWait = TimeSpan.Zero });
        await DeleteAsync(rig, await rig.RegisterAsync());
        await using (AccountEraser.Start(rig.Memory, rig.Time, rig.Log))
            await Poll.UntilAsync(() => rig.Memory.AccountCount, n => n == 0, "the first pass");
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("30", 30)]
    [InlineData("91", null)]
    [InlineData("x", null)]
    public void The_waiting_time_is_an_option_of_the_server(string value, int? days) =>
        (ServerOptions.Parse(["--account-erase-days", value], out _)?.AccountEraseDays).ShouldBe(days);

    [Fact]
    public async Task Nothing_of_a_deletion_puts_a_password_or_a_username_into_the_log()
    {
        var rig = new ServiceRig();
        var token = await rig.RegisterAsync("unmistakable.user", "unmistakable-password-1");
        await DeleteAsync(rig, token, "unmistakable-password-2");
        await DeleteAsync(rig, token, "unmistakable-password-1");
        await rig.LoginAsync("unmistakable.user", "unmistakable-password-1");
        await rig.Service.KeepAsync("unmistakable.user", "unmistakable-password-1", ServiceRig.Address, default);
        string.Join("\n", rig.Log.Lines).ShouldNotContain("unmistakable", Case.Insensitive);
        new Game.Shared.Protocol.DeleteAccountRequest("unmistakable").ToString().ShouldNotContain("unmistakable");
    }
}

/// <summary>T-703 against a real PostgreSQL: what is left after erasing, and the races.</summary>
[Trait("Category", "Database")]
public sealed class AccountErasureDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static long _lastItem = 1_000_000;

    private GameDatabase Database => fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set.");

    private PostgresAccountStore Accounts => new(Database);

    private PostgresCharacterStore Characters => new(Database);

    private static string UniqueName() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 12));

    private static byte[] Token() => SessionTokens.HashOf(SessionTokens.New())!;

    private static async Task<(Guid Account, Guid Character, string Name, string Username)> AccountAsync(PostgresAccountStore store)
    {
        var account = Guid.NewGuid();
        var username = "u" + Guid.NewGuid().ToString("N")[..16];
        (await store.TryCreateAccountAsync(account, username, "hash", Now, default)).ShouldBeTrue();
        var name = UniqueName();
        var (_, character) = await store.CreateCharacterAsync(account, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.wanderer", 3, null, Now, default);
        return (account, character!.Id, name, username);
    }

    private static async Task<CharacterTicket> PlayedAsync(PostgresCharacterStore store, Guid character, long run, bool leave = true)
    {
        var claim = await store.ClaimAsync(character, run, default);
        claim.Outcome.ShouldBe(ClaimOutcome.Claimed);
        var ticket = new CharacterTicket(character) { Claim = claim.Claim, TakenVersion = claim.Version, LandedVersion = claim.Version };
        (await store.SaveAsync([Save(ticket, leave)], default)).ShouldBeOfType<SaveResult.Saved>();
        return ticket;
    }

    private static CharacterSave Save(CharacterTicket ticket, bool final) => new(ticket, ++ticket.TakenVersion,
        SavedContent.Full(() => Interlocked.Increment(ref _lastItem), Now) with
        {
            Items = [new InventoryEntry(Interlocked.Increment(ref _lastItem), "item.x", 3, null)], AsideItems = [],
        }, final, []);

    private async Task<int> RowsAsync(Guid account, Guid character)
    {
        await using var db = Database.Open();
        return await db.Accounts.CountAsync(a => a.Id == account) + await db.Sessions.CountAsync(s => s.AccountId == account) + await db.Characters.CountAsync(c => c.AccountId == account)
            + await db.CharacterStates.CountAsync(s => s.CharacterId == character) + await db.Items.CountAsync(i => i.CharacterId == character)
            + await db.Outfits.CountAsync(o => o.CharacterId == character);   // T-708
    }

    [DatabaseFact]
    public async Task Erasing_leaves_nothing_but_a_record_without_personal_data()
    {
        var (account, character, name, username) = await AccountAsync(Accounts);
        var (other, othersCharacter, _, _) = await AccountAsync(Accounts);
        var deleted = UniqueName();
        var (_, gone) = await Accounts.CreateCharacterAsync(account, Guid.NewGuid(), deleted, CharacterNameRules.Key(deleted), "job.wanderer", 3, null, Now, default);
        (await Accounts.DeleteCharacterAsync(account, gone!.Id, Now, default)).ShouldBe(DeleteCharacterOutcome.Deleted);
        var run = await Characters.StartRunAsync(default);
        await PlayedAsync(Characters, character, run);
        await PlayedAsync(Characters, othersCharacter, run);
        await Accounts.TryAddSessionAsync(account, "hash", null, Token(), Now, Now.AddHours(1), 10, default);
        var othersRows = await RowsAsync(other, othersCharacter);
        await using (var before = Database.Open()) (await before.Outfits.CountAsync(o => o.CharacterId == character)).ShouldBe(2, "the character owns outfits (T-708), so erasing must take them");
        // T-904: it leads a guild alone (erased with it, and its name with it); the other account leads a guild that stays.
        var guilds = new PostgresGuildStore(Database);
        var guildName = "Erased " + char.ToUpperInvariant(UniqueName()[0]) + UniqueName()[..6];
        var (_, own) = await guilds.ReserveAsync(character, guildName, CharacterNameRules.Key(guildName), "icon.emblem.oak", [new("Leader", []), new("Member", [])], [], Now, default);
        var othersGuildName = "Kept " + char.ToUpperInvariant(UniqueName()[0]) + UniqueName()[..6];
        var (_, others) = await guilds.ReserveAsync(othersCharacter, othersGuildName, CharacterNameRules.Key(othersGuildName), "icon.emblem.oak", [new("Leader", []), new("Member", [])], [], Now,
            default);
        await using (var found = Database.Open())
        {
            await PostgresGuildStore.FoundAsync(found, own, character, default);
            await PostgresGuildStore.FoundAsync(found, others, othersCharacter, default);
        }
        (await guilds.JoinAsync(others, othersCharacter, gone.Id, 30, Now, default)).Refusal.ShouldBe(Game.Shared.Vocabulary.GuildReasons.Unavailable, "a deleted character joins nothing");

        (await Accounts.TryRequestErasureAsync(account, "hash", Now, Now.AddDays(14), default))!.Count.ShouldBe(1);
        (await Accounts.EraseAsync(account, Now.AddDays(14).AddSeconds(-1), default)).ShouldBe(EraseOutcome.Nothing, "not due");
        (await Accounts.ListErasableAsync(Now.AddDays(14), 1_000, default)).ShouldContain(account);
        (await Accounts.EraseAsync(account, Now.AddDays(14), default)).ShouldBe(EraseOutcome.Erased);

        (await RowsAsync(account, character)).ShouldBe(0);
        (await RowsAsync(other, othersCharacter)).ShouldBe(othersRows, "nobody else lost anything");
        await using var db = Database.Open();
        (await db.Characters.AnyAsync(c => c.Id == gone.Id)).ShouldBeFalse("a deleted character goes too");
        var record = await db.AccountDeletions.AsNoTracking().SingleAsync(d => d.AccountId == account);
        (record.RequestedAt, record.ErasedAt).ShouldBe((Now, (DateTime?)Now.AddDays(14)));
        var everything = await EverythingAsync(db);
        foreach (var text in new[] { name, deleted, username, character.ToString(), gone.Id.ToString(), guildName }) everything.ShouldNotContain(text, Case.Insensitive);
        everything.ShouldContain(othersGuildName, Case.Sensitive, "the other account's guild stays");
        (await Accounts.EraseAsync(account, Now.AddDays(15), default)).ShouldBe(EraseOutcome.Nothing);
        (await Accounts.CreateCharacterAsync(other, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.wanderer", 3, null, Now, default)).Outcome.ShouldBe(CreateCharacterOutcome.Created);
    }

    /// <summary>Every row of every table as text, whatever tables there are.</summary>
    private static async Task<string> EverythingAsync(GameDb db)
    {
        var all = new System.Text.StringBuilder();
        foreach (var table in await db.Database.SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync())
#pragma warning disable EF1002 // the name comes from the schema
            foreach (var row in await db.Database.SqlQueryRaw<string>($"SELECT t::text AS \"Value\" FROM \"{table}\" t").ToListAsync()) all.AppendLine(row);
#pragma warning restore EF1002
        return all.ToString();
    }

    [DatabaseFact]
    public async Task While_it_waits_nobody_signs_in_or_makes_a_character_and_it_can_be_taken_back()
    {
        var (account, _, _, username) = await AccountAsync(Accounts);
        (await Accounts.TryRequestErasureAsync(account, "other", Now, Now.AddDays(14), default)).ShouldBeNull();
        (await Accounts.TryCancelErasureAsync(account, "hash", default)).ShouldBeFalse("nothing was asked");
        await Accounts.TryRequestErasureAsync(account, "hash", Now, Now.AddDays(14), default);
        await Accounts.TryRequestErasureAsync(account, "hash", Now.AddDays(1), Now.AddDays(15), default);
        (await Accounts.FindAccountAsync(username, default))!.EraseAfter.ShouldBe(Now.AddDays(14), "asking twice does not put it off");
        (await Accounts.TryAddSessionAsync(account, "hash", null, Token(), Now, Now.AddHours(1), 10, default)).ShouldBeNull();
        var name = UniqueName();
        (await Accounts.CreateCharacterAsync(account, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.wanderer", 3, null, Now, default)).Outcome.ShouldBe(CreateCharacterOutcome.AccountGone);
        (await Accounts.TryCancelErasureAsync(account, "other", default)).ShouldBeFalse();
        (await Accounts.TryCancelErasureAsync(account, "hash", default)).ShouldBeTrue();
        (await Accounts.TryCancelErasureAsync(account, "hash", default)).ShouldBeFalse();
        (await Accounts.EraseAsync(account, Now.AddYears(1), default)).ShouldBe(EraseOutcome.Nothing);
        (await Accounts.TryAddSessionAsync(account, "hash", null, Token(), Now, Now.AddHours(1), 10, default)).ShouldNotBeNull();
        await using var db = Database.Open();
        (await db.AccountDeletions.AnyAsync(d => d.AccountId == account)).ShouldBeFalse();
    }

    [DatabaseFact]
    public async Task A_character_a_live_server_holds_keeps_its_account_and_its_last_save_brings_nothing_back()
    {
        var (account, character, _, _) = await AccountAsync(Accounts);
        var run = await Characters.StartRunAsync(default);
        var ticket = await PlayedAsync(Characters, character, run, leave: false);
        await Accounts.TryRequestErasureAsync(account, "hash", Now, Now, default);
        (await Accounts.EraseAsync(account, Now, default)).ShouldBe(EraseOutcome.InUse);
        (await RowsAsync(account, character)).ShouldBeGreaterThan(3);
        (await Characters.SaveAsync([Save(ticket, final: true)], default)).ShouldBeOfType<SaveResult.Saved>("it is saved as it leaves");
        (await Accounts.EraseAsync(account, Now, default)).ShouldBe(EraseOutcome.Erased);
        (await Characters.SaveAsync([Save(ticket, final: false)], default)).ShouldBeOfType<SaveResult.Lost>("a server that still thinks it plays it");
        (await Characters.ClaimAsync(character, run, default)).Outcome.ShouldBe(ClaimOutcome.NotFound);
        (await RowsAsync(account, character)).ShouldBe(0);
    }

    [DatabaseFact]
    public async Task A_character_of_a_dead_server_does_not_keep_its_account()
    {
        var (account, character, _, _) = await AccountAsync(Accounts);
        var run = await Characters.StartRunAsync(default);
        await PlayedAsync(Characters, character, run, leave: false);
        await Characters.EndRunAsync(run, default);
        await Accounts.TryRequestErasureAsync(account, "hash", Now, Now, default);
        (await Accounts.EraseAsync(account, Now, default)).ShouldBe(EraseOutcome.Erased);
        (await RowsAsync(account, character)).ShouldBe(0);
    }

    [DatabaseFact]
    public async Task Servers_at_once_erase_it_once()
    {
        for (var round = 0; round < 10; round++)
        {
            var (account, character, _, _) = await AccountAsync(Accounts);
            await Accounts.TryRequestErasureAsync(account, "hash", Now, Now, default);
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Accounts.EraseAsync(account, Now, default))));
            outcomes.Count(o => o == EraseOutcome.Erased).ShouldBe(1, $"round {round}");
            outcomes.Count(o => o == EraseOutcome.Nothing).ShouldBe(5, $"round {round}");
            (await RowsAsync(account, character)).ShouldBe(0);
        }
    }

    [DatabaseFact]
    public async Task It_is_either_kept_or_erased_and_either_entered_or_erased_never_both()
    {
        for (var round = 0; round < 25; round++)
        {
            // A run of its own each round: a run nobody beats for its lease (20 s) is dead, and a busy machine took longer than that over all the rounds (ADR-002 row 343).
            var run = await Characters.StartRunAsync(default);
            var (account, character, _, _) = await AccountAsync(Accounts);
            await Accounts.TryRequestErasureAsync(account, "hash", Now, Now, default);
            var erase = Task.Run(() => Accounts.EraseAsync(account, Now, default));
            var keep = Task.Run(() => Accounts.TryCancelErasureAsync(account, "hash", default));
            await Task.WhenAll(erase, keep);
            (await keep).ShouldBe(await erase != EraseOutcome.Erased, $"round {round}");
            (await RowsAsync(account, character) == 0).ShouldBe(await erase == EraseOutcome.Erased, $"round {round}");

            (account, character, _, _) = await AccountAsync(Accounts);
            await Accounts.TryRequestErasureAsync(account, "hash", Now, Now, default);
            var gone = Task.Run(() => Accounts.EraseAsync(account, Now, default));
            var claim = Task.Run(() => Characters.ClaimAsync(character, run, default));
            await Task.WhenAll(gone, claim);
            ((await claim).Outcome == ClaimOutcome.Claimed).ShouldBe(await gone == EraseOutcome.InUse, $"round {round}");
        }
    }

    [DatabaseFact]
    public async Task No_session_survives_a_deletion_that_races_its_sign_in()
    {
        for (var round = 0; round < 20; round++)
        {
            var (account, _, _, _) = await AccountAsync(Accounts);
            var signIns = Enumerable.Range(0, 8).Select(_ => Accounts.TryAddSessionAsync(account, "hash", null, Token(), Now, Now.AddHours(1), 100, default)).ToList();
            var ended = await Accounts.TryRequestErasureAsync(account, "hash", Now, Now.AddDays(14), default);
            await Task.WhenAll(signIns);
            ended!.Count.ShouldBe(signIns.Count(s => s.Result is not null), $"round {round}");
            await using var db = Database.Open();
            (await db.Sessions.CountAsync(s => s.AccountId == account)).ShouldBe(0, $"round {round}");
        }
    }

    [DatabaseFact]
    public async Task An_erasure_that_fails_in_the_middle_leaves_everything()
    {
        await using var database = new GameDatabase(fixture.NewConnectionString());
        await database.MigrateAsync();
        var accounts = new PostgresAccountStore(database);
        var characters = new PostgresCharacterStore(database);
        var (account, character, _, _) = await AccountAsync(accounts);
        await PlayedAsync(characters, character, await characters.StartRunAsync(default));
        await accounts.TryRequestErasureAsync(account, "hash", Now, Now, default);
        await using var db = database.Open();
        // The last step fails: everything before it must come back.
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION no_update() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''stop''; END'");
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER no_update BEFORE UPDATE ON account_deletions FOR EACH ROW EXECUTE FUNCTION no_update()");
        await Should.ThrowAsync<StoreUnavailableException>(() => accounts.EraseAsync(account, Now, default));
        (await db.Accounts.CountAsync(), await db.Characters.CountAsync(), await db.CharacterStates.CountAsync(), await db.Items.CountAsync()).ShouldBe((1, 1, 1, 1));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER no_update ON account_deletions");
        (await accounts.EraseAsync(account, Now, default)).ShouldBe(EraseOutcome.Erased);
        (await db.Accounts.CountAsync(), await db.Characters.CountAsync(), await db.CharacterStates.CountAsync(), await db.Items.CountAsync()).ShouldBe((0, 0, 0, 0));
    }

    [DatabaseFact]
    public async Task An_item_given_to_somebody_else_stays_theirs()
    {
        var (account, character, _, _) = await AccountAsync(Accounts);
        var (_, other, _, _) = await AccountAsync(Accounts);
        var run = await Characters.StartRunAsync(default);
        var giver = await PlayedAsync(Characters, character, run, leave: false);
        var taker = await PlayedAsync(Characters, other, run, leave: false);
        await using var db = Database.Open();
        var given = await db.Items.AsNoTracking().SingleAsync(i => i.CharacterId == character);
        var give = Save(giver, final: true);
        give = give with { State = give.State with { Items = [] } };
        var take = Save(taker, final: true);
        take = take with { State = take.State with { Items = [new InventoryEntry(given.Id, given.ItemId, given.Quantity, null)] } };
        (await Characters.SaveAsync([give, take], default)).ShouldBeOfType<SaveResult.Saved>();
        await Accounts.TryRequestErasureAsync(account, "hash", Now, Now, default);
        (await Accounts.EraseAsync(account, Now, default)).ShouldBe(EraseOutcome.Erased);
        (await db.Items.AsNoTracking().SingleAsync(i => i.Id == given.Id)).CharacterId.ShouldBe(other);
    }
}
