using System.Globalization;
using System.Net;
using Game.Shared.Vocabulary;

namespace Game.Admin;

/// <summary>The settings cannot be used; the panel refuses to start. The message lists every problem and never quotes a secret.</summary>
public sealed class PanelSettingsException(IReadOnlyList<string> problems)
    : Exception("The staff panel's settings are not usable (docs/ADMIN_PANEL.md):\n - " + string.Join("\n - ", problems))
{
    /// <summary>Each problem.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// The panel's settings (docs/ADMIN_PANEL.md): the sections <c>AdminPanel</c>, <c>Server</c> and <c>Oidc</c>. Secrets (the service credential, the OIDC client secret,
/// the certificate's password) are never settings: each is read from an environment variable whose <b>name</b> a setting may change.
/// </summary>
public sealed record PanelSettings
{
    /// <summary>The shortest service credential accepted: the server's own bound (docs/ADMIN_API.md).</summary>
    public const int MinCredentialLength = 32;

    /// <summary>The status page never refreshes faster than this.</summary>
    public const int MinStatusRefreshSeconds = 5;

    /// <summary>Where the panel listens: <c>http(s)://address:port</c>.</summary>
    public string Listen { get; init; } = "http://127.0.0.1:7790";

    /// <summary>Says the listen address is reachable only by staff (a VPN, or a firewall allowlist): without it only loopback is allowed.</summary>
    public bool PrivateNetwork { get; init; }

    /// <summary>The host names a request may name (the <c>Host</c> header), without ports. Empty: the loopback names, allowed on a loopback address only.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>The <c>.pfx</c> with the panel's TLS certificate and key.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>The environment variable with the certificate's password.</summary>
    public string CertificatePasswordEnv { get; init; } = "GAME_PANEL_CERTIFICATE_PASSWORD";

    /// <summary>The password-less sign-in for trying the panel on one PC. Refused outside Development and on any non-loopback address.</summary>
    public bool DevelopmentSignIn { get; init; }

    /// <summary>How long a sign-in lasts; it is never extended, so a role taken away at the provider is gone after this.</summary>
    public int SessionMinutes { get; init; } = 480;

    /// <summary>How often the status page asks again.</summary>
    public int StatusRefreshSeconds { get; init; } = 10;

    /// <summary>The <c>content/</c> folder, read only, for names. Empty: looked for above the working directory.</summary>
    public string? ContentDirectory { get; init; }

    /// <summary>Where the keys that protect cookies and antiforgery tokens are kept; empty: ASP.NET Core's default.</summary>
    public string? KeyDirectory { get; init; }

    /// <summary>The server's admin API: <c>https://address:port</c> (http only to loopback, in Development).</summary>
    public string ServerUrl { get; init; } = "http://127.0.0.1:7781";

    /// <summary>The environment variable with the service credential.</summary>
    public string CredentialEnv { get; init; } = "GAME_ADMIN_CREDENTIAL";

    /// <summary>Longest a call to the server may take. Above the server's own request bound, so its <c>timeout</c> arrives first.</summary>
    public int ServerTimeoutMs { get; init; } = 12_000;

    /// <summary>A PEM certificate to trust for the server besides the system's authorities, matched exactly (a self-signed one).</summary>
    public string? ServerCertificatePath { get; init; }

    /// <summary>The identity provider.</summary>
    public OidcSettings Oidc { get; init; } = new();

    /// <summary>True when <see cref="Oidc"/> names a provider.</summary>
    public bool HasProvider => !string.IsNullOrWhiteSpace(Oidc.Authority);

    /// <summary>The listen address as Kestrel binds it (a null address is <c>localhost</c>), or null when <see cref="Listen"/> is not one.</summary>
    public (bool Https, IPAddress? Address, int Port)? ListenEndpoint
    {
        get
        {
            if (!Uri.TryCreate(Listen, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.PathAndQuery != "/" || uri.UserInfo.Length > 0) return null;
            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return (uri.Scheme == "https", null, uri.Port);
            return IPAddress.TryParse(uri.DnsSafeHost, out var address) ? (uri.Scheme == "https", address, uri.Port) : null;
        }
    }

    /// <summary>True when the panel listens on loopback only.</summary>
    public bool LoopbackOnly => ListenEndpoint is { } e && (e.Address is null || IPAddress.IsLoopback(e.Address));

    /// <summary>The host names requests may use.</summary>
    public IReadOnlyList<string> EffectiveHosts => AllowedHosts.Count > 0 ? AllowedHosts : ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>Reads the settings. A value that does not parse is added to <paramref name="problems"/>.</summary>
    public static PanelSettings Read(IConfiguration configuration, List<string> problems)
    {
        var panel = configuration.GetSection("AdminPanel");
        var server = configuration.GetSection("Server");
        var oidc = configuration.GetSection("Oidc");
        var d = new PanelSettings();

        static string? Text(IConfiguration section, string key) => section[key]?.Trim() is { Length: > 0 } value ? value : null;

        bool Flag(IConfiguration section, string key)
        {
            if (Text(section, key) is not { } text) return false;
            if (bool.TryParse(text, out var value)) return value;
            problems.Add($"{key} must be true or false");
            return false;
        }

        int Number(IConfiguration section, string key, int fallback)
        {
            if (Text(section, key) is not { } text) return fallback;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
            problems.Add($"{key} must be a whole number");
            return fallback;
        }

        // A list is either one text (separated by ';') or numbered entries. Not ',': a group name may hold one.
        static IReadOnlyList<string> List(IConfiguration section, string key)
        {
            var child = section.GetSection(key);
            var values = child.Value is { } one ? one.Split(';') : child.GetChildren().Select(c => c.Value ?? string.Empty);
            return values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        }

        var roles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var roleSection = oidc.GetSection("Roles");
        foreach (var role in roleSection.GetChildren()) roles[role.Key] = List(roleSection, role.Key);

        return new PanelSettings
        {
            Listen = Text(panel, "Listen") ?? d.Listen,
            PrivateNetwork = Flag(panel, "PrivateNetwork"),
            AllowedHosts = List(panel, "AllowedHosts"),
            CertificatePath = Text(panel, "CertificatePath"),
            CertificatePasswordEnv = Text(panel, "CertificatePasswordEnv") ?? d.CertificatePasswordEnv,
            DevelopmentSignIn = Flag(panel, "DevelopmentSignIn"),
            SessionMinutes = Number(panel, "SessionMinutes", d.SessionMinutes),
            StatusRefreshSeconds = Number(panel, "StatusRefreshSeconds", d.StatusRefreshSeconds),
            ContentDirectory = Text(panel, "ContentDirectory"),
            KeyDirectory = Text(panel, "KeyDirectory"),
            ServerUrl = Text(server, "Url") ?? d.ServerUrl,
            CredentialEnv = Text(server, "CredentialEnv") ?? d.CredentialEnv,
            ServerTimeoutMs = Number(server, "TimeoutMs", d.ServerTimeoutMs),
            ServerCertificatePath = Text(server, "CertificatePath"),
            Oidc = new OidcSettings
            {
                Authority = Text(oidc, "Authority"),
                ClientId = Text(oidc, "ClientId"),
                ClientSecretEnv = Text(oidc, "ClientSecretEnv") ?? d.Oidc.ClientSecretEnv,
                Scopes = List(oidc, "Scopes") is { Count: > 0 } scopes ? scopes : d.Oidc.Scopes,
                RoleClaim = Text(oidc, "RoleClaim") ?? d.Oidc.RoleClaim,
                NameClaim = Text(oidc, "NameClaim") ?? d.Oidc.NameClaim,
                UseUserInfo = Flag(oidc, "UseUserInfo"),
                Roles = roles,
            },
        };
    }

    /// <summary>
    /// Why these settings cannot be used (the panel then refuses to start), or nothing. <paramref name="environment"/> reads the secrets' variables; no value of one is in
    /// a message.
    /// </summary>
    public IReadOnlyList<string> Problems(bool development, Func<string, string?> environment)
    {
        var problems = new List<string>();
        if (ListenEndpoint is not { } listen)
            problems.Add("AdminPanel:Listen must be http(s)://address:port, with an IP address or localhost");
        else
        {
            if (!listen.Https && !development) problems.Add("outside Development the panel listens with HTTPS only: AdminPanel:Listen must start with https://");
            if (!listen.Https && !LoopbackOnly) problems.Add("plain HTTP is for loopback only: AdminPanel:Listen must be https:// on this address");
            if (listen.Https && CertificatePath is null) problems.Add("HTTPS needs a certificate: set AdminPanel:CertificatePath to a .pfx file");
            if (!LoopbackOnly)
            {
                if (development) problems.Add("in Development the panel listens on loopback only");
                else if (!PrivateNetwork)
                    problems.Add("AdminPanel:Listen is not loopback: only on a private network (a VPN or an allowlist), and AdminPanel:PrivateNetwork=true says it is one");
                if (AllowedHosts.Count == 0) problems.Add("on a non-loopback address AdminPanel:AllowedHosts must name the host names staff use");
            }
        }
        if (AllowedHosts.Any(h => h.Contains('*'))) problems.Add("AdminPanel:AllowedHosts takes no wildcards: name each host");
        if (CertificatePath is { } path && !File.Exists(path)) problems.Add($"AdminPanel:CertificatePath '{path}' does not exist");

        if (DevelopmentSignIn && !development)
            problems.Add("AdminPanel:DevelopmentSignIn is refused outside the Development environment: it signs anyone in as any role without a password");
        if (!HasProvider && !(DevelopmentSignIn && development)) problems.Add("no way to sign in: set Oidc:Authority and Oidc:ClientId (or, in Development, AdminPanel:DevelopmentSignIn=true)");
        if (HasProvider) problems.AddRange(Oidc.Problems(development, environment));

        if (SessionMinutes is < 5 or > 1_440) problems.Add("AdminPanel:SessionMinutes must be 5-1440");
        if (StatusRefreshSeconds is < MinStatusRefreshSeconds or > 3_600) problems.Add($"AdminPanel:StatusRefreshSeconds must be {MinStatusRefreshSeconds}-3600");

        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var url) || (url.Scheme != "http" && url.Scheme != "https") || url.PathAndQuery != "/" || url.UserInfo.Length > 0)
            problems.Add("Server:Url must be https://host:port (no path, no user)");
        else if (url.Scheme == "http" && !(development && url.IsLoopback))
            problems.Add("Server:Url must be https:// (http only to loopback, in Development): the service credential goes with every request");
        if (ServerTimeoutMs is < 100 or > 120_000) problems.Add("Server:TimeoutMs must be 100-120000");
        if (ServerCertificatePath is { } pem && !File.Exists(pem)) problems.Add($"Server:CertificatePath '{pem}' does not exist");
        var secret = (environment(CredentialEnv) ?? string.Empty).Trim();
        if (secret.Length < MinCredentialLength)
            problems.Add($"the service credential is missing: set the environment variable {CredentialEnv} to the server's own ({MinCredentialLength} characters or more)");
        else if (secret.Any(c => c is < '!' or > '~'))
            problems.Add($"the service credential in {CredentialEnv} must be printable ASCII without spaces (it travels in a header)");
        return problems;
    }
}

