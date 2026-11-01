namespace Game.Tests;

/// <summary>
/// Measures bytes allocated on the current thread by an action, for "allocates nothing" tests.
/// Runs the action once to warm up, then up to <c>attempts</c> more times and returns the smallest count. Under a loaded parallel
/// test run the runtime can charge a one-off allocation (tiered JIT, on-stack replacement) to the thread; a real per-call allocation
/// shows up in every attempt, so the minimum stays meaningful.
/// </summary>
internal static class AllocationProbe
{
    public static long MinAllocatedBytes(Action action, int attempts = 3)
    {
        action();
        var best = long.MaxValue;
        for (var i = 0; i < attempts && best != 0; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return best;
    }
}
