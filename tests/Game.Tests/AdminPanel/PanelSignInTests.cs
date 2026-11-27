using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.Admin;
using Game.Admin.Auth;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Admin;
using Game.Tests.Network;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Game.Tests.AdminPanel;

/// <summary>
/// An OpenID Connect provider in process, for the tests only: discovery, keys, an authorization endpoint that signs in at once, and a token endpoint that checks the client
/// secret and the PKCE verifier before it signs an ID token.
/// </summary>
public sealed class FakeProvider : IAsyncDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, (string Challenge, string Nonce)> _codes = new();
    private WebApplication _app = null!;

    public Uri Address { get; private set; } = null!;

    public string Issuer => Address.GetLeftPart(UriPartial.Authority);

    /// <summary>The claims of the next sign-in, besides the protocol's.</summary>
    public Dictionary<string, object> Claims { get; set; } = new();

    /// <summary>The last authorization request's query.</summary>
    public IReadOnlyDictionary<string, string> Authorization { get; private set; } = new Dictionary<string, string>();

    public static async Task<FakeProvider> StartAsync()
    {
        var provider = new FakeProvider();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = provider._app = builder.Build();
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = provider.Issuer,
            ["authorization_endpoint"] = provider.Issuer + "/authorize",
            ["token_endpoint"] = provider.Issuer + "/token",
            ["jwks_uri"] = provider.Issuer + "/keys",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        }));
        app.MapGet("/keys", () =>
        {
            var parameters = provider._key.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "test", n = Encode(parameters.Modulus!), e = Encode(parameters.Exponent!) } } });
        });
        app.MapGet("/authorize", (HttpContext context) =>
        {
            var query = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
            provider.Authorization = query;
            var code = Guid.NewGuid().ToString("N");
            provider._codes[code] = (query["code_challenge"], query["nonce"]);
            return Results.Redirect(QueryHelpers.AddQueryString(query["redirect_uri"], new Dictionary<string, string?> { ["code"] = code, ["state"] = query["state"] }));
        });
        app.MapPost("/token", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            if (form["client_secret"] != PanelRig.ClientSecret || !provider._codes.TryRemove(form["code"].ToString(), out var pending)
                || Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) != pending.Challenge)
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            return Results.Json(new { access_token = "unused", token_type = "Bearer", id_token = provider.Token(pending.Nonce) });
        });
        await app.StartAsync();
        provider.Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        return provider;
    }

    private string Token(string nonce)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>(Claims)
        {
            ["iss"] = Issuer, ["aud"] = "staff-panel", ["iat"] = now, ["nbf"] = now - 5, ["exp"] = now + 300, ["nonce"] = nonce,
        };
        var signed = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "test", typ = "JWT" })) + "." + Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        return signed + "." + Encode(_key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _key.Dispose();
    }
}

/// <summary>T-805: who may sign in, as what; the development sign-in's limits; the settings the panel refuses.</summary>
public class PanelSignInTests
{
    private static OidcSettings Roles(string claim = "groups") => new()
    {
        RoleClaim = claim,
        Roles = new Dictionary<string, IReadOnlyList<string>>
        {
            [StaffRoles.Support] = ["game-support"],
            [StaffRoles.Gm] = ["game-gm", "game-leads"],
            [StaffRoles.Admin] = ["game-admin"],
        },
    };

    [Theory]
    [InlineData("support", "game-support")]
    [InlineData("gm", "game-support", "game-leads")]
    [InlineData("admin", "game-gm", "game-admin", "other")]
    [InlineData("gm", "[\"game-gm\",\"other\"]")]
    [InlineData(null, "Game-Admin")]
    [InlineData(null, "game-admins")]
    [InlineData(null, "[not json")]
    [InlineData(null)]
    public void The_highest_mapped_group_gives_the_role_and_nothing_else_does(string? role, params string[] groups)
    {
        RoleMapping.RoleOf(groups.Select(g => new Claim("groups", g)), Roles()).ShouldBe(role);
    }

