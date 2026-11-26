using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Game.Server.Admin;

/// <summary>
/// The admin API's own Kestrel listener (ARCHITECTURE §12.1, T-804): a second one beside the gateway's, on its own address and port, loopback unless
/// <see cref="AdminOptions.PrivateNetwork"/> says otherwise, TLS outside development (<see cref="AdminOptions.Problems"/>). Headers, bodies and time are bounded; it serves
/// <see cref="AdminApi"/> and nothing else (no WebSockets, no files, no other path).
/// </summary>
public sealed class AdminHost : IAsyncDisposable
{
    /// <summary>Largest request headers, in bytes.</summary>
    public const int MaxHeaderBytes = 8 * 1024;

    private readonly WebApplication _app;

    private AdminHost(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    /// <summary>The bound base address (<c>http://127.0.0.1:port</c> or <c>https://...</c>), with the real port when 0 was asked for.</summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// Checks the options, loads the certificate (password from <see cref="AdminOptions.CertificatePasswordEnv"/>), binds and starts. Throws
    /// <see cref="InvalidOperationException"/> listing every problem when they are not usable.
    /// </summary>
    public static async Task<AdminHost> StartAsync(AdminOptions options, int gamePort, AdminApi api, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var problems = options.Problems(gamePort, environment);
        if (!options.Enabled) problems = [.. problems, "the admin API is off (no --admin-port)"];
        if (problems.Count > 0) throw new InvalidOperationException("Admin API options are not usable: " + string.Join("; ", problems) + ".");

        X509Certificate2? certificate = null;
        if (options.CertificatePath is { Length: > 0 } path)
        {
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(path, environment(options.CertificatePasswordEnv));
            }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                throw new InvalidOperationException($"Could not load the admin TLS certificate '{path}': {e.Message}", e);
            }
            if (!certificate.HasPrivateKey) throw new InvalidOperationException($"The admin TLS certificate '{path}' has no private key.");
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = options.Development ? "Development" : "Production",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders(); // Kestrel's own lines could carry header values
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestHeadersTotalSize = MaxHeaderBytes;
            kestrel.Limits.MaxRequestHeaderCount = 32;
            kestrel.Limits.MaxRequestLineSize = 2 * 1024;
            kestrel.Limits.MaxRequestBodySize = options.MaxBodyBytes;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromMilliseconds(Math.Min(options.RequestTimeoutMs, 5_000));
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            kestrel.Limits.MaxConcurrentConnections = 64;
            kestrel.Limits.MinRequestBodyDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(240, TimeSpan.FromSeconds(2));
            kestrel.Listen(options.ListenAddress, options.Port!.Value, listen =>
            {
                if (certificate is not null) listen.UseHttps(certificate);
            });
        });

        var app = builder.Build();
        app.Run(async context =>
        {
            // The whole request, body and answer, within its time; a command that ran out of time is refused (or, past its change, answered all the same: the change is audited).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(options.RequestTimeoutMs);
            context.RequestAborted = timeout.Token;
            await api.HandleAsync(context).ConfigureAwait(false);
        });

        await app.StartAsync().ConfigureAwait(false);
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                    ?? throw new InvalidOperationException("Kestrel reported no bound address for the admin API.");
        return new AdminHost(app, new UriBuilder(bound) { Scheme = certificate is null ? "http" : "https", Path = string.Empty }.Uri);
    }

    /// <summary>Stops listening.</summary>
    public async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _app.StopAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);
}
