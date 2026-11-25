using System.Net.Sockets;
using System.Text;
using Game.Server.Accounts;
using Game.Server.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Network;

namespace Game.Tests.Accounts;

/// <summary>T-701: the account endpoints over real HTTP, on the gateway's own listener.</summary>
public class AccountEndpointTests
{
    private static async Task<(GatewayRig Rig, AccountHttp Http, MemoryAccountStore Store)> StartAsync(AccountLimits? limits = null, bool development = true, bool tls = false)
    {
        var store = new MemoryAccountStore();
        var rig = await GatewayRig.StartAsync(development: development, tls: tls,
            accounts: new AccountRigOptions(store, new FakeHasher(), limits ?? new AccountLimits { RequestBurst = 10_000, RegisterBurst = 1_000 }, new FakeTime()));
        return (rig, new AccountHttp(rig.Endpoint), store);
    }

    [Fact]
    public async Task The_whole_life_of_an_account_over_HTTP()
    {
        var (rig, http, _) = await StartAsync();
        await using var _1 = rig;
        using var _2 = http;

        var (status, body, response) = await http.PostAsync(AccountPaths.Register, new { username = "Ada", password = ServiceRig.Password });
        status.ShouldBe(201);
        body.GetProperty("token").GetString()!.Length.ShouldBe(SessionTokens.TokenLength);
        body.GetProperty("expires_in").GetInt64().ShouldBe(86_400);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var token = body.GetProperty("token").GetString()!;

        (status, body, _) = await http.PostAsync(AccountPaths.Characters, new { name = "Lyra" }, token);
        status.ShouldBe(201);
        var id = body.GetProperty("id").GetString()!;
        body.GetProperty("slot").GetInt32().ShouldBe(0);

        (status, body, _) = await http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: token);
        status.ShouldBe(200);
        body.GetProperty("slots").GetInt32().ShouldBe(3);
        body.GetProperty("characters")[0].GetProperty("name").GetString().ShouldBe("Lyra");

