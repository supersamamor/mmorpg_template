using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Game.Admin;
using Game.Admin.Api;
using Game.Admin.Auth;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Admin;

namespace Game.Tests.AdminPanel;

/// <summary>T-805: the panel's one way to call the server, against a fake server: the five headers, one attempt, a timeout, and every refusal as a term with words.</summary>
public class PanelClientTests
{
    private static readonly Staff Robin = new("oidc|robin", "Robin Ünal %", StaffRoles.Gm, "s", DateTimeOffset.MaxValue);

    private static AdminApiClient Client(Uri server, int timeoutMs = 10_000)
    {
        var settings = new PanelSettings { ServerUrl = server.GetLeftPart(UriPartial.Authority) };
        var http = new HttpClient(AdminApiClient.Handler(settings)) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        return new AdminApiClient(http, new PanelCredential("  " + AdminRig.Credential + "\n"), settings);
    }

    [Fact]
    public async Task Every_request_carries_the_five_headers()
    {
        await using var server = await FakeAdminServer.StartAsync();
        var client = Client(server.Address);
        (await client.GetStatusAsync(Robin)).Value!.PlayersOnline.ShouldBe(3);
        (await client.GetCommandsAsync(Robin)).Value!.Commands[0].ShouldBe(new CommandInfo("kick", "support"));
        server.Answer = r => r.Method == "POST" ? new FakeAnswer(Body: """{"outcome":"accepted","request_id":"r","audit_id":7,"error":null,"result":{"sessions_ended":2}}""") : null;
        var kicked = await client.RunCommandAsync(Robin, "kick", """{"account_id":"a","reason":"r"}""");
        (kicked.Answer!.Outcome, kicked.Answer.AuditId, kicked.OutcomeUnknown, kicked.Failure).ShouldBe((AdminOutcomes.Accepted, 7L, false, (ApiFailure?)null));

        server.Requests.Count.ShouldBe(3);
        foreach (var request in server.Requests)
        {
            request.Headers["Authorization"].ShouldBe("Bearer " + AdminRig.Credential);
            request.Headers[AdminHeaders.StaffId].ShouldBe("oidc|robin");
            Uri.UnescapeDataString(request.Headers[AdminHeaders.StaffName]).ShouldBe("Robin Ünal %");
            request.Headers[AdminHeaders.StaffName].ShouldAllBe(c => c < 128);
            request.Headers[AdminHeaders.StaffRole].ShouldBe("gm");
            request.Headers[AdminHeaders.RequestId].ShouldMatch("^[0-9a-f]{32}$");
            request.Headers.ShouldNotContainKey("Cookie");
        }
        server.Requests.Select(r => r.Headers[AdminHeaders.RequestId]).Distinct().Count().ShouldBe(3);
        var post = server.Requests[2];
        (post.PathAndQuery, post.Body, post.Headers["Content-Type"]).ShouldBe(("/admin/v1/commands/kick", """{"account_id":"a","reason":"r"}""", "application/json"));
        post.Headers[AdminHeaders.RequestId].ShouldBe(kicked.RequestId);
    }

    [Fact]
    public async Task Search_values_are_escaped_and_empty_ones_left_out()
    {
        await using var server = await FakeAdminServer.StartAsync();
        var client = Client(server.Address);
        await client.FindAccountsAsync(Robin, "a&id=x #?", "", null);
        await client.FindCharactersAsync(Robin, null, null, null, null);
        await client.SearchAuditAsync(Robin, "t/..", "oidc|x", "kick", "2026-01-01T00:00", null, "9");
        server.Requests.Select(r => r.PathAndQuery).ShouldBe([
            "/admin/v1/accounts?name=a%26id%3Dx%20%23%3F",
            "/admin/v1/characters",
            "/admin/v1/audit?target=t%2F..&staff=oidc%7Cx&action=kick&from=2026-01-01T00%3A00&before=9",
        ]);
    }

    [Fact]
    public void Every_error_term_of_the_server_has_words()
    {
        foreach (var term in AdminErrors.All.Ordered)
        {
            ErrorWords.Knows(term).ShouldBeTrue(term);
            ErrorWords.For(term).ShouldNotContain(term);
        }
        foreach (var term in new[] { ErrorWords.Unreachable, ErrorWords.NoAnswerInTime, ErrorWords.BadAnswer, ErrorWords.Untrusted }) ErrorWords.Knows(term).ShouldBeTrue(term);
        ErrorWords.For("a_later_term").ShouldContain("a_later_term");
    }

