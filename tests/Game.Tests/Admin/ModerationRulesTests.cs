using System.Text.Json;
using Game.Client.Core;
using Game.Client.Core.Net;
using Game.Server.Admin;
using Game.Server.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Network;

namespace Game.Tests.Admin;

/// <summary>T-806 without a database: the role table, each command's field refusals, maintenance and announcements end to end over the gateway, the wire and the client's words.</summary>
public class ModerationRulesTests
{
    private static Task<AdminRig> RigAsync() => AdminRig.StartAsync(commands: AdminCommandTable.AllCommands());

    private static Dictionary<string, object> Body(params (string Key, object Value)[] fields)
    {
        var body = new Dictionary<string, object> { ["reason"] = "a test" };
        foreach (var (key, value) in fields) body[key] = value;
        return body;
    }

    // ---- the role table ----

    [Fact]
    public void Every_action_has_one_row_with_the_role_of_section_12_2()
    {
        AdminCommandTable.LowestRole.Keys.Order(StringComparer.Ordinal).ShouldBe(AdminActions.All.Order(StringComparer.Ordinal));
        AdminCommandTable.AllCommands().Select(c => c.Action).Order(StringComparer.Ordinal).ShouldBe(AdminActions.All.Order(StringComparer.Ordinal));
        var expected = new Dictionary<string, string>
        {
            ["kick"] = "support", ["mute"] = "support", ["unmute"] = "support", ["open_request"] = "support", ["complete_request"] = "support", ["cancel_request"] = "support",
            ["ban"] = "gm", ["unban"] = "gm", ["give_item"] = "gm", ["remove_item"] = "gm", ["change_currency"] = "gm", ["unstick"] = "gm", ["announce"] = "gm",
            ["start_maintenance"] = "admin", ["end_maintenance"] = "admin",
        };
        foreach (var (action, role) in expected) AdminCommandTable.LowestRole[action].ShouldBe(role, action);
    }

