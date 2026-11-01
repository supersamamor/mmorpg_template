namespace Game.Shared.Time
{
    /// <summary>
    /// Injected monotonic clock (CLAUDE.md rule 8). Simulation code never reads the wall clock: it takes an
    /// <see cref="IClock"/> so tests can advance time by hand. The value only ever grows and has no fixed epoch;
    /// use differences between two readings, never the absolute value.
    /// </summary>
    public interface IClock
    {
        /// <summary>Milliseconds since an arbitrary fixed point; never decreases.</summary>
        long NowMilliseconds { get; }
    }
}
