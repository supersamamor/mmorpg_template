using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Game.Server.Net;

/// <summary>
/// Kestrel listening for WebSocket upgrades on <see cref="GatewayOptions.Path"/> and handing each one to <see cref="Gateway"/>. In
/// development it listens on loopback only and allows plain <c>ws://</c>; outside development it listens on every interface (or
/// <see cref="GatewayOptions.BindAddress"/>) and only with TLS (<c>wss://</c>), and refuses to start without a certificate (<see cref="GatewayOptions.Problems"/>). Nothing else is served.
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GatewayHost(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    /// <summary>The bound WebSocket endpoint (<c>ws://127.0.0.1:port/ws</c> or <c>wss://...</c>), with the real port when 0 was asked for.</summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// Validates the options, loads the certificate (password from <see cref="GatewayOptions.CertificatePasswordEnv"/>), binds and starts
    /// listening. Throws <see cref="InvalidOperationException"/> listing every problem when the options are unusable.
    /// </summary>
    public static async Task<GatewayHost> StartAsync(Gateway gateway, Func<string, string?>? environment = null)
    {
        var options = gateway.Options;
        environment ??= Environment.GetEnvironmentVariable;
        var problems = options.Problems();
        if (problems.Count > 0) throw new InvalidOperationException("Gateway options are not usable: " + string.Join("; ", problems) + ".");

        X509Certificate2? certificate = null;
        if (options.CertificatePath is { Length: > 0 } path)
        {
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(path, environment(options.CertificatePasswordEnv));
            }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                throw new InvalidOperationException($"Could not load the TLS certificate '{path}': {e.Message}", e);
            }
            if (!certificate.HasPrivateKey) throw new InvalidOperationException($"The TLS certificate '{path}' has no private key.");
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = options.Development ? "Development" : "Production",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Listen(options.ListenAddress, options.Port, listen =>
            {
                if (certificate is not null) listen.UseHttps(certificate);
            });
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromMilliseconds(options.KeepAliveMs) });
        app.Run(async context =>
        {
            if (context.Request.Path != options.Path || !context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = context.Request.Path == options.Path ? StatusCodes.Status400BadRequest : StatusCodes.Status404NotFound;
                return;
            }
            var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var peer = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}";
            await gateway.RunSessionAsync(socket, peer).ConfigureAwait(false);
        });

        await app.StartAsync().ConfigureAwait(false);
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                    ?? throw new InvalidOperationException("Kestrel reported no bound address.");
        var uri = new UriBuilder(bound) { Scheme = certificate is null ? "ws" : "wss", Path = options.Path }.Uri;
        return new GatewayHost(app, uri);
    }

    /// <summary>Stops listening (call <see cref="Gateway.ShutdownAsync"/> first so sessions get their <c>bye</c>).</summary>
    public async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _app.StopAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);
}
