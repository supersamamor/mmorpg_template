using System;
using System.Globalization;

namespace Game.Client.Core.Combat;

/// <summary>How a number looks: who it concerns, crossed with hit, crit and miss. The view maps each to a size and a colour.</summary>
public enum NumberStyle
{
    /// <summary>Damage this client dealt.</summary>
    Dealt = 0,

    /// <summary>A crit this client dealt.</summary>
    DealtCrit = 1,

    /// <summary>A miss by this client.</summary>
    DealtMiss = 2,

    /// <summary>Damage this client took.</summary>
    Taken = 3,

    /// <summary>A crit this client took.</summary>
    TakenCrit = 4,

    /// <summary>A miss against this client.</summary>
    TakenMiss = 5,

    /// <summary>Damage between others.</summary>
    Other = 6,

    /// <summary>A crit between others.</summary>
    OtherCrit = 7,

    /// <summary>A miss between others.</summary>
    OtherMiss = 8,
}

/// <summary>Rules for <see cref="NumberStyle"/>.</summary>
public static class NumberStyles
{
    /// <summary>Number of styles.</summary>
    public const int Count = 9;

    /// <summary>The style for a hit seen from this client's side. Styles run Dealt, Taken, Other in that order (<see cref="HitSide"/> is Other, Dealt, Taken).</summary>
    public static NumberStyle Of(HitSide side, HitKind kind)
    {
        var group = side == HitSide.Dealt ? 0 : side == HitSide.Taken ? 1 : 2;
        return (NumberStyle)(group * 3 + (int)kind);
    }

    /// <summary>True for a crit style (larger, with an accent).</summary>
    public static bool IsCrit(NumberStyle style) => (int)style % 3 == (int)HitKind.Crit;

    /// <summary>True for a miss style (the word, not a number).</summary>
    public static bool IsMiss(NumberStyle style) => (int)style % 3 == (int)HitKind.Miss;

    /// <summary>True for damage this client took (a different colour from what it deals).</summary>
    public static bool IsTaken(NumberStyle style) => (int)style / 3 == 1;

    /// <summary>True for a fight this client is not part of (smaller, dimmer).</summary>
    public static bool IsOther(NumberStyle style) => (int)style / 3 == 2;
}

/// <summary>One floating number.</summary>
public struct NumberSlot
{
    /// <summary>In use.</summary>
    public bool Active;

    /// <summary>Its look.</summary>
    public NumberStyle Style;

    /// <summary>The text: digits, or the miss word.</summary>
    public string Text;

    /// <summary>World X it is anchored at.</summary>
    public double X;

    /// <summary>World Z it is anchored at.</summary>
    public double Z;

    /// <summary>The entity it floats over.</summary>
    public long Target;

    /// <summary>Sideways lane: 0 is straight above, then 1, -1, 2, -2 ... so numbers on one target spread out.</summary>
    public int Lane;

    /// <summary>View time it appeared.</summary>
    public double Start;
}

/// <summary>
/// The floating damage numbers' state: a fixed pool, no allocation per hit after warm-up (digit strings are cached). Numbers that land on one target close
/// together take different lanes so they spread out instead of stacking. The cap is <see cref="Capacity"/>: past it the oldest number is dropped and its slot
/// reused (chosen over merging, so every number still on screen is one real hit). Pure; the view draws the slots.
/// </summary>
public sealed class DamageNumberPool
{
    private const int CachedAmounts = 2048;
    private const int MaxLane = 3;

    private readonly NumberSlot[] _slots;
    private readonly string[] _digits = new string[CachedAmounts];
    private readonly double _lifetime;
    private readonly double _laneWindow;
    private int _active;

    /// <summary>Creates a pool of <paramref name="capacity"/> numbers that live <paramref name="lifetimeSeconds"/>; numbers on one target within <paramref name="laneWindowSeconds"/> of each other take different lanes.</summary>
    public DamageNumberPool(int capacity, double lifetimeSeconds, double laneWindowSeconds)
    {
        _slots = new NumberSlot[Math.Max(1, capacity)];
        _lifetime = lifetimeSeconds;
        _laneWindow = laneWindowSeconds;
    }

    /// <summary>Most numbers on screen at once.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Seconds a number lives.</summary>
    public double Lifetime => _lifetime;

    /// <summary>Numbers showing now.</summary>
    public int ActiveCount => _active;

    /// <summary>Numbers dropped because the pool was full.</summary>
    public long DroppedForCap { get; private set; }

    /// <summary>The slots; check <see cref="NumberSlot.Active"/>. Index it, do not enumerate.</summary>
    public NumberSlot[] Slots => _slots;

    /// <summary>Starts a number for an impact.</summary>
    public void OnImpact(in HitImpact impact)
    {
        var style = NumberStyles.Of(impact.Side, impact.Kind);
        var lane = LaneFor(impact.Target, impact.At);
        var index = Free();
        _slots[index] = new NumberSlot
        {
            Active = true,
            Style = style,
            Text = NumberStyles.IsMiss(style) ? UiText.MissWord : AmountText(impact.Amount),
            X = impact.X,
            Z = impact.Z,
            Target = impact.Target,
            Lane = lane,
            Start = impact.At,
        };
        _active++;
    }

    /// <summary>Retires numbers whose time is up. True while any number is still showing.</summary>
    public bool Update(double now)
    {
        if (_active == 0) return false;
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Active && now - _slots[i].Start >= _lifetime)
            {
                _slots[i].Active = false;
                _active--;
            }
        }
        return _active > 0;
    }

    /// <summary>Forgets every number (a new map, a new login).</summary>
    public void Clear()
    {
        for (var i = 0; i < _slots.Length; i++) _slots[i].Active = false;
        _active = 0;
    }

    private int Free()
    {
        var oldest = 0;
        var oldestStart = double.MaxValue;
        for (var i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].Active) return i;
            if (_slots[i].Start < oldestStart)
            {
                oldestStart = _slots[i].Start;
                oldest = i;
            }
        }
        DroppedForCap++;
        _active--; // the caller adds one back
        return oldest;
    }

    private int LaneFor(long target, double now)
    {
        // Lanes 0, 1, -1, 2, -2, ...: the first one no recent number on this target holds. Past the last lane it stays on lane 0.
        for (var step = 0; step <= MaxLane * 2; step++)
        {
            var lane = step == 0 ? 0 : ((step + 1) / 2) * ((step & 1) == 1 ? 1 : -1);
            var taken = false;
            for (var i = 0; i < _slots.Length && !taken; i++)
                taken = _slots[i].Active && _slots[i].Target == target && _slots[i].Lane == lane && now - _slots[i].Start < _laneWindow;
            if (!taken) return lane;
        }
        return 0;
    }

    private string AmountText(int amount)
    {
        if (amount < 0) amount = 0;
        if (amount >= CachedAmounts) return amount.ToString(CultureInfo.InvariantCulture);
        return _digits[amount] ??= amount.ToString(CultureInfo.InvariantCulture);
    }
}
