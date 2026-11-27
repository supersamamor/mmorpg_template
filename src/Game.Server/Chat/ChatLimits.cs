namespace Game.Server.Chat;

/// <summary>
/// The per-account rate of chat (T-901): a token bucket of <see cref="ChatOptions.Burst"/> lines refilled at <see cref="ChatOptions.LinesPerMinute"/>. Per <b>account</b>, not per
/// connection: a connection is cheap to replace (drop and resume, sign in again), and each new one would bring a full bucket; the account is what a player cannot replace in a second.
/// A dev login, which has no account, is counted by its entity. Thread-safe; bounded (an idle bucket is full again and can be forgotten).
/// </summary>
public sealed class ChatLimits(ChatOptions options, TimeProvider time)
{
    /// <summary>Buckets kept; past it the full ones are forgotten.</summary>
    public const int MaxBuckets = 20_000;

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    /// <summary>Takes one line from <paramref name="key"/>'s bucket: true, or false with the seconds until one is back (at least 1).</summary>
    public bool TryTake(string key, out long retryAfterSeconds)
    {
        var now = time.GetUtcNow().UtcTicks / (double)TimeSpan.TicksPerSecond;   // the wall clock: tests set it, and a minute is a minute
        var perSecond = options.LinesPerMinute / 60.0;
        lock (_buckets)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                if (_buckets.Count >= MaxBuckets) Forget(now, perSecond);
                bucket = new Bucket { Tokens = options.Burst, At = now };
                _buckets[key] = bucket;
            }
            bucket.Tokens = Math.Min(options.Burst, bucket.Tokens + Math.Max(0, now - bucket.At) * perSecond);   // a clock set back gives nothing and takes nothing
            bucket.At = now;
            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                retryAfterSeconds = 0;
                return true;
            }
            retryAfterSeconds = Math.Max(1, (long)Math.Ceiling((1 - bucket.Tokens) / perSecond));
            return false;
        }
    }

    /// <summary>The key of an account.</summary>
    public static string Of(Guid account) => "a:" + account.ToString("N");

    /// <summary>The key of a login without an account (the dev login).</summary>
    public static string Of(World.EntityId entity) => "e:" + entity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Forget(double now, double perSecond)
    {
        foreach (var (key, bucket) in _buckets.ToList())
            if (bucket.Tokens + (now - bucket.At) * perSecond >= options.Burst) _buckets.Remove(key);
        if (_buckets.Count >= MaxBuckets) _buckets.Clear();   // a flood of accounts at once: start again rather than grow
    }

    private sealed class Bucket
    {
        public double Tokens;
        public double At;
    }
}
