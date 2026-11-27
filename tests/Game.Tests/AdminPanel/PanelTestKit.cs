using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Game.Admin;
using Game.Tests.Accounts;
using Game.Tests.Admin;
using Game.Tests.World;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Game.Tests.AdminPanel;

/// <summary>Keeps every log line of the panel, at every level, with its exceptions.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public string Text => string.Join("\n", _lines);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}")) : string.Empty;
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {values} {exception}");
        }
    }
}

/// <summary>The real panel in process on a free loopback port, logging everything.</summary>
public sealed class PanelRig : IAsyncDisposable
{
    public const string ClientSecret = "test-only-oidc-client-secret-9f1c";

    private readonly WebApplication _app;

    private PanelRig(WebApplication app, CapturedLogs logs, Uri address)
    {
        _app = app;
        Logs = logs;
        Address = address;
    }

    public CapturedLogs Logs { get; }

    public Uri Address { get; }

    public IServiceProvider Services => _app.Services;

    public static string ContentDirectory => Path.Combine(TestGrids.RepoRoot(), "content");

    public static Func<string, string?> Environment(string credential = AdminRig.Credential, string? certificatePassword = null) => name => name switch
    {
        "GAME_ADMIN_CREDENTIAL" => credential,
        "GAME_PANEL_OIDC_CLIENT_SECRET" => ClientSecret,
        "GAME_PANEL_CERTIFICATE_PASSWORD" => certificatePassword,
        _ => null,
    };

    /// <summary>Builds (but does not start) the panel with <paramref name="settings"/> over the defaults of a local development panel.</summary>
    public static WebApplication Build(Uri server, IReadOnlyDictionary<string, string?>? settings, string environmentName, CapturedLogs logs, TimeProvider? time = null,
        Func<string, string?>? environment = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["AdminPanel:Listen"] = "http://127.0.0.1:0",
            ["AdminPanel:DevelopmentSignIn"] = "true",
            ["AdminPanel:ContentDirectory"] = ContentDirectory,
            ["Server:Url"] = server.GetLeftPart(UriPartial.Authority),
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            ["Logging:LogLevel:System.Net.Http"] = "Trace",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) values[key] = value;
        return PanelApp.Build(new WebApplicationOptions { EnvironmentName = environmentName, ApplicationName = "Game.Admin", ContentRootPath = AppContext.BaseDirectory },
            builder =>
            {
                builder.Configuration.Sources.Clear(); // nothing of this machine's environment or of an appsettings.json
                builder.Configuration.AddInMemoryCollection(values);
                builder.Logging.ClearProviders();
                builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
                builder.Logging.AddProvider(logs);
                builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
                if (time is not null) builder.Services.AddSingleton(time);
            }, environment ?? Environment());
    }

    public static async Task<PanelRig> StartAsync(Uri server, IReadOnlyDictionary<string, string?>? settings = null, string environmentName = "Development",
        TimeProvider? time = null, Func<string, string?>? environment = null)
    {
        var logs = new CapturedLogs();
        var app = Build(server, settings, environmentName, logs, time, environment);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new PanelRig(app, logs, new Uri(address));
    }

    public PanelBrowser Browser() => new(Address);

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _app.StopAsync(timeout.Token);
        await _app.DisposeAsync();
    }
}

/// <summary>One answer of the panel.</summary>
public sealed record Page(int Status, string Html, HttpResponseMessage Response)
{
    public string? Location => Response.Headers.Location?.ToString();

    /// <summary>The page's text without tags, for words a test looks for.</summary>
    public string Text => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(Html, "<[^>]*>", " "), @"\s+", " "));

    /// <summary>The hidden fields of the form that posts to <paramref name="marker"/> (its handler name or its action).</summary>
    public Dictionary<string, string> Hidden(string marker)
    {
        var form = Regex.Matches(Html, "<form\\b.*?</form>", RegexOptions.Singleline).Select(m => m.Value).Single(f => f.Contains(marker));
        return Regex.Matches(form, "<input type=\"hidden\"[^>]*>").ToDictionary(
            m => Regex.Match(m.Value, " name=\"([^\"]*)\"").Groups[1].Value,
            m => WebUtility.HtmlDecode(Regex.Match(m.Value, " value=\"([^\"]*)\"").Groups[1].Value));
    }

    /// <summary>The tag of the button or link of a command.</summary>
    public string CommandTag(string action) => Regex.Match(Html, $"<(a|button)\\b[^>]*data-command=\"{action}\"[^>]*>").Value;
}

