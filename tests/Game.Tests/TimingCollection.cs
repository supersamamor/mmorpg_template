namespace Game.Tests;

/// <summary>
/// Tests that measure wall time or allocations, or that depend on real time passing (socket rate limits, grace periods), run one at a
/// time and never beside other tests: a busy neighbour skews the numbers and makes timing assertions flaky.
/// Put <c>[Collection(TimingCollection.Name)]</c> on every such class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    /// <summary>Collection name.</summary>
    public const string Name = "Timing (not parallel)";
}
