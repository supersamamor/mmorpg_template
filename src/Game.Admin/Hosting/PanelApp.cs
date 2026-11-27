using System.Security.Cryptography.X509Certificates;
using Game.Admin.Api;
using Game.Admin.Auth;
using Game.Admin.Commands;
using Game.Admin.Components;
using Game.Admin.ContentView;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;

namespace Game.Admin;

/// <summary>Builds the panel. <see cref="Program"/> runs it; the tests start the same app in process.</summary>
public static class PanelApp
{
    /// <summary>
    /// The app, checked and ready to run. Throws <see cref="PanelSettingsException"/> when the settings are not usable. <paramref name="configure"/> runs last, before the
    /// build (tests replace the clock or the settings there); <paramref name="environment"/> reads the secrets' variables.
    /// </summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var builder = WebApplication.CreateBuilder(options);
        builder.Services.AddSingleton(TimeProvider.System);
        configure?.Invoke(builder);

        var problems = new List<string>();
        var settings = PanelSettings.Read(builder.Configuration, problems);
        var development = builder.Environment.IsDevelopment();
        problems.AddRange(settings.Problems(development, environment));
        // The panel listens where AdminPanel:Listen says and nowhere else: a second endpoint would pass by the checks above.
        if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any()) problems.Add("set AdminPanel:Listen, not Kestrel:Endpoints");
        if (problems.Count > 0) throw new PanelSettingsException(problems);

        var certificate = LoadCertificate(settings, environment);
        var (https, address, port) = settings.ListenEndpoint!.Value;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024; // the largest form is a command with its reason
            void Endpoint(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listen)
            {
                if (certificate is not null) listen.UseHttps(certificate);
            }
            if (address is null) kestrel.ListenLocalhost(port, Endpoint);
            else kestrel.Listen(address, port, Endpoint);
        });
        if (https) builder.WebHost.UseKestrelHttpsConfiguration();
        if (development) builder.WebHost.UseStaticWebAssets();

        var services = builder.Services;
        services.AddSingleton(settings);
        // The host names a request may use: a page elsewhere that points a name of its own at this address is turned away (DNS rebinding).
        services.Configure<HostFilteringOptions>(hosts =>
        {
            hosts.AllowedHosts = settings.EffectiveHosts.ToList();
            hosts.AllowEmptyHosts = false;
        });
        services.PostConfigure<HostFilteringOptions>(hosts => hosts.AllowedHosts = settings.EffectiveHosts.ToList()); // over the framework's own "AllowedHosts" setting
        var protection = services.AddDataProtection().SetApplicationName("Game.Admin");
        if (settings.KeyDirectory is { } keys) protection.PersistKeysToFileSystem(Directory.CreateDirectory(keys));

        services.AddSingleton<StaffSessions>();
        SignInSetup.Add(services, settings, development, environment);
        // Everything needs a sign-in unless it says otherwise: pages, the status page's connection, anything added later.
        services.AddAuthorization(authorization => authorization.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();
        services.AddRazorComponents().AddInteractiveServerComponents();

        services.AddSingleton(new PanelCredential(environment(settings.CredentialEnv)!));
        services.AddHttpClient<AdminApiClient>(http =>
            {
                http.Timeout = TimeSpan.FromMilliseconds(settings.ServerTimeoutMs);
                http.MaxResponseContentBufferSize = AdminApiClient.MaxAnswerBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => AdminApiClient.Handler(settings))
            .RemoveAllLoggers(); // the factory's loggers can write request headers: the credential is one
        services.AddSingleton<CommandTickets>();

        var content = ContentNames.Load(settings.ContentDirectory, out var contentDetail);
        services.AddSingleton(content);

        var app = builder.Build();
        if (contentDetail is not null) app.Logger.LogWarning("No content names: {Detail}", contentDetail);
        if (development && settings.DevelopmentSignIn)
            app.Logger.LogWarning("The development sign-in is ON: anyone on this machine can sign in as any role without a password. For local use only.");

        SecurityHeaders.Use(app);
        app.UseHostFiltering();
        if (!development)
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
            app.UseHsts();
        }
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets().AllowAnonymous();
        SignInSetup.Map(app, settings);
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode(server => server.ContentSecurityFrameAncestorsPolicy = null); // SecurityHeaders says it
        app.MapFallback(() => Results.Content("<!DOCTYPE html><html lang=\"en\"><title>Not found</title><p>There is no such page. <a href=\"/\">Back to the status</a>.</p></html>",
            "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound)).AllowAnonymous();
        return app;
    }

    private static X509Certificate2? LoadCertificate(PanelSettings settings, Func<string, string?> environment)
    {
        if (settings.ListenEndpoint is not { Https: true } || settings.CertificatePath is not { } path) return null;
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, environment(settings.CertificatePasswordEnv));
            return certificate.HasPrivateKey ? certificate : throw new PanelSettingsException([$"the certificate '{path}' has no private key"]);
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            throw new PanelSettingsException([$"could not load the certificate '{path}': {e.Message}"]);
        }
    }
}
