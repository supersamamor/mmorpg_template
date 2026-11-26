using System.Net;
using System.Security.Cryptography;
using System.Text;
using Game.Server.Accounts;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Admin;

/// <summary>
/// The service credential the panel proves itself with (ARCHITECTURE §12.1). Only its SHA-256 is kept; a candidate is hashed the same way and the two digests are compared with
/// <see cref="CryptographicOperations.FixedTimeEquals"/>, so the time taken depends neither on how much of it is right nor on its length. It never appears in a log line, an
/// exception or <see cref="ToString"/>.
/// </summary>
public sealed class ServiceCredential
{
    private readonly byte[] _digest;

    /// <summary>Keeps the digest of <paramref name="secret"/> (trimmed); throws when it is shorter than <see cref="AdminOptions.MinCredentialLength"/>.</summary>
    public ServiceCredential(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        secret = secret.Trim();
        if (secret.Length < AdminOptions.MinCredentialLength) throw new ArgumentException($"A service credential has at least {AdminOptions.MinCredentialLength} characters.", nameof(secret));
        _digest = Digest(secret);
        Key = Convert.ToHexString(SHA256.HashData(_digest))[..16]; // a name for the rate limit's table: a hash of the digest, so it gives nothing away
    }

    /// <summary>A short, non-secret name of this credential (for the rate limit; never the credential or its digest).</summary>
    public string Key { get; }

    /// <summary>True when <paramref name="candidate"/> is the credential. Constant time in the candidate's content and length.</summary>
    public bool Matches(string? candidate) => CryptographicOperations.FixedTimeEquals(_digest, Digest(candidate ?? string.Empty));

    /// <summary>The credential from an <c>Authorization: Bearer ...</c> header value, or null.</summary>
    public static string? FromHeader(string? authorization) =>
        authorization is { } value && value.StartsWith(AdminHeaders.CredentialScheme, StringComparison.Ordinal) ? value[AdminHeaders.CredentialScheme.Length..].Trim() : null;

    /// <inheritdoc />
    public override string ToString() => "ServiceCredential { *** }";

