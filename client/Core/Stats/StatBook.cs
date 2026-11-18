using System.Collections.Generic;
using Game.Shared.Protocol;

namespace Game.Client.Core.Stats;

/// <summary>
/// The player's own stats as the server last said them (T-503, <c>stats</c>): unspent stat points, the six primary stats with the cost of each next +1, and the
/// derived stats the panel shows. Nothing here computes a cost or a derived value: they are the server's numbers, and the server checks every raise.
/// <see cref="Version"/> moves on every change, so a screen redraws only then.
/// </summary>
public sealed class StatBook
{
    private static readonly IReadOnlyList<StatRecord> NoStats = new List<StatRecord>();
    private static readonly IReadOnlyList<DerivedRecord> NoDerived = new List<DerivedRecord>();

    /// <summary>Unspent stat points.</summary>
    public int Points { get; private set; }

    /// <summary>The primary stats in the server's order, each with the cost of its next +1 (0: it cannot be raised).</summary>
    public IReadOnlyList<StatRecord> Stats { get; private set; } = NoStats;

    /// <summary>The derived stats of the panel, in the server's order.</summary>
    public IReadOnlyList<DerivedRecord> Derived { get; private set; } = NoDerived;

    /// <summary>True once the server has sent the stats.</summary>
    public bool Known => Stats.Count > 0;

    /// <summary>Counts changes, so a screen redraws only when this moved.</summary>
    public int Version { get; private set; }

    /// <summary>Takes the server's stats: replaces everything.</summary>
    public void Apply(StatsMessage message)
    {
        Points = message.Points;
        Stats = message.Stats;
        Derived = message.Derived;
        Version++;
    }

    /// <summary>True when the stat has a next cost the unspent points cover (only decides whether the + button looks pressable; the server decides).</summary>
    public bool Affordable(StatRecord stat) => stat.NextCost > 0 && stat.NextCost <= Points;

    /// <summary>Forgets everything (a new login).</summary>
    public void Reset()
    {
        Points = 0;
        Stats = NoStats;
        Derived = NoDerived;
        Version++;
    }
}
