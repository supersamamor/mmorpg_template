namespace Game.Server.Net;

/// <summary>
/// Brute-force guard for resume tokens (ARCHITECTURE §11): failed resumes are counted per remote address in a small
/// <see cref="TokenBucket"/> (<see cref="GatewayOptions.ResumeFailureBurst"/> failures, refilled at
/// <see cref="GatewayOptions.ResumeFailuresPerMinute"/>). A failure that finds the bucket empty locks the address out of resuming for
/// <see cref="GatewayOptions.ResumeLockoutMs"/>. Time comes from the gateway's clock. Thread-safe.
/// </summary>
/// <remarks>256-bit tokens cannot be guessed anyway; this keeps a scanner from burning CPU and log lines, and bounds what one address can try.</remarks>
public sealed class ResumeGuard(GatewayOptions options)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, State> _byAddress = new(StringComparer.Ordinal);

    private sealed class State(TokenBucket bucket)
    {
        public readonly TokenBucket Bucket = bucket;
        public long LockedUntilMs = -1;
        public long LastFailureMs;
    }

    /// <summary>Addresses being tracked (with recent failures or a lockout).</summary>
    public int Tracked
    {
        get { lock (_lock) return _byAddress.Count; }
    }

    /// <summary>True while <paramref name="address"/> may not resume.</summary>
    public bool IsLockedOut(string address, long nowMs)
    {
        lock (_lock) return _byAddress.TryGetValue(address, out var state) && nowMs < state.LockedUntilMs;
    }

    /// <summary>Counts a failed resume from <paramref name="address"/>. True when this failure locked it out.</summary>
    public bool Fail(string address, long nowMs)
    {
        lock (_lock)
        {
            if (!_byAddress.TryGetValue(address, out var state))
            {
                state = new State(new TokenBucket(options.ResumeFailuresPerMinute / 60.0, options.ResumeFailureBurst, nowMs));
                _byAddress.Add(address, state);
            }
            state.LastFailureMs = nowMs;
            if (state.Bucket.TryTake(nowMs)) return false;
            state.LockedUntilMs = nowMs + options.ResumeLockoutMs;
            return true;
        }
    }

    /// <summary>Forgets addresses whose lockout is over and whose bucket has refilled (gateway sweeper).</summary>
    public void Prune(long nowMs)
    {
        var refillMs = options.ResumeFailureBurst * 60_000.0 / options.ResumeFailuresPerMinute;
        lock (_lock)
        {
            List<string>? stale = null;
            foreach (var (address, state) in _byAddress)
                if (nowMs >= state.LockedUntilMs && nowMs - state.LastFailureMs >= refillMs) (stale ??= new()).Add(address);
            if (stale is null) return;
            foreach (var address in stale) _byAddress.Remove(address);
        }
    }
}
