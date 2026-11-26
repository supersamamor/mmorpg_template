namespace Game.Admin;

/// <summary>The headers of every answer: a strict content security policy (this origin only, no inline script or style, no frames) and no caching of pages.</summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Everything from this origin only: no third-party script, style or font, no inline script or style. <c>connect-src 'self'</c> covers the status page's WebSocket.
    /// <c>form-action 'self'</c>: forms post to the panel only (signing in at the provider starts with a link).
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; " +
        "form-action 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'";

    /// <summary>Adds them to every response, error pages and static files too.</summary>
    public static void Use(WebApplication app) => app.Use((context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["X-Robots-Tag"] = "noindex, nofollow";
            // Pages hold player data and antiforgery tokens: never kept. Static files (no player data) keep their own caching.
            if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true) headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return next();
    });
}