    [Fact]
    public void Only_the_configured_claim_counts()
    {
        var claims = new[] { new Claim("groups", "game-admin"), new Claim("roles", "game-support") };
        RoleMapping.RoleOf(claims, Roles("roles")).ShouldBe(StaffRoles.Support);
    }

    [Fact]
    public void A_staff_id_and_name_fit_what_the_server_takes()
    {
        RoleMapping.IsStaffId("auth0|5f.a_b-c@d:e").ShouldBeTrue();
        foreach (var bad in new[] { null, "", "a b", "é", "a/b", new string('a', 129) }) RoleMapping.IsStaffId(bad).ShouldBeFalse(bad);
        RoleMapping.DisplayName("  Robin\r\nQ\t ", "x").ShouldBe("RobinQ");
        RoleMapping.DisplayName("\u0001", "fallback").ShouldBe("fallback");
        RoleMapping.DisplayName(new string('n', 63) + "\U0001F600", "x").Length.ShouldBeLessThanOrEqualTo(64);
    }

    [Fact]
    public async Task The_code_flow_signs_a_staff_member_in_with_the_mapped_role()
    {
        await using var provider = await FakeProvider.StartAsync();
        await using var server = await FakeAdminServer.StartAsync();
        await using var panel = await PanelRig.StartAsync(server.Address, ProviderSettings(provider));
        provider.Claims = new() { ["sub"] = "idp|42", ["name"] = "Sam Staff", ["groups"] = new[] { "everyone", "game-gm", "game-support" }, ["email"] = "sam@example.test" };
        using var browser = panel.Browser();

        var last = await SignInAtProviderAsync(browser, "/commands");

        last.Location.ShouldBe("/commands");
        provider.Authorization["response_type"].ShouldBe("code");
        provider.Authorization["code_challenge_method"].ShouldBe("S256");
        provider.Authorization["scope"].Split(' ').ShouldBe(["openid", "profile"]);
        provider.Authorization.Values.ShouldNotContain(v => v.Contains(PanelRig.ClientSecret));
        var commands = await browser.GetAsync("/commands");
        commands.Status.ShouldBe(200);
        commands.Text.ShouldContain("Sam Staff · gm");
        var request = server.Requests.Last();
        (request.Headers[AdminHeaders.StaffId], request.Headers[AdminHeaders.StaffRole], Uri.UnescapeDataString(request.Headers[AdminHeaders.StaffName]))
            .ShouldBe(("idp|42", "gm", "Sam Staff"));
        foreach (var page in browser.Seen)
        {
            page.Html.ShouldNotContain(PanelRig.ClientSecret);
            page.Html.ShouldNotContain("sam@example.test");
        }
        panel.Logs.Text.ShouldNotContain(PanelRig.ClientSecret);
        panel.Logs.Text.ShouldNotContain(AdminRig.Credential);
    }

