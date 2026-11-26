using System.Text.Json;
using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Server.Persistence;
using Game.Server.World.Saving;
using Game.Shared.Accounts;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Network;
using Game.Tests.Saving;
using Microsoft.EntityFrameworkCore;

namespace Game.Tests.Admin;

/// <summary>
/// T-806 against a real PostgreSQL: bans at sign-in, at <c>hello</c> and live (also from another process, through the ban watch), mutes, item and currency fixes in the world and out of it
/// with saves racing them, unstick, the support queue and its export, erasure, and the log.
/// </summary>
[Trait("Category", "Database")]
public sealed class ModerationDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private static long _lastItem = 7_000_000_000 + Random.Shared.Next(1_000_000) * 1_000L;

    private GameDatabase Database => fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set.");

    private PostgresModerationStore Moderation => new(Database);

    private static async Task EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, int timeoutMs = 15_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        T last;
        while (!done(last = await read()))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(what + ": still " + last);
            await Task.Delay(25);
        }
    }

    private static string Tag() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));

    private Task<AdminRig> RigAsync(int saveWaitMs = 10_000) => AdminRig.StartAsync(store: new PostgresAdminStore(Database),
        accounts: new AccountRigOptions(new PostgresAccountStore(Database), new FakeHasher(), new AccountLimits(), new FakeTime { Now = DateTimeOffset.UtcNow })
        {
            Characters = new PostgresCharacterStore(Database), SaveOptions = new CharacterSaveOptions { SpacingMs = 20 },
        },
        commands: AdminCommandTable.AllCommands(), moderation: Moderation, saveWaitMs: saveWaitMs);

    private sealed record Player(string Username, string Session, Guid Account, Guid Character);

    private static async Task<Player> RegisterAsync(AdminRig rig)
    {
        var username = "m" + Tag();
        var session = (await rig.Game.Accounts!.RegisterAsync(username, ServiceRig.Password, "a", default)).Value!.Token;
        var character = (await rig.Game.Accounts.CreateAsync(session, "M" + Tag(), default)).Value!.Id;
        var account = (await rig.Store.FindAccountsAsync(new AccountQuery(username, null, null, 1), default)).Items.Single().Id;
        return new Player(username, session, account, Guid.Parse(character));
    }

    private async Task SavedAsync(Guid character, long above = 0)
    {
        await EventuallyAsync(async () =>
        {
            await using var db = Database.Open();
            return await db.CharacterStates.AsNoTracking().Where(s => s.CharacterId == character).Select(s => s.Version).SingleOrDefaultAsync();
        }, v => v > above, "a save of the character landed");
    }

    private async Task<(long Version, long Claim, long? HeldBy)> StateAsync(Guid character)
    {
        await using var db = Database.Open();
        var s = await db.CharacterStates.AsNoTracking().SingleAsync(r => r.CharacterId == character);
        return (s.Version, s.Claim, s.HeldBy);
    }

    private static Dictionary<string, object> Body(params (string Key, object Value)[] fields)
    {
        var body = new Dictionary<string, object> { ["reason"] = "support ticket 7" };
        foreach (var (key, value) in fields) body[key] = value;
        return body;
    }

    private static ItemDef Material(AdminRig rig) => rig.Game.Content.Items.All.First(i => i.Kind == ItemKind.Material && i.MaxStack > 1);

    // ---- bans ----

    [DatabaseFact]
    public async Task A_ban_ends_the_game_and_is_refused_at_sign_in_and_hello_until_it_is_lifted()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        using var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString());
        await client.WaitForAsync<WelcomeMessage>();
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);

        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/ban", Body(("account_id", player.Account.ToString()), ("minutes", 120)));
        (status, body.GetProperty("outcome").GetString()).ShouldBe((200, AdminOutcomes.Accepted), body.ToString());
        body.GetProperty("result").GetProperty("sessions_ended").GetInt32().ShouldBe(1);
        await client.WaitClosedAsync();
        var bye = client.Messages.OfType<ByeMessage>().Single();
        (bye.Reason, bye.RetryAfterSeconds).ShouldBe((ByeReasons.Banned, (long?)7_200));
        client.Messages.OfType<ByeMessage>().ShouldNotContain(b => b.ToString().Contains("support ticket"), "staff's reason is never shown");
        await Poll.UntilAsync(() => rig.Game.Gateway.CharacterCount, n => n == 0, "the character left");

        var signIn = await rig.Game.Accounts!.LoginAsync(player.Username, ServiceRig.Password, "a", default);
        (signIn.Error, signIn.RetryAfterMs / 1000).ShouldBe((AccountErrors.AccountBanned, 7_200));
        (await rig.Game.Accounts.LoginAsync(player.Username, "wrong password, wrong", "a", default)).Error.ShouldBe(AccountErrors.InvalidCredentials, "told only to who has the password");

        // A session that exists all the same (made on another process before the ban reached it) plays nothing.
        var token = SessionTokens.New();
        (await new PostgresAccountStore(Database).TryAddSessionAsync(player.Account, (await new PostgresAccountStore(Database).FindAccountAsync(player.Account, default))!.PasswordHash, null,
            SessionTokens.HashOf(token)!, DateTime.UtcNow, DateTime.UtcNow.AddHours(1), 10, default)).ShouldNotBeNull();
        using var again = await rig.Game.PlayAsync(token, player.Character.ToString());
        (await again.WaitForByeAsync()).ShouldBe(ByeReasons.Banned);
        again.Messages.OfType<ByeMessage>().Single().RetryAfterSeconds.ShouldBe(7_200);

        (await rig.Http.PostAsync(AdminPaths.Commands + "/ban", Body(("account_id", player.Account.ToString())))).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.AlreadyBanned);
        var (_, moderation, _) = await rig.Http.GetAsync($"{AdminPaths.Accounts}/{player.Account}{AdminPaths.ModerationSuffix}");
        moderation.GetProperty("banned").GetBoolean().ShouldBeTrue();
        moderation.GetProperty("bans").EnumerateArray().Single().GetProperty("in_force").GetBoolean().ShouldBeTrue();

        var (liftStatus, lifted, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/unban", Body(("account_id", player.Account.ToString())));
        (liftStatus, lifted.GetProperty("outcome").GetString()).ShouldBe((200, AdminOutcomes.Done));
        (await rig.Http.PostAsync(AdminPaths.Commands + "/unban", Body(("account_id", player.Account.ToString())))).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.NotBanned);
        var back = await rig.Game.Accounts.LoginAsync(player.Username, ServiceRig.Password, "a", default);
        back.Ok.ShouldBeTrue(back.Error);
        using var playing = await rig.Game.PlayAsync(back.Value!.Token, player.Character.ToString());
        await playing.WaitForAsync<WelcomeMessage>();

        var audit = await rig.Store.SearchAuditAsync(new AuditQuery(player.Account.ToString(), null, null, null, null, null, 10), default);
        audit.Items.Select(a => a.Action).ShouldBe([AdminActions.Unban, AdminActions.Ban]);
        (audit.Items[1].Before + audit.Items[1].After).ShouldNotContain(player.Username);
    }

    [DatabaseFact]
    public async Task A_permanent_ban_has_no_end_and_holds_for_a_new_store_as_after_a_restart()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);
        var (_, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/ban", Body(("account_id", player.Account.ToString())));
        body.GetProperty("outcome").GetString().ShouldBe(AdminOutcomes.Done, "nobody was playing");
        var signIn = await rig.Game.Accounts!.LoginAsync(player.Username, ServiceRig.Password, "a", default);
        (signIn.Error, signIn.RetryAfterMs).ShouldBe((AccountErrors.AccountBanned, 0L));
        (await new PostgresModerationStore(Database).ActiveSanctionAsync(player.Account, SanctionKinds.Ban, DateTime.UtcNow.AddYears(50), default))!.Until.ShouldBeNull();
        (await Moderation.AddSanctionAsync(Guid.NewGuid(), SanctionKinds.Ban, null, Now, null!, default)).Error.ShouldBe(AdminErrors.UnknownTarget);
    }

    [DatabaseFact]
    public async Task A_ban_made_through_another_process_ends_the_game_here_at_the_next_watch()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        using var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString());
        await client.WaitForAsync<WelcomeMessage>();
        // Another process: the ban is in the database, and this gateway never heard of it.
        var draft = new AuditDraft(new StaffIdentity("oidc|other", "Other", StaffRoles.Gm), "req-elsewhere", AdminActions.Ban, AdminTargetKinds.Account, player.Account.ToString(), "{}", "{}", "elsewhere",
            DateTime.UtcNow);
        (await Moderation.AddSanctionAsync(player.Account, SanctionKinds.Ban, null, DateTime.UtcNow, draft, default)).Error.ShouldBeNull();
        await Task.Delay(200);
        client.Messages.OfType<ByeMessage>().ShouldBeEmpty("this process ends it only when it looks");
        (await BanWatch.SweepAsync(Moderation, rig.Game.Gateway, TimeProvider.System, rig.Log, default)).ShouldBe(1);
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Banned);
        (await BanWatch.SweepAsync(Moderation, rig.Game.Gateway, TimeProvider.System, rig.Log, default)).ShouldBe(0, "nobody banned plays any more");
    }

    // ---- mutes ----

    [DatabaseFact]
    public async Task A_mute_is_what_the_speak_check_answers_until_it_is_lifted()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        var checks = rig.Checks!;
        (await checks.MaySpeakAsync(player.Account, default)).ShouldBeTrue();
        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/mute", Body(("account_id", player.Account.ToString()), ("minutes", 30)));
        (status, body.GetProperty("outcome").GetString()).ShouldBe((200, AdminOutcomes.Done), body.ToString());
        (await checks.MaySpeakAsync(player.Account, default)).ShouldBeFalse();
        (await rig.Http.PostAsync(AdminPaths.Commands + "/mute", Body(("account_id", player.Account.ToString()), ("minutes", 5)))).Body.GetProperty("error").GetString()
            .ShouldBe(AdminErrors.AlreadyMuted);
        var (_, moderation, _) = await rig.Http.GetAsync($"{AdminPaths.Accounts}/{player.Account}{AdminPaths.ModerationSuffix}");
        (moderation.GetProperty("muted").GetBoolean(), moderation.GetProperty("banned").GetBoolean()).ShouldBe((true, false));
        (await rig.Game.Accounts!.LoginAsync(player.Username, ServiceRig.Password, "a", default)).Ok.ShouldBeTrue("a mute is not a ban");
        (await rig.Http.PostAsync(AdminPaths.Commands + "/unmute", Body(("account_id", player.Account.ToString())))).Status.ShouldBe(200);
        (await checks.MaySpeakAsync(player.Account, default)).ShouldBeTrue();
        (await Moderation.ActiveSanctionAsync(player.Account, SanctionKinds.Mute, DateTime.UtcNow, default)).ShouldBeNull();
        var other = new ModerationChecks(Moderation, new FakeTime { Now = DateTimeOffset.UtcNow.AddMinutes(31) });
        await rig.Http.PostAsync(AdminPaths.Commands + "/mute", Body(("account_id", player.Account.ToString()), ("minutes", 30)));
        (await other.MaySpeakAsync(player.Account, default)).ShouldBeTrue("a mute ends by itself");
    }

    // ---- items and currency in the world ----

    [DatabaseFact]
    public async Task Items_given_in_the_world_are_new_entries_marked_admin_and_land_with_their_row_in_the_save()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        using var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString());
        await client.WaitForAsync<WelcomeMessage>();
        await SavedAsync(player.Character);
        var item = Material(rig);
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);

        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/give_item",
            Body(("character_id", player.Character.ToString()), ("item_id", item.Id), ("quantity", item.MaxStack + 1)));
        (status, body.GetProperty("outcome").GetString()).ShouldBe((200, AdminOutcomes.Done), body.ToString());
        var auditId = body.GetProperty("audit_id").GetInt64();
        var ids = body.GetProperty("result").GetProperty("entry_ids").GetString()!.Split(',').Select(long.Parse).ToList();
        ids.Count.ShouldBe(2, "more than a stack: two new entries");
        var change = await client.WaitForAsync<InventoryChangedMessage>(m => m.Cause == InventoryChanges.Staff);
        change.Entries.Select(e => e.Id).Order().ShouldBe(ids.Order());

        await using (var db = Database.Open())
        {
            (await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => i.CharacterId).Distinct().SingleAsync()).ShouldBe(player.Character, "the save that has them landed");
            var marks = await db.AdminItems.AsNoTracking().Where(a => ids.Contains(a.ItemInstanceId)).ToListAsync();
            marks.Select(m => (m.AuditId, m.ItemId)).Distinct().Single().ShouldBe((auditId, item.Id));
            marks.Sum(m => m.Quantity).ShouldBe(item.MaxStack + 1);
            var row = await db.Audit.AsNoTracking().SingleAsync(a => a.Id == auditId);
            (row.Action, row.TargetId).ShouldBe((AdminActions.GiveItem, player.Character.ToString()));
            JsonDocument.Parse(row.After).RootElement.GetProperty("carried").GetInt64().ShouldBe(item.MaxStack + 1);
            (await db.Audit.CountAsync(a => a.TargetId == player.Character.ToString())).ShouldBe(1, "one row, however many saves follow");
        }

        // Take one unit of the first, then the whole second: in the world.
        var (removed, removeBody, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/remove_item", Body(("character_id", player.Character.ToString()), ("entry_id", ids[1]), ("quantity", 1)));
        (removed, removeBody.GetProperty("outcome").GetString()).ShouldBe((200, AdminOutcomes.Done), removeBody.ToString());
        (await client.WaitForAsync<InventoryChangedMessage>(m => m.Cause == InventoryChanges.Staff && m.Removed.Contains(ids[1]))).ShouldNotBeNull();
        (await rig.Http.PostAsync(AdminPaths.Commands + "/remove_item", Body(("character_id", player.Character.ToString()), ("entry_id", ids[1]), ("quantity", 1)))).Body
            .GetProperty("error").GetString().ShouldBe(AdminErrors.ItemGone);
        (await rig.Http.PostAsync(AdminPaths.Commands + "/remove_item", Body(("character_id", player.Character.ToString()), ("entry_id", ids[0]), ("quantity", 100_000)))).Body
            .GetProperty("error").GetString().ShouldBe(AdminErrors.NotEnough);

        // Currency, and its refusal.
        var (paid, paidBody, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/change_currency", Body(("character_id", player.Character.ToString()), ("amount", 250)));
        (paid, paidBody.GetProperty("result").GetProperty("currency").GetInt64()).ShouldBe((200, 250L), paidBody.ToString());
        (await rig.Http.PostAsync(AdminPaths.Commands + "/change_currency", Body(("character_id", player.Character.ToString()), ("amount", -251)))).Body
            .GetProperty("error").GetString().ShouldBe(AdminErrors.NotEnough);
        (await client.WaitForAsync<InventoryChangedMessage>(m => m.Currency == 250)).ShouldNotBeNull();

        var (_, history, _) = await rig.Http.GetAsync($"{AdminPaths.Characters}/{player.Character}{AdminPaths.StaffChangesSuffix}");
        history.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("action").GetString()).ShouldBe([AdminActions.ChangeCurrency, AdminActions.RemoveItem, AdminActions.GiveItem]);
    }

    [DatabaseFact]
    public async Task A_worn_entry_is_taken_off_before_it_is_removed()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        using var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString());
        var inventory = await client.WaitForAsync<InventoryMessage>();
        var worn = inventory.Entries.First(e => e.Slot is not null);
        await SavedAsync(player.Character);
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);
        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/remove_item", Body(("character_id", player.Character.ToString()), ("entry_id", worn.Id), ("quantity", 1)));
        status.ShouldBe(200, body.ToString());
        var row = (await rig.Store.SearchAuditAsync(new AuditQuery(player.Character.ToString(), null, AdminActions.RemoveItem, null, null, null, 1), default)).Items.Single();
        JsonDocument.Parse(row.Before).RootElement.GetProperty("worn").GetString().ShouldBe(worn.Slot);
        await using var db = Database.Open();
        (await db.Items.AnyAsync(i => i.Id == worn.Id)).ShouldBeFalse();
    }

    [DatabaseFact]
    public async Task A_change_whose_save_is_slow_is_accepted_and_its_row_lands_with_the_save()
    {
        await using var rig = await RigAsync(saveWaitMs: 1);
        var player = await RegisterAsync(rig);
        using var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString());
        await client.WaitForAsync<WelcomeMessage>();
        await SavedAsync(player.Character);
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);
        var (_, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/change_currency", Body(("character_id", player.Character.ToString()), ("amount", 5)));
        body.GetProperty("outcome").GetString().ShouldBe(AdminOutcomes.Accepted, body.ToString());
        body.GetProperty("result").GetProperty("saving").GetString().ShouldBe("pending");
        await EventuallyAsync(async () =>
        {
            await using var db = Database.Open();
            return await db.Audit.CountAsync(a => a.TargetId == player.Character.ToString());
        }, n => n == 1, "the row landed with the save");
    }

    [DatabaseFact]
    public async Task A_staff_note_is_written_once_whichever_save_lands_it_and_never_without_its_save()
    {
        var characters = new PostgresCharacterStore(Database);
        var accounts = new PostgresAccountStore(Database);
        var account = Guid.NewGuid();
        (await accounts.TryCreateAccountAsync(account, "n" + Tag(), "hash", Now, default)).ShouldBeTrue();
        var name = "N" + Tag();
        var (_, made) = await accounts.CreateCharacterAsync(account, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.x", 3, null, Now, default);
        var run = await characters.StartRunAsync(default);
        var claim = await characters.ClaimAsync(made!.Id, run, default);
        var ticket = new CharacterTicket(made.Id) { Claim = claim.Claim, TakenVersion = claim.Version, LandedVersion = claim.Version };
        CharacterSave Copy(params StaffNote[] notes) => new(ticket, ++ticket.TakenVersion, SavedContent.Full(() => Interlocked.Increment(ref _lastItem), Now), false, [], notes);
        (await characters.SaveAsync([Copy()], default)).ShouldBeOfType<SaveResult.Saved>();

        var given = Interlocked.Increment(ref _lastItem);
        var note = new StaffNote(new AuditDraft(new StaffIdentity("oidc|gm", "Gale", StaffRoles.Gm), "req-note", AdminActions.GiveItem, AdminTargetKinds.Character, made.Id.ToString(), "{}",
            "{}", "the note", Now), [(given, "item.x", 1)]) { Version = ticket.TakenVersion + 1 };
        // A save that fails after it wrote everything (here: a duplicate item makes it roll back) writes no note either.
        var other = Interlocked.Increment(ref _lastItem);
        await using (var db = Database.Open())
        {
            var (_, someone) = await accounts.CreateCharacterAsync(account, Guid.NewGuid(), "O" + Tag(), CharacterNameRules.Key("O" + Tag()), "job.x", 3, null, Now, default);
            db.Items.Add(new ItemRow { Id = other, CharacterId = someone!.Id, ItemId = "item.x", Quantity = 1 });
            await db.SaveChangesAsync();
        }
        var bad = new CharacterSave(ticket, ticket.TakenVersion + 1, SavedContent.Full(() => Interlocked.Increment(ref _lastItem), Now) with { Items = [new InventoryEntry(other, "item.x", 1)] },
            false, [], [note]);
        (await characters.SaveAsync([bad], default)).ShouldBeOfType<SaveResult.Duplicated>();
        await using (var db = Database.Open()) (await db.Audit.CountAsync(a => a.RequestId == "req-note")).ShouldBe(0, "rolled back with its save");

        var first = Copy(note);
        (await characters.SaveAsync([first], default)).ShouldBeOfType<SaveResult.Saved>();
        (await characters.SaveAsync([first], default)).ShouldBeOfType<SaveResult.Saved>("a second try of a save that landed");
        (await characters.SaveAsync([Copy(note)], default)).ShouldBeOfType<SaveResult.Saved>("a later save that still carries it");
        note.AuditId.ShouldBeGreaterThan(0);
        await using (var db = Database.Open())
        {
            (await db.Audit.CountAsync(a => a.RequestId == "req-note")).ShouldBe(1);
            (await db.AdminItems.AsNoTracking().SingleAsync(a => a.ItemInstanceId == given)).AuditId.ShouldBe(note.AuditId);
        }
        await characters.EndRunAsync(run, default);
    }

    // ---- items, currency and place out of the world ----

    [DatabaseFact]
    public async Task Out_of_the_world_a_fix_is_made_in_the_database_and_the_next_login_has_it()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);
        var item = Material(rig);
        var give = Body(("character_id", player.Character.ToString()), ("item_id", item.Id), ("quantity", 3));
        (await rig.Http.PostAsync(AdminPaths.Commands + "/give_item", give)).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.NeverSaved);

        using (var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString()))
        {
            await client.WaitForAsync<WelcomeMessage>();
            await SavedAsync(player.Character);
            await client.SendAsync(new LogoutMessage());
            await client.WaitForByeAsync();
        }
        await Poll.UntilAsync(() => rig.Game.Saves!.Holds(player.Character), held => !held, "the last save landed");
        var before = await StateAsync(player.Character);
        before.HeldBy.ShouldBeNull();

        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/give_item", give);
        (status, body.GetProperty("outcome").GetString()).ShouldBe((200, AdminOutcomes.Done), body.ToString());
        body.GetProperty("result").GetProperty("was_online").GetBoolean().ShouldBeFalse();
        var entry = long.Parse(body.GetProperty("result").GetProperty("entry_ids").GetString()!);
        var after = await StateAsync(player.Character);
        (after.Claim, after.Version).ShouldBe((before.Claim + 1, before.Version + 1), "a new stay: no earlier save can land after it");
        (await rig.Http.PostAsync(AdminPaths.Commands + "/change_currency", Body(("character_id", player.Character.ToString()), ("amount", 40)))).Status.ShouldBe(200);
        (await rig.Http.PostAsync(AdminPaths.Commands + "/change_currency", Body(("character_id", player.Character.ToString()), ("amount", -41)))).Body.GetProperty("error").GetString()
            .ShouldBe(AdminErrors.NotEnough);
        var (moved, movedBody, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/unstick", Body(("character_id", player.Character.ToString())));
        moved.ShouldBe(200, movedBody.ToString());
        var start = rig.Game.Content.Balance.NewCharacter;
        (movedBody.GetProperty("result").GetProperty("map").GetString(), movedBody.GetProperty("result").GetProperty("cell_x").GetInt32()).ShouldBe((start.StartMapId, start.StartX),
            "no save point: the start");
        await using (var db = Database.Open())
        {
            (await db.AdminItems.AsNoTracking().SingleAsync(a => a.ItemInstanceId == entry)).Quantity.ShouldBe(3);
            (await db.Items.AsNoTracking().SingleAsync(i => i.Id == entry)).CharacterId.ShouldBe(player.Character);
        }

        using var back = await rig.Game.PlayAsync(player.Session, player.Character.ToString());
        var inventory = await back.WaitForAsync<InventoryMessage>();
        inventory.Entries.ShouldContain(e => e.Id == entry && e.Quantity == 3 && e.Item == item.Id);
        inventory.Currency.ShouldBe(40);
    }

    [DatabaseFact]
    public async Task An_offline_fix_waits_for_a_live_holder_and_a_late_save_of_an_earlier_stay_never_undoes_it()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        // Another server's stay: it claimed and saved the character and still holds it.
        var characters = new PostgresCharacterStore(Database);
        var run = await characters.StartRunAsync(default);
        var claim = await characters.ClaimAsync(player.Character, run, default);
        var ticket = new CharacterTicket(player.Character) { Claim = claim.Claim, TakenVersion = claim.Version, LandedVersion = claim.Version };
        CharacterSave Copy() => new(ticket, ++ticket.TakenVersion, SavedContent.Full(() => Interlocked.Increment(ref _lastItem), Now) with { Currency = 1_000 }, false, []);
        (await characters.SaveAsync([Copy()], default)).ShouldBeOfType<SaveResult.Saved>();
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);
        var pay = Body(("character_id", player.Character.ToString()), ("amount", -300));
        (await rig.Http.PostAsync(AdminPaths.Commands + "/change_currency", pay)).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.CharacterBusy, "a live server holds it");

        // That server stops beating (it died): the lease runs out, and the fix goes in.
        await using (var db = Database.Open())
            await db.Database.ExecuteSqlAsync($"UPDATE server_runs SET seen_at = now() - interval '1 hour' WHERE id = {run}");
        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/change_currency", pay);
        (status, body.GetProperty("result").GetProperty("currency").GetInt64()).ShouldBe((200, 700L), body.ToString());

        // The dead stay's save, late: refused, so the fix stays.
        (await characters.SaveAsync([Copy()], default)).ShouldBeOfType<SaveResult.Lost>();
        await using (var db = Database.Open()) (await db.CharacterStates.AsNoTracking().SingleAsync(s => s.CharacterId == player.Character)).Currency.ShouldBe(700);
    }

    [DatabaseFact]
    public async Task An_offline_fix_and_a_login_never_both_happen_on_the_same_state()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        using (var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString()))
        {
            await client.WaitForAsync<WelcomeMessage>();
            await SavedAsync(player.Character);
            await client.SendAsync(new LogoutMessage());
            await client.WaitForByeAsync();
        }
        await Poll.UntilAsync(() => rig.Game.Saves!.Holds(player.Character), held => !held, "the last save landed");
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);
        // Race twenty fixes against a login: each either goes in before the claim (the login reads it) or after it (in the world, by the tick) or is refused while held; none is lost.
        var login = rig.Game.PlayAsync(player.Session, player.Character.ToString());
        var fixes = Enumerable.Range(0, 20).Select(_ => rig.Http.PostAsync(AdminPaths.Commands + "/change_currency", Body(("character_id", player.Character.ToString()), ("amount", 1)))).ToList();
        using var playing = await login;
        await playing.WaitForAsync<WelcomeMessage>();
        var answers = await Task.WhenAll(fixes);
        var made = answers.Count(a => a.Status == 200);
        answers.Where(a => a.Status != 200).ShouldAllBe(a => a.Body.GetProperty("error").GetString() == AdminErrors.CharacterBusy);
        await Task.Delay(300);
        var latest = playing.Messages.OfType<InventoryChangedMessage>().Select(m => (long?)m.Currency).LastOrDefault() ?? playing.Messages.OfType<InventoryMessage>().Last().Currency;
        latest.ShouldBe(made, "every fix that was answered 200 is in the character, once");
    }

    // ---- the support queue ----

    [DatabaseFact]
    public async Task A_deletion_from_support_takes_the_in_app_road_with_its_wait()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        using var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString());
        await client.WaitForAsync<WelcomeMessage>();
        var (status, opened, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/open_request", Body(("account_id", player.Account.ToString()), ("kind", SupportRequestKinds.Deletion)));
        status.ShouldBe(200, opened.ToString());
        var request = opened.GetProperty("result").GetProperty("request_id").GetInt64();
        var (_, queue, _) = await rig.Http.GetAsync($"{AdminPaths.Requests}?status=open&account={player.Account}");
        queue.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt64().ShouldBe(request);

        var (doneStatus, done, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/complete_request", Body(("request_id", request)));
        doneStatus.ShouldBe(200, done.ToString());
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.SessionEnded, "as the in-app request ends the game");
        var signIn = await rig.Game.Accounts!.LoginAsync(player.Username, ServiceRig.Password, "a", default);
        signIn.Error.ShouldBe(AccountErrors.AccountDeleting, "the wait: the owner can still keep it");
        signIn.RetryAfterMs.ShouldBeGreaterThan((long)TimeSpan.FromDays(13).TotalMilliseconds);
        await using (var db = Database.Open())
            (await db.AccountDeletions.AsNoTracking().SingleAsync(d => d.AccountId == player.Account)).ErasedAt.ShouldBeNull();
        (await rig.Http.PostAsync(AdminPaths.Commands + "/complete_request", Body(("request_id", request)))).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.RequestClosed);
        var (_, second, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/open_request", Body(("account_id", player.Account.ToString()), ("kind", SupportRequestKinds.Deletion)));
        (await rig.Http.PostAsync(AdminPaths.Commands + "/complete_request", Body(("request_id", second.GetProperty("result").GetProperty("request_id").GetInt64())))).Body
            .GetProperty("error").GetString().ShouldBe(AdminErrors.AccountDeleting);
        (await rig.Http.PostAsync(AdminPaths.Commands + "/cancel_request", Body(("request_id", second.GetProperty("result").GetProperty("request_id").GetInt64())))).Status.ShouldBe(200);
    }

    [DatabaseFact]
    public async Task An_export_holds_what_is_stored_about_the_account_and_no_secret()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        using (var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString()))
        {
            await client.WaitForAsync<WelcomeMessage>();
            await SavedAsync(player.Character);
        }
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);
        await rig.Http.PostAsync(AdminPaths.Commands + "/mute", Body(("account_id", player.Account.ToString()), ("minutes", 10)));
        rig.Http.Staff = ("staff-1", "Robin Q", StaffRoles.Support);
        var (_, opened, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/open_request", Body(("account_id", player.Account.ToString()), ("kind", SupportRequestKinds.Export)));
        var request = opened.GetProperty("result").GetProperty("request_id").GetInt64();
        var (status, done, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/complete_request", Body(("request_id", request)));
        status.ShouldBe(200, done.ToString());
        var export = done.GetProperty("result").GetProperty("export");
        export.GetProperty("account").GetProperty("username").GetString().ShouldBe(player.Username);
        var character = export.GetProperty("characters").EnumerateArray().Single();
        character.GetProperty("id").GetString().ShouldBe(player.Character.ToString());
        character.GetProperty("state").GetProperty("base_level").GetInt32().ShouldBeGreaterThan(0);
        character.GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);
        export.GetProperty("sanctions").EnumerateArray().Single().GetProperty("kind").GetString().ShouldBe(SanctionKinds.Mute);
        export.GetProperty("support_requests").EnumerateArray().Single().GetProperty("kind").GetString().ShouldBe(SupportRequestKinds.Export);
        export.GetProperty("staff_actions").EnumerateArray().Select(a => a.GetProperty("action").GetString()).ShouldContain(AdminActions.Mute);
        var text = export.GetRawText();
        var hash = (await new PostgresAccountStore(Database).FindAccountAsync(player.Account, default))!.PasswordHash;
        foreach (var secret in new[] { hash, player.Session, "support ticket 7", "oidc|gm", "Gale M" }) text.ShouldNotContain(secret);
        var row = (await rig.Store.SearchAuditAsync(new AuditQuery(request.ToString(), null, AdminActions.CompleteRequest, null, null, null, 1), default)).Items.Single();
        (row.Before + row.After).ShouldNotContain(player.Username, Case.Insensitive, "the row says what was done, never the data");
    }

    // ---- erasure ----

    [DatabaseFact]
    public async Task Erasing_the_account_takes_its_bans_mutes_requests_and_item_marks()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        using (var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString()))
        {
            await client.WaitForAsync<WelcomeMessage>();
            await SavedAsync(player.Character);
            rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Gm);
            var (_, given, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/give_item", Body(("character_id", player.Character.ToString()), ("item_id", Material(rig).Id), ("quantity", 1)));
            given.GetProperty("outcome").GetString().ShouldBe(AdminOutcomes.Done, given.ToString());
            await client.SendAsync(new LogoutMessage());
            await client.WaitForByeAsync();
        }
        await Poll.UntilAsync(() => rig.Game.Saves!.Holds(player.Character), held => !held, "the last save landed");
        await rig.Http.PostAsync(AdminPaths.Commands + "/mute", Body(("account_id", player.Account.ToString()), ("minutes", 10)));
        await rig.Http.PostAsync(AdminPaths.Commands + "/ban", Body(("account_id", player.Account.ToString())));
        var (_, opened, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/open_request", Body(("account_id", player.Account.ToString()), ("kind", SupportRequestKinds.Deletion)));
        await rig.Http.PostAsync(AdminPaths.Commands + "/complete_request", Body(("request_id", opened.GetProperty("result").GetProperty("request_id").GetInt64())));
        await using (var db = Database.Open())
            (await db.AdminItems.CountAsync(a => db.Items.Any(i => i.Id == a.ItemInstanceId && i.CharacterId == player.Character))).ShouldBe(1);

        var accounts = new PostgresAccountStore(Database);
        (await accounts.EraseAsync(player.Account, DateTime.UtcNow.AddDays(15), default)).ShouldBe(EraseOutcome.Erased);
        await using var after = Database.Open();
        (await after.Sanctions.CountAsync(s => s.AccountId == player.Account)).ShouldBe(0);
        (await after.SupportRequests.CountAsync(r => r.AccountId == player.Account)).ShouldBe(0);
        var everything = string.Join("\n", await after.AdminItems.AsNoTracking().Select(a => a.ItemInstanceId + " " + a.ItemId).ToListAsync());
        (await after.Items.CountAsync(i => i.CharacterId == player.Character)).ShouldBe(0);
        foreach (var gone in await after.AdminItems.AsNoTracking().ToListAsync()) (await after.Items.AnyAsync(i => i.Id == gone.ItemInstanceId) || gone.AuditId > 0).ShouldBeTrue(everything);
        var rows = await after.Audit.AsNoTracking().Where(a => a.TargetId == player.Account.ToString() || a.TargetId == player.Character.ToString()).ToListAsync();
        rows.Count.ShouldBeGreaterThanOrEqualTo(4, "staff actions stay (docs/STORE_COMPLIANCE.md §6)");
        foreach (var row in rows) (row.Before + row.After).ShouldNotContain(player.Username);
    }

    // ---- the log ----

    [DatabaseFact]
    public async Task Nothing_secret_or_personal_is_logged()
    {
        await using var rig = await RigAsync();
        var player = await RegisterAsync(rig);
        using var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString());
        await client.WaitForAsync<WelcomeMessage>();
        rig.Http.Staff = ("oidc|gm", "Gale M", StaffRoles.Admin);
        await rig.Http.PostAsync(AdminPaths.Commands + "/announce", Body(("text", "A private-looking line")));
        await rig.Http.PostAsync(AdminPaths.Commands + "/ban", Body(("account_id", player.Account.ToString()), ("minutes", 5)));
        await client.WaitForByeAsync();
        await rig.Game.Accounts!.LoginAsync(player.Username, ServiceRig.Password, "a", default);
        var log = string.Join("\n", rig.Game.Log.Lines);
        foreach (var secret in new[] { player.Username, player.Session, ServiceRig.Password, "support ticket 7", "Gale M", AdminRig.Credential, "A private-looking line" })
            log.ShouldNotContain(secret);
        log.ShouldContain(player.Account.ToString(), Case.Sensitive, "accounts appear by id");
    }
}
