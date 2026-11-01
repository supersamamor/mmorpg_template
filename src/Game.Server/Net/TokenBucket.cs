namespace Game.Server.Net;

/// <summary>
/// Per-session rate limit (ARCHITECTURE §11): a token bucket of <c>burst</c> tokens refilled at <c>perSecond</c>, one token per message.
/// Time comes from the caller (the gateway's clock), so tests drive it by hand. Not thread-safe: one per session, used by its receive loop.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _perMs;
    private readonly int _burst;
    private double _tokens;
    private long _lastMs;

    /// <summary>Creates a full bucket at <paramref name="nowMs"/>.</summary>
    public TokenBucket(double perSecond, int burst, long nowMs)
    {
        if (!(perSecond > 0)) throw new ArgumentOutOfRangeException(nameof(perSecond));
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        _perMs = perSecond / 1000.0;
        _burst = burst;
        _tokens = burst;
        _lastMs = nowMs;
    }

    /// <summary>Messages refused since the bucket was last full (see <see cref="GatewayOptions.RateLimitDropsBeforeBye"/>).</summary>
    public int DropsSinceFull { get; private set; }

    /// <summary>Messages refused in total.</summary>
    public long Drops { get; private set; }

    /// <summary>Takes a token if there is one; otherwise counts a drop and returns false.</summary>
    public bool TryTake(long nowMs)
    {
        if (nowMs > _lastMs)
        {
            _tokens = Math.Min(_burst, _tokens + (nowMs - _lastMs) * _perMs);
            _lastMs = nowMs;
        }
        if (_tokens >= _burst) DropsSinceFull = 0;
        if (_tokens >= 1)
        {
            _tokens -= 1;
            return true;
        }
        DropsSinceFull++;
        Drops++;
        return false;
    }
}
