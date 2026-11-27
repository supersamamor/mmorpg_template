using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Server.Chat;
using Game.Server.Persistence;
using Game.Server.World.Saving;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Admin;
using Game.Tests.Network;
using Microsoft.EntityFrameworkCore;

namespace Game.Tests.Chat;

/// <summary>
/// T-901 against a real PostgreSQL and the real gateway: two players on one map talk, whisper, block; a report stores the server's copy of a line; staff read and close it through the
/// admin API (roles and audit), mute the sender (its next line is refused with how long is left); blocks are stored and read at sign-in; erasing either account takes the blocks and
/// reports that name it; the export holds them; and nothing a player wrote is in the log.
/// </summary>
[Trait("Category", "Database")]
public sealed class ChatDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private GameDatabase Database => fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set.");

    private static string Tag() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));

    private sealed record Player(string Username, string Session, Guid Account, Guid Character, string Name);

    private async Task<AdminRig> RigAsync()
    {
        var chat = new PostgresChatStore(Database);
        return await AdminRig.StartAsync(store: new PostgresAdminStore(Database),
            accounts: new AccountRigOptions(new PostgresAccountStore(Database), new FakeHasher(), new AccountLimits(), new FakeTime { Now = DateTimeOffset.UtcNow })
            {
                Characters = new PostgresCharacterStore(Database), SaveOptions = new CharacterSaveOptions { SpacingMs = 20 },
            },
            commands: AdminCommandTable.AllCommands(), moderation: new PostgresModerationStore(Database), chat: chat);
    }

    private static async Task<Player> RegisterAsync(AdminRig rig)
    {
        var username = "c" + Tag();
        var name = "C" + Tag();
        var session = (await rig.Game.Accounts!.RegisterAsync(username, ServiceRig.Password, "a", default)).Value!.Token;
        var character = (await rig.Game.Accounts.CreateAsync(session, name, default)).Value!.Id;
        var account = (await rig.Store.FindAccountsAsync(new AccountQuery(username, null, null, 1), default)).Items.Single().Id;
        return new Player(username, session, account, Guid.Parse(character), name);
    }

    private static async Task<TestClient> PlayAsync(AdminRig rig, Player player)
    {
        var client = await rig.Game.PlayAsync(player.Session, player.Character.ToString());
        await client.WaitForAsync<WelcomeMessage>();
        await client.WaitForAsync<ChatStateMessage>();
        return client;
    }

    private static Dictionary<string, object> Body(params (string Key, object Value)[] fields)
    {
        var body = new Dictionary<string, object> { ["reason"] = "chat ticket 9" };
        foreach (var (key, value) in fields) body[key] = value;
        return body;
    }

    [DatabaseFact]
    public async Task Two_players_talk_whisper_block_report_and_staff_close_the_report_and_mute_the_sender()
    {
        await using var rig = await RigAsync();
        var ada = await RegisterAsync(rig);
        var bo = await RegisterAsync(rig);
        using var a = await PlayAsync(rig, ada);
        using var b = await PlayAsync(rig, bo);
        a.Messages.OfType<ChatStateMessage>().Single().ShouldSatisfyAllConditions(s => s.MaxLength.ShouldBe(new ChatOptions().MaxLength), s => s.Blocked.ShouldBeEmpty());

        // Map: both hear it, the sender only once the server took it.
        await a.SendAsync(new ChatMessage(ChatChannels.Map, null, "PLAYER-WORDS hello  map"));
        var heard = await b.WaitForAsync<ChatLineMessage>(l => l.Text == "PLAYER-WORDS hello map");
        (heard.Channel, heard.From, heard.Name).ShouldBe((ChatChannels.Map, a.Id, ada.Name));
        (await a.WaitForAsync<ChatLineMessage>(l => l.Line == heard.Line)).Text.ShouldBe(heard.Text);

        // Whisper, by name in another letter case.
        await b.SendAsync(new ChatMessage(ChatChannels.Whisper, ada.Name.ToUpperInvariant(), "PLAYER-WORDS psst"));
        var whisper = await a.WaitForAsync<ChatLineMessage>(l => l.Channel == ChatChannels.Whisper);
        (whisper.Name, whisper.To, whisper.Text).ShouldBe((bo.Name, ada.Name, "PLAYER-WORDS psst"));

        // Report: by number only; the server stores what it sent.
        await b.SendAsync(new ReportMessage(heard.Line));
        await b.WaitForAsync<ChatReportedMessage>(r => r.Line == heard.Line);

        // Block: stored, sent back, and Ada's lines stop reaching Bo; her whisper reads as "not online".
        await b.SendAsync(new BlockMessage(ada.Name));
        (await b.WaitForAsync<ChatStateMessage>(s => s.Blocked.Count == 1)).Blocked.ShouldBe([ada.Name]);
        await a.SendAsync(new ChatMessage(ChatChannels.Whisper, bo.Name, "PLAYER-WORDS are you there"));
        (await a.WaitForAsync<ChatRefusedMessage>()).Reason.ShouldBe(ChatReasons.NotOnline);
        await a.SendAsync(new ChatMessage(ChatChannels.Map, null, "PLAYER-WORDS after the block"));
        var after = await a.WaitForAsync<ChatLineMessage>(l => l.Text == "PLAYER-WORDS after the block");
        await b.SendAsync(new ChatMessage(ChatChannels.Map, null, "PLAYER-WORDS mark"));   // Bo's own line comes back after anything queued for it before
        await b.WaitForAsync<ChatLineMessage>(l => l.Text == "PLAYER-WORDS mark");
        b.Messages.OfType<ChatLineMessage>().ShouldNotContain(l => l.Line == after.Line);
        await using (var db = Database.Open())
            (await db.ChatBlocks.AsNoTracking().SingleAsync(x => x.AccountId == bo.Account)).BlockedCharacterId.ShouldBe(ada.Character);

        // Staff read the open reports with the line, and close one: support may, the row is audited without the line.
        var (status, list, _) = await rig.Http.GetAsync(AdminPaths.ChatReports + "?status=open&account=" + ada.Account);
        status.ShouldBe(200, list.ToString());
        var report = list.GetProperty("items").EnumerateArray().Single();
        (report.GetProperty("text").GetString(), report.GetProperty("channel").GetString()).ShouldBe(("PLAYER-WORDS hello map", ChatChannels.Map));
        (report.GetProperty("sender_account_id").GetGuid(), report.GetProperty("sender_character_id").GetGuid()).ShouldBe((ada.Account, ada.Character));
        (report.GetProperty("reporter_account_id").GetGuid(), report.GetProperty("reporter_character_id").GetGuid()).ShouldBe((bo.Account, bo.Character));
        var id = report.GetProperty("id").GetInt64();
        rig.Http.Staff = ("oidc|s", "Sam S", "player");
        (await rig.Http.PostAsync(AdminPaths.Commands + "/close_report", Body(("report_id", id)))).Status.ShouldBe(403, "not a staff role");
        rig.Http.Staff = ("oidc|s", "Sam S", StaffRoles.Support);
        (await rig.Http.PostAsync(AdminPaths.Commands + "/close_report", new Dictionary<string, object> { ["report_id"] = id })).Body.GetProperty("error").GetString()
            .ShouldBe(AdminErrors.ReasonRequired);
        var (closeStatus, closed, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/close_report", Body(("report_id", id)), requestId: "close-1");
        (closeStatus, closed.GetProperty("outcome").GetString()).ShouldBe((200, AdminOutcomes.Done), closed.ToString());
        var auditId = closed.GetProperty("audit_id").GetInt64();
        (await rig.Http.PostAsync(AdminPaths.Commands + "/close_report", Body(("report_id", id)))).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.ReportClosed);
        (await rig.Http.PostAsync(AdminPaths.Commands + "/close_report", Body(("report_id", id + 1_000_000)))).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.UnknownTarget);
        (await rig.Http.PostAsync(AdminPaths.Commands + "/close_report", Body(("report_id", "x")))).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.InvalidRequest);
        var (_, open, _) = await rig.Http.GetAsync(AdminPaths.ChatReports + "?status=open&account=" + ada.Account);
        open.GetProperty("items").GetArrayLength().ShouldBe(0);
        var (_, closedList, _) = await rig.Http.GetAsync(AdminPaths.ChatReports + "?status=closed&account=" + bo.Account);
        closedList.GetProperty("items").EnumerateArray().Single().GetProperty("closed_audit_id").GetInt64().ShouldBe(auditId);
        (await rig.Http.GetAsync(AdminPaths.ChatReports + "?status=nonsense")).Status.ShouldBe(400);
        var row = (await rig.Store.SearchAuditAsync(new AuditQuery(id.ToString(), null, AdminActions.CloseReport, null, null, null, 5), default)).Items.Single();
        (row.Id, row.StaffId, row.StaffRole, row.TargetKind, row.RequestId, row.Reason).ShouldBe((auditId, "oidc|s", StaffRoles.Support, AdminTargetKinds.ChatReport, "close-1", "chat ticket 9"));
        (row.Before + row.After).ShouldNotContain("PLAYER-WORDS", Case.Insensitive, "the line stays in the report, not in the log");

        // Mute through the admin API: Ada's next line is refused, with how long is left, and reaches nobody.
        var (_, muted, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/mute", Body(("account_id", ada.Account.ToString()), ("minutes", 10)));
        muted.GetProperty("outcome").GetString().ShouldBe(AdminOutcomes.Done, muted.ToString());
        await a.SendAsync(new ChatMessage(ChatChannels.Map, null, "PLAYER-WORDS muted line"));
        var refused = await a.WaitForAsync<ChatRefusedMessage>(r => r.Reason == ChatReasons.Muted);
        refused.RetryAfterSeconds!.Value.ShouldBeInRange(590, 600);
        a.Messages.OfType<ChatLineMessage>().ShouldNotContain(l => l.Text.Contains("muted line"));

        rig.Game.Log.Lines.ShouldNotContain(l => l.Contains("PLAYER-WORDS"), "nothing a player wrote is logged");
    }

    [DatabaseFact]
    public async Task A_block_is_kept_and_read_again_at_the_next_sign_in()
    {
        await using var rig = await RigAsync();
        var ada = await RegisterAsync(rig);
        var bo = await RegisterAsync(rig);
        using (var b = await PlayAsync(rig, bo))
        {
            await b.SendAsync(new BlockMessage(ada.Name));   // Ada is not online: found by her stored name
            await b.WaitForAsync<ChatStateMessage>(s => s.Blocked.Count == 1);
            await b.SendAsync(new BlockMessage("Nobody" + Tag()));
            (await b.WaitForAsync<ChatRefusedMessage>()).Reason.ShouldBe(ChatReasons.UnknownCharacter);
            await b.SendAsync(new LogoutMessage());
            await b.WaitForByeAsync();
        }
        var again = await rig.Game.Accounts!.LoginAsync(bo.Username, ServiceRig.Password, "a", default);
        using var b2 = await rig.Game.PlayAsync(again.Value!.Token, bo.Character.ToString());
        (await b2.WaitForAsync<ChatStateMessage>()).Blocked.ShouldBe([ada.Name], "read at sign-in");
        using var a = await PlayAsync(rig, ada);
        await a.SendAsync(new ChatMessage(ChatChannels.Map, null, "hello"));
        await a.WaitForAsync<ChatLineMessage>();
        await b2.SendAsync(new UnblockMessage(ada.Name.ToLowerInvariant()));
        (await b2.WaitForAsync<ChatStateMessage>(s => s.Blocked.Count == 0)).Blocked.ShouldBeEmpty();
        b2.Messages.OfType<ChatLineMessage>().ShouldNotContain(l => l.Text == "hello");
        await using var db = Database.Open();
        (await db.ChatBlocks.CountAsync(x => x.AccountId == bo.Account)).ShouldBe(0);
    }

    [DatabaseFact]
    public async Task Erasing_either_account_takes_the_reports_and_blocks_that_name_it_and_the_export_holds_them()
    {
        var store = new PostgresChatStore(Database);
        var accounts = new PostgresAccountStore(Database);
        async Task<(Guid Account, Guid Character, string Name)> MakeAsync()
        {
            var account = Guid.NewGuid();
            (await accounts.TryCreateAccountAsync(account, "e" + Tag(), "hash", DateTime.UtcNow, default)).ShouldBeTrue();
            var name = "E" + Tag();
            var (_, character) = await accounts.CreateCharacterAsync(account, Guid.NewGuid(), name, Game.Shared.Accounts.CharacterNameRules.Key(name), "job.wanderer", 3, null, DateTime.UtcNow, default);
            return (account, character!.Id, name);
        }
        var said = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
        var sender = await MakeAsync();
        var reporter = await MakeAsync();
        var bystander = await MakeAsync();
        (await store.AddBlockAsync(reporter.Account, sender.Character, 10, said, default)).ShouldBe(BlockOutcome.Added);
        (await store.AddBlockAsync(reporter.Account, sender.Character, 10, said, default)).ShouldBe(BlockOutcome.AlreadyBlocked);
        (await store.AddBlockAsync(sender.Account, bystander.Character, 1, said, default)).ShouldBe(BlockOutcome.Added);
        (await store.AddBlockAsync(sender.Account, reporter.Character, 1, said, default)).ShouldBe(BlockOutcome.Limit);
        (await store.AddBlockAsync(bystander.Account, reporter.Character, 10, said, default)).ShouldBe(BlockOutcome.Added);
        (await store.FindCharacterAsync(Game.Shared.Accounts.CharacterNameRules.Key(sender.Name.ToUpperInvariant()), default))!.Id.ShouldBe(sender.Character);
        (await store.BlocksAsync(reporter.Account, default)).ShouldBe([new BlockedCharacter(sender.Character, sender.Name)]);
        var first = await store.AddReportAsync(new ChatReportDraft(reporter.Account, reporter.Character, sender.Account, sender.Character, ChatChannels.Map, "SENDER-LINE one", said, said), default);
        await store.AddReportAsync(new ChatReportDraft(bystander.Account, bystander.Character, reporter.Account, reporter.Character, ChatChannels.Whisper, "REPORTER-LINE two", said, said), default);

        var export = (await new PostgresModerationStore(Database).ExportAsync(sender.Account, said, default))!;
        export["chat_blocks"]!.AsArray().Single()!["character"]!.GetValue<string>().ShouldBe(bystander.Name);
        export["chat_reports_about"]!.AsArray().Single()!["text"]!.GetValue<string>().ShouldBe("SENDER-LINE one");
        export["chat_reports_about"]!.ToJsonString().ShouldNotContain(reporter.Account.ToString(), customMessage: "who reported is not the sender's data");
        var reporterExport = (await new PostgresModerationStore(Database).ExportAsync(reporter.Account, said, default))!;
        reporterExport["chat_reports_made"]!.AsArray().Single()!["id"]!.GetValue<long>().ShouldBe(first);

        // The sender is erased: the report of its line and both blocks that name it go; the bystander's report of the reporter stays.
        await using (var db = Database.Open()) await db.Accounts.Where(x => x.Id == sender.Account).ExecuteUpdateAsync(s => s.SetProperty(x => x.EraseAfter, said));
        (await accounts.EraseAsync(sender.Account, said, default)).ShouldBe(EraseOutcome.Erased);
        await using (var db = Database.Open())
        {
            (await db.ChatReports.CountAsync(r => r.SenderAccountId == sender.Account || r.ReporterAccountId == sender.Account)).ShouldBe(0);
            (await db.ChatBlocks.CountAsync(x => x.AccountId == sender.Account || x.BlockedCharacterId == sender.Character)).ShouldBe(0);
            (await db.ChatReports.CountAsync(r => r.ReporterAccountId == bystander.Account)).ShouldBe(1);
            (await db.ChatBlocks.CountAsync(x => x.AccountId == bystander.Account)).ShouldBe(1);
        }
        // Now the reporter of the remaining one is the one reported: erasing it takes that report too, and the block of it.
        await using (var db = Database.Open()) await db.Accounts.Where(x => x.Id == reporter.Account).ExecuteUpdateAsync(s => s.SetProperty(x => x.EraseAfter, said));
        (await accounts.EraseAsync(reporter.Account, said, default)).ShouldBe(EraseOutcome.Erased);
        await using (var db = Database.Open())
        {
            (await db.ChatReports.CountAsync(r => r.ReporterAccountId == bystander.Account || r.SenderAccountId == reporter.Account)).ShouldBe(0);
            (await db.ChatBlocks.CountAsync(x => x.AccountId == bystander.Account)).ShouldBe(0);
            var everything = string.Join("\n", await db.Database.SqlQuery<string>($"SELECT t::text AS \"Value\" FROM chat_reports t").ToListAsync());
            everything.ShouldNotContain("SENDER-LINE");
            everything.ShouldNotContain("REPORTER-LINE");
        }
    }

    [DatabaseFact]
    public async Task Closed_reports_are_erased_after_their_days_and_open_ones_are_kept()
    {
        var store = new PostgresChatStore(Database);
        var accounts = new PostgresAccountStore(Database);
        var account = Guid.NewGuid();
        (await accounts.TryCreateAccountAsync(account, "p" + Tag(), "hash", DateTime.UtcNow, default)).ShouldBeTrue();
        var name = "P" + Tag();
        var (_, character) = await accounts.CreateCharacterAsync(account, Guid.NewGuid(), name, Game.Shared.Accounts.CharacterNameRules.Key(name), "job.wanderer", 3, null, DateTime.UtcNow, default);
        var other = Guid.NewGuid();
        (await accounts.TryCreateAccountAsync(other, "p" + Tag(), "hash", DateTime.UtcNow, default)).ShouldBeTrue();
        var name2 = "P" + Tag();
        var (_, character2) = await accounts.CreateCharacterAsync(other, Guid.NewGuid(), name2, Game.Shared.Accounts.CharacterNameRules.Key(name2), "job.wanderer", 3, null, DateTime.UtcNow, default);
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = await store.AddReportAsync(new ChatReportDraft(account, character!.Id, other, character2!.Id, ChatChannels.Map, "x", at, at), default);
        var open = await store.AddReportAsync(new ChatReportDraft(account, character.Id, other, character2.Id, ChatChannels.Map, "y", at, at), default);
        var staff = new StaffIdentity("oidc|s", "Sam", StaffRoles.Support);
        var change = await store.CloseReportAsync(old, at.AddDays(1), r => new AuditDraft(staff, "r1", AdminActions.CloseReport, AdminTargetKinds.ChatReport, r.Id.ToString(), "{}", "{}", "done", at.AddDays(1)), default);
        change.Error.ShouldBeNull();
        (await store.PurgeClosedAsync(at.AddDays(1), default)).ShouldBe(0, "not yet");
        (await store.PurgeClosedAsync(at.AddDays(31), default)).ShouldBe(1);
        (await store.FindReportsAsync(null, account, null, 10, default)).Items.Select(r => r.Id).ShouldBe([open]);
    }
}