    [Theory]
    [InlineData("idp|42", "everyone", SignInPaths.NoRole)]
    [InlineData("has space", "game-admin", SignInPaths.BadSubject)]
    public async Task Without_a_role_or_a_usable_id_there_is_no_sign_in(string subject, string group, string error)
    {
        await using var provider = await FakeProvider.StartAsync();
        await using var server = await FakeAdminServer.StartAsync();
        await using var panel = await PanelRig.StartAsync(server.Address, ProviderSettings(provider));
        provider.Claims = new() { ["sub"] = subject, ["groups"] = new[] { group } };
        using var browser = panel.Browser();

        var last = await SignInAtProviderAsync(browser, "/accounts");

        last.Location.ShouldBe("/signin?error=" + error);
        (await browser.GetAsync(last.Location!)).Text.ShouldContain(error == SignInPaths.NoRole ? "no group that gives a staff role" : "cannot be used as a staff id");
        (await browser.GetAsync("/accounts")).Status.ShouldBe(302);
        server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_provider_sign_in_never_returns_to_another_site()
    {
        await using var provider = await FakeProvider.StartAsync();
        await using var server = await FakeAdminServer.StartAsync();
        await using var panel = await PanelRig.StartAsync(server.Address, ProviderSettings(provider));
        provider.Claims = new() { ["sub"] = "idp|1", ["groups"] = new[] { "game-support" } };
        using var browser = panel.Browser();
        (await SignInAtProviderAsync(browser, "//evil.example/x")).Location.ShouldBe("/");
    }

    private static Dictionary<string, string?> ProviderSettings(FakeProvider provider) => new()
    {
        ["AdminPanel:DevelopmentSignIn"] = "false",
        ["Oidc:Authority"] = provider.Issuer,
        ["Oidc:ClientId"] = "staff-panel",
        ["Oidc:Roles:support"] = "game-support",
        ["Oidc:Roles:gm:0"] = "game-gm",
        ["Oidc:Roles:admin"] = "game-admin;game-owners",
    };

    /// <summary>Walks the redirects: panel, provider, panel's callback. Returns the callback's answer.</summary>
    private static async Task<Page> SignInAtProviderAsync(PanelBrowser browser, string returnUrl)
    {
        var challenge = await browser.GetAsync("/signin/provider?returnUrl=" + Uri.EscapeDataString(returnUrl));
        challenge.Status.ShouldBe(302, challenge.Html);
        using var provider = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var back = await provider.GetAsync(challenge.Location);
        var callback = back.Headers.Location!;
        // The handler's one-time cookies are marked Secure. A browser sends those to http://localhost; this client does not, so it copies them over by hand.
        var cookies = string.Join("; ", challenge.Response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        return await browser.GetAsync(callback.PathAndQuery, request => request.Headers.Add("Cookie", cookies));
    }

    [Fact]
    public async Task The_development_sign_in_is_refused_outside_Development()
    {
        await using var server = await FakeAdminServer.StartAsync();
        var certificate = Path.Combine(Path.GetTempPath(), $"panel-tests-{Guid.NewGuid():N}.pfx");
        GatewayRig.WriteSelfSignedCertificate(certificate);
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["AdminPanel:Listen"] = "https://127.0.0.1:0",
                ["AdminPanel:CertificatePath"] = certificate,
                ["Server:Url"] = "https://127.0.0.1:1",
                ["Oidc:Authority"] = "https://idp.invalid",
                ["Oidc:ClientId"] = "staff-panel",
                ["Oidc:Roles:admin"] = "game-admin",
            };
            var environment = PanelRig.Environment(certificatePassword: GatewayRig.CertificatePassword);

            // 1. Switched on outside Development, the panel does not start.
            foreach (var name in new[] { "Production", "Staging", "Test" })
                Should.Throw<PanelSettingsException>(() => PanelRig.Build(server.Address, settings, name, new CapturedLogs(), environment: environment))
                    .Problems.ShouldContain(p => p.Contains("DevelopmentSignIn is refused outside the Development environment"), name);

            // 2. Switched off, its address is not there and the sign-in page does not offer it.
            settings["AdminPanel:DevelopmentSignIn"] = "false";
            settings["Server:Url"] = "https://127.0.0.1:1";
            await using var panel = await PanelRig.StartAsync(new Uri("https://127.0.0.1:1"), settings, "Production", environment: environment);
            panel.Address.Scheme.ShouldBe("https");
            using var browser = panel.Browser();
            var page = await browser.GetAsync("/signin");
            page.Status.ShouldBe(200);
            page.Html.ShouldNotContain("/signin/development");
            page.Text.ShouldContain("Sign in with your staff account");
            var post = await browser.PostAsync("/signin/development", [new("role", "admin")]);
            post.Status.ShouldBe(404);
            post.Response.Headers.TryGetValues("Set-Cookie", out var set);
            (set ?? []).ShouldNotContain(c => c.Contains("panel-session="));
            (await browser.GetAsync("/accounts")).Status.ShouldBe(302, "not signed in");

            // 3. Outside Development the cookies are __Host-, Secure and HttpOnly.
            var session = panel.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var antiforgery = panel.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
            (session.Cookie.Name, session.Cookie.SecurePolicy, session.Cookie.HttpOnly, session.Cookie.SameSite, session.SlidingExpiration)
                .ShouldBe(("__Host-panel-session", CookieSecurePolicy.Always, true, SameSiteMode.Lax, false));
            (antiforgery.Cookie.Name, antiforgery.Cookie.SecurePolicy, antiforgery.Cookie.HttpOnly, antiforgery.Cookie.SameSite)
                .ShouldBe(("__Host-panel-antiforgery", CookieSecurePolicy.Always, true, SameSiteMode.Strict));
            var provider = panel.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
            (provider.ResponseType, provider.UsePkce, provider.RequireHttpsMetadata, provider.SaveTokens).ShouldBe(("code", true, true, false));
        }
        finally
        {
            File.Delete(certificate);
        }
    }