    [Fact]
    public async Task Each_refusal_comes_back_as_its_term_after_one_request()
    {
        await using var server = await FakeAdminServer.StartAsync();
        var client = Client(server.Address);
        foreach (var term in AdminErrors.All.Ordered)
        {
            var before = server.Requests.Count;
            server.Answer = _ => new FakeAnswer(409, $$"""{"error":"{{term}}","request_id":"r"}""");
            var read = await client.GetStatusAsync(Robin);
            (read.Value, read.Failure!.Term, read.Failure.Status).ShouldBe(((AdminStatusAnswer?)null, term, 409));
            var plain = await client.RunCommandAsync(Robin, "kick", "{}");
            (plain.Answer, plain.Failure!.Term, plain.OutcomeUnknown).ShouldBe(((AdminCommandAnswer?)null, term, false));
            server.Answer = _ => new FakeAnswer(409, $$"""{"outcome":"refused","request_id":"r","audit_id":null,"error":"{{term}}","result":null}""");
            var refused = await client.RunCommandAsync(Robin, "kick", "{}");
            (refused.Answer!.Outcome, refused.Answer.Error, refused.OutcomeUnknown).ShouldBe((AdminOutcomes.Refused, term, false));
            (server.Requests.Count - before).ShouldBe(3, "nothing is tried again");
        }
    }