    [Theory]
    [InlineData("ban", "support")]
    [InlineData("unban", "support")]
    [InlineData("give_item", "support")]
    [InlineData("remove_item", "support")]
    [InlineData("change_currency", "support")]
    [InlineData("unstick", "support")]
    [InlineData("announce", "support")]
    [InlineData("start_maintenance", "gm")]
    [InlineData("end_maintenance", "gm")]
    public async Task A_role_below_the_command_is_refused_before_anything_is_read(string action, string role)
    {
        await using var rig = await RigAsync();
        rig.Http.Staff = ("staff-1", "Robin Q", role);
        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/" + action, Body());
        (status, body.GetProperty("error").GetString()).ShouldBe((403, AdminErrors.Forbidden));
        rig.Memory!.Audit.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_command_list_shows_every_command_and_its_role()
    {
        await using var rig = await RigAsync();
        var (_, body, _) = await rig.Http.GetAsync(AdminPaths.Commands);
        var listed = body.GetProperty("commands").EnumerateArray().ToDictionary(c => c.GetProperty("action").GetString()!, c => c.GetProperty("lowest_role").GetString()!);
        listed.Count.ShouldBe(AdminActions.All.Count);
        listed["ban"].ShouldBe("gm");
        listed["start_maintenance"].ShouldBe("admin");
    }

    // ---- fields ----

    public static TheoryData<string, string> BadBodies => new()
    {
        { "ban", """{"reason":"r"}""" },
        { "ban", """{"reason":"r","account_id":"not-a-guid"}""" },
        { "ban", """{"reason":"r","account_id":"00000000-0000-0000-0000-000000000001","minutes":0}""" },
        { "ban", """{"reason":"r","account_id":"00000000-0000-0000-0000-000000000001","minutes":"60"}""" },
        { "ban", """{"reason":"r","account_id":"00000000-0000-0000-0000-000000000001","minutes":99999999}""" },
        { "mute", """{"reason":"r","account_id":"00000000-0000-0000-0000-000000000001"}""" },
        { "give_item", """{"reason":"r","character_id":"00000000-0000-0000-0000-000000000001","item_id":"item.x","quantity":0}""" },
        { "give_item", """{"reason":"r","character_id":"00000000-0000-0000-0000-000000000001","item_id":"item.x","quantity":10001}""" },
        { "give_item", """{"reason":"r","character_id":"00000000-0000-0000-0000-000000000001","quantity":1}""" },
        { "remove_item", """{"reason":"r","character_id":"00000000-0000-0000-0000-000000000001","entry_id":0,"quantity":1}""" },
        { "change_currency", """{"reason":"r","character_id":"00000000-0000-0000-0000-000000000001","amount":0}""" },
        { "change_currency", """{"reason":"r","character_id":"00000000-0000-0000-0000-000000000001","amount":1.5}""" },
        { "unstick", """{"reason":"r"}""" },
        { "announce", """{"reason":"r","text":"   "}""" },
        { "announce", """{"reason":"r","text":"two\nlines"}""" },
        { "start_maintenance", """{"reason":"r","warning_seconds":3601}""" },
        { "start_maintenance", """{"reason":"r"}""" },
        { "open_request", """{"reason":"r","account_id":"00000000-0000-0000-0000-000000000001","kind":"refund"}""" },
        { "complete_request", """{"reason":"r","request_id":"7"}""" },
        { "cancel_request", """{"reason":"r","request_id":-1}""" },
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_field_is_refused_with_nothing_changed(string action, string json)
    {
        await using var rig = await RigAsync();
        rig.Http.Staff = ("staff-1", "Robin Q", "admin");
        var (status, body, _) = await rig.Http.SendAsync(HttpMethod.Post, AdminPaths.Commands + "/" + action, json);
        (status, body.GetProperty("error").GetString()).ShouldBe((400, AdminErrors.InvalidRequest), json);
        rig.Memory!.Audit.ShouldBeEmpty();
        rig.Game.Gateway.InMaintenance.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_item_and_a_missing_store_are_refused()
    {
        await using var rig = await RigAsync();
        rig.Http.Staff = ("staff-1", "Robin Q", "gm");
        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/give_item",
            Body(("character_id", Guid.NewGuid().ToString()), ("item_id", "item.nothing_like_it"), ("quantity", 1)));
        (status, body.GetProperty("error").GetString()).ShouldBe((409, AdminErrors.UnknownItem));
        var (banStatus, ban, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/ban", Body(("account_id", Guid.NewGuid().ToString())));
        (banStatus, ban.GetProperty("error").GetString()).ShouldBe((503, AdminErrors.Unavailable), "no moderation store: nothing to ban in");
    }

    // ---- server: announcements and maintenance ----

    [Fact]
    public async Task An_announcement_reaches_everyone_playing_and_is_audited()
    {
        await using var rig = await RigAsync();
        using var a = await rig.Game.JoinAsync("a");
        using var b = await rig.Game.JoinAsync("b");
        rig.Http.Staff = ("staff-1", "Robin Q", "gm");
        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/announce", Body(("text", "The bridge opens at noon.")));
        status.ShouldBe(200, body.ToString());
        body.GetProperty("result").GetProperty("sent").GetInt32().ShouldBe(2);
        foreach (var client in new[] { a, b })
        {
            var said = await client.WaitForAsync<AnnouncementMessage>();
            (said.Kind, said.Text, said.InSeconds).ShouldBe((AnnouncementKinds.Staff, "The bridge opens at noon.", (long?)null));
        }
        var row = rig.Memory!.Audit.Single();
        (row.Action, row.TargetKind, row.TargetId).ShouldBe((AdminActions.Announce, AdminTargetKinds.Server, "server"));
        row.After.ShouldContain("The bridge opens at noon.");
    }

    [Fact]
    public async Task Maintenance_blocks_new_logins_warns_ends_the_games_and_ends()
    {
        await using var rig = await RigAsync();
        using var playing = await rig.Game.JoinAsync("playing");
        rig.Http.Staff = ("staff-1", "Robin Q", "admin");
        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/start_maintenance", Body(("warning_seconds", 2)));
        (status, body.GetProperty("outcome").GetString()).ShouldBe((200, AdminOutcomes.Accepted), body.ToString());
        rig.Game.Gateway.InMaintenance.ShouldBeTrue();
        (await playing.WaitForAsync<AnnouncementMessage>()).ShouldBe(new AnnouncementMessage(AnnouncementKinds.Maintenance, null, 2));
        var (again, second, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/start_maintenance", Body(("warning_seconds", 5)));
        (again, second.GetProperty("error").GetString()).ShouldBe((409, AdminErrors.MaintenanceOn));

        using var late = await rig.Game.ConnectAsync();
        await late.SendAsync(new HelloMessage(ProtocolVersion.Current, "late", rig.Game.ContentHash));
        (await late.WaitForByeAsync()).ShouldBe(ByeReasons.Maintenance, "no new login");
        var (_, status1, _) = await rig.Http.GetAsync(AdminPaths.Status);
        status1.GetProperty("maintenance").GetProperty("on").GetBoolean().ShouldBeTrue();
        status1.GetProperty("maintenance").GetProperty("games_end_at").GetString().ShouldNotBeNull();

        // When the warning is over every game ends; the server runs on.
        (await playing.WaitForByeAsync()).ShouldBe(ByeReasons.Maintenance);
        await Poll.UntilAsync(() => rig.Game.Gateway.CharacterCount, n => n == 0, "the character left the world");

        var (endStatus, ended, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/end_maintenance", Body());
        (endStatus, ended.GetProperty("outcome").GetString()).ShouldBe((200, AdminOutcomes.Done), ended.ToString());
        rig.Game.Gateway.InMaintenance.ShouldBeFalse();
        using var back = await rig.Game.JoinAsync("back");
        back.Messages.OfType<WelcomeMessage>().ShouldNotBeEmpty();
        (await rig.Http.PostAsync(AdminPaths.Commands + "/end_maintenance", Body())).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.MaintenanceOff);
        rig.Memory!.Audit.Select(r => r.Action).ShouldBe([AdminActions.StartMaintenance, AdminActions.EndMaintenance]);
    }

    [Fact]
    public async Task Ending_maintenance_before_its_time_calls_the_end_of_the_games_off()
    {
        await using var rig = await RigAsync();
        using var playing = await rig.Game.JoinAsync("playing");
        rig.Http.Staff = ("staff-1", "Robin Q", "admin");
        await rig.Http.PostAsync(AdminPaths.Commands + "/start_maintenance", Body(("warning_seconds", 1)));
        var (_, ended, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/end_maintenance", Body());
        ended.GetProperty("result").GetProperty("games_end_called_off").GetBoolean().ShouldBeTrue();
        await Task.Delay(1_500);
        playing.Messages.OfType<ByeMessage>().ShouldBeEmpty("the game goes on");
        rig.Game.Gateway.CharacterCount.ShouldBe(1);
    }

    // ---- the wire and the client ----

    [Fact]
    public void Announcements_and_a_ban_bye_go_over_the_wire_both_ways()
    {
        foreach (ServerMessage message in new ServerMessage[]
                 {
                     new AnnouncementMessage(AnnouncementKinds.Staff, "Hello \"all\"", null), new AnnouncementMessage(AnnouncementKinds.Maintenance, null, 300),
                     new ByeMessage(ByeReasons.Banned, 86_400), new ByeMessage(ByeReasons.Banned), new ByeMessage(ByeReasons.Maintenance),
                 })
        {
            var bytes = ProtocolJson.ToBytes(message);
            ProtocolJson.ReadServer(bytes).Message.ShouldBe(message, System.Text.Encoding.UTF8.GetString(bytes));
        }
        System.Text.Encoding.UTF8.GetString(ProtocolJson.ToBytes(new ByeMessage(ByeReasons.Banned))).ShouldNotContain("retry_after", Case.Sensitive, "a ban without an end says none");
    }

    [Fact]
    public void The_client_says_how_long_a_ban_lasts_and_never_why()
    {
        UiText.Notice(ConnectionNotice.Banned, 2 * 86_400).ShouldContain("2 days");
        UiText.Notice(ConnectionNotice.Banned, 3_000).ShouldContain("50 minutes");
        UiText.Notice(ConnectionNotice.Banned, 3_600).ShouldContain("an hour");
        UiText.Notice(ConnectionNotice.Banned, null).ShouldNotContain(" for ");
        UiText.Account(Game.Client.Core.Accounts.AccountNotice.Refused, AccountErrors.AccountBanned, 0).ShouldBe(UiText.Banned(null));
        UiText.Notice(ConnectionNotice.Maintenance).ShouldContain("maintenance");
        UiText.Announcement(AnnouncementKinds.Maintenance, null, 120).ShouldContain("2 minutes");
        UiText.Announcement(AnnouncementKinds.Staff, "Hi", null).ShouldEndWith("Hi");
        UiText.LongestNotice.Length.ShouldBeGreaterThanOrEqualTo(UiText.Notice(ConnectionNotice.Banned, 9_999L * 86_400).Length);
        UiText.LongestAnnouncement.Length.ShouldBeGreaterThan(AnnouncementKinds.MaxTextLength - 3);
    }
}
