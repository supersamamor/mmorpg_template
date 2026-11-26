using System.Globalization;
using System.Net;
using System.Reflection;

namespace Game.Server.Admin;

/// <summary>
/// The admin API's listener (ARCHITECTURE §12, T-804): server configuration, never content. Each value has a default here and can be set by an environment variable
/// <c>GAME_ADMIN_&lt;NAME&gt;</c> or a command-line option <c>--admin-&lt;name&gt; value</c> (kebab case; the command line wins). Off unless <see cref="Port"/> is set. The service
/// credential itself is never an option (options show up in process listings): it is the environment variable <see cref="CredentialEnv"/> names.
/// </summary>
public sealed record AdminOptions
{
    /// <summary>The command-line prefix.</summary>
    public const string ArgPrefix = "--admin-";

    /// <summary>The environment-variable prefix.</summary>
    public const string EnvPrefix = "GAME_ADMIN_";

    /// <summary>The shortest service credential accepted, in characters (a random one: <c>openssl rand -base64 32</c> gives 44).</summary>
    public const int MinCredentialLength = 32;

    /// <summary>Development mode (from the environment name, like the gateway's): plain HTTP on loopback only.</summary>
    public bool Development { get; init; }

    /// <summary>The TCP port; null (the default) keeps the admin API off. 0 picks a free one (tests). Never the game's port.</summary>
    public int? Port { get; init; }

    /// <summary>The IP address to listen on. Empty: loopback. Anything but loopback needs <see cref="PrivateNetwork"/>, and development allows only loopback.</summary>
    public string BindAddress { get; init; } = string.Empty;

    /// <summary>
    /// Says the address is on a private network that only staff tooling reaches (a VPN, or a firewall allowlist in front of it): without it a non-loopback address is
    /// refused. The server cannot see the network; whoever deploys it says so here, and answers for it.
    /// </summary>
    public bool PrivateNetwork { get; init; }

    /// <summary>PKCS#12 (<c>.pfx</c>) with the listener's TLS certificate and key. Required outside development. Never commit one.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>The environment variable with the certificate's password (empty when unset).</summary>
    public string CertificatePasswordEnv { get; init; } = "GAME_ADMIN_CERTIFICATE_PASSWORD";

    /// <summary>The environment variable with the service credential, at least <see cref="MinCredentialLength"/> characters. Never logged.</summary>
    public string CredentialEnv { get; init; } = "GAME_ADMIN_CREDENTIAL";

    /// <summary>Largest request body, in bytes (a command with its reason).</summary>
    public int MaxBodyBytes { get; init; } = 8 * 1024;

    /// <summary>Longest a request may take, arrival and answer together; past it the request is cut off.</summary>
    public int RequestTimeoutMs { get; init; } = 10_000;

    /// <summary>Longest a command's step on the tick may take to come back; past it the command is refused (<c>timeout</c>).</summary>
    public int StepTimeoutMs { get; init; } = 3_000;

    /// <summary>Requests per second one credential may make, on average.</summary>
    public double RateLimitPerSecond { get; init; } = 10;

    /// <summary>Requests one credential may make in a burst.</summary>
    public int RateLimitBurst { get; init; } = 30;

    /// <summary>Requests with a missing or wrong credential one address may make in a burst; then it waits.</summary>
    public int FailureBurst { get; init; } = 5;

    /// <summary>Failed requests per minute an address gets back.</summary>
    public double FailuresPerMinute { get; init; } = 10;

    /// <summary>The largest page of a lookup.</summary>
    public int MaxPageSize { get; init; } = 100;

    /// <summary>True when the admin API should listen.</summary>
    public bool Enabled => Port is not null;

    /// <summary>The address Kestrel binds; only meaningful when <see cref="Problems"/> is empty.</summary>
    public IPAddress ListenAddress => BindAddress.Length > 0 && IPAddress.TryParse(BindAddress, out var address) ? address : IPAddress.Loopback;

    /// <summary>Usage text.</summary>
    public static string Usage =>
        "admin API options (--admin-<name> <value> or env GAME_ADMIN_<NAME>; off unless port is set): " +
        string.Join(", ", Settable.Select(p => Kebab(p.Name) + " (" + (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).Name.ToLowerInvariant() + ")"));

