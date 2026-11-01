using System.Globalization;
using System.Net;
using System.Reflection;

namespace Game.Server.Net;

/// <summary>
/// Every network setting of the gateway: where it listens, TLS, and the per-session limits and timeouts. This is server configuration, not
/// game content (it never goes in <c>content/</c>): each value has a default here and can be overridden by an environment variable
/// <c>GAME_NET_&lt;NAME&gt;</c> or a command-line option <c>--net-&lt;name&gt; value</c> (names in kebab case, e.g. <c>--net-port 7000</c>,
/// <c>GAME_NET_PORT=7000</c>). The command line wins over the environment. Times are milliseconds.
/// </summary>
public sealed record GatewayOptions
{
    /// <summary>The command-line prefix of gateway options.</summary>
    public const string ArgPrefix = "--net-";

    /// <summary>The environment-variable prefix of gateway options.</summary>
    public const string EnvPrefix = "GAME_NET_";

    /// <summary>
    /// Development mode: plain <c>ws://</c> is allowed (on loopback only) and the dev login stub is enabled. Outside development the
    /// gateway listens on every interface, only over TLS (<c>wss://</c>), and refuses the dev login. Set by the host from the environment
    /// name, not by a <c>--net-</c> option.
    /// </summary>
    public bool Development { get; init; }

    /// <summary>
    /// IP address to listen on. Empty: loopback in development, every interface otherwise. Development only accepts a loopback address.
    /// </summary>
    public string BindAddress { get; init; } = string.Empty;

    /// <summary>TCP port (0 picks a free one, for tests).</summary>
    public int Port { get; init; } = 7780;

    /// <summary>The WebSocket endpoint's path.</summary>
    public string Path { get; init; } = "/ws";

    /// <summary>
    /// PKCS#12 (<c>.pfx</c>) file with the TLS certificate and key. Required outside development. Never commit one; deploy it next to
    /// the server.
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>
    /// Name of the environment variable that holds the certificate file's password (the password itself is never an option, so it does not
    /// show up in process listings or logs). Empty password when the variable is unset.
    /// </summary>
    public string CertificatePasswordEnv { get; init; } = "GAME_NET_CERTIFICATE_PASSWORD";

    /// <summary>Largest client message accepted, in bytes; a longer one ends the session (<c>oversize</c>).</summary>
    public int MaxMessageBytes { get; init; } = 1024;

    /// <summary>Time from connecting to <c>hello</c> before the session ends (<c>timeout</c>).</summary>
    public int HelloTimeoutMs { get; init; } = 5_000;

    /// <summary>Time without any client message before the session ends (<c>timeout</c>). Clients ping well inside this.</summary>
    public int IdleTimeoutMs { get; init; } = 30_000;

    /// <summary>Most sessions at once; more get <c>server_full</c>.</summary>
    public int MaxSessions { get; init; } = 1_000;

    /// <summary>Longest dev display name, in UTF-16 code units.</summary>
    public int MaxNameLength { get; init; } = 24;

    /// <summary>Messages a client may send per second, on average (token bucket refill rate).</summary>
    public double RateLimitPerSecond { get; init; } = 20;

    /// <summary>Messages a client may send in a burst (token bucket size).</summary>
    public int RateLimitBurst { get; init; } = 40;

    /// <summary>
    /// Messages dropped by the rate limit, without the bucket ever refilling to full in between, after which the session ends
    /// (<c>rate_limit</c>). A client that bursts now and then never gets near it; one that floods does within seconds.
    /// </summary>
    public int RateLimitDropsBeforeBye { get; init; } = 50;

    /// <summary>Messages a session may have waiting to be sent. When full, its pending deltas are dropped and it is resynced with a snapshot.</summary>
    public int SendQueueMessages { get; init; } = 64;

    /// <summary>A resyncing session asks for its snapshot once its queue is down to this many messages.</summary>
    public int ResyncBelowMessages { get; init; } = 8;

    /// <summary>A session that stays behind (resyncing, or a send not completing) this long is disconnected (<c>too_slow</c>).</summary>
    public int SlowClientTimeoutMs { get; init; } = 10_000;

