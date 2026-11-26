using Game.Content.ClientExport;
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
}
