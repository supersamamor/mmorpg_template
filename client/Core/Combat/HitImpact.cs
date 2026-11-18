using Game.Shared.Vocabulary;

namespace Game.Client.Core.Combat;

/// <summary>How a hit came out, as a closed set (the wire has the <c>HitResults</c> terms).</summary>
public enum HitKind
{
    /// <summary>A normal hit.</summary>
    Hit = 0,

    /// <summary>A critical hit.</summary>
    Crit = 1,

    /// <summary>The attack missed.</summary>
    Miss = 2,

    /// <summary>Damage from a status tick (poison, T-501): not an attack, so the source does not swing and no hit effect plays; the number shows.</summary>
    Status = 3,
}

/// <summary>Whose fight a hit is, from this client's point of view.</summary>
public enum HitSide
{
    /// <summary>Neither side is this client's character.</summary>
    Other = 0,

    /// <summary>This client's character dealt it.</summary>
    Dealt = 1,

    /// <summary>This client's character took it.</summary>
    Taken = 2,
}

/// <summary>
/// One hit at its impact moment: the single timing source for everything that reacts to a hit landing (hit reaction, damage number, effect, and the audio task).
/// Subscribe to <c>WorldView.Combat.Impact</c>. Presentation only; the server decided the outcome.
/// </summary>
/// <param name="Attacker">Attacker entity id.</param>
/// <param name="Target">Target entity id.</param>
/// <param name="Amount">Damage shown (0 for a miss).</param>
/// <param name="Kind">Hit, crit or miss.</param>
/// <param name="Side">Dealt by, taken by, or neither of this client's character.</param>
/// <param name="X">World X of the target when it landed.</param>
/// <param name="Z">World Z of the target when it landed.</param>
/// <param name="At">View-clock time of the impact, in seconds.</param>
public readonly record struct HitImpact(long Attacker, long Target, int Amount, HitKind Kind, HitSide Side, double X, double Z, double At);

/// <summary>Receives an impact. An <c>in</c> parameter keeps it allocation-free.</summary>
public delegate void ImpactHandler(in HitImpact impact);

/// <summary>Maps wire terms to <see cref="HitKind"/>.</summary>
public static class HitKinds
{
    /// <summary>The kind of a <c>HitResults</c> term; anything unknown counts as a plain hit.</summary>
    public static HitKind Of(string result) => result == HitResults.Crit ? HitKind.Crit : result == HitResults.Miss ? HitKind.Miss
        : result == HitResults.Status ? HitKind.Status : HitKind.Hit;
}
