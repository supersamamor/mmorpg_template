using Game.Admin;
using Game.Admin.Api;
using Game.Admin.Auth;
using Game.Content.ClientExport;
using Game.Server.Admin;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Admin;
using Game.Tests.Network;
using Game.Tests.World;

namespace Game.Tests.AdminPanel;

/// <summary>
/// T-805 end to end: the real server stack in process with its admin API on (accounts and the audit log in memory), the real panel in front of it, and a browser that
/// signs in with the development sign-in, reads the status and kicks a player who is in the world.
/// </summary>
public class PanelServerTests
{
    [Fact]
    public async Task Sign_in_read_the_status_and_kick_a_playing_character()
    {
        await using var rig = await AdminRig.StartAsync(o => o with { RateLimitBurst = 500 }); // the rig's clock stands still
        var session = (await rig.Game.Accounts!.RegisterAsync("ada", ServiceRig.Password, "a", default)).Value!.Token;
        var character = (await rig.Game.Accounts.CreateAsync(session, "Lyra", default)).Value!.Id;
        var account = rig.Accounts!.AccountList.Single().Id;
        var player = await rig.Game.PlayAsync(session, character);
        await player.WaitForAsync<WelcomeMessage>();
        await player.WaitForAsync<SnapshotMessage>();

        await using var panel = await PanelRig.StartAsync(rig.Host.Endpoint);
        using var browser = panel.Browser();
        await browser.SignInAsync(StaffRoles.Support);

        var status = await browser.GetAsync("/status");
        status.Status.ShouldBe(200);
        status.Html.ShouldContain("data-fact=\"players_online\">1<");
        status.Html.ShouldContain(rig.Game.ContentHash);
        rig.Game.ContentHash.ShouldBe(ClientBundleExporter.Hash(TestGrids.SampleContent));
        status.Text.ShouldNotContain("The panel's content is not the server's");
        foreach (var instance in rig.Game.World.Instances) status.Html.ShouldContain(instance.MapId);

        var found = await browser.GetAsync("/accounts?name=ad");
        found.Html.ShouldContain($"href=\"/accounts/{account}\"");
        var one = await browser.GetAsync($"/accounts/{account}");
        one.Text.ShouldContain("ada");
        var kick = one.CommandTag(AdminActions.Kick);
        kick.ShouldStartWith("<a ");

        var path = $"/commands/kick?account_id={account}";
        var form = await browser.GetAsync(path);
        var review = await browser.SubmitCommandAsync(path, form, ("step", "review"), ("account_id", account.ToString()), ("reason", "a test kick"));
        review.Text.ShouldContain("playing now");
        rig.Memory!.Audit.ShouldBeEmpty();
        var done = await browser.SubmitCommandAsync(path, review, ("step", "confirm"));

        done.Html.ShouldContain("data-outcome=\"accepted\"", customMessage: done.Text);
        (await player.WaitForByeAsync()).ShouldBe(ByeReasons.Kicked);
        await Poll.UntilAsync(() => rig.Game.Gateway.CharacterCount, n => n == 0, "the character left");
        var row = rig.Memory.Audit.ShouldHaveSingleItem();
        (row.StaffId, row.StaffName, row.StaffRole, row.Action, row.TargetId, row.Reason)
            .ShouldBe(("development|robinq", "Robin Q", "support", "kick", account.ToString(), "a test kick"));
        done.Text.ShouldContain("audit row " + row.Id);
        done.Html.ShouldContain(row.RequestId);

        var log = await browser.GetAsync($"/audit?target={account}&action=kick");
        log.Text.ShouldContain("a test kick");
        log.Text.ShouldContain("development|robinq");

        foreach (var page in browser.Seen) page.Html.ShouldNotContain(AdminRig.Credential);
        panel.Logs.Text.ShouldNotContain(AdminRig.Credential);
        string.Join("\n", rig.Game.Log.Lines).ShouldNotContain(AdminRig.Credential);
    }