    /// <summary>How long the closing handshake (after <c>bye</c>) may take before the socket is dropped.</summary>
    public int CloseTimeoutMs { get; init; } = 2_000;

    /// <summary>How often timeouts are checked.</summary>
    public int SweepIntervalMs { get; init; } = 250;

    /// <summary>WebSocket protocol-level ping interval (keeps NATs open; separate from the game's <c>ping</c>).</summary>
    public int KeepAliveMs { get; init; } = 15_000;

    /// <summary>The address Kestrel binds (see <see cref="BindAddress"/>); only meaningful when <see cref="Problems"/> is empty.</summary>
    public IPAddress ListenAddress =>
        BindAddress.Length > 0 && IPAddress.TryParse(BindAddress, out var address) ? address
        : Development ? IPAddress.Loopback : IPAddress.Any;

    /// <summary>Usage text for the <c>--net-</c> options.</summary>
    public static string Usage =>
        "gateway options (--net-<name> <value> or env GAME_NET_<NAME>): " +
        string.Join(", ", Settable.Select(p => Kebab(p.Name) + " (" + p.PropertyType.Name.ToLowerInvariant() + ")"));

    private static IEnumerable<PropertyInfo> Settable =>
        typeof(GatewayOptions).GetProperties().Where(p => p.CanWrite && p.Name != nameof(Development));

    /// <summary>
    /// Applies environment variables, then <c>--net-</c> command-line options, over the defaults. Returns null with an error for an
    /// unknown name or a value that does not parse. <paramref name="args"/> holds only the gateway's options (name, value pairs).
    /// </summary>
    public static GatewayOptions? Parse(IReadOnlyList<string> args, Func<string, string?> environment, bool development, out string? error)
    {
        var options = new GatewayOptions { Development = development };
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
                    throw new FormatException($"unknown gateway option '{arg}'");
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

    /// <summary>Problems that make these options unusable (the host refuses to start), or none.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!Development && string.IsNullOrWhiteSpace(CertificatePath))
            problems.Add("outside development the gateway needs a TLS certificate: set --net-certificate-path (or GAME_NET_CERTIFICATE_PATH) to a .pfx file");
        if (CertificatePath is { Length: > 0 } path && !File.Exists(path))
            problems.Add($"certificate file '{path}' does not exist");
        if (BindAddress.Length > 0 && !IPAddress.TryParse(BindAddress, out _)) problems.Add($"bind-address '{BindAddress}' is not an IP address");
        else if (Development && !IPAddress.IsLoopback(ListenAddress))
            problems.Add("in development the gateway listens on loopback only (plain ws://); bind-address must be a loopback address");
        if (Port is < 0 or > 65535) problems.Add("port must be 0-65535");
        if (!Path.StartsWith('/')) problems.Add("path must start with '/'");
        if (MaxMessageBytes < 64) problems.Add("max-message-bytes must be at least 64");
        if (HelloTimeoutMs < 1 || IdleTimeoutMs < 1 || SlowClientTimeoutMs < 1 || CloseTimeoutMs < 1 || SweepIntervalMs < 1 || KeepAliveMs < 0)
            problems.Add("timeouts and intervals must be above 0");
        if (MaxSessions < 1) problems.Add("max-sessions must be at least 1");
        if (MaxNameLength < 1) problems.Add("max-name-length must be at least 1");
        if (!(RateLimitPerSecond > 0) || RateLimitBurst < 1 || RateLimitDropsBeforeBye < 1) problems.Add("rate limits must be above 0");
        if (SendQueueMessages < 2 || ResyncBelowMessages < 0 || ResyncBelowMessages >= SendQueueMessages)
            problems.Add("send-queue-messages must be at least 2 and resync-below-messages below it");
        return problems;
    }

    private static GatewayOptions With(GatewayOptions options, PropertyInfo property, string text)
    {
        object value;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string)) value = text;
        else if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) value = i;
        else if (type == typeof(double) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) value = d;
        else throw new FormatException($"'{text}' is not a valid {Kebab(property.Name)}");
        var copy = options with { };
        property.SetValue(copy, value);
        return copy;
    }

    private static string Kebab(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
}
