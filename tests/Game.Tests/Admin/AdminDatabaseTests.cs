using System.Text.Json;
using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Server.Persistence;
using Game.Server.World.Saving;
using Game.Shared.Accounts;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Network;
using Game.Tests.Saving;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Tests.Admin;

/// <summary>T-804 against a real PostgreSQL: the audit log is append-only for the server's own role, a write and its row are one transaction, the lookups, erasure, and kick end to end.</summary>
[Trait("Category", "Database")]
public sealed class AdminDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private GameDatabase Database => fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set.");

    private PostgresAdminStore Store => new(Database);

    private PostgresAccountStore Accounts => new(Database);

    private static readonly StaffIdentity Robin = new("oidc|robin", "Robin Q", StaffRoles.Support);

    private static AuditDraft Draft(string target, StaffIdentity? staff = null, string reason = "a reason", DateTime? at = null, string action = AdminActions.Kick) =>
        new(staff ?? Robin, "req-" + Guid.NewGuid().ToString("N")[..8], action, AdminTargetKinds.Account, target, "{\"online\":true}", "{\"online\":false}", reason, at ?? Now);

    private async Task<Guid> AccountAsync(string? username = null, int sessions = 0)
    {
        var id = Guid.NewGuid();
        (await Accounts.TryCreateAccountAsync(id, username ?? "u" + Guid.NewGuid().ToString("N")[..16], "hash", Now, default)).ShouldBeTrue();
        for (var i = 0; i < sessions; i++)
            (await Accounts.TryAddSessionAsync(id, "hash", null, SessionTokens.HashOf(SessionTokens.New())!, Now, Now.AddDays(1), 10, default)).ShouldNotBeNull();
        return id;
    }

    // ---- append-only ----

    [DatabaseFact]
    public async Task Audit_rows_can_be_added_but_never_changed_deleted_or_truncated()
    {
        var target = Guid.NewGuid().ToString();
        var id = await Store.AppendAuditAsync(Draft(target), default);
        await using var db = Database.Open();
        foreach (var sql in new[]
                 {
                     $"UPDATE admin_audit SET reason = 'rewritten' WHERE id = {id}", "UPDATE admin_audit SET after = before", $"DELETE FROM admin_audit WHERE id = {id}",
                     "DELETE FROM admin_audit", "TRUNCATE admin_audit", "TRUNCATE admin_audit, admin_staff",
                 })
        {
            var e = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql), sql);
            e.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
            e.MessageText.ShouldContain("append-only");
        }
        var row = await db.Audit.SingleAsync(a => a.Id == id);
        row.Reason = "changed through EF";
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        (await db.Audit.AsNoTracking().SingleAsync(a => a.Id == id)).Reason.ShouldBe("a reason");
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"DELETE FROM admin_staff WHERE id = {Robin.Id}"), "a staff row named by the log stays");
        var current = await db.Database.SqlQuery<string>($"SELECT current_user AS \"Value\"").SingleAsync();
        var owner = await db.Database.SqlQuery<string>($"SELECT tableowner AS \"Value\" FROM pg_tables WHERE tablename = 'admin_audit'").SingleAsync();
        owner.ShouldBe(current, "the server's own role owns the table, and still cannot change a row: the triggers hold for the owner too");
    }

    [DatabaseFact]
    public async Task The_migration_has_the_audit_tables_and_their_triggers()
    {
        await using var db = Database.Open();
        db.Database.HasPendingModelChanges().ShouldBeFalse();
        var triggers = await db.Database.SqlQuery<string>($"SELECT tgname AS \"Value\" FROM pg_trigger WHERE tgrelid = 'admin_audit'::regclass AND NOT tgisinternal ORDER BY tgname").ToListAsync();
        triggers.ShouldBe(["admin_audit_no_change", "admin_audit_no_truncate"]);
        var columns = await db.Database.SqlQuery<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'admin_audit' ORDER BY ordinal_position").ToListAsync();
        columns.ShouldBe(["id", "at", "request_id", "staff_id", "staff_role", "action", "target_kind", "target_id", "before", "after", "reason"]);
        await Should.ThrowAsync<StoreUnavailableException>(() => Store.AppendAuditAsync(Draft("x", reason: "  "), default), "the database refuses an empty reason too");
    }

    // ---- writes and their rows ----

    [DatabaseFact]
    public async Task Ending_the_sessions_and_its_row_are_one_transaction()
    {
        var account = await AccountAsync(sessions: 3);
        var other = await AccountAsync(sessions: 1);
        var ended = await Store.EndSessionsAsync(account, n => Draft(account.ToString()) with { After = $"{{\"sessions_ended\":{n}}}" }, default);
        ended!.TokenHashes.Count.ShouldBe(3);
        await using var db = Database.Open();
        (await db.Sessions.CountAsync(s => s.AccountId == account), await db.Sessions.CountAsync(s => s.AccountId == other)).ShouldBe((0, 1));
        var row = await db.Audit.AsNoTracking().SingleAsync(a => a.Id == ended.AuditId);
        JsonDocument.Parse(row.After).RootElement.GetProperty("sessions_ended").GetInt32().ShouldBe(3);

        // A row the database refuses takes the change back with it.
        var again = await AccountAsync(sessions: 2);
        await Should.ThrowAsync<StoreUnavailableException>(() => Store.EndSessionsAsync(again, _ => Draft(again.ToString(), reason: " "), default));
        (await db.Sessions.CountAsync(s => s.AccountId == again)).ShouldBe(2);
        (await db.Audit.CountAsync(a => a.TargetId == again.ToString())).ShouldBe(0);

        var before = await db.Audit.CountAsync();
        (await Store.EndSessionsAsync(Guid.NewGuid(), _ => Draft("nobody"), default)).ShouldBeNull();
        (await db.Audit.CountAsync()).ShouldBe(before, "no account, no change, no row");
    }

    [DatabaseFact]
    public async Task The_staff_row_follows_the_panel_and_each_row_keeps_its_role()
    {
        var staff = new StaffIdentity("oidc|" + Guid.NewGuid().ToString("N")[..8], "First Name", StaffRoles.Support);
        var target = Guid.NewGuid().ToString();
        await Store.AppendAuditAsync(Draft(target, staff, at: Now), default);
        await Store.AppendAuditAsync(Draft(target, staff with { Name = "Second Name", Role = StaffRoles.Gm }, at: Now.AddMinutes(1)), default);
        await using var db = Database.Open();
        var row = await db.Staff.AsNoTracking().SingleAsync(s => s.Id == staff.Id);
        (row.DisplayName, row.Role, row.FirstSeenAt, row.LastSeenAt).ShouldBe(("Second Name", StaffRoles.Gm, Now, Now.AddMinutes(1)));
        var page = await Store.SearchAuditAsync(new AuditQuery(target, null, null, null, null, null, 10), default);
        page.Items.Select(a => a.StaffRole).ShouldBe([StaffRoles.Gm, StaffRoles.Support], "newest first, each with the role it was made with");
    }

    [DatabaseFact]
    public async Task The_audit_log_is_searched_by_target_staff_action_and_time_a_page_at_a_time()
    {
        var target = Guid.NewGuid().ToString();
        var other = new StaffIdentity("oidc|" + Guid.NewGuid().ToString("N")[..8], "Kim", StaffRoles.Admin);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++) ids.Add(await Store.AppendAuditAsync(Draft(target, i % 2 == 0 ? Robin : other, at: Now.AddHours(i)), default));
        await Store.AppendAuditAsync(Draft(Guid.NewGuid().ToString()), default);

        var first = await Store.SearchAuditAsync(new AuditQuery(target, null, null, null, null, null, 2), default);
        first.Items.Select(a => a.Id).ShouldBe([ids[4], ids[3]]);
        first.Next.ShouldBe(ids[3].ToString());
        var second = await Store.SearchAuditAsync(new AuditQuery(target, null, null, null, null, long.Parse(first.Next!), 2), default);
        second.Items.Select(a => a.Id).ShouldBe([ids[2], ids[1]]);
        var last = await Store.SearchAuditAsync(new AuditQuery(target, null, null, null, null, ids[1], 2), default);
        (last.Items.Single().Id, last.Next).ShouldBe((ids[0], (string?)null));

        (await Store.SearchAuditAsync(new AuditQuery(target, other.Id, null, null, null, null, 10), default)).Items.Select(a => a.Id).ShouldBe([ids[3], ids[1]]);
        (await Store.SearchAuditAsync(new AuditQuery(target, null, null, Now.AddHours(1), Now.AddHours(3), null, 10), default)).Items.Select(a => a.Id).ShouldBe([ids[2], ids[1]]);
        (await Store.SearchAuditAsync(new AuditQuery(target, null, "ban", null, null, null, 10), default)).Items.ShouldBeEmpty();
        var row = (await Store.SearchAuditAsync(new AuditQuery(null, other.Id, AdminActions.Kick, null, null, null, 1), default)).Items.Single();
        (row.StaffName, row.StaffRole, row.Action, row.TargetKind, row.TargetId, row.Reason).ShouldBe(("Kim", "admin", "kick", "account", target, "a reason"));
        JsonDocument.Parse(row.Before).RootElement.GetProperty("online").GetBoolean().ShouldBeTrue();
    }

    // ---- reads ----

    [DatabaseFact]
    public async Task Accounts_are_found_by_prefix_or_id_with_counts_and_no_hash()
    {
        var tag = "t" + Guid.NewGuid().ToString("N")[..6];
        var a = await AccountAsync(tag + "_a", sessions: 2);
        var b = await AccountAsync(tag + "_b");
        await AccountAsync(tag + "xc");   // '_' is not a wildcard
        var name = new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));
        await Accounts.CreateCharacterAsync(a, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.x", 3, null, Now, default);

        var page = await Store.FindAccountsAsync(new AccountQuery(tag + "_", null, null, 1), default);
        (page.Items.Single().Id, page.Next).ShouldBe((a, tag + "_a"));
        var rest = await Store.FindAccountsAsync(new AccountQuery(tag + "_", null, page.Next, 1), default);
        (rest.Items.Single().Id, rest.Next).ShouldBe((b, (string?)null));
        var one = (await Store.FindAccountsAsync(new AccountQuery(null, a, null, 5), default)).Items.Single();
        (one.Username, one.Characters, one.Sessions).ShouldBe((tag + "_a", 1, 2));
        typeof(AdminAccount).GetProperties().Select(p => p.Name).ShouldNotContain(n => n.Contains("Hash") || n.Contains("Token"));
        var detail = await Store.GetAccountAsync(a, default);
        detail!.Value.Characters.Single().Name.ShouldBe(name);
        (await Store.GetAccountAsync(Guid.NewGuid(), default)).ShouldBeNull();
    }

    [DatabaseFact]
    public async Task Characters_are_found_and_one_shows_its_saved_state()
    {
        var account = await AccountAsync();
        var tag = new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 8));
        var ids = new List<Guid>();
        foreach (var suffix in new[] { "a", "b" })
        {
            var (_, made) = await Accounts.CreateCharacterAsync(account, Guid.NewGuid(), "Q" + tag + suffix, CharacterNameRules.Key("Q" + tag + suffix), "job.x", 3, null, Now, default);
            ids.Add(made!.Id);
        }
        await Accounts.DeleteCharacterAsync(account, ids[1], Now, default);
        var page = await Store.FindCharactersAsync(new CharacterQuery("q" + tag, null, null, null, 10), default);
        page.Items.Select(c => c.Id).ShouldBe(ids);
        page.Items[1].DeletedAt.ShouldBe(Now, "deleted ones too: staff see them");
        (await Store.FindCharactersAsync(new CharacterQuery(null, null, account, null, 1), default)).Next.ShouldNotBeNull();

        (await Store.GetCharacterAsync(ids[0], default))!.State.ShouldBeNull("never saved");
        var characters = new PostgresCharacterStore(Database);
        var run = await characters.StartRunAsync(default);
        var claim = await characters.ClaimAsync(ids[0], run, default);
        var ticket = new CharacterTicket(ids[0]) { Claim = claim.Claim, TakenVersion = claim.Version, LandedVersion = claim.Version };
        long next = 9_000_000 + Random.Shared.Next(1_000_000) * 100L;
        var full = SavedContent.Full(() => ++next, Now);
        (await characters.SaveAsync([new CharacterSave(ticket, ++ticket.TakenVersion, full, true, [])], default)).ShouldBeOfType<SaveResult.Saved>();
        var detail = (await Store.GetCharacterAsync(ids[0], default))!;
        var state = detail.State!;
        (state.Version, state.MapId, state.BaseLevel, state.Currency).ShouldBe((1, full.MapId, full.BaseLevel, full.Currency));
        state.Items.Count.ShouldBe(full.Items.Count + full.AsideItems.Count, "the bag and what is set aside");
        JsonDocument.Parse(state.Stats).RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        await characters.EndRunAsync(run, default);
    }

    // ---- erasure ----

    [DatabaseFact]
    public async Task Erasing_an_account_leaves_its_audit_rows_which_hold_nothing_personal()
    {
        var username = "erase" + Guid.NewGuid().ToString("N")[..8];
        var account = await AccountAsync(username, sessions: 1);
        var ended = await Store.EndSessionsAsync(account, n => Draft(account.ToString()), default);
        (await Accounts.TryRequestErasureAsync(account, "hash", Now, Now, default)).ShouldNotBeNull();
        (await Accounts.EraseAsync(account, Now, default)).ShouldBe(EraseOutcome.Erased);
        await using var db = Database.Open();
        (await db.Accounts.CountAsync(a => a.Id == account)).ShouldBe(0);
        var row = await db.Audit.AsNoTracking().SingleAsync(a => a.Id == ended!.AuditId);
        row.TargetId.ShouldBe(account.ToString(), "an id that now leads nowhere");
        (row.Before + row.After + row.TargetId).ShouldNotContain(username);
    }

    // ---- kick end to end ----

    [DatabaseFact]
    public async Task Kick_with_the_database_ends_the_game_and_writes_its_row()
    {
        var accounts = new PostgresAccountStore(Database);
        await using var rig = await AdminRig.StartAsync(store: Store, accounts: new AccountRigOptions(accounts, new FakeHasher(), new AccountLimits(), new FakeTime { Now = DateTimeOffset.UtcNow }));
        var username = "kick" + Guid.NewGuid().ToString("N")[..8];
        var session = (await rig.Game.Accounts!.RegisterAsync(username, ServiceRig.Password, "a", default)).Value!.Token;
        var character = (await rig.Game.Accounts.CreateAsync(session, "K" + new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 9)), default)).Value!.Id;
        var client = await rig.Game.PlayAsync(session, character);
        await client.WaitForAsync<WelcomeMessage>();

        var (_, found, _) = await rig.Http.GetAsync($"{AdminPaths.Accounts}?name={username}");
        var account = found.GetProperty("items").EnumerateArray().Single();
        account.GetProperty("online").GetBoolean().ShouldBeTrue();
        var id = account.GetProperty("id").GetGuid();
        var (status, body, _) = await rig.Http.KickAsync(id, "end to end");
        status.ShouldBe(200, body.ToString());
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Kicked);
        (await (await rig.Game.PlayAsync(session, character)).WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused);

        var (_, audit, _) = await rig.Http.GetAsync($"{AdminPaths.Audit}?target={id}");
        var row = audit.GetProperty("items").EnumerateArray().Single();
        (row.GetProperty("id").GetInt64(), row.GetProperty("reason").GetString(), row.GetProperty("staff_name").GetString(), row.GetProperty("after").GetProperty("sessions_ended").GetInt32())
            .ShouldBe((body.GetProperty("audit_id").GetInt64(), "end to end", "Robin Q", 1));
        var (_, detail, _) = await rig.Http.GetAsync($"{AdminPaths.Characters}/{character}");
        detail.GetProperty("character").GetProperty("id").GetString().ShouldBe(character);
        detail.GetRawText().ShouldNotContain("password");
    }
}