    /// <summary>
    /// The panel reads the answers into its own types (Game.Shared has only some), so a field the two name differently would show as empty. Here the server's own serializer
    /// writes a character with every field set, and the panel's client must read each one back.
    /// </summary>
    [Fact]
    public async Task The_panel_reads_every_field_of_what_the_server_writes()
    {
        var at = new DateTime(2026, 11, 20, 8, 30, 0, DateTimeKind.Utc);
        var account = new AdminAccount(Guid.NewGuid(), "ada", at, at.AddDays(1), at.AddDays(2), 1, 3);
        var character = new AdminCharacter(Guid.NewGuid(), account.Id, 2, "Lyra", "lyra", "job.a", at, at.AddDays(3));
        var state = new AdminSavedState(9, at, 4, 5, "map.a", 6, 7, "north", 8, 10, "map.b", 11, 12, 13, 14, "job.b", 15, 16, 17, 18, 19,
            """{"stat.a":1}""", """{"skill.a":2}""", """{"done":["quest.a"]}""", """{"skills":{}}""", """[{"kind":"x"}]""", """{"head":"outfit.a"}""", true, ["outfit.a"],
            [new AdminItem(20, "item.a", 21, "weapon", ["item.card"], 22, "gone")]);
        await using var rig = await AdminRig.StartAsync(store: new OneOfEach(account, new AdminCharacterDetail(character, state),
            new AuditRecord(23, at, "req", "staff", "Kim", "gm", "kick", "account", account.Id.ToString(), """{"online":true}""", """{"online":false}""", "why")));
        var settings = new PanelSettings { ServerUrl = rig.Host.Endpoint.GetLeftPart(UriPartial.Authority) };
        using var http = new HttpClient(AdminApiClient.Handler(settings));
        var client = new AdminApiClient(http, new PanelCredential(AdminRig.Credential), settings);
        var me = new Staff("oidc|kim", "Kim", StaffRoles.Support, "s", DateTimeOffset.MaxValue);

        var accounts = (await client.FindAccountsAsync(me, null, null, null)).Value!;
        accounts.Items.ShouldHaveSingleItem().ShouldBe(new AccountAnswer(account.Id, "ada", at, at.AddDays(1), at.AddDays(2), 1, 3, false));
        var detail = (await client.GetAccountAsync(me, account.Id)).Value!;
        var expected = new CharacterAnswer(character.Id, account.Id, 2, "Lyra", "job.a", at, at.AddDays(3), false);
        detail.Characters.ShouldHaveSingleItem().ShouldBe(expected);
        (await client.FindCharactersAsync(me, null, null, null, null)).Value!.Items.ShouldHaveSingleItem().ShouldBe(expected);

        var read = (await client.GetCharacterAsync(me, character.Id)).Value!;
        read.Character.ShouldBe(expected);
        var got = read.State!;
        (got.Version, got.SavedAt, got.Claim, got.HeldBy, got.MapId, got.CellX, got.CellY, got.Facing, got.Hp, got.Sp).ShouldBe((9L, at, 4L, 5L, "map.a", 6, 7, "north", 8, 10));
        (got.SaveMapId, got.SaveCellX, got.SaveCellY, got.BaseLevel, got.BaseExp, got.JobId, got.JobLevel, got.JobExp, got.StatPoints, got.SkillPoints)
            .ShouldBe(("map.b", 11, 12, 13, 14L, "job.b", 15, 16L, 17, 18));
        (got.Currency, got.OutfitsHidden, got.Outfits.Single()).ShouldBe((19L, true, "outfit.a"));
        (got.Stats.GetRawText(), got.Skills.GetRawText(), got.Quests.GetRawText(), got.Cooldowns.GetRawText(), got.SetAside.GetRawText(), got.OutfitSlots.GetRawText())
            .ShouldBe(("""{"stat.a":1}""", """{"skill.a":2}""", """{"done":["quest.a"]}""", """{"skills":{}}""", """[{"kind":"x"}]""", """{"head":"outfit.a"}"""));
        var item = got.Items.ShouldHaveSingleItem();
        (item.Id, item.ItemId, item.Quantity, item.Slot, item.Cards.Single(), item.Refine, item.Aside).ShouldBe((20L, "item.a", 21, "weapon", "item.card", 22, "gone"));

        var row = (await client.SearchAuditAsync(me, null, null, null, null, null, null)).Value!.Items.ShouldHaveSingleItem();
        (row.Id, row.At, row.RequestId, row.StaffId, row.StaffName, row.StaffRole, row.Action, row.TargetKind, row.TargetId, row.Reason)
            .ShouldBe((23L, at, "req", "staff", "Kim", "gm", "kick", "account", account.Id.ToString(), "why"));
        (row.Before.GetRawText(), row.After.GetRawText()).ShouldBe(("""{"online":true}""", """{"online":false}"""));

        var commands = (await client.GetCommandsAsync(me)).Value!;
        commands.Commands.ShouldContain(new CommandInfo(AdminActions.Kick, StaffRoles.Support));
        (commands.ReadRole, commands.Roles.Count).ShouldBe((StaffRoles.Support, 3));
    }

    /// <summary>A store that answers every read with one fixed row.</summary>
    private sealed class OneOfEach(AdminAccount account, AdminCharacterDetail character, AuditRecord row) : IAdminStore
    {
        public Task<AdminPage<AdminAccount>> FindAccountsAsync(AccountQuery query, CancellationToken cancellation) => Task.FromResult(new AdminPage<AdminAccount>([account], null));

        public Task<(AdminAccount Account, IReadOnlyList<AdminCharacter> Characters)?> GetAccountAsync(Guid id, CancellationToken cancellation) =>
            Task.FromResult<(AdminAccount, IReadOnlyList<AdminCharacter>)?>((account, [character.Character]));

        public Task<AdminPage<AdminCharacter>> FindCharactersAsync(CharacterQuery query, CancellationToken cancellation) =>
            Task.FromResult(new AdminPage<AdminCharacter>([character.Character], null));

        public Task<AdminCharacterDetail?> GetCharacterAsync(Guid id, CancellationToken cancellation) => Task.FromResult<AdminCharacterDetail?>(character);

        public Task<AdminPage<AuditRecord>> SearchAuditAsync(AuditQuery query, CancellationToken cancellation) => Task.FromResult(new AdminPage<AuditRecord>([row], null));

        public Task<long> AppendAuditAsync(AuditDraft draft, CancellationToken cancellation) => throw new NotSupportedException();

        public Task<SessionsEnded?> EndSessionsAsync(Guid id, Func<int, AuditDraft> audit, CancellationToken cancellation) => throw new NotSupportedException();
    }
}