    [Fact]
    public void The_development_sign_in_answers_only_this_machine_in_Development()
    {
        var on = new PanelSettings { DevelopmentSignIn = true };
        SignInSetup.DevelopmentSignInAllowed(true, on, IPAddress.Loopback).ShouldBeTrue();
        SignInSetup.DevelopmentSignInAllowed(true, on, IPAddress.IPv6Loopback).ShouldBeTrue();
        SignInSetup.DevelopmentSignInAllowed(true, on, IPAddress.Parse("10.0.0.5")).ShouldBeFalse();
        SignInSetup.DevelopmentSignInAllowed(true, on, null).ShouldBeFalse();
        SignInSetup.DevelopmentSignInAllowed(false, on, IPAddress.Loopback).ShouldBeFalse();
        SignInSetup.DevelopmentSignInAllowed(true, new PanelSettings(), IPAddress.Loopback).ShouldBeFalse();
    }

    private static PanelSettings Local => new() { DevelopmentSignIn = true };

    private static PanelSettings Deployed => new()
    {
        Listen = "https://10.8.0.2:443",
        PrivateNetwork = true,
        AllowedHosts = ["panel.internal"],
        CertificatePath = typeof(PanelSettings).Assembly.Location, // any file that exists
        ServerUrl = "https://10.8.0.1:7781",
        Oidc = Roles() with { Authority = "https://idp.invalid", ClientId = "staff-panel" },
    };

    [Fact]
    public void Good_settings_have_no_problem()
    {
        Local.Problems(development: true, PanelRig.Environment()).ShouldBeEmpty();
        Deployed.Problems(development: false, PanelRig.Environment()).ShouldBeEmpty();
    }

    [Fact]
    public void Unsafe_settings_are_refused()
    {
        void Refused(PanelSettings settings, string part, bool development = false) =>
            settings.Problems(development, PanelRig.Environment()).ShouldContain(p => p.Contains(part), part);

        Refused(Deployed with { Listen = "http://127.0.0.1:7790" }, "HTTPS only");
        Refused(Deployed with { CertificatePath = null }, "needs a certificate");
        Refused(Deployed with { PrivateNetwork = false }, "PrivateNetwork");
        Refused(Deployed with { AllowedHosts = [] }, "AllowedHosts must name");
        Refused(Deployed with { AllowedHosts = ["*"] }, "no wildcards");
        Refused(Deployed with { Listen = "https://panel.example:443" }, "AdminPanel:Listen must be");
        Refused(Local with { Listen = "http://0.0.0.0:7790" }, "loopback only", development: true);
        Refused(Deployed with { ServerUrl = "http://127.0.0.1:7781" }, "Server:Url must be https");
        Refused(Local with { ServerUrl = "http://10.8.0.1:7781" }, "Server:Url must be https", development: true);
        Refused(Deployed with { ServerUrl = "https://user:pw@10.8.0.1:7781" }, "Server:Url must be");
        Refused(Deployed with { StatusRefreshSeconds = 4 }, "StatusRefreshSeconds");
        Refused(Deployed with { SessionMinutes = 100_000 }, "SessionMinutes");
        Refused(Deployed with { Oidc = Deployed.Oidc with { Authority = "http://idp.example" } }, "Oidc:Authority");
        Refused(Deployed with { Oidc = Deployed.Oidc with { Roles = new Dictionary<string, IReadOnlyList<string>>() } }, "nobody could sign in");
        Refused(Deployed with { Oidc = Deployed.Oidc with { Roles = new Dictionary<string, IReadOnlyList<string>> { ["owner"] = ["x"] } } }, "not a staff role");
        Refused(new PanelSettings(), "no way to sign in", development: true);
        Deployed.Problems(false, name => name == "GAME_ADMIN_CREDENTIAL" ? AdminRig.Credential : null).ShouldContain(p => p.Contains("client secret is missing"));
    }