    private static IEnumerable<PropertyInfo> Settable =>
        typeof(AdminOptions).GetProperties().Where(p => p.CanWrite && p.Name != nameof(Development));

    /// <summary>Environment, then the <c>--admin-</c> options (name, value pairs), over the defaults. Null with an error for an unknown name or a value that does not parse.</summary>
    public static AdminOptions? Parse(IReadOnlyList<string> args, Func<string, string?> environment, bool development, out string? error)
    {
        var options = new AdminOptions { Development = development };
        var byName = Settable.ToDictionary(p => Kebab(p.Name), StringComparer.Ordinal);
        error = null;
        try
        {
            foreach (var (name, property) in byName)
                if (environment(EnvPrefix + name.Replace('-', '_').ToUpperInvariant()) is { Length: > 0 } value)
                    options = With(options, property, value);
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith(ArgPrefix, StringComparison.Ordinal) || !byName.TryGetValue(arg[ArgPrefix.Length..], out var property))
                    throw new FormatException($"unknown admin option '{arg}'");
                if (i + 1 >= args.Count) throw new FormatException($"{arg} needs a value");
                options = With(options, property, args[++i]);
            }
        }
        catch (FormatException e)
        {
            error = e.Message;
            return null;
        }
        return options;
    }

    /// <summary>
    /// Why these options cannot be used (the server then refuses to start), or nothing. <paramref name="gamePort"/> is the gateway's; <paramref name="environment"/> reads
    /// the credential's variable (its value is never in a message).
    /// </summary>
    public IReadOnlyList<string> Problems(int gamePort, Func<string, string?> environment)
    {
        var problems = new List<string>();
        if (!Enabled) return problems;
        if (Port is < 0 or > 65535) problems.Add("admin port must be 0-65535");
        else if (Port != 0 && Port == gamePort) problems.Add("the admin API never listens on the game's port: set another --admin-port");
        if (BindAddress.Length > 0 && !IPAddress.TryParse(BindAddress, out _)) problems.Add($"admin bind-address '{BindAddress}' is not an IP address");
        else if (!IPAddress.IsLoopback(ListenAddress))
        {
            if (Development) problems.Add("in development the admin API listens on loopback only (plain HTTP); admin bind-address must be a loopback address");
            else if (!PrivateNetwork)
                problems.Add($"the admin API listens on {ListenAddress}, which is not loopback: only on a private network (a VPN or an allowlist), and --admin-private-network true says it is one");
        }
        if (!Development && string.IsNullOrWhiteSpace(CertificatePath))
            problems.Add("outside development the admin API needs TLS: set --admin-certificate-path (or GAME_ADMIN_CERTIFICATE_PATH) to a .pfx file");
        if (CertificatePath is { Length: > 0 } path && !File.Exists(path)) problems.Add($"admin certificate file '{path}' does not exist");
        if (string.IsNullOrWhiteSpace(CredentialEnv)) problems.Add("admin credential-env must name an environment variable");
        else if ((environment(CredentialEnv) ?? string.Empty).Trim().Length < MinCredentialLength)
            problems.Add($"the admin API needs its service credential: set the environment variable {CredentialEnv} to a random secret of at least {MinCredentialLength} characters");
        if (MaxBodyBytes < 256) problems.Add("admin max-body-bytes must be at least 256");
        if (RequestTimeoutMs < 1 || StepTimeoutMs < 1) problems.Add("admin timeouts must be above 0");
        if (!(RateLimitPerSecond > 0) || RateLimitBurst < 1 || FailureBurst < 1 || !(FailuresPerMinute > 0)) problems.Add("admin rate limits must be above 0");
        if (MaxPageSize is < 1 or > 1_000) problems.Add("admin max-page-size must be 1-1000");
        return problems;
    }

    private static AdminOptions With(AdminOptions options, PropertyInfo property, string text)
    {
        object value;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string)) value = text;
        else if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) value = i;
        else if (type == typeof(double) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) value = d;
        else if (type == typeof(bool) && bool.TryParse(text, out var b)) value = b;
        else throw new FormatException($"'{text}' is not a valid admin {Kebab(property.Name)}");
        var copy = options with { };
        property.SetValue(copy, value);
        return copy;
    }

    private static string Kebab(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
}