        (await http.SendAsync(HttpMethod.Delete, AccountPaths.Characters + "/" + id, token: token)).Status.ShouldBe(204);
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: token)).Body.GetProperty("characters").GetArrayLength().ShouldBe(0);
        (await http.PostAsync(AccountPaths.Logout, token: token)).Status.ShouldBe(204);
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: token)).Status.ShouldBe(401);
        (status, body, _) = await http.PostAsync(AccountPaths.Login, new { username = "ADA", password = ServiceRig.Password });
        status.ShouldBe(200);
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: body.GetProperty("token").GetString())).Status.ShouldBe(200);
    }

    [Fact]
    public async Task Every_refusal_has_its_status_and_term()
    {
        var (rig, http, store) = await StartAsync();
        await using var _1 = rig;
        using var _2 = http;
        var ada = await http.RegisterAsync("ada");
        var bob = await http.RegisterAsync("bobby");
        var adas = await http.CreateAsync(ada, "Lyra");
        await http.CreateAsync(ada, "Mira");
        await http.CreateAsync(ada, "Nara");

        async Task Refused(int status, string error, Task<(int Status, System.Text.Json.JsonElement Body, HttpResponseMessage Response)> call)
        {
            var (actual, body, _) = await call;
            (actual, body.GetProperty("error").GetString()).ShouldBe((status, error));
            AccountErrors.All.Contains(error).ShouldBeTrue();
        }
        var good = ServiceRig.Password;
        await Refused(400, "invalid_username", http.PostAsync(AccountPaths.Register, new { username = "a", password = good }));
        await Refused(400, "invalid_password", http.PostAsync(AccountPaths.Register, new { username = "carl", password = "short" }));
        await Refused(409, "username_taken", http.PostAsync(AccountPaths.Register, new { username = "ADA", password = good }));
        await Refused(400, "invalid_username", http.PostAsync(AccountPaths.Login, new { username = "a b", password = good }));
        await Refused(400, "invalid_password", http.PostAsync(AccountPaths.Login, new { username = "ada", password = "short" }));
        await Refused(401, "invalid_credentials", http.PostAsync(AccountPaths.Login, new { username = "ada", password = good + "x" }));
        await Refused(401, "invalid_credentials", http.PostAsync(AccountPaths.Login, new { username = "nobody", password = good }));
        foreach (var token in new[] { null, "x", new string('A', 43) })
        {
            await Refused(401, "invalid_session", http.PostAsync(AccountPaths.Logout, token: token));
            await Refused(401, "invalid_session", http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: token));
            await Refused(401, "invalid_session", http.PostAsync(AccountPaths.Characters, new { name = "Opal" }, token));
            await Refused(401, "invalid_session", http.PostAsync(AccountPaths.Password, new { current = good, @new = good + "x" }, token));
            await Refused(401, "invalid_session", http.SendAsync(HttpMethod.Delete, AccountPaths.Characters + "/" + adas, token: token));
        }
        await Refused(400, "invalid_password", http.PostAsync(AccountPaths.Password, new { current = good, @new = "short" }, ada));
        await Refused(400, "same_password", http.PostAsync(AccountPaths.Password, new { current = good, @new = good }, ada));
        await Refused(401, "invalid_credentials", http.PostAsync(AccountPaths.Password, new { current = good + "x", @new = good + "y" }, ada));
        foreach (var bad in new[] { "", "[]", "{}", """{"current":"1234567890"}""", """{"current":"1234567890","new":1}""", """{"current":"1234567890","new":"a","new":"b"}""" })
            await Refused(400, "invalid_request", http.SendAsync(HttpMethod.Post, AccountPaths.Password, bad, ada));
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Password, token: ada)).Status.ShouldBe(405);
        await Refused(400, "invalid_name", http.PostAsync(AccountPaths.Characters, new { name = "L1" }, bob));
        await Refused(409, "name_taken", http.PostAsync(AccountPaths.Characters, new { name = "LYRA" }, bob));
        await Refused(409, "no_free_slot", http.PostAsync(AccountPaths.Characters, new { name = "Opal" }, ada));
        await Refused(404, "unknown_character", http.SendAsync(HttpMethod.Delete, AccountPaths.Characters + "/" + adas, token: bob));
        await Refused(400, "invalid_request", http.SendAsync(HttpMethod.Delete, AccountPaths.Characters + "/not-a-guid", token: ada));
        rig.Accounts!.InWorld = c => c == Guid.Parse(adas);
        await Refused(409, "character_in_use", http.SendAsync(HttpMethod.Delete, AccountPaths.Characters + "/" + adas, token: ada));
        store.Away = true;
        var (status, body, response) = await http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: ada);
        status.ShouldBe(503);
        body.GetProperty("error").GetString().ShouldBe("unavailable");
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"username":"ada"}""")]
    [InlineData("""{"username":"ada","password":1234567890}""")]
    [InlineData("""{"username":null,"password":"1234567890"}""")]
    [InlineData("""{"username":"ada","username":"bob","password":"1234567890"}""")]
    [InlineData("""{"username":"ada","password":"1234567890"} x""")]
    [InlineData("""{"username":"ada","password":"\ud800567890"}""")]
    public async Task A_body_that_is_not_what_the_endpoint_expects_is_refused(string body)
    {
        var (rig, http, store) = await StartAsync();
        await using var _1 = rig;
        using var _2 = http;
        foreach (var path in new[] { AccountPaths.Register, AccountPaths.Login })
        {
            var (status, answer, _) = await http.SendAsync(HttpMethod.Post, path, body);
            (status, answer.GetProperty("error").GetString()).ShouldBe((400, "invalid_request"));
        }
        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Bodies_are_bounded_and_must_be_JSON()
    {
        var (rig, http, store) = await StartAsync();
        await using var _1 = rig;
        using var _2 = http;
        var json = """{"username":"ada","password":"correct horse battery"}""";
        (await http.SendAsync(HttpMethod.Post, AccountPaths.Register, json, contentType: "text/plain")).Status.ShouldBe(400);
        var longest = $$"""{"username":"ada","password":"{{new string('x', 128)}}","x":"{{new string('y', AccountEndpoints.MaxBodyBytes)}}"}""";
        (await http.SendAsync(HttpMethod.Post, AccountPaths.Register, longest)).Status.ShouldBe(400);
        // The same without a Content-Length: the reading itself is bounded.
        using var chunked = new HttpRequestMessage(HttpMethod.Post, new UriBuilder(rig.Endpoint) { Scheme = "http", Path = AccountPaths.Register }.Uri)
        {
            Content = new StringContent(longest, Encoding.UTF8, "application/json"),
        };
        chunked.Headers.TransferEncodingChunked = true;
        using var client = new HttpClient();
        ((int)(await client.SendAsync(chunked)).StatusCode).ShouldBe(400);
        store.Calls.ShouldBe(0);
        // The longest password with every character escaped still fits.
        var escaped = string.Concat(Enumerable.Repeat("\\u00e9", 128));
        (await http.SendAsync(HttpMethod.Post, AccountPaths.Register, $$"""{"username":"abcdefghijklmnopqrstuvwx","password":"{{escaped}}"}""")).Status.ShouldBe(201);
    }

    [Fact]
    public async Task Other_paths_and_methods_are_not_served_and_the_game_socket_still_is()
    {
        var (rig, http, _) = await StartAsync();
        await using var _1 = rig;
        using var _2 = http;
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Login)).Status.ShouldBe(405);
        (await http.SendAsync(HttpMethod.Put, AccountPaths.Characters)).Status.ShouldBe(405);
        (await http.SendAsync(HttpMethod.Post, AccountPaths.Characters + "/x")).Status.ShouldBe(405);
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Root)).Status.ShouldBe(404);
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Root + "/other")).Status.ShouldBe(404);
        (await http.SendAsync(HttpMethod.Get, "/accounting")).Status.ShouldBe(404);
        (await rig.JoinAsync()).Id.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Requests_are_limited_per_address_before_anything_else()
    {
        var (rig, http, store) = await StartAsync(new AccountLimits { RequestBurst = 3, RequestsPerMinute = 60 });
        await using var _1 = rig;
        using var _2 = http;
        for (var i = 0; i < 3; i++) (await http.SendAsync(HttpMethod.Get, AccountPaths.Characters)).Status.ShouldBe(401);
        var (status, body, response) = await http.PostAsync(AccountPaths.Register, new { username = "ada", password = ServiceRig.Password });
        (status, body.GetProperty("error").GetString(), body.GetProperty("retry_after").GetInt64()).ShouldBe((429, "rate_limited", 1));
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBe(1);
        store.Calls.ShouldBe(0);
        rig.Clock.Advance(1000);
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Characters)).Status.ShouldBe(401);
    }

    [Fact]
    public async Task Outside_development_the_endpoints_speak_TLS_only()
    {
        var (rig, http, _) = await StartAsync(development: false, tls: true);
        await using var _1 = rig;
        using var _2 = http;
        rig.Endpoint.Scheme.ShouldBe("wss");
        await http.RegisterAsync("ada");
        using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = new UriBuilder(rig.Endpoint) { Scheme = "http", Path = AccountPaths.Login }.Uri;
        await Should.ThrowAsync<Exception>(() => plain.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json")));
    }

    [Fact]
    public async Task A_client_that_leaves_in_the_middle_of_its_body_harms_nobody()
    {
        var (rig, http, _) = await StartAsync();
        await using var _1 = rig;
        using var _2 = http;
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Endpoint.Host, rig.Endpoint.Port);
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST {AccountPaths.Login} HTTP/1.1\r\nHost: x\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{{\"user"));
        }
        await http.RegisterAsync("ada");
        rig.Log.Lines.ShouldNotContain(l => l.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_password_token_or_username_ever_reaches_the_log()
    {
        var (rig, http, store) = await StartAsync();
        await using var _1 = rig;
        using var _2 = http;
        const string user = "unmistakable.user", secret = "unmistakable-password-1", wrong = "unmistakable-password-2";
        var token = await http.RegisterAsync(user, secret);
        await http.PostAsync(AccountPaths.Register, new { username = user, password = secret });
        for (var i = 0; i < 8; i++) await http.PostAsync(AccountPaths.Login, new { username = user, password = wrong });
        rig.Clock.Advance(1_000_000);
        var second = (await http.PostAsync(AccountPaths.Login, new { username = user, password = secret })).Body.GetProperty("token").GetString()!;
        var id = await http.CreateAsync(token, "Lyra");
        var client = await rig.PlayAsync(token, id);
        await client.WaitForAsync<WelcomeMessage>();
        var refused = await rig.PlayAsync(new string('Z', 43), id);
        await refused.WaitForByeAsync();
        store.Away = true;
        await http.PostAsync(AccountPaths.Login, new { username = user, password = secret });
        store.Away = false;
        await http.PostAsync(AccountPaths.Password, new { current = wrong, @new = secret + "x" }, token);
        (await http.PostAsync(AccountPaths.Password, new { current = secret, @new = wrong }, token)).Status.ShouldBe(200);
        await http.SendAsync(HttpMethod.Get, AccountPaths.Root + "/" + token);
        await http.PostAsync(AccountPaths.Logout, token: second);
        var log = string.Join("\n", rig.Log.Lines);
        log.ShouldContain("[accounts]");
        foreach (var value in new[] { user, secret, wrong, token, second, new string('Z', 43), client.Token }) log.ShouldNotContain(value, Case.Insensitive);
    }

    [Fact]
    public async Task A_password_change_answers_with_a_new_session()
    {
        var (rig, http, _) = await StartAsync();
        await using var _1 = rig;
        using var _2 = http;
        var token = await http.RegisterAsync("ada");
        var (status, body, response) = await http.PostAsync(AccountPaths.Password, new { current = ServiceRig.Password, @new = "a brand new one" }, token);
        status.ShouldBe(200);
        body.GetProperty("expires_in").GetInt64().ShouldBe(86_400);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: token)).Status.ShouldBe(401);
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: body.GetProperty("token").GetString())).Status.ShouldBe(200);
        (await http.PostAsync(AccountPaths.Login, new { username = "ada", password = ServiceRig.Password })).Status.ShouldBe(401);
        (await http.PostAsync(AccountPaths.Login, new { username = "ada", password = "a brand new one" })).Status.ShouldBe(200);
    }
}
