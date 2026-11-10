using System;
using System.Collections.Generic;

namespace Game.Client.Core.Audio;

/// <summary>One hit of a delta's <c>hits</c> at a moment of its presentation.</summary>
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
/// The one source of hit timing that audio and combat visuals share (T-305 and T-303). <see cref="Swings"/> are attacks that reached the moment their attack clip
/// starts, <see cref="Impacts"/> the moment the hit lands (the target's reaction, the damage number). Both lists hold only what became due since the previous frame
/// and are replaced on the next one, so nothing replays (row 101). The combat-visuals implementation applies the theme's per-rig impact delay.
/// </summary>
public interface IImpactSource
{
    /// <summary>Attacks whose swing starts now.</summary>
    IReadOnlyList<ImpactEvent> Swings { get; }

    /// <summary>Hits that land now.</summary>
    IReadOnlyList<ImpactEvent> Impacts { get; }
}

/// <summary>
/// The interim <see cref="IImpactSource"/>, used until T-303's merges: a hit seen at view time T is due at T plus the render delay, the same rule
/// <see cref="World.EntityView"/> uses for its attack and hit counters, so sound lines up with the animation that exists today. Fixed-size ring, no allocation per hit.
/// </summary>
public sealed class ImpactScheduler : IImpactSource
{
    private readonly ImpactEvent[] _pending;
    private readonly double[] _due;
    private readonly List<ImpactEvent> _swings = new List<ImpactEvent>(32);
    private readonly List<ImpactEvent> _impacts = new List<ImpactEvent>(32);
    private int _count;

    /// <summary>Creates a scheduler holding at most <paramref name="capacity"/> hits; past that the oldest are dropped.</summary>
    public ImpactScheduler(int capacity = 256)
    {
        _pending = new ImpactEvent[capacity];
        _due = new double[capacity];
    }

    /// <inheritdoc />
    public IReadOnlyList<ImpactEvent> Swings => _swings;

    /// <inheritdoc />
    public IReadOnlyList<ImpactEvent> Impacts => _impacts;

    /// <summary>Hits waiting for their moment.</summary>
    public int Pending => _count;

    /// <summary>A delta's hit arrived at view time <paramref name="now"/>.</summary>
    public void Queue(in ImpactEvent hit, double now, double renderDelay)
    {
        if (_count == _pending.Length)
        {
            Array.Copy(_pending, 1, _pending, 0, _count - 1);
            Array.Copy(_due, 1, _due, 0, _count - 1);
            _count--;
        }
        _pending[_count] = hit;
        _due[_count] = now + renderDelay;
        _count++;
    }

    /// <summary>Moves everything due at view time <paramref name="now"/> into <see cref="Swings"/> and <see cref="Impacts"/> (replacing the previous frame's).</summary>
    public void Advance(double now)
    {
        _swings.Clear();
        _impacts.Clear();
        var kept = 0;
        for (var i = 0; i < _count; i++)
        {
            if (_due[i] <= now)
            {
                _swings.Add(_pending[i]);
                _impacts.Add(_pending[i]);
                continue;
            }
            _pending[kept] = _pending[i];
            _due[kept] = _due[i];
            kept++;
        }
        _count = kept;
    }

    /// <summary>Forgets everything waiting (a new login or a map change).</summary>
    public void Clear()
    {
        _count = 0;
        _swings.Clear();
        _impacts.Clear();
    }
}