    [Fact]
    public void A_short_or_missing_credential_stops_the_panel_and_is_never_quoted()
    {
        var problems = Local.Problems(true, _ => "short-secret-value");
        problems.ShouldContain(p => p.Contains("GAME_ADMIN_CREDENTIAL"));
        problems.ShouldNotContain(p => p.Contains("short-secret-value"));
        var spaced = "has a space in it but is long enough 0123456789";
        Local.Problems(true, _ => spaced).ShouldContain(p => p.Contains("printable ASCII"));
        Local.Problems(true, _ => spaced).ShouldNotContain(p => p.Contains(spaced));
    }

    [Fact]
    public async Task A_second_endpoint_from_Kestrels_own_settings_is_refused()
    {
        await using var server = await FakeAdminServer.StartAsync();
        Should.Throw<PanelSettingsException>(() => PanelRig.Build(server.Address, new Dictionary<string, string?> { ["Kestrel:Endpoints:Extra:Url"] = "http://0.0.0.0:7999" },
            "Development", new CapturedLogs())).Problems.ShouldContain(p => p.Contains("Kestrel:Endpoints"));
    }

    [Fact]
    public async Task A_sign_in_ends_with_its_time_and_with_signing_out_even_for_a_copied_cookie()
    {
        await using var server = await FakeAdminServer.StartAsync();
        var time = new FakeTime { Now = DateTimeOffset.UtcNow };
        await using var panel = await PanelRig.StartAsync(server.Address, new Dictionary<string, string?> { ["AdminPanel:SessionMinutes"] = "30" }, time: time);
        using var browser = panel.Browser();
        await browser.SignInAsync("support");
        var home = await browser.GetAsync("/commands");
        home.Status.ShouldBe(200);
        var copy = new CookieContainer();
        foreach (Cookie cookie in browser.Cookies.GetCookies(panel.Address)) copy.Add(panel.Address, new Cookie(cookie.Name, cookie.Value, "/"));
        using var thief = new PanelBrowser(panel.Address, copy);
        (await thief.GetAsync("/commands")).Status.ShouldBe(200);

        var signedOut = await browser.PostAsync("/signout", home.Hidden("action=\"/signout\""));
        signedOut.Location.ShouldBe("/signin");
        (await thief.GetAsync("/commands")).Status.ShouldBe(302, "the sign-in is over, not only the browser's cookie");

        using var again = panel.Browser();
        await again.SignInAsync("support");
        time.Now += TimeSpan.FromMinutes(29);
        (await again.GetAsync("/commands")).Status.ShouldBe(200);
        time.Now += TimeSpan.FromMinutes(2);
        var expired = await again.GetAsync("/commands");
        (expired.Status, new Uri(panel.Address, expired.Location!).PathAndQuery).ShouldBe((302, "/signin?returnUrl=%2Fcommands"));
    }

    [Theory]
    [InlineData("/x?y=1", "/x?y=1")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("/a\r\nb", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void A_return_address_stays_on_the_panel(string? asked, string used) => SignInSetup.LocalUrl(asked).ShouldBe(used);
}
