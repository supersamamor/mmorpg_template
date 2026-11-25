using System.Net;
using System.Net.Sockets;
using Game.Server.Net;

namespace Game.Server.Accounts;

/// <summary>The limits of the account endpoints. Server configuration, not content.</summary>
public sealed record AccountLimits
{
    /// <summary>Requests one address may make in a burst (any endpoint), and per minute after that.</summary>
    public int RequestBurst { get; init; } = 30;

    /// <summary>Requests per minute per address.</summary>
    public double RequestsPerMinute { get; init; } = 60;

    /// <summary>Registrations one address may try in a burst, and per hour after that.</summary>
    public int RegisterBurst { get; init; } = 5;

    /// <summary>Registrations per hour per address.</summary>
    public double RegistersPerHour { get; init; } = 20;

    /// <summary>Characters one account may try to create in a burst, and per hour after that (a deleted character keeps its name, so this also bounds hoarding).</summary>
    public int CreateBurst { get; init; } = 10;

    /// <summary>Character creations per hour per account.</summary>
    public double CreatesPerHour { get; init; } = 10;

    /// <summary>Failed logins in a row an account gets before it is locked; each one after that doubles the lock.</summary>
    public int AccountFreeFailures { get; init; } = 5;

    /// <summary>Failed logins an address gets before it is locked: more than an account's, since players share addresses.</summary>
    public int AddressFreeFailures { get; init; } = 20;

    /// <summary>The first lock, in milliseconds.</summary>
    public int LockBaseMs { get; init; } = 5_000;

    /// <summary>The longest lock. An attacker can keep a username locked, so this bounds what that costs its owner (who stays signed in meanwhile).</summary>
    public int LockCapMs { get; init; } = 300_000;

    /// <summary>Failures are forgotten after this long without another.</summary>
    public int ForgetMs { get; init; } = 900_000;

    /// <summary>Most keys (addresses, usernames) each table remembers: an attacker with many of them cannot grow the server's memory.</summary>
    public int MaxTracked { get; init; } = 50_000;

    /// <summary>How long a session lasts.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Live sessions an account keeps; a sign-in beyond that ends the oldest.</summary>
    public int MaxSessions { get; init; } = 10;
}

/// <summary>
/// Counts failures per key and locks a key out for longer and longer. An attempt is counted as a failure when it <b>starts</b> and taken back when it succeeds,
/// so a hundred attempts at once get no more tries than a hundred in a row. Time comes from the caller. Thread-safe.
/// </summary>
public sealed class FailureLock(int freeFailures, int lockBaseMs, int lockCapMs, int forgetMs, int maxTracked)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    private sealed class State
    {
        public int Failures;
        public long LockedUntilMs;
        public long LastMs;
    }

    /// <summary>Keys remembered now.</summary>
    public int Tracked
    {
        get { lock (_lock) return _states.Count; }
    }

    /// <summary>Starts an attempt. Returns 0 when it may go on (and counts it as failed until <see cref="Succeed"/> or <see cref="Undo"/>), else the milliseconds to wait.</summary>
    public long TryBegin(string key, long nowMs)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(key, out var state))
            {
                if (_states.Count >= maxTracked) MakeRoom(nowMs);
                _states.Add(key, state = new State());
            }
            else if (nowMs < state.LockedUntilMs) return state.LockedUntilMs - nowMs;
            else if (nowMs - state.LastMs >= forgetMs) state.Failures = 0;
            state.LastMs = nowMs;
            state.Failures++;
            if (state.Failures > freeFailures)
            {
                var doublings = Math.Min(state.Failures - freeFailures - 1, 30);
                state.LockedUntilMs = nowMs + Math.Min(lockCapMs, (long)lockBaseMs << doublings);
            }
            return 0;
        }
    }

    /// <summary>The attempt succeeded: the key's failures are forgotten.</summary>
    public void Succeed(string key)
    {
        lock (_lock) _states.Remove(key);
    }

    /// <summary>The attempt was not a failure of this key's (it succeeded, or could not be judged): only it is taken back.</summary>
    public void Undo(string key)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(key, out var state)) return;
            state.Failures--;
            if (state.Failures <= freeFailures) state.LockedUntilMs = 0;
            if (state.Failures <= 0) _states.Remove(key);
        }
    }

    /// <summary>Forgets keys with no lock and no recent failure; if that frees nothing, the tenth longest untouched (so a full table is not scanned for every new key).</summary>
    private void MakeRoom(long nowMs)
    {
        var stale = _states.Where(s => nowMs >= s.Value.LockedUntilMs && nowMs - s.Value.LastMs >= forgetMs).Select(s => s.Key).ToList();
        if (stale.Count == 0) stale.AddRange(_states.OrderBy(s => s.Value.LastMs).Take(Math.Max(1, _states.Count / 10)).Select(s => s.Key));
        foreach (var key in stale) _states.Remove(key);
    }
}

/// <summary>A <see cref="TokenBucket"/> per key, in a table that cannot grow without bound. Thread-safe.</summary>
public sealed class KeyedBuckets(double perSecond, int burst, int maxTracked)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (TokenBucket Bucket, long LastMs)> _buckets = new(StringComparer.Ordinal);
    private readonly long _refillMs = (long)Math.Ceiling(burst / perSecond * 1000);

    /// <summary>Takes one token of <paramref name="key"/>; false when it has none left.</summary>
    public bool TryTake(string key, long nowMs)
    {
        lock (_lock)
        {
            if (!_buckets.TryGetValue(key, out var entry))
            {
                if (_buckets.Count >= maxTracked) MakeRoom(nowMs);
                entry.Bucket = new TokenBucket(perSecond, burst, nowMs);
            }
            _buckets[key] = (entry.Bucket, nowMs);
            return entry.Bucket.TryTake(nowMs);
        }
    }

    /// <summary>Milliseconds until a key that has run out has a token again.</summary>
    public long RetryAfterMs => (long)Math.Ceiling(1000 / perSecond);

    private void MakeRoom(long nowMs)
    {
        var full = _buckets.Where(b => nowMs - b.Value.LastMs >= _refillMs).Select(b => b.Key).ToList();
        if (full.Count == 0) full.AddRange(_buckets.OrderBy(b => b.Value.LastMs).Take(Math.Max(1, _buckets.Count / 10)).Select(b => b.Key));
        foreach (var key in full) _buckets.Remove(key);
    }
}

/// <summary>Who a request comes from, for the limits.</summary>
public static class AddressKeys
{
    /// <summary>
    /// The key of an address: an IPv4 address itself, and the /64 of an IPv6 address, because one subscriber holds a whole /64 and could change addresses at will.
    /// </summary>
    public static string Of(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>The key of an address as text (a session's); text that is no address is its own key.</summary>
    public static string Of(string address) => IPAddress.TryParse(address.Trim('[', ']'), out var parsed) ? Of(parsed) : address;
}
