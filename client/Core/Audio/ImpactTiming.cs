using System;
using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Audio;

/// <summary>One hit at the moment it lands.</summary>
public readonly struct ImpactEvent
{
    /// <summary>Creates an event.</summary>
    public ImpactEvent(long attacker, long target, int amount, string result)
    {
        Attacker = attacker;
        Target = target;
        Amount = amount;
        Result = result;
    }

    /// <summary>Entity id of the attacker.</summary>
    public long Attacker { get; }

    /// <summary>Entity id of the target.</summary>
    public long Target { get; }

    /// <summary>Damage (0 on a miss).</summary>
    public int Amount { get; }

    /// <summary>A "Hit results" term.</summary>
    public string Result { get; }
}

/// <summary>
/// The moments hits land, as audio sees them. The implementation is <see cref="CombatImpactSource"/> over <c>WorldView.Combat.Impact</c> (T-303), the one timing source
/// shared with the damage numbers and the hit reactions; the per-rig impact delay is already applied and stale hits are never replayed.
/// </summary>
public interface IImpactSource
{
    /// <summary>Hits that landed since the last <see cref="Clear"/>.</summary>
    IReadOnlyList<ImpactEvent> Impacts { get; }

    /// <summary>Forgets the impacts the consumer has handled.</summary>
    void Clear();
}

/// <summary>An <see cref="IImpactSource"/> over <see cref="Combat.CombatTimeline.Impact"/>: collects each landing into a reusable list. Allocation-free after warm-up.</summary>
public sealed class CombatImpactSource : IImpactSource
{
    private readonly List<ImpactEvent> _impacts = new List<ImpactEvent>(32);

    /// <summary>Subscribes to a timeline. The event fires from <c>WorldView.Advance</c>, so create this before the first <c>ClientSession.Update</c>.</summary>
    public CombatImpactSource(Combat.CombatTimeline timeline) => timeline.Impact += OnImpact;

    /// <inheritdoc />
    public IReadOnlyList<ImpactEvent> Impacts => _impacts;

    /// <inheritdoc />
    public void Clear() => _impacts.Clear();

    private void OnImpact(in Combat.HitImpact impact) =>
        _impacts.Add(new ImpactEvent(impact.Attacker, impact.Target, impact.Amount, impact.Kind == Combat.HitKind.Crit ? HitResults.Crit : impact.Kind == Combat.HitKind.Miss ? HitResults.Miss : HitResults.Hit));
}