    [Fact]
    public async Task A_rate_limit_says_when_to_try_again_and_is_not_retried()
    {
        await using var server = await FakeAdminServer.StartAsync();
        server.Answer = _ => new FakeAnswer(429, """{"error":"rate_limited","request_id":"r"}""", Headers: new Dictionary<string, string> { ["Retry-After"] = "4" });
        var read = await Client(server.Address).GetStatusAsync(Robin);
        (read.Failure!.RetryAfterSeconds, read.Failure.Term).ShouldBe((4, AdminErrors.RateLimited));
        read.Failure.Words.ShouldContain("4 s");
        server.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(200, "<html>hello</html>")]
    [InlineData(200, "{}")]
    [InlineData(200, "null")]
    [InlineData(500, "")]
    [InlineData(404, "{\"error\":\"<script>\"}")]
    public async Task An_answer_that_is_not_the_APIs_is_said_to_be_so_and_a_commands_fate_unknown(int status, string body)
    {
        await using var server = await FakeAdminServer.StartAsync();
        server.Answer = _ => new FakeAnswer(status, body);
        var client = Client(server.Address);
        (await client.GetStatusAsync(Robin)).Failure!.Term.ShouldBe(ErrorWords.BadAnswer);
        var command = await client.RunCommandAsync(Robin, "kick", "{}");
        (command.Failure!.Term, command.OutcomeUnknown).ShouldBe((ErrorWords.BadAnswer, true));
        server.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_outcome_that_disagrees_with_its_status_is_not_believed()
    {
        await using var server = await FakeAdminServer.StartAsync();
        server.Answer = _ => new FakeAnswer(503, """{"outcome":"done","request_id":"r","audit_id":1,"error":null,"result":null}""");
        var command = await Client(server.Address).RunCommandAsync(Robin, "kick", "{}");
        (command.Answer, command.OutcomeUnknown).ShouldBe(((AdminCommandAnswer?)null, true));
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        await using var elsewhere = await FakeAdminServer.StartAsync();
        await using var server = await FakeAdminServer.StartAsync();
        server.Answer = _ => new FakeAnswer(307, "", Headers: new Dictionary<string, string> { ["Location"] = new Uri(elsewhere.Address, "/admin/v1/status").ToString() });
        var client = Client(server.Address);
        (await client.GetStatusAsync(Robin)).Failure!.Status.ShouldBe(307);
        (await client.RunCommandAsync(Robin, "kick", "{}")).OutcomeUnknown.ShouldBeTrue();
        elsewhere.Requests.ShouldBeEmpty("the credential went nowhere else");
    }

    [Fact]
    public async Task It_gives_up_in_time_and_sends_a_command_only_once()
    {
        await using var server = await FakeAdminServer.StartAsync();
        server.Answer = r => r.Method == "POST" ? new FakeAnswer(DelayMs: 20_000) : null;
        var client = Client(server.Address, timeoutMs: 500);
        var command = await client.RunCommandAsync(Robin, "kick", "{}");
        (command.Failure!.Term, command.OutcomeUnknown).ShouldBe((ErrorWords.NoAnswerInTime, true));
        server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_connection_that_drops_under_a_command_is_not_tried_again()
    {
        await using var server = await FakeAdminServer.StartAsync();
        var client = Client(server.Address);
        (await client.GetStatusAsync(Robin)).Value.ShouldNotBeNull(); // so the command goes over a connection that was used before
        server.Answer = r => r.Method == "POST" ? new FakeAnswer(Abort: true) : null;
        var command = await client.RunCommandAsync(Robin, "kick", "{}");
        (command.Failure!.Term, command.OutcomeUnknown).ShouldBe((ErrorWords.Unreachable, true));
        server.Requests.Count(r => r.Method == "POST").ShouldBe(1);
    }

    [Fact]
    public async Task A_server_that_is_not_there_is_unreachable()
    {
        var gone = await FakeAdminServer.StartAsync();
        var address = gone.Address;
        await gone.DisposeAsync();
        (await Client(address).GetStatusAsync(Robin)).Failure!.Term.ShouldBe(ErrorWords.Unreachable);
    }

    [Theory]
    [InlineData("../status")]
    [InlineData("kick/..")]
    [InlineData("kick?x=1")]
    [InlineData("Kick")]
    [InlineData("")]
    public async Task A_name_that_is_not_a_commands_is_never_put_in_a_path(string action)
    {
        await using var server = await FakeAdminServer.StartAsync();
        var command = await Client(server.Address).RunCommandAsync(Robin, action, "{}");
        command.Failure!.Term.ShouldBe(AdminErrors.NotFound);
        server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void A_pinned_certificate_adds_exactly_itself()
    {
        static X509Certificate2 Make(int fromDays, int toDays)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(fromDays), DateTimeOffset.UtcNow.AddDays(toDays));
        }
        using var pinned = Make(-1, 1);
        using var other = Make(-1, 1);
        using var old = Make(-3, -2);
        var now = DateTime.Now;
        const SslPolicyErrors chain = SslPolicyErrors.RemoteCertificateChainErrors;
        AdminApiClient.Trusts(pinned, pinned, chain, now).ShouldBeTrue();
        AdminApiClient.Trusts(pinned, other, SslPolicyErrors.None, now).ShouldBeTrue("the system's authorities still count");
        AdminApiClient.Trusts(pinned, other, chain, now).ShouldBeFalse();
        AdminApiClient.Trusts(pinned, pinned, chain | SslPolicyErrors.RemoteCertificateNameMismatch, now).ShouldBeFalse("another host's name");
        AdminApiClient.Trusts(pinned, null, SslPolicyErrors.RemoteCertificateNotAvailable, now).ShouldBeFalse();
        AdminApiClient.Trusts(old, old, chain, now).ShouldBeFalse("out of date");
    }

    [Fact]
    public void The_credential_never_prints_itself() => new PanelCredential(AdminRig.Credential).ToString().ShouldNotContain(AdminRig.Credential);

    [Fact]
    public void The_panel_never_references_the_server_and_pulls_nothing_from_elsewhere()
    {
        var panel = Path.Combine(Game.Tests.World.TestGrids.RepoRoot(), "src", "Game.Admin");
        File.ReadAllText(Path.Combine(panel, "Game.Admin.csproj")).ShouldNotContain("Game.Server.csproj");
        typeof(PanelApp).Assembly.GetReferencedAssemblies().Select(a => a.Name).ShouldNotContain("Game.Server");
        foreach (var file in Directory.EnumerateFiles(panel, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".razor") || f.EndsWith(".css")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            var text = File.ReadAllText(file);
            text.ShouldNotContain("http://", Case.Insensitive, file);
            text.ShouldNotContain("https://", Case.Insensitive, file);
            text.ShouldNotContain("@import", Case.Insensitive, file);
        }
    }
}
