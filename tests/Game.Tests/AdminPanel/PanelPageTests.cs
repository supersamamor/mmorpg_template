using System.Text.RegularExpressions;
using Game.Admin;
using Game.Admin.Commands;
using Game.Admin.ContentView;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Admin;
using Game.Tests.World;

namespace Game.Tests.AdminPanel;

/// <summary>
/// T-805: the pages against a fake server: commands greyed out by role, the server's refusal shown, one send per confirmation, the strict headers, and no secret on a
/// page or in a log.
/// </summary>
public class PanelPageTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CharacterId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string ItemId = TestGrids.SampleContent.Items.All.First().Id;
    private static readonly string MapId = TestGrids.SampleContent.Maps.All.First().Id;
    private static readonly string JobId = TestGrids.SampleContent.Jobs.All.First().Id;

    private static string Account(bool online = true) =>
        $$"""{"id":"{{AccountId}}","username":"ada","created_at":"2026-11-01T10:00:00Z","password_changed_at":"2026-11-01T10:00:00Z","erase_after":null,"characters":1,"sessions":2,"online":{{(online ? "true" : "false")}}}""";

    private static string Character => $$"""{"id":"{{CharacterId}}","account_id":"{{AccountId}}","slot":0,"name":"Lyra","job_id":"{{JobId}}","created_at":"2026-11-02T10:00:00Z","deleted_at":null,"online":false}""";

    private static string State =>
        $$$"""{"version":3,"saved_at":"2026-11-03T10:00:00Z","claim":1,"held_by":null,"map_id":"{{{MapId}}}","cell_x":4,"cell_y":5,"facing":"south","hp":40,"sp":10,"save_map_id":null,"save_cell_x":null,"save_cell_y":null,"base_level":7,"base_exp":12,"job_id":"{{{JobId}}}","job_level":2,"job_exp":0,"stat_points":1,"skill_points":0,"currency":950,"stats":{"stat.str":5},"skills":{},"quests":{"active":[],"done":["quest.gone_from_content"]},"cooldowns":{"skills":{},"items":{}},"set_aside":[],"outfit_slots":{},"outfits_hidden":false,"outfits":[],"items":[{"id":901,"item_id":"{{{ItemId}}}","quantity":3,"slot":null,"cards":[],"refine":0,"aside":null},{"id":902,"item_id":"item.gone_from_content","quantity":1,"slot":null,"cards":[],"refine":0,"aside":null}]}""";

    private const string Row =
        """{"id":5,"at":"2026-11-26T09:00:00Z","request_id":"req-1","staff_id":"oidc|kim","staff_name":"Kim <b>","staff_role":"gm","action":"kick","target_kind":"account","target_id":"11111111-1111-1111-1111-111111111111","before":{"online":true},"after":{"online":false},"reason":"<script>alert(1)</script>"}""";

    private static FakeAnswer? Data(Received r)
    {
        if (r.Method != "GET") return null;
        var path = r.PathAndQuery.Split('?')[0];
        return path switch
        {
            "/admin/v1/accounts" => new FakeAnswer(Body: $$"""{"items":[{{Account()}}],"next":"ada"}"""),
            _ when path == $"/admin/v1/accounts/{AccountId}" => new FakeAnswer(Body: $$"""{"account":{{Account()}},"characters":[{{Character}}]}"""),
            "/admin/v1/characters" => new FakeAnswer(Body: $$"""{"items":[{{Character}}],"next":null}"""),
            _ when path == $"/admin/v1/characters/{CharacterId}" => new FakeAnswer(Body: $$"""{"character":{{Character}},"state":{{State}}}"""),
            "/admin/v1/audit" => new FakeAnswer(Body: $$"""{"items":[{{Row}}],"next":"5"}"""),
            _ => null,
        };
    }

    private static async Task<(FakeAdminServer Server, PanelRig Panel, PanelBrowser Browser)> StartAsync(string role = "admin", Func<Received, FakeAnswer?>? answer = null,
        TimeProvider? time = null)
    {
        var server = await FakeAdminServer.StartAsync();
        server.Answer = r => answer?.Invoke(r) ?? Data(r);
        var panel = await PanelRig.StartAsync(server.Address, time: time);
        var browser = panel.Browser();
        await browser.SignInAsync(role);
        return (server, panel, browser);
    }

    private static string[] AllPages =>
    [
        "/", "/status", "/accounts", "/accounts?name=ad", $"/accounts/{AccountId}", "/characters", $"/characters?account={AccountId}", $"/characters/{CharacterId}", "/audit",
        $"/audit?target={AccountId}&staff=oidc%7Ckim&action=kick", "/commands", $"/commands/kick?account_id={AccountId}", $"/commands/test_gm?account_id={AccountId}", "/signin",
    ];

    [Theory]
    [InlineData("support", "kick")]
    [InlineData("gm", "kick", "test_gm")]
    [InlineData("admin", "kick", "test_gm", "test_admin")]
    public async Task A_command_below_its_lowest_role_is_greyed_out(string role, params string[] allowed)
    {
        var (server, panel, browser) = await StartAsync(role);
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        foreach (var path in new[] { "/commands", $"/accounts/{AccountId}", $"/characters/{CharacterId}" })
        {
            var page = await browser.GetAsync(path);
            page.Status.ShouldBe(200, path);
            foreach (var action in new[] { "kick", "test_gm", "test_admin", "test_owner" })
            {
                var tag = page.CommandTag(action);
                tag.ShouldNotBeEmpty($"{path} lists {action}");
                if (allowed.Contains(action))
                {
                    tag.ShouldStartWith("<a ");
                    tag.ShouldContain($"href=\"/commands/{action}");
                }
                else
                {
                    tag.ShouldStartWith("<button ");
                    tag.ShouldContain(" disabled");
                }
            }
            page.CommandTag("test_owner").ShouldContain(" disabled", customMessage: "a lowest role the panel does not know is never reached");
        }
        var form = await browser.GetAsync("/commands/test_owner");
        Regex.Match(form.Html, "<button[^>]*value=\"review\"[^>]*>").Value.ShouldContain(" disabled");
    }

    [Fact]
    public async Task A_greyed_out_command_sent_anyway_shows_the_servers_refusal()
    {
        var (server, panel, browser) = await StartAsync("support", r => r.Method == "POST"
            ? new FakeAnswer(403, """{"outcome":"refused","request_id":"srv-1","audit_id":null,"error":"forbidden","result":null}""")
            : null);
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        const string path = "/commands/test_admin";
        var form = await browser.GetAsync(path);
        Regex.Match(form.Html, "<button[^>]*value=\"review\"[^>]*>").Value.ShouldContain(" disabled");
        var review = await browser.SubmitCommandAsync(path, form, ("step", "review"), ("reason", "testing the refusal"));
        var done = await browser.SubmitCommandAsync(path, review, ("step", "confirm"));
        done.Status.ShouldBe(200);
        done.Html.ShouldContain("data-outcome=\"refused\"");
        done.Text.ShouldContain("The server refused: nothing was changed.");
        done.Text.ShouldContain("Your role is not allowed to do this.");
        server.Requests.Single(r => r.Method == "POST").Headers[AdminHeaders.StaffRole].ShouldBe("support");
    }

    [Fact]
    public async Task A_command_is_shown_before_it_is_sent_and_sent_once()
    {
        var (server, panel, browser) = await StartAsync("support", r => r.Method == "POST"
            ? new FakeAnswer(Body: """{"outcome":"accepted","request_id":"srv-2","audit_id":12,"error":null,"result":{"was_online":true,"sessions_ended":2}}""")
            : null);
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        var path = $"/commands/kick?account_id={AccountId}";
        var form = await browser.GetAsync(path);
        form.Text.ShouldContain("No personal data in reasons.");

        var missing = await browser.SubmitCommandAsync(path, form, ("step", "review"), ("account_id", AccountId.ToString()), ("reason", "   "));
        missing.Text.ShouldContain("A reason is required");
        server.Requests.ShouldNotContain(r => r.Method == "POST");

        var review = await browser.SubmitCommandAsync(path, form, ("step", "review"), ("account_id", AccountId.ToString()), ("reason", "spam\r\nin the square "));
        review.Text.ShouldContain("Account ada");
        review.Text.ShouldContain("No personal data in reasons.");
        review.Text.ShouldContain("\"reason\": \"spam\\nin the square\"");
        server.Requests.ShouldNotContain(r => r.Method == "POST", "nothing is sent before the confirmation");

        var done = await browser.SubmitCommandAsync(path, review, ("step", "confirm"));
        done.Html.ShouldContain("data-outcome=\"accepted\"");
        done.Text.ShouldContain("audit row 12");
        done.Text.ShouldContain("sessions_ended");
        var sent = server.Requests.Single(r => r.Method == "POST");
        sent.PathAndQuery.ShouldBe("/admin/v1/commands/kick");
        System.Text.Json.JsonDocument.Parse(sent.Body).RootElement.EnumerateObject().Select(x => (x.Name, x.Value.GetString()))
            .ShouldBe([("account_id", AccountId.ToString()), ("reason", "spam\nin the square")]);

        // A reload, the back button or a double press posts the same confirmation again.
        var again = await browser.SubmitCommandAsync(path, review, ("step", "confirm"));
        again.Text.ShouldContain("was used already or has expired");
        server.Requests.Count(r => r.Method == "POST").ShouldBe(1);
        panel.Logs.Text.ShouldNotContain("in the square");
        panel.Logs.Text.ShouldContain("ran 'kick': accepted");
    }

    [Fact]
    public async Task A_command_whose_fate_is_unknown_says_so()
    {
        var (server, panel, browser) = await StartAsync("support", r => r.Method == "POST" ? new FakeAnswer(Abort: true) : null);
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        var path = $"/commands/kick?account_id={AccountId}";
        var review = await browser.SubmitCommandAsync(path, await browser.GetAsync(path), ("step", "review"), ("account_id", AccountId.ToString()), ("reason", "r"));
        var done = await browser.SubmitCommandAsync(path, review, ("step", "confirm"));
        done.Text.ShouldContain("does not know whether the server applied the command");
        server.Requests.Count(r => r.Method == "POST").ShouldBe(1);
    }

    [Fact]
    public async Task A_command_without_a_page_gets_a_form_of_named_typed_fields()
    {
        var (server, panel, browser) = await StartAsync("gm", r => r.Method == "POST"
            ? new FakeAnswer(400, """{"outcome":"refused","request_id":"srv-3","audit_id":null,"error":"invalid_request","result":null}""")
            : null);
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        var path = $"/commands/test_gm?account_id={AccountId}";
        var form = await browser.GetAsync(path);
        form.Text.ShouldContain("no page of its own");
        form.Html.ShouldContain($"value=\"{AccountId}\"");
        var review = await browser.SubmitCommandAsync(path, form, ("step", "review"), ("field_name_0", "account_id"), ("field_value_0", AccountId.ToString()),
            ("field_name_1", "minutes"), ("field_kind_1", "number"), ("field_value_1", "30"), ("field_name_2", "silent"), ("field_kind_2", "boolean"),
            ("field_value_2", "true"), ("reason", "a test"));
        var done = await browser.SubmitCommandAsync(path, review, ("step", "confirm"));
        done.Text.ShouldContain("The server did not accept the request");
        var body = System.Text.Json.JsonDocument.Parse(server.Requests.Single(r => r.Method == "POST").Body).RootElement;
        (body.GetProperty("account_id").GetString(), body.GetProperty("minutes").GetInt64(), body.GetProperty("silent").GetBoolean(), body.GetProperty("reason").GetString())
            .ShouldBe((AccountId.ToString(), 30L, true, "a test"));
        (await browser.GetAsync("/commands/not_listed")).Text.ShouldContain("The server lists no command");
    }

    [Theory]
    [InlineData("reason", "text", "x", "its own box")]
    [InlineData("Bad Name", "text", "x", "not a field name")]
    [InlineData("", "text", "x", "needs a name")]
    [InlineData("n", "number", "1.5", "whole number")]
    [InlineData("n", "boolean", "yes", "true or false")]
    [InlineData("n", "object", "{}", "no known kind")]
    public void The_generic_form_refuses_what_it_cannot_send_as_typed(string name, string kind, string value, string problem)
    {
        var body = CommandForms.Build("test_gm", [new CommandField(name, kind, value)], "r");
        body.Json.ShouldBeNull();
        body.Problem!.ShouldContain(problem);
    }

    [Fact]
    public void The_generic_form_bounds_what_it_sends()
    {
        CommandForms.Build("test_gm", [new("a", "text", "1"), new("a", "text", "2")], "r").Problem!.ShouldContain("twice");
        CommandForms.Build("test_gm", Enumerable.Range(0, 9).Select(i => new CommandField("f" + i, "text", "x")).ToList(), "r").Problem.ShouldNotBeNull();
        CommandForms.Build("test_gm", [new("a", "text", new string('x', 1001))], "r").Problem.ShouldNotBeNull();
        CommandForms.Build("test_gm", [], new string('r', 501)).Problem!.ShouldContain("reason");
        CommandForms.Build("test_gm", [new("", "text", ""), new("a", "text", "")], "r").Json.ShouldNotBeNull();
        CommandForms.Build("kick", [new("account_id", "text", "not-an-id")], "r").Problem.ShouldNotBeNull();
        CommandForms.Build("kick", [new("account_id", "text", AccountId.ToString()), new("extra", "text", "x")], "r").Json!.ShouldNotContain("extra");
    }

    [Fact]
    public void A_confirmation_is_taken_once_by_its_own_sign_in_in_time()
    {
        var time = new FakeTime();
        var tickets = new CommandTickets(time);
        var ticket = tickets.Issue("s1", "kick", "{}");
        tickets.Take(ticket, "s2", "kick").ShouldBeNull();
        tickets.Take(ticket, "s1", "ban").ShouldBeNull();
        tickets.Take(ticket, "s1", "kick")!.Json.ShouldBe("{}");
        tickets.Take(ticket, "s1", "kick").ShouldBeNull();
        var late = tickets.Issue("s1", "kick", "{}");
        time.Now += CommandTickets.Lifetime + TimeSpan.FromSeconds(1);
        tickets.Take(late, "s1", "kick").ShouldBeNull();
        var first = tickets.Issue("s1", "kick", "{}");
        for (var i = 0; i < CommandTickets.MaxPending; i++)
        {
            time.Now += TimeSpan.FromMilliseconds(1);
            tickets.Issue("s1", "kick", "{}");
        }
        tickets.Take(first, "s1", "kick").ShouldBeNull("the oldest makes room");
    }

    [Fact]
    public async Task A_confirmation_made_by_one_sign_in_is_no_use_to_another()
    {
        var (server, panel, browser) = await StartAsync("admin");
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        using var other = panel.Browser();
        await other.SignInAsync("support", "Other");
        var path = $"/commands/kick?account_id={AccountId}";
        var review = await browser.SubmitCommandAsync(path, await browser.GetAsync(path), ("step", "review"), ("account_id", AccountId.ToString()), ("reason", "r"));
        var theirs = await other.GetAsync(path);
        var stolen = await other.SubmitCommandAsync(path, theirs, ("step", "confirm"), ("ticket", review.Hidden("name=\"_handler\"")["ticket"]));
        stolen.Text.ShouldContain("was used already or has expired");
        server.Requests.ShouldNotContain(r => r.Method == "POST");
    }

    [Fact]
    public async Task Staff_id_and_role_come_from_the_sign_in_whatever_the_browser_sends()
    {
        var (server, panel, browser) = await StartAsync("support", r => r.Method == "POST" ? new FakeAnswer(403, """{"error":"forbidden","request_id":"r"}""") : null);
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        var path = $"/commands/kick?account_id={AccountId}&role=admin&staff_id=boss";
        await browser.GetAsync("/accounts?role=admin", request =>
        {
            request.Headers.Add(AdminHeaders.StaffRole, "admin");
            request.Headers.Add(AdminHeaders.StaffId, "boss");
        });
        var review = await browser.SubmitCommandAsync(path, await browser.GetAsync(path), ("step", "review"), ("account_id", AccountId.ToString()), ("reason", "r"),
            ("role", "admin"), ("staff_id", "boss"));
        await browser.SubmitCommandAsync(path, review, ("step", "confirm"), (AdminHeaders.StaffRole, "admin"));
        server.Requests.ShouldContain(r => r.Method == "POST");
        foreach (var request in server.Requests)
            (request.Headers[AdminHeaders.StaffId], request.Headers[AdminHeaders.StaffRole]).ShouldBe(("development|robinq", "support"));
    }

    [Fact]
    public async Task Pages_show_the_data_with_names_from_content()
    {
        var (server, panel, browser) = await StartAsync();
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        var names = ContentNames.Of(TestGrids.SampleContent);

        var status = await browser.GetAsync("/");
        status.Html.ShouldContain("data-fact=\"players_online\">3<");
        status.Text.ShouldContain("every 10 s");
        status.Text.ShouldContain("The panel's content is not the server's");

        var accounts = await browser.GetAsync("/accounts?name=ad");
        accounts.Html.ShouldContain($"href=\"/accounts/{AccountId}\"");
        accounts.Html.ShouldContain("href=\"/accounts?name=ad&amp;after=ada\"");
        server.Requests.ShouldContain(r => r.PathAndQuery == "/admin/v1/accounts?name=ad");
        (await browser.GetAsync("/accounts?name=ad&after=ada&id=")).Status.ShouldBe(200);
        server.Requests.ShouldContain(r => r.PathAndQuery == "/admin/v1/accounts?name=ad&after=ada");

        (await browser.GetAsync($"/accounts/{AccountId}")).Text.ShouldContain("Lyra");
        var character = await browser.GetAsync($"/characters/{CharacterId}");
        character.Text.ShouldContain($"{names.NameOf(ItemId)} ({ItemId})");
        character.Text.ShouldContain(names.Describe(MapId));
        character.Text.ShouldContain(names.Describe(JobId));
        character.Text.ShouldContain("item.gone_from_content", customMessage: "an id content no longer has is shown as it is");
        character.Text.ShouldContain("quest.gone_from_content");
        character.Text.ShouldContain("950");

        var audit = await browser.GetAsync("/audit?staff=oidc%7Ckim&from=2026-11-01T00%3A00");
        audit.Html.ShouldContain("href=\"/audit?staff=oidc%7Ckim&amp;from=2026-11-01T00%3A00&amp;before=5\"");
        audit.Html.ShouldNotContain("<script>alert", customMessage: "a reason is text, never markup");
        audit.Html.ShouldNotContain("Kim <b>");
        server.Requests.ShouldContain(r => r.PathAndQuery == "/admin/v1/audit?staff=oidc%7Ckim&from=2026-11-01T00%3A00");
    }

    [Theory]
    [InlineData("rate_limited", 429, "Too many requests")]
    [InlineData("unauthorized", 401, "refused the panel&#x27;s service credential|refused the panel's service credential")]
    [InlineData("unavailable", 503, "database could not answer")]
    public async Task A_read_the_server_refuses_says_why(string term, int status, string words)
    {
        var (server, panel, browser) = await StartAsync("support", _ => new FakeAnswer(status, $$"""{"error":"{{term}}","request_id":"r"}"""));
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        foreach (var path in new[] { "/", "/accounts", $"/accounts/{AccountId}", "/characters", $"/characters/{CharacterId}", "/audit", "/commands", "/commands/kick" })
        {
            var page = await browser.GetAsync(path);
            page.Status.ShouldBe(200, path);
            words.Split('|').ShouldContain(w => page.Text.Contains(w), path);
        }
    }

    [Fact]
    public async Task Every_answer_has_the_strict_headers_and_no_inline_or_foreign_code()
    {
        var (server, panel, browser) = await StartAsync();
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        foreach (var path in AllPages.Append("/nothing-here"))
        {
            var page = await browser.GetAsync(path);
            page.Status.ShouldBe(path == "/nothing-here" ? 404 : 200, path);
            var csp = string.Join(",", page.Response.Headers.GetValues("Content-Security-Policy"));
            csp.ShouldBe(SecurityHeaders.ContentSecurityPolicy, path);
            csp.ShouldNotContain("unsafe-");
            page.Response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
            page.Response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
            page.Response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
            page.Response.Headers.CacheControl!.NoStore.ShouldBeTrue(path);
            page.Response.Headers.Contains("Server").ShouldBeFalse();
            if (path == "/nothing-here") continue;
            foreach (Match script in Regex.Matches(page.Html, "<script\\b([^>]*)>(.*?)</script>", RegexOptions.Singleline))
            {
                script.Groups[1].Value.ShouldContain("src=\"_framework/", customMessage: path);
                script.Groups[2].Value.Trim().ShouldBeEmpty(path);
            }
            page.Html.ShouldNotContain("<style", Case.Insensitive, path);
            Regex.IsMatch(page.Html, "<[^>]*\\sstyle=").ShouldBeFalse(path);
            Regex.IsMatch(page.Html, "<[^>]*\\son[a-z]+=").ShouldBeFalse(path);
            Regex.IsMatch(page.Html, "(src|href|action)=\"(https?:)?//").ShouldBeFalse(path);
            page.Html.ShouldNotContain("=\"data:", customMessage: "the policy loads nothing from a data: address");
        }
        foreach (var file in new[] { "/panel.css", "/_framework/blazor.web.js" })
        {
            using var anonymous = panel.Browser();
            var served = await anonymous.GetAsync(file);
            served.Status.ShouldBe(200, file);
            served.Html.Length.ShouldBeGreaterThan(500, file);
        }
    }

    [Fact]
    public async Task Nothing_is_served_to_somebody_not_signed_in()
    {
        var (server, panel, signedIn) = await StartAsync();
        await using var s = server;
        await using var p = panel;
        signedIn.Dispose();
        var before = server.Requests.Count;
        using var browser = panel.Browser();
        foreach (var path in AllPages.Where(a => a != "/signin"))
        {
            var page = await browser.GetAsync(path);
            page.Status.ShouldBe(302, path);
            page.Location!.ShouldContain("/signin?returnUrl=", customMessage: path);
        }
        (await browser.PostAsync("/_blazor/negotiate?negotiateVersion=1", [])).Status.ShouldNotBe(200, "no live connection without a sign-in");
        server.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_post_without_its_antiforgery_token_does_nothing()
    {
        var (server, panel, browser) = await StartAsync();
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        var path = $"/commands/kick?account_id={AccountId}";
        var review = await browser.SubmitCommandAsync(path, await browser.GetAsync(path), ("step", "review"), ("account_id", AccountId.ToString()), ("reason", "r"));
        var fields = review.Hidden("name=\"_handler\"");
        fields["step"] = "confirm";
        fields.Remove("__RequestVerificationToken").ShouldBeTrue();
        (await browser.PostAsync(path, fields)).Status.ShouldBe(400);
        server.Requests.ShouldNotContain(r => r.Method == "POST");
        (await browser.PostAsync("/signout", [])).Status.ShouldBe(400);
        (await browser.GetAsync("/commands")).Status.ShouldBe(200, "still signed in");
        using var anonymous = panel.Browser();
        (await anonymous.PostAsync("/signin/development", [new("role", "admin"), new("name", "x")])).Status.ShouldBe(400);
        (await anonymous.GetAsync("/commands")).Status.ShouldBe(302);
    }

    [Fact]
    public async Task A_request_naming_another_host_is_turned_away()
    {
        await using var server = await FakeAdminServer.StartAsync();
        await using var panel = await PanelRig.StartAsync(server.Address, new Dictionary<string, string?> { ["AllowedHosts"] = "*" });
        using var browser = panel.Browser();
        (await browser.GetAsync("/signin")).Status.ShouldBe(200);
        (await browser.GetAsync("/signin", r => r.Headers.Host = "panel.attacker.example")).Status.ShouldBe(400, "even with the framework's own AllowedHosts open");
        (await browser.GetAsync("/signin", r => r.Headers.Host = "localhost")).Status.ShouldBe(200);
    }

    [Fact]
    public async Task No_secret_is_on_a_page_or_in_a_log_at_any_level()
    {
        var (server, panel, browser) = await StartAsync("admin", r => r.Method == "POST"
            ? new FakeAnswer(Body: """{"outcome":"done","request_id":"srv-4","audit_id":1,"error":null,"result":{}}""")
            : null);
        await using var s = server;
        await using var p = panel;
        using var b = browser;
        foreach (var path in AllPages) await browser.GetAsync(path);
        var kick = $"/commands/kick?account_id={AccountId}";
        var review = await browser.SubmitCommandAsync(kick, await browser.GetAsync(kick), ("step", "review"), ("account_id", AccountId.ToString()), ("reason", "private words"));
        await browser.SubmitCommandAsync(kick, review, ("step", "confirm"));
        // And when calls fail: refused credential, no answer.
        server.Answer = _ => new FakeAnswer(401, """{"error":"unauthorized","request_id":"r"}""");
        await browser.GetAsync("/accounts");
        server.Answer = _ => new FakeAnswer(Abort: true);
        await browser.GetAsync("/accounts");

        server.Requests.ShouldContain(r => r.Headers["Authorization"].Contains(AdminRig.Credential), "the server did get it");
        browser.Seen.Count.ShouldBeGreaterThan(AllPages.Length);
        foreach (var page in browser.Seen)
        {
            var all = page.Html + page.Response.Headers + page.Response.Content.Headers;
            all.ShouldNotContain(AdminRig.Credential);
            all.ShouldNotContain(PanelRig.ClientSecret);
            all.ShouldNotContain("Bearer ", Case.Sensitive);
        }
        var log = panel.Logs.Text;
        log.ShouldContain("Request starting", customMessage: "the log was captured at its most talkative");
        log.ShouldNotContain(AdminRig.Credential);
        log.ShouldNotContain(PanelRig.ClientSecret);
        log.ShouldNotContain("Bearer ", Case.Sensitive);
        log.ShouldNotContain("private words");
    }
}
