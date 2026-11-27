using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Game.Admin.Api;
using Game.Admin.Commands;
using Game.Admin.ContentView;
using Game.Server.Admin;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.World;

namespace Game.Tests.AdminPanel;

/// <summary>
/// T-809: the pages for moderation, support, the server and chat reports, against a fake server that lists the commands with the real server's lowest roles
/// (<see cref="AdminCommandTable"/>): what the reads return is shown, a role sees only its actions, every command is sent once as confirmed, refusals are in words, an
/// export is only a download, and a reported line goes nowhere but the reports page.
/// </summary>
public class PanelModerationTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CharacterId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ReporterAccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ReporterCharacterId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly string ItemId = TestGrids.SampleContent.Items.All.First().Id;
    private static readonly string MapId = TestGrids.SampleContent.Maps.All.First().Id;
    private static readonly string JobId = TestGrids.SampleContent.Jobs.All.First().Id;

    private const string OpenLine = "zork you <b>fools</b> & \"all\"";
    private const string ClosedLine = "second reported sentence";
    private const string ExportSecret = "exported-username-ada-77";

    private static string CommandList => JsonSerializer.Serialize(new
    {
        commands = AdminCommandTable.LowestRole.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new { action = c.Key, lowest_role = c.Value }),
        read_role = "support",
        roles = StaffRoles.All.Ordered,
    });

    private static string CharacterJson(Guid id, Guid account, string name) =>
        $$"""{"id":"{{id}}","account_id":"{{account}}","slot":0,"name":"{{name}}","job_id":"{{JobId}}","created_at":"2026-11-02T10:00:00Z","deleted_at":null,"online":false}""";

    private static string State =>
        $$$"""{"version":3,"saved_at":"2026-11-03T10:00:00Z","claim":1,"held_by":null,"map_id":"{{{MapId}}}","cell_x":4,"cell_y":5,"facing":"south","hp":40,"sp":10,"save_map_id":null,"save_cell_x":null,"save_cell_y":null,"base_level":7,"base_exp":12,"job_id":"{{{JobId}}}","job_level":2,"job_exp":0,"stat_points":1,"skill_points":0,"currency":950,"stats":{},"skills":{},"quests":{},"cooldowns":{},"set_aside":[],"outfit_slots":{},"outfits_hidden":false,"outfits":[],"items":[{"id":901,"item_id":"{{{ItemId}}}","quantity":3,"slot":null,"cards":[],"refine":0,"aside":null},{"id":902,"item_id":"item.gone_from_content","quantity":1,"slot":"weapon","cards":[],"refine":0,"aside":null}]}""";

    private static string Moderation(bool banned, bool muted) =>
        $$"""{"account_id":"{{AccountId}}","banned":{{(banned ? "true" : "false")}},"muted":{{(muted ? "true" : "false")}},"bans":[{"id":71,"kind":"ban","from":"2026-11-10T10:00:00Z","until":null,"in_force":{{(banned ? "true" : "false")}},"audit_id":501,"lifted_at":null,"lifted_audit_id":null}],"mutes":[{"id":72,"kind":"mute","from":"2026-11-11T10:00:00Z","until":"2026-11-12T10:00:00Z","in_force":{{(muted ? "true" : "false")}},"audit_id":502,"lifted_at":"2026-11-11T11:00:00Z","lifted_audit_id":503}],"requests":[{"id":81,"account_id":"{{AccountId}}","kind":"export","status":"open","opened_at":"2026-11-20T10:00:00Z","opened_audit_id":601,"closed_at":null,"closed_audit_id":null},{"id":80,"account_id":"{{AccountId}}","kind":"deletion","status":"cancelled","opened_at":"2026-11-19T10:00:00Z","opened_audit_id":599,"closed_at":"2026-11-19T11:00:00Z","closed_audit_id":600}]}""";

    private static string StaffChange =>
        $$"""{"id":640,"at":"2026-11-26T09:00:00Z","request_id":"req-9","staff_id":"oidc|kim","staff_name":"Kim","staff_role":"gm","action":"give_item","target_kind":"character","target_id":"{{CharacterId}}","before":{"carried":1},"after":{"carried":4},"reason":"lost in a crash"}""";

    private static string Report(long id, string status, string line) =>
        $$"""{"id":{{id}},"reported_at":"2026-11-27T12:30:05Z","reporter_account_id":"{{ReporterAccountId}}","reporter_character_id":"{{ReporterCharacterId}}","sender_account_id":"{{AccountId}}","sender_character_id":"{{CharacterId}}","channel":"whisper","text":{{JsonSerializer.Serialize(line)}},"said_at":"2026-11-27T12:29:41Z","status":"{{status}}","closed_at":{{(status == "closed" ? "\"2026-11-27T13:00:00Z\"" : "null")}},"closed_audit_id":{{(status == "closed" ? "701" : "null")}}}""";

    private static FakeAnswer? Data(Received r, bool banned, bool muted, bool maintenance)
    {
        if (r.Method != "GET") return null;
        var path = r.PathAndQuery.Split('?')[0];
        var account = $$"""{"id":"{{AccountId}}","username":"ada","created_at":"2026-11-01T10:00:00Z","password_changed_at":"2026-11-01T10:00:00Z","erase_after":null,"characters":1,"sessions":2,"online":true}""";
        return path switch
        {
            "/admin/v1/commands" => new FakeAnswer(Body: CommandList),
            "/admin/v1/status" => new FakeAnswer(Body: FakeAdminServer.StatusJson[..^1] +
                $$$""","maintenance":{"on":{{{(maintenance ? "true" : "false")}}},"since":{{{(maintenance ? "\"2026-11-27T00:00:00Z\"" : "null")}}},"games_end_at":null}}"""),
            "/admin/v1/chat-reports" => new FakeAnswer(Body: r.PathAndQuery.Contains("status=open")
                ? $$"""{"items":[{{Report(12, "open", OpenLine)}}],"next":"12"}"""
                : $$"""{"items":[{{Report(9, "closed", ClosedLine)}}],"next":null}"""),
            _ when path == $"/admin/v1/accounts/{AccountId}" =>
                new FakeAnswer(Body: $$"""{"account":{{account}},"characters":[{{CharacterJson(CharacterId, AccountId, "Lyra")}}]}"""),
            _ when path == $"/admin/v1/accounts/{AccountId}/moderation" => new FakeAnswer(Body: Moderation(banned, muted)),
            _ when path == $"/admin/v1/characters/{CharacterId}" =>
                new FakeAnswer(Body: $$"""{"character":{{CharacterJson(CharacterId, AccountId, "Lyra")}},"state":{{State}}}"""),
            _ when path == $"/admin/v1/characters/{ReporterCharacterId}" =>
                new FakeAnswer(Body: $$"""{"character":{{CharacterJson(ReporterCharacterId, ReporterAccountId, "Mira")}},"state":null}"""),
            _ when path == $"/admin/v1/characters/{CharacterId}/staff-changes" => new FakeAnswer(Body: $$"""{"items":[{{StaffChange}}],"next":"640"}"""),
            _ => null,
        };
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required FakeAdminServer Server { get; init; }
        public required PanelRig Panel { get; init; }
        public required PanelBrowser Browser { get; init; }

        /// <summary>What the server answers to a command.</summary>
        public Func<Received, FakeAnswer?>? OnPost { get; set; }

        public bool Banned { get; set; }
        public bool Muted { get; set; }
        public bool Maintenance { get; set; }

        public IReadOnlyList<Received> Posts => Server.Requests.Where(r => r.Method == "POST").ToList();

        public async ValueTask DisposeAsync()
        {
            Browser.Dispose();
            await Panel.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private static async Task<Rig> StartAsync(string role = "admin")
    {
        var server = await FakeAdminServer.StartAsync();
        var panel = await PanelRig.StartAsync(server.Address);
        var rig = new Rig { Server = server, Panel = panel, Browser = panel.Browser() };
        server.Answer = r => (r.Method == "POST" ? rig.OnPost?.Invoke(r) ?? Done() : null) ?? Data(r, rig.Banned, rig.Muted, rig.Maintenance);
        await rig.Browser.SignInAsync(role);
        return rig;
    }

    private static FakeAnswer Done(string result = "{}") => new(Body: $$"""{"outcome":"done","request_id":"srv-9","audit_id":77,"error":null,"result":{{result}}}""");

    /// <summary>The links of a command on a page: where each goes.</summary>
    private static List<string> Links(Page page, string action) =>
        Regex.Matches(page.Html, $"<a\\b[^>]*data-command=\"{action}\"[^>]*>").Select(m => WebUtility.HtmlDecode(Regex.Match(m.Value, "href=\"([^\"]*)\"").Groups[1].Value)).ToList();

    private static string Confirmed(Page review) => WebUtility.HtmlDecode(Regex.Match(review.Html, "<pre data-confirm[^>]*>(.*?)</pre>", RegexOptions.Singleline).Groups[1].Value);

    private static async Task<(Page Review, Page Done)> RunAsync(Rig rig, string path, params (string Name, string Value)[] fields)
    {
        var form = await rig.Browser.GetAsync(path);
        form.Status.ShouldBe(200, path);
        var review = await rig.Browser.SubmitCommandAsync(path, form, [("step", "review"), .. fields]);
        Confirmed(review).ShouldNotBeEmpty($"{path} asks for a confirmation: {review.Text}");
        return (review, await rig.Browser.SubmitCommandAsync(path, review, ("step", "confirm")));
    }

    // ---- what the pages show, and to whom -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_account_page_shows_bans_mutes_and_requests_with_their_actions()
    {
        await using var rig = await StartAsync();
        var page = await rig.Browser.GetAsync($"/accounts/{AccountId}");
        page.Html.ShouldContain("data-fact=\"banned\">no<");
        page.Html.ShouldContain("data-fact=\"muted\">no<");
        foreach (var expected in new[] { "71", "no end", "501", "72", "2026-11-12 10:00:00 UTC", "lifted by 503", "81", "export", "open", "80", "deletion", "cancelled" })
            page.Text.ShouldContain(expected);
        Links(page, "ban").ShouldBe([$"/commands/ban?account_id={AccountId}"]);
        Links(page, "mute").ShouldBe([$"/commands/mute?account_id={AccountId}"]);
        Links(page, "unban").ShouldBeEmpty("nothing to lift");
        Links(page, "unmute").ShouldBeEmpty("nothing to lift");
        Links(page, "kick").Count.ShouldBe(1);
        Links(page, "open_request").ShouldBe([$"/commands/open_request?account_id={AccountId}&kind=deletion", $"/commands/open_request?account_id={AccountId}&kind=export"]);
        Links(page, "complete_request").ShouldBe([$"/commands/complete_request?request_id=81&account_id={AccountId}"], "only the open request has actions");
        Links(page, "cancel_request").ShouldBe([$"/commands/cancel_request?request_id=81&account_id={AccountId}"]);
        page.CommandTag("give_item").ShouldBeEmpty("a character's command is not on the account's page");
        page.CommandTag("announce").ShouldBeEmpty();

        rig.Banned = rig.Muted = true;
        var standing = await rig.Browser.GetAsync($"/accounts/{AccountId}");
        standing.Html.ShouldContain("data-fact=\"banned\">yes<");
        Links(standing, "ban").ShouldBeEmpty();
        Links(standing, "mute").ShouldBeEmpty();
        Links(standing, "unban").Count.ShouldBe(1);
        Links(standing, "unmute").Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_account_whose_moderation_cannot_be_read_says_why_and_offers_both_sides()
    {
        await using var rig = await StartAsync();
        var normal = rig.Server.Answer!;
        rig.Server.Answer = r => r.PathAndQuery.EndsWith("/moderation", StringComparison.Ordinal)
            ? new FakeAnswer(503, """{"error":"unavailable","request_id":"r"}""")
            : normal(r);
        var page = await rig.Browser.GetAsync($"/accounts/{AccountId}");
        page.Text.ShouldContain(ErrorWords.For(AdminErrors.Unavailable));
        foreach (var action in new[] { "ban", "unban", "mute", "unmute" }) Links(page, action).Count.ShouldBe(1, action);
    }

    [Fact]
    public async Task The_character_page_offers_fixes_removal_by_entry_and_shows_staff_changes()
    {
        await using var rig = await StartAsync();
        var page = await rig.Browser.GetAsync($"/characters/{CharacterId}");
        var target = $"account_id={AccountId}&character_id={CharacterId}";
        Links(page, "give_item").ShouldBe([$"/commands/give_item?{target}"]);
        Links(page, "change_currency").ShouldBe([$"/commands/change_currency?{target}"]);
        Links(page, "unstick").ShouldBe([$"/commands/unstick?{target}"]);
        Links(page, "remove_item").ShouldBe([$"/commands/remove_item?{target}&entry_id=901", $"/commands/remove_item?{target}&entry_id=902"]);
        page.Text.ShouldContain("Changes staff made");
        page.Text.ShouldContain("lost in a crash");
        page.Text.ShouldContain("640");
        page.Html.ShouldContain($"href=\"/characters/{CharacterId}?changes_before=640\"");
        await rig.Browser.GetAsync($"/characters/{CharacterId}?changes_before=640");
        rig.Server.Requests.ShouldContain(r => r.PathAndQuery == $"/admin/v1/characters/{CharacterId}/staff-changes?before=640");
        page.CommandTag("ban").ShouldBeEmpty();
        rig.Posts.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_item_forms_pick_from_content_and_from_the_saved_state()
    {
        await using var rig = await StartAsync("gm");
        var names = ContentNames.Of(TestGrids.SampleContent);
        names.ItemIds.Count.ShouldBe(TestGrids.SampleContent.Items.All.Count());
        var give = await rig.Browser.GetAsync($"/commands/give_item?character_id={CharacterId}");
        foreach (var id in names.ItemIds) give.Html.ShouldContain($"<option value=\"{id}\"");
        give.Text.ShouldContain(names.Describe(ItemId));

        var remove = await rig.Browser.GetAsync($"/commands/remove_item?character_id={CharacterId}&entry_id=902");
        Regex.Match(remove.Html, "<option value=\"902\"[^>]*>").Value.ShouldContain("selected");
        Regex.Match(remove.Html, "<option value=\"901\"[^>]*>").Value.ShouldNotContain("selected");
        remove.Text.ShouldContain("worn in weapon");
        var review = await rig.Browser.SubmitCommandAsync($"/commands/remove_item?character_id={CharacterId}&entry_id=902", remove, ("step", "review"),
            ("character_id", CharacterId.ToString()), ("entry_id", "901"), ("quantity", "2"), ("reason", "a duplicate"));
        review.Text.ShouldContain("Character Lyra");
        review.Text.ShouldContain($"Entry 901: {names.Describe(ItemId)}, quantity 3");
    }

    [Fact]
    public async Task An_address_fills_in_ids_only_never_a_reason_a_text_or_an_amount()
    {
        await using var rig = await StartAsync();
        var announce = await rig.Browser.GetAsync("/commands/announce?text=from-the-link&reason=from-the-link");
        Fields(announce).ShouldNotContain("from-the-link");
        var currency = await rig.Browser.GetAsync($"/commands/change_currency?character_id={CharacterId}&amount=987654&reason=from-the-link");
        Fields(currency).ShouldNotContain("987654");
        Fields(currency).ShouldNotContain("from-the-link");
        var report = await rig.Browser.GetAsync("/commands/close_report?report_id=12abc&reason=from-the-link");
        Fields(report).ShouldNotContain("12abc");
    }

    /// <summary>The page without its form tags (a form's action is the address itself): what is left holds every field's value.</summary>
    private static string Fields(Page page) => Regex.Replace(page.Html, "<form\\b[^>]*>", "");

    [Theory]
    [InlineData("support")]
    [InlineData("gm")]
    [InlineData("admin")]
    public async Task A_role_sees_only_the_actions_the_servers_table_gives_it(string role)
    {
        await using var rig = await StartAsync(role);
        var expected = new Dictionary<string, (bool Banned, bool Muted, bool Maintenance, string Path, string[] Actions)>
        {
            ["account"] = (false, false, false, $"/accounts/{AccountId}", ["ban", "mute", "open_request", "complete_request", "cancel_request"]),
            ["banned account"] = (true, true, false, $"/accounts/{AccountId}", ["unban", "unmute"]),
            ["character"] = (false, false, false, $"/characters/{CharacterId}", ["give_item", "remove_item", "change_currency", "unstick"]),
            ["server"] = (false, false, false, "/server", ["announce", "start_maintenance"]),
            ["server in maintenance"] = (false, false, true, "/server", ["announce", "end_maintenance"]),
            ["reports"] = (false, false, false, "/chat-reports", ["close_report", "mute", "ban"]),
        };
        foreach (var (name, view) in expected)
        {
            (rig.Banned, rig.Muted, rig.Maintenance) = (view.Banned, view.Muted, view.Maintenance);
            var page = await rig.Browser.GetAsync(view.Path);
            page.Status.ShouldBe(200, name);
            foreach (var action in view.Actions)
            {
                var allowed = StaffRoles.AtLeast(role, AdminCommandTable.LowestRole[action]);
                (Links(page, action).Count > 0).ShouldBe(allowed, $"{role} on {name}: {action}");
                if (!allowed) page.Html.ShouldNotContain($"/commands/{action}", customMessage: $"{role} on {name}: no way to {action} at all");
            }
        }
    }

    [Fact]
    public async Task The_server_page_shows_maintenance_and_the_announcement_counts_to_80_as_typed()
    {
        await using var rig = await StartAsync();
        var off = await rig.Browser.GetAsync("/server");
        off.Html.ShouldContain("data-fact=\"maintenance\">off<");
        Links(off, "end_maintenance").ShouldBeEmpty();
        rig.Maintenance = true;
        var on = await rig.Browser.GetAsync("/server");
        on.Html.ShouldContain("data-fact=\"maintenance\">on<");
        on.Text.ShouldContain("2026-11-27T00:00:00Z");
        Links(on, "start_maintenance").ShouldBeEmpty();

        var form = await rig.Browser.GetAsync("/commands/announce");
        var input = Regex.Match(form.Html, "<input name=\"text\"[^>]*>").Value;
        input.ShouldContain($"maxlength=\"{AnnouncementKinds.MaxTextLength}\"");
        input.ShouldContain("data-count=\"text-count\"");
        form.Html.ShouldContain("<output id=\"text-count\">0</output> of 80 characters");
        form.Html.ShouldMatch("<script src=\"panel[^\"]*\\.js\">");
        using var anonymous = rig.Panel.Browser();
        var script = await anonymous.GetAsync("/panel.js");
        script.Status.ShouldBe(200);
        script.Html.ShouldContain("data");
        script.Html.ShouldContain("addEventListener(\"input\"");

        var typed = await rig.Browser.SubmitCommandAsync("/commands/announce", form, ("step", "review"), ("text", "Back in five."), ("reason", " "));
        typed.Html.ShouldContain("<output id=\"text-count\">13</output>", customMessage: "a form shown again counts what is in it");
        var long81 = await rig.Browser.SubmitCommandAsync("/commands/announce", form, ("step", "review"), ("text", new string('a', 81)), ("reason", "r"));
        long81.Text.ShouldContain("1 to 80 characters");
        rig.Posts.ShouldBeEmpty();
    }

    // ---- what is sent ---------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string, string, string> EveryCommand()
    {
        var account = AccountId.ToString();
        var character = CharacterId.ToString();
        var forAccount = $"?account_id={account}";
        var forCharacter = $"?account_id={account}&character_id={character}";
        return new TheoryData<string, string, string, string>
        {
            { "ban", forAccount, $"account_id={account}&minutes=1440", $$"""{"account_id":"{{account}}","minutes":1440,"reason":"r"}""" },
            { "ban", forAccount, $"account_id={account}&minutes=", $$"""{"account_id":"{{account}}","reason":"r"}""" },
            { "unban", forAccount, $"account_id={account}", $$"""{"account_id":"{{account}}","reason":"r"}""" },
            { "mute", forAccount, $"account_id={account}&minutes=60", $$"""{"account_id":"{{account}}","minutes":60,"reason":"r"}""" },
            { "unmute", forAccount, $"account_id={account}", $$"""{"account_id":"{{account}}","reason":"r"}""" },
            { "give_item", forCharacter, $"character_id={character}&item_id={ItemId}&quantity=5", $$"""{"character_id":"{{character}}","item_id":"{{ItemId}}","quantity":5,"reason":"r"}""" },
            { "remove_item", forCharacter + "&entry_id=901", $"character_id={character}&entry_id=901&quantity=2", $$"""{"character_id":"{{character}}","entry_id":901,"quantity":2,"reason":"r"}""" },
            { "change_currency", forCharacter, $"character_id={character}&amount=-250", $$"""{"character_id":"{{character}}","amount":-250,"reason":"r"}""" },
            { "unstick", forCharacter, $"character_id={character}", $$"""{"character_id":"{{character}}","reason":"r"}""" },
            { "announce", "", "text= Restart at noon. ", """{"text":"Restart at noon.","reason":"r"}""" },
            { "start_maintenance", "", "warning_seconds=0", """{"warning_seconds":0,"reason":"r"}""" },
            { "end_maintenance", "", "", """{"reason":"r"}""" },
            { "open_request", forAccount + "&kind=export", $"account_id={account}&kind=export", $$"""{"account_id":"{{account}}","kind":"export","reason":"r"}""" },
            { "complete_request", "?request_id=81&account_id=" + account, "request_id=81", """{"request_id":81,"reason":"r"}""" },
            { "cancel_request", "?request_id=81&account_id=" + account, "request_id=81", """{"request_id":81,"reason":"r"}""" },
            { "close_report", "?report_id=12&from=reports", "report_id=12", """{"report_id":12,"reason":"r"}""" },
        };
    }

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public async Task Each_command_sends_exactly_the_confirmed_JSON_once(string action, string query, string fields, string expected)
    {
        await using var rig = await StartAsync();
        var path = $"/commands/{action}{query}";
        var typed = fields.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(f => (f.Split('=')[0], f.Split('=')[1])).Append(("reason", "r")).ToArray();
        var form = await rig.Browser.GetAsync(path);
        form.Text.ShouldContain("No personal data in reasons.");
        form.Text.ShouldContain("Describe what happened. Never write a name, an e-mail address or anything else about a person");
        var review = await rig.Browser.SubmitCommandAsync(path, form, [("step", "review"), .. typed]);
        review.Text.ShouldContain("No personal data in reasons.");
        rig.Posts.ShouldBeEmpty("nothing is sent before the confirmation");
        var confirmed = Confirmed(review);
        JsonSerializer.Serialize(JsonDocument.Parse(confirmed).RootElement).ShouldBe(expected);

        var done = await rig.Browser.SubmitCommandAsync(path, review, ("step", "confirm"));
        done.Html.ShouldContain("data-outcome=\"done\"");
        done.Text.ShouldContain("audit row 77");
        var sent = rig.Posts.ShouldHaveSingleItem();
        sent.PathAndQuery.ShouldBe($"/admin/v1/commands/{action}");
        sent.Body.ShouldBe(confirmed.ReplaceLineEndings(sent.Body.Contains("\r\n") ? "\r\n" : "\n"));

        (await rig.Browser.SubmitCommandAsync(path, review, ("step", "confirm"))).Text.ShouldContain("was used already or has expired");
        rig.Posts.Count.ShouldBe(1);
    }

    [Fact]
    public void Every_admin_action_has_a_page_of_its_own()
    {
        foreach (var action in AdminActions.All.Ordered) CommandForms.Known.Keys.ShouldContain(action);
        foreach (var action in CommandForms.Known.Keys) AdminActions.All.Contains(action).ShouldBeTrue(action);
    }

    [Theory]
    [InlineData("mute", "minutes", "", "whole number")]
    [InlineData("mute", "minutes", "0", "whole number")]
    [InlineData("mute", "minutes", "5270401", "whole number")]
    [InlineData("ban", "minutes", "soon", "whole number")]
    [InlineData("start_maintenance", "warning_seconds", "3601", "from 0 to 3600")]
    [InlineData("announce", "text", "two\nlines", "one line")]
    [InlineData("give_item", "item_id", "", "Pick an item")]
    [InlineData("open_request", "kind", "everything", "kind of request")]
    public void A_field_that_cannot_be_sent_as_typed_is_refused_before_anything_is_sent(string action, string field, string value, string problem)
    {
        var fields = CommandForms.Known[action]
            .Select(spec => new CommandField(spec.Name, CommandForms.Text, spec.Name == field ? value : spec.Input == FieldInput.Id ? AccountId.ToString() : "1"))
            .ToList();
        var body = CommandForms.Build(action, fields, "r");
        body.Json.ShouldBeNull();
        body.Problem!.ShouldContain(problem);
    }

    [Fact]
    public async Task Every_refusal_of_the_server_is_shown_in_its_words()
    {
        await using var rig = await StartAsync();
        var path = $"/commands/unstick?character_id={CharacterId}";
        foreach (var term in AdminErrors.All.Ordered)
        {
            rig.OnPost = _ => new FakeAnswer(409, $$"""{"outcome":"refused","request_id":"srv-5","audit_id":null,"error":"{{term}}","result":null}""");
            var (_, done) = await RunAsync(rig, path, ("character_id", CharacterId.ToString()), ("reason", "r"));
            done.Html.ShouldContain("data-outcome=\"refused\"", customMessage: term);
            done.Text.ShouldContain(ErrorWords.For(term), customMessage: term);
        }
        rig.Posts.Count.ShouldBe(AdminErrors.All.Ordered.Count());
    }

    [Fact]
    public async Task A_fix_accepted_before_its_save_does_not_claim_an_audit_row_yet_and_a_lost_answer_may_have_happened()
    {
        await using var rig = await StartAsync();
        var path = $"/commands/change_currency?account_id={AccountId}&character_id={CharacterId}";
        rig.OnPost = _ => new FakeAnswer(Body: """{"outcome":"accepted","request_id":"srv-6","audit_id":null,"error":null,"result":{"saving":"pending","currency":10}}""");
        var (_, accepted) = await RunAsync(rig, path, ("character_id", CharacterId.ToString()), ("amount", "5"), ("reason", "r"));
        accepted.Text.ShouldContain("Its audit row is written with the character's next save");
        accepted.Text.ShouldNotContain("it is in the audit log");
        accepted.Text.ShouldContain("pending");
        accepted.Html.ShouldContain($"href=\"/characters/{CharacterId}\"");

        rig.OnPost = _ => new FakeAnswer(Abort: true);
        var (_, lost) = await RunAsync(rig, path, ("character_id", CharacterId.ToString()), ("amount", "5"), ("reason", "r"));
        lost.Text.ShouldContain("does not know whether the server applied the command");
        rig.Posts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_command_whose_target_cannot_be_read_is_not_offered_for_confirmation()
    {
        await using var rig = await StartAsync();
        var unknown = Guid.NewGuid();
        var path = $"/commands/give_item?character_id={unknown}";
        var form = await rig.Browser.GetAsync(path);
        var review = await rig.Browser.SubmitCommandAsync(path, form, ("step", "review"), ("character_id", unknown.ToString()), ("item_id", ItemId), ("quantity", "1"), ("reason", "r"));
        review.Html.ShouldNotContain("data-confirm");
        review.Text.ShouldContain(ErrorWords.For(AdminErrors.NotFound));
        rig.Posts.ShouldBeEmpty();
    }

    // ---- the export -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_export_is_a_download_on_that_one_page_and_the_panel_keeps_no_copy()
    {
        await using var rig = await StartAsync("support");
        rig.OnPost = _ => Done($$$$"""{"target_kind":"request","target_id":"81","export":{"account":{"username":"{{{{ExportSecret}}}}","note":"<script>x</script>"}}}""");
        var path = $"/commands/complete_request?request_id=81&account_id={AccountId}";
        var (review, done) = await RunAsync(rig, path, ("request_id", "81"), ("reason", "the owner asked, ticket 5"));
        review.Text.ShouldContain("Request 81: export, open, for account ada");

        var link = Regex.Match(done.Html, "<a\\b[^>]*data-export[^>]*>").Value;
        link.ShouldContain("download=\"account-export-77.json\"");
        var href = WebUtility.HtmlDecode(Regex.Match(link, "href=\"([^\"]*)\"").Groups[1].Value);
        const string prefix = "data:application/json;charset=utf-8;base64,";
        href.ShouldStartWith(prefix);
        var file = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(href[prefix.Length..]))).RootElement;
        file.GetProperty("account").GetProperty("username").GetString().ShouldBe(ExportSecret);
        file.GetProperty("account").GetProperty("note").GetString().ShouldBe("<script>x</script>");
        done.Html.ShouldNotContain(ExportSecret, customMessage: "in the download only, never as text on the page");
        done.Text.ShouldContain("The panel keeps no copy");
        done.Response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        // The audit is the server's own: the one command, and no other request after it.
        rig.Posts.ShouldHaveSingleItem().PathAndQuery.ShouldBe("/admin/v1/commands/complete_request");

        var again = await rig.Browser.SubmitCommandAsync(path, review, ("step", "confirm"));
        again.Html.ShouldNotContain("data-export");
        foreach (var later in new[] { path, $"/accounts/{AccountId}", "/audit" })
        {
            var page = await rig.Browser.GetAsync(later);
            page.Html.ShouldNotContain("data-export", customMessage: later);
            page.Html.ShouldNotContain("data:", customMessage: later);
        }
        rig.Panel.Logs.Text.ShouldNotContain(ExportSecret);
        rig.Panel.Logs.Text.ShouldNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(ExportSecret))[..16]);
        rig.Posts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_export_and_other_commands_offer_no_download()
    {
        await using var rig = await StartAsync("support");
        rig.OnPost = _ => new FakeAnswer(403, """{"outcome":"refused","request_id":"srv-7","audit_id":null,"error":"forbidden","result":{"export":{"a":1}}}""");
        var path = $"/commands/complete_request?request_id=81&account_id={AccountId}";
        var (_, refused) = await RunAsync(rig, path, ("request_id", "81"), ("reason", "r"));
        refused.Html.ShouldNotContain("data-export");
        refused.Text.ShouldContain(ErrorWords.For(AdminErrors.Forbidden));

        rig.OnPost = _ => Done("""{"export":{"a":1}}""");
        var (_, other) = await RunAsync(rig, $"/commands/cancel_request?request_id=81&account_id={AccountId}", ("request_id", "81"), ("reason", "r"));
        other.Html.ShouldNotContain("data-export");
    }

    // ---- chat reports ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_reports_page_shows_open_then_closed_with_names_links_and_the_warning()
    {
        await using var rig = await StartAsync("gm");
        var page = await rig.Browser.GetAsync("/chat-reports");
        page.Text.ShouldContain("A reported line is personal data. Never copy it, or any part of it, into a reason");
        page.Html.ShouldNotContain("<b>fools", customMessage: "a line is text, never markup");
        page.Text.ShouldContain(OpenLine);
        page.Text.IndexOf(OpenLine, StringComparison.Ordinal).ShouldBeLessThan(page.Text.IndexOf(ClosedLine, StringComparison.Ordinal), "open first");
        foreach (var expected in new[] { "whisper", "2026-11-27 12:29:41 UTC", "2026-11-27 12:30:05 UTC", "Lyra", "Mira", "2026-11-27 13:00:00 UTC", "audit row 701" })
            page.Text.ShouldContain(expected);
        foreach (var href in new[] { $"/characters/{CharacterId}", $"/accounts/{AccountId}", $"/characters/{ReporterCharacterId}", $"/accounts/{ReporterAccountId}" })
            page.Html.ShouldContain($"href=\"{href}\"");
        Links(page, "close_report").ShouldBe(["/commands/close_report?report_id=12&from=reports"], "a closed report is not closed again");
        Links(page, "mute").ShouldBe(Enumerable.Repeat($"/commands/mute?account_id={AccountId}&from=reports", 2).ToList());
        Links(page, "ban").ShouldBe(Enumerable.Repeat($"/commands/ban?account_id={AccountId}&from=reports", 2).ToList());
        var reads = rig.Server.Requests.Select(r => r.PathAndQuery).Where(p => p.StartsWith("/admin/v1/chat-reports", StringComparison.Ordinal)).ToList();
        reads.ShouldBe(["/admin/v1/chat-reports?status=open&limit=6", "/admin/v1/chat-reports?status=closed&limit=6"]);
        rig.Server.Requests.Count(r => r.PathAndQuery == $"/admin/v1/characters/{CharacterId}").ShouldBe(1, "each name is read once a page");

        page.Html.ShouldContain("href=\"/chat-reports?open_before=12\"");
        await rig.Browser.GetAsync("/chat-reports?open_before=12");
        rig.Server.Requests.ShouldContain(r => r.PathAndQuery == "/admin/v1/chat-reports?status=open&before=12&limit=6");
    }

    [Fact]
    public async Task A_name_read_the_server_refuses_leaves_ids_and_is_not_asked_on()
    {
        await using var rig = await StartAsync("support");
        var normal = rig.Server.Answer!;
        rig.Server.Answer = r => r.PathAndQuery.StartsWith("/admin/v1/characters/", StringComparison.Ordinal)
            ? new FakeAnswer(429, """{"error":"rate_limited","request_id":"r"}""")
            : normal(r);
        var page = await rig.Browser.GetAsync("/chat-reports");
        page.Text.ShouldContain(ErrorWords.For(AdminErrors.RateLimited));
        page.Text.ShouldContain(CharacterId.ToString());
        page.Text.ShouldContain(OpenLine);
        rig.Server.Requests.Count(r => r.PathAndQuery.StartsWith("/admin/v1/characters/", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task The_reported_line_is_never_put_into_a_reason_an_address_or_a_log()
    {
        await using var rig = await StartAsync();
        var reports = await rig.Browser.GetAsync("/chat-reports");
        var links = new[] { "close_report", "mute", "ban" }.SelectMany(a => Links(reports, a)).Distinct().ToList();
        links.Count.ShouldBe(3);
        foreach (var link in links)
        {
            Uri.UnescapeDataString(link).ShouldNotContain("zork");
            var form = await rig.Browser.GetAsync(link);
            form.Text.ShouldContain("A reported line is personal data.");
            form.Text.ShouldNotContain("zork");
            Regex.Match(form.Html, "<textarea name=\"reason\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline).Groups[1].Value.ShouldBeEmpty(link);
            var fields = link.Contains("close_report") ? [("report_id", "12")] : new List<(string, string)> { ("account_id", AccountId.ToString()), ("minutes", "60") };
            var review = await rig.Browser.SubmitCommandAsync(link, form, [("step", "review"), .. fields, ("reason", "insults, chat report 12")]);
            Confirmed(review).ShouldNotContain("zork");
            review.Text.ShouldNotContain("zork");
            var done = await rig.Browser.SubmitCommandAsync(link, review, ("step", "confirm"));
            done.Html.ShouldContain("href=\"/chat-reports\"");
        }
        rig.Posts.Count.ShouldBe(3);
        foreach (var post in rig.Posts)
        {
            post.Body.ShouldNotContain("zork");
            JsonDocument.Parse(post.Body).RootElement.GetProperty("reason").GetString().ShouldBe("insults, chat report 12");
        }
        foreach (var request in rig.Server.Requests) Uri.UnescapeDataString(request.PathAndQuery).ShouldNotContain("zork");
        rig.Panel.Logs.Text.ShouldNotContain("zork");
        rig.Panel.Logs.Text.ShouldNotContain(ClosedLine);
        new ChatReportAnswer(1, default, default, default, default, default, "map", OpenLine, default, "open", null, null).ToString().ShouldNotContain("zork");
    }
}
