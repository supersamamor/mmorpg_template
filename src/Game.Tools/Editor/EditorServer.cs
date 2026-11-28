using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Game.Tools.Editor;

/// <summary>
/// The content editor's web server (T-801): one page and a small JSON API, on a loopback address only. There is no sign-in: being on this machine is the only check, so
/// <see cref="Problem"/> refuses any address that is not loopback, and the server answers only requests whose <c>Host</c> is a loopback name (a page elsewhere cannot reach it
/// through a name that points here) and refuses cross-site writes (a save must come from the editor's own page).
/// </summary>
public sealed class EditorServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EditorServer(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>Where the page is.</summary>
    public Uri Address { get; }

    /// <summary>Why the editor may not listen on <paramref name="host"/>, or null when it may (a loopback IP address).</summary>
    public static string? Problem(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return $"'{host}' is not an IP address; the editor listens on 127.0.0.1 or ::1 only";
        return IPAddress.IsLoopback(address) ? null : $"the editor refuses to listen on {address}: it has no sign-in, so it listens on a loopback address only (127.0.0.1 or ::1)";
    }

    /// <summary>Starts the editor on <paramref name="host"/>:<paramref name="port"/> (0: any free port). Throws <see cref="ArgumentException"/> for an address that is not loopback.</summary>
    public static async Task<EditorServer> StartAsync(ContentEditor editor, string host = "127.0.0.1", int port = 7880)
    {
        if (Problem(host) is { } problem) throw new ArgumentException(problem, nameof(host));
        var address = IPAddress.Parse(host);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(address, port));
        var app = builder.Build();
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        app.Use(async (context, next) =>
        {
            if (!LoopbackHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Cache-Control"] = "no-store";
            if (HttpMethods.IsPost(context.Request.Method) && (context.Request.Headers["X-Editor"] != "1" || !(context.Request.ContentType ?? "").StartsWith("application/json", StringComparison.Ordinal)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;   // a form or a page on another site cannot set the header
                return;
            }
            await next(context);
        });

        app.MapGet("/", () => Results.Content(EditorPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/api/kinds", () => Results.Json(editor.Kinds, json));
        app.MapGet("/api/kinds/{kind}", (string kind) => Results.Json(editor.Documents(kind), json));
        app.MapGet("/api/schema/{kind}", (string kind) => editor.Schema(kind) is { } schema ? Results.Content(schema, "application/json") : Results.NotFound());
        app.MapGet("/api/ids", () => Results.Json(editor.Ids(), json));
        app.MapGet("/api/doc", (string path) =>
        {
            var (document, version, problem) = editor.Load(path);
            return document is null ? Results.Json(new { problem }, json, statusCode: 404) : Results.Json(new { path, version, document }, json);
        });
        app.MapPost("/api/doc", async (HttpRequest request) =>
        {
            JsonObject? body;
            try
            {
                body = await JsonNode.ParseAsync(request.Body) as JsonObject;
            }
            catch (JsonException)
            {
                body = null;
            }
            if (body?["path"]?.ToString() is not { } path || body["document"] is not JsonObject document) return Results.Json(new { problem = "expected {path, version, document}" }, json, statusCode: 400);
            var result = editor.Save(path, (JsonObject)document.DeepClone(), body["version"]?.ToString());
            var issues = result.Issues.Select(i => new { severity = i.Severity.ToString().ToLowerInvariant(), file = i.File, line = i.Line, column = i.Column, path = i.Path, code = i.Code, message = i.Message });
            var answer = new { saved = result.Saved, mode = result.Mode?.ToString().ToLowerInvariant(), version = result.Version, problem = result.Problem, issues };
            return Results.Json(answer, json, statusCode: result.Saved ? 200 : result.Problem is null ? 422 : 409);
        });

        await app.StartAsync();
        var bound = new Uri(app.Urls.First());
        return new EditorServer(app, new Uri($"http://{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString())}:{bound.Port}/"));
    }

    private static bool LoopbackHost(string host) =>
        host is "localhost" || (IPAddress.TryParse(host.Trim('[', ']'), out var a) && IPAddress.IsLoopback(a));

    /// <summary>Stops the server.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