/// <summary>The OpenID Connect provider and how its claims become staff roles.</summary>
public sealed record OidcSettings
{
    /// <summary>The provider's issuer URL; empty: no provider.</summary>
    public string? Authority { get; init; }

    /// <summary>The panel's client id.</summary>
    public string? ClientId { get; init; }

    /// <summary>The environment variable with the client secret.</summary>
    public string ClientSecretEnv { get; init; } = "GAME_PANEL_OIDC_CLIENT_SECRET";

    /// <summary>The scopes asked for (<c>openid</c> is always among them).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = ["openid", "profile"];

    /// <summary>The claim whose values name the staff member's groups.</summary>
    public string RoleClaim { get; init; } = "groups";

    /// <summary>The claim with the display name.</summary>
    public string NameClaim { get; init; } = "name";

    /// <summary>Also ask the userinfo endpoint (some providers put groups only there).</summary>
    public bool UseUserInfo { get; init; }

    /// <summary>For each staff role, the claim values that give it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Roles { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Why the provider's settings cannot be used, or nothing.</summary>
    public IEnumerable<string> Problems(bool development, Func<string, string?> environment)
    {
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority) || (authority.Scheme != "https" && !(development && authority.Scheme == "http" && authority.IsLoopback)))
            yield return "Oidc:Authority must be an https:// URL (http only to loopback, in Development)";
        if (string.IsNullOrWhiteSpace(ClientId)) yield return "Oidc:ClientId is missing";
        if (string.IsNullOrWhiteSpace(environment(ClientSecretEnv))) yield return $"the client secret is missing: set the environment variable {ClientSecretEnv}";
        foreach (var role in Roles.Keys.Where(r => !StaffRoles.All.Contains(r)))
            yield return $"Oidc:Roles:{role} is not a staff role ({string.Join(", ", StaffRoles.All.Ordered)})";
        if (!Roles.Any(r => StaffRoles.All.Contains(r.Key) && r.Value.Count > 0))
            yield return "Oidc:Roles maps no group to a role, so nobody could sign in";
    }
}