    private static byte[] Digest(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// The staff member a request is made for, as the panel says (ARCHITECTURE §12.2): the identity provider's subject, a display name and a role. The server checks the shape and that
/// the role is a role; that the person is who the panel says, and holds that role, it takes from the panel, which signed them in with OIDC and MFA (docs/ADMIN_API.md "Trust").
/// </summary>
/// <param name="Id">The stable id (the OIDC subject).</param>
/// <param name="Name">The display name.</param>
/// <param name="Role">A "Staff roles" term.</param>
public sealed record StaffIdentity(string Id, string Name, string Role)
{
    /// <summary>The longest id.</summary>
    public const int MaxIdLength = 128;

    /// <summary>The longest display name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// The staff member from the three headers, or an "Admin errors" term: <c>invalid_staff</c> for a missing or malformed id or name, <c>unknown_role</c> for a missing role
    /// or one that is not a role. The name is percent-decoded (headers are ASCII).
    /// </summary>
    public static StaffIdentity? Parse(string? id, string? name, string? role, out string? error)
    {
        error = null;
        if (id is null || id.Length is 0 or > MaxIdLength || !id.All(IsIdChar))
        {
            error = AdminErrors.InvalidStaff;
            return null;
        }
        string? decoded = null;
        try
        {
            if (name is not null) decoded = Uri.UnescapeDataString(name).Trim();
        }
        catch (UriFormatException)
        {
            decoded = null;
        }
        if (decoded is null || decoded.Length is 0 or > MaxNameLength || decoded.Any(char.IsControl))
        {
            error = AdminErrors.InvalidStaff;
            return null;
        }
        if (role is null || !StaffRoles.All.Contains(role))
        {
            error = AdminErrors.UnknownRole;
            return null;
        }
        return new StaffIdentity(id, decoded, role);
    }

    /// <summary>The id only: display names are for the panel and the audit log, not the server's log.</summary>
    public override string ToString() => $"staff {Id} ({Role})";

    private static bool IsIdChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or '@' or ':' or '|';
}

/// <summary>
/// The admin API's rate limits: one token bucket per credential for every request it makes, and one per address for requests whose credential is missing or wrong, so guessing
/// is slow and the panel cannot be starved by someone without the credential. Both tables are bounded. Thread-safe; time comes from <paramref name="time"/>.
/// </summary>
public sealed class AdminRateLimit(AdminOptions options, TimeProvider time)
{
    /// <summary>The most addresses (and credentials) kept; past it a table starts over (a flood from that many addresses is a network problem, not a guessing one).</summary>
    public const int MaxKeys = 4_096;

    private readonly object _lock = new();
    private readonly DateTimeOffset _start = time.GetUtcNow();
    private readonly Dictionary<string, Bucket> _credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bucket> _failures = new(StringComparer.Ordinal);

    private double NowMs => (time.GetUtcNow() - _start).TotalMilliseconds; // a clock set back only stops the refill for a while

    /// <summary>Takes a request of the credential; false (with when to retry) when it is over its rate.</summary>
    public bool AllowRequest(string credentialKey, out int retryAfterSeconds)
    {
        lock (_lock)
            return Take(_credentials, credentialKey, options.RateLimitPerSecond / 1000.0, options.RateLimitBurst, out retryAfterSeconds);
    }

    /// <summary>True while the address may still try a credential (it has failed less than its burst lately).</summary>
    public bool MayTry(IPAddress? address, out int retryAfterSeconds)
    {
        lock (_lock)
        {
            retryAfterSeconds = 0;
            if (!_failures.TryGetValue(AddressKeys.Of(address), out var bucket)) return true;
            bucket.Refill(NowMs, options.FailuresPerMinute / 60_000.0, options.FailureBurst);
            if (bucket.Tokens >= 1) return true;
            retryAfterSeconds = bucket.SecondsToOne(options.FailuresPerMinute / 60_000.0);
            return false;
        }
    }

    /// <summary>Counts a request with a missing or wrong credential against its address.</summary>
    public void CountFailure(IPAddress? address)
    {
        lock (_lock) Take(_failures, AddressKeys.Of(address), options.FailuresPerMinute / 60_000.0, options.FailureBurst, out _);
    }

    private bool Take(Dictionary<string, Bucket> table, string key, double perMs, int burst, out int retryAfterSeconds)
    {
        if (!table.TryGetValue(key, out var bucket))
        {
            if (table.Count >= MaxKeys) table.Clear();
            table[key] = bucket = new Bucket(burst, NowMs);
        }
        bucket.Refill(NowMs, perMs, burst);
        retryAfterSeconds = 0;
        if (bucket.Tokens >= 1)
        {
            bucket.Tokens -= 1;
            return true;
        }
        retryAfterSeconds = bucket.SecondsToOne(perMs);
        return false;
    }

    private sealed class Bucket(double tokens, double atMs)
    {
        public double Tokens = tokens;
        private double _atMs = atMs;

        public void Refill(double nowMs, double perMs, int burst)
        {
            if (nowMs <= _atMs) return;
            Tokens = Math.Min(burst, Tokens + (nowMs - _atMs) * perMs);
            _atMs = nowMs;
        }

        public int SecondsToOne(double perMs) => Math.Max(1, (int)Math.Ceiling((1 - Tokens) / perMs / 1000.0));
    }
}

/// <summary>
/// Every admin command and the lowest role that may run it (ARCHITECTURE §12.2), in one place. The server checks it for every command, whatever the panel shows; reads need any role.
/// T-806 adds its commands here.
/// </summary>
public static class AdminCommandTable
{
    /// <summary>The lowest role for each "Admin actions" term.</summary>
    public static readonly IReadOnlyDictionary<string, string> LowestRole = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AdminActions.Kick] = StaffRoles.Support,
    };

    /// <summary>The lowest role for reads (status, lookups, the audit log).</summary>
    public const string ReadRole = StaffRoles.Support;

    /// <summary>True when <paramref name="role"/> may run <paramref name="action"/>; false for an unknown action or role.</summary>
    public static bool Allows(string action, string role) => LowestRole.TryGetValue(action, out var lowest) && StaffRoles.AtLeast(role, lowest);
}
