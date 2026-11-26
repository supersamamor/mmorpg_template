using System.Net;
using Game.Shared.Vocabulary;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Game.Admin.Auth;

/// <summary>The panel's paths for signing in and out.</summary>
public static class SignInPaths
{
    /// <summary>The sign-in page.</summary>
    public const string Page = "/signin";

    /// <summary>Starts the provider's authorization-code flow.</summary>
    public const string Provider = "/signin/provider";

    /// <summary>The development sign-in (only mapped where it is allowed).</summary>
    public const string Development = "/signin/development";

    /// <summary>Signs out of the panel.</summary>
    public const string SignOut = "/signout";

    /// <summary>The <c>error</c> of the sign-in page: signed in at the provider, but no group gives a role.</summary>
    public const string NoRole = "no_role";

    /// <summary>The provider's subject cannot be a staff id.</summary>
    public const string BadSubject = "bad_subject";

    /// <summary>The sign-in did not finish.</summary>
    public const string Failed = "failed";
}

/// <summary>Cookies, OpenID Connect (authorization code with PKCE; MFA is the provider's) and the development sign-in.</summary>
public static class SignInSetup
{
    private const string LogCategory = "Game.Admin.SignIn";

    /// <summary>Adds the schemes.</summary>
    public static void Add(IServiceCollection services, PanelSettings settings, bool development, Func<string, string?> environment)
    {
        var secure = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        var prefix = development ? "" : "__Host-";
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = prefix + "panel-antiforgery";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.SecurePolicy = secure;
            antiforgery.SuppressXFrameOptionsHeader = true; // SecurityHeaders sets it, once
        });
        var authentication = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
        {
            cookie.Cookie.Name = prefix + "panel-session";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax; // the provider sends the browser back from another site
            cookie.Cookie.SecurePolicy = secure;
            cookie.Cookie.Path = "/";
            cookie.LoginPath = SignInPaths.Page;
            cookie.AccessDeniedPath = SignInPaths.Page;
            cookie.ReturnUrlParameter = "returnUrl";
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(settings.SessionMinutes);
            cookie.SlidingExpiration = false;
            cookie.Events.OnValidatePrincipal = async context =>
            {
                if (context.HttpContext.RequestServices.GetRequiredService<StaffSessions>().Resolve(context.Principal) is not null) return;
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            };
        });
        if (!settings.HasProvider) return;
        authentication.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc => ConfigureProvider(oidc, settings, development, environment));
    }

    /// <summary>The provider's options: the authorization-code flow with PKCE; only the staff member is kept of what comes back.</summary>
    public static void ConfigureProvider(OpenIdConnectOptions oidc, PanelSettings settings, bool development, Func<string, string?> environment)
    {
        var o = settings.Oidc;
        oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        oidc.Authority = o.Authority;
        oidc.ClientId = o.ClientId;
        oidc.ClientSecret = environment(o.ClientSecretEnv);
        oidc.RequireHttpsMetadata = !development;
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;
        oidc.UsePkce = true;
        oidc.SaveTokens = false; // the panel calls nothing with them, and the cookie stays small
        oidc.MapInboundClaims = false;
        oidc.GetClaimsFromUserInfoEndpoint = o.UseUserInfo;
        if (o.UseUserInfo)
        {
            oidc.ClaimActions.MapUniqueJsonKey(o.NameClaim, o.NameClaim);
            oidc.ClaimActions.MapJsonKey(o.RoleClaim, o.RoleClaim);
        }
        oidc.Scope.Clear();
        foreach (var scope in o.Scopes.Prepend("openid").Distinct(StringComparer.Ordinal)) oidc.Scope.Add(scope);
        oidc.Events.OnTicketReceived = context =>
        {
            var sessions = context.HttpContext.RequestServices.GetRequiredService<StaffSessions>();
            var log = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
            var claims = context.Principal?.Claims.ToList() ?? [];
            var subject = claims.FirstOrDefault(c => c.Type == "sub")?.Value;
            var role = RoleMapping.RoleOf(claims, o);
            var problem = !RoleMapping.IsStaffId(subject) ? SignInPaths.BadSubject : role is null ? SignInPaths.NoRole : null;
            if (problem is not null)
            {
                log.LogWarning("A sign-in at the provider was refused: {Problem}", problem);
                context.Response.Redirect(SignInPaths.Page + "?error=" + problem);
                context.HandleResponse();
                return Task.CompletedTask;
            }
            var expires = sessions.NewExpiry();
            var name = RoleMapping.DisplayName(claims.FirstOrDefault(c => c.Type == o.NameClaim)?.Value, subject!);
            context.Principal = sessions.Principal(subject!, name, role!, expires, CookieAuthenticationDefaults.AuthenticationScheme);
            context.Properties!.ExpiresUtc = expires;
            context.Properties.IsPersistent = false;
            log.LogInformation("Staff {StaffId} signed in as {Role}", subject, role);
            return Task.CompletedTask;
        };
        oidc.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
                .LogWarning("A sign-in at the provider failed: {Failure}", context.Failure?.GetType().Name); // the type only: the message may quote the provider's answer
            context.Response.Redirect(SignInPaths.Page + "?error=" + SignInPaths.Failed);
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Whether the development sign-in answers a request: only in the Development environment, only when switched on, only for a caller on this machine. Outside
    /// Development the panel refuses to start with it switched on, and its address is not mapped at all.
    /// </summary>
    public static bool DevelopmentSignInAllowed(bool development, PanelSettings settings, IPAddress? caller) =>
        development && settings.DevelopmentSignIn && caller is not null && IPAddress.IsLoopback(caller);

    /// <summary>Maps the sign-in and sign-out endpoints. Each POST checks its antiforgery token.</summary>
    public static void Map(WebApplication app, PanelSettings settings)
    {
        var development = app.Environment.IsDevelopment();
        if (settings.HasProvider)
        {
            app.MapGet(SignInPaths.Provider, (HttpContext context) =>
                    Results.Challenge(new AuthenticationProperties { RedirectUri = LocalUrl(context.Request.Query["returnUrl"]) }, [OpenIdConnectDefaults.AuthenticationScheme]))
                .AllowAnonymous();
        }
        if (development && settings.DevelopmentSignIn)
        {
            app.MapPost(SignInPaths.Development, async (HttpContext context, IAntiforgery antiforgery, StaffSessions sessions, ILoggerFactory logs) =>
            {
                if (!DevelopmentSignInAllowed(development, settings, context.Connection.RemoteIpAddress)) return Results.NotFound();
                if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var role = form["role"].ToString();
                if (!StaffRoles.All.Contains(role)) return Results.BadRequest();
                var name = RoleMapping.DisplayName(form["name"], "Developer");
                var slug = new string(name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());
                var id = "development|" + (slug.Length > 0 ? slug : "staff");
                var expires = sessions.NewExpiry();
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    sessions.Principal(id, name, role, expires, CookieAuthenticationDefaults.AuthenticationScheme), new AuthenticationProperties { ExpiresUtc = expires });
                logs.CreateLogger(LogCategory).LogWarning("Development sign-in: {StaffId} as {Role}", id, role);
                return Results.LocalRedirect(LocalUrl(form["returnUrl"]));
            }).AllowAnonymous();
        }
        app.MapPost(SignInPaths.SignOut, async (HttpContext context, IAntiforgery antiforgery, StaffSessions sessions) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
            if (sessions.Resolve(context.User) is { } staff) sessions.End(staff);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect(SignInPaths.Page);
        }).AllowAnonymous();
    }

    /// <summary><paramref name="url"/> when it is a path on this site, else <c>/</c>: a sign-in never redirects elsewhere.</summary>
    public static string LocalUrl(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) && !url.Any(char.IsControl) ? url : "/";
}