/// <summary>A browser as far as the panel can tell: cookies, forms with their antiforgery fields, no automatic redirects.</summary>
public sealed class PanelBrowser : IDisposable
{
    private readonly HttpClient _http;

    public PanelBrowser(Uri address, CookieContainer? cookies = null)
    {
        Cookies = cookies ?? new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = Cookies, AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        _http = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
    }

    public CookieContainer Cookies { get; }

    /// <summary>Every page this browser was shown.</summary>
    public List<Page> Seen { get; } = new();

    public async Task<Page> GetAsync(string path, Action<HttpRequestMessage>? change = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        change?.Invoke(request);
        return await SendAsync(request);
    }

    public async Task<Page> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        return await SendAsync(request);
    }

    private async Task<Page> SendAsync(HttpRequestMessage request)
    {
        var response = await _http.SendAsync(request);
        var page = new Page((int)response.StatusCode, await response.Content.ReadAsStringAsync(), response);
        Seen.Add(page);
        return page;
    }

    public async Task SignInAsync(string role, string name = "Robin Q")
    {
        var page = await GetAsync("/signin");
        var form = page.Hidden("/signin/development");
        form["role"] = role;
        form["name"] = name;
        var signedIn = await PostAsync("/signin/development", form);
        signedIn.Status.ShouldBe(302, signedIn.Html);
    }

    /// <summary>Submits the command form of <paramref name="page"/> (at <paramref name="path"/>) with <paramref name="fields"/> over its hidden ones.</summary>
    public Task<Page> SubmitCommandAsync(string path, Page page, params (string Name, string Value)[] fields)
    {
        var form = page.Hidden("name=\"_handler\"");
        foreach (var (name, value) in fields) form[name] = value;
        return PostAsync(path, form);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>A request the fake server received.</summary>
public sealed record Received(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>How the fake server answers; <see cref="Abort"/> drops the connection instead.</summary>
public sealed record FakeAnswer(int Status = 200, string Body = "{}", int DelayMs = 0, bool Abort = false, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>A server that records what the panel sends and answers what a test says: the admin API's shapes (docs/ADMIN_API.md).</summary>
public sealed class FakeAdminServer : IAsyncDisposable
{
    public const string StatusJson =
        """{"content_hash":"abc","started_at":"2026-11-26T00:00:00Z","players_online":3,"sessions":4,"instances":[{"map_id":"map.nowhere","tick":10,"tick_p50_micros":100,"tick_p99_micros":200,"tick_max_micros":300,"tick_lifetime_max_micros":400,"system_errors":0,"handler_errors":0,"intents_dropped":0}],"saves":null,"errors":{"logged_errors":1,"logged_warnings":2,"server_error_byes":0}}""";

    /// <summary>The real command, two a later server could add, and one with a role this panel does not know.</summary>
    public const string CommandsJson =
        """{"commands":[{"action":"kick","lowest_role":"support"},{"action":"test_gm","lowest_role":"gm"},{"action":"test_admin","lowest_role":"admin"},{"action":"test_owner","lowest_role":"owner"}],"read_role":"support","roles":["support","gm","admin"]}""";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Received> _received = new();

    private FakeAdminServer(WebApplication app) => _app = app;

    public Uri Address { get; private set; } = null!;

    public IReadOnlyList<Received> Requests => _received.ToList();

    /// <summary>The answer for a request; null falls back to the status and the command list.</summary>
    public Func<Received, FakeAnswer?>? Answer { get; set; }

    public static async Task<FakeAdminServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeAdminServer(app);
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = new Received(context.Request.Method, context.Request.Path + context.Request.QueryString.ToString(),
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), await reader.ReadToEndAsync());
            server._received.Enqueue(request);
            var answer = server.Answer?.Invoke(request) ?? request.PathAndQuery switch
            {
                "/admin/v1/status" => new FakeAnswer(Body: StatusJson),
                "/admin/v1/commands" => new FakeAnswer(Body: CommandsJson),
                _ => new FakeAnswer(404, """{"error":"not_found","request_id":"x"}"""),
            };
            if (answer.DelayMs > 0) await Task.Delay(answer.DelayMs, context.RequestAborted);
            if (answer.Abort)
            {
                context.Abort();
                return;
            }
            context.Response.StatusCode = answer.Status;
            context.Response.ContentType = "application/json";
            foreach (var (name, value) in answer.Headers ?? new Dictionary<string, string>()) context.Response.Headers[name] = value;
            await context.Response.WriteAsync(answer.Body);
        });
        await app.StartAsync();
        server.Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _app.StopAsync(timeout.Token);
        await _app.DisposeAsync();
    }
}
