using System;

namespace Game.Client.Core.Audio;

/// <summary>
/// One looping channel (music or ambience) with an equal-power crossfade between two slots (T-305). Setting a different key starts it in the free slot and fades
/// the old one out over the same time. A third change during a fade cuts the old outgoing sound at once. Pure; time is the audio clock.
/// </summary>
public sealed class CrossfadeChannel
{
    private readonly double _seconds;
    private int _slot; // the slot of Current
    private double _fadeStart;

    /// <summary>Creates a channel that fades over <paramref name="seconds"/>.</summary>
    public CrossfadeChannel(double seconds) => _seconds = Math.Max(0, seconds);

    /// <summary>The key playing (fading in or full), or null.</summary>
    public string? Current { get; private set; }

    /// <summary>The key fading out, or null.</summary>
    public string? Outgoing { get; private set; }

    /// <summary>The slot (0 or 1) of <see cref="Current"/>.</summary>
    public int CurrentSlot => _slot;

    /// <summary>The slot of <see cref="Outgoing"/>.</summary>
    public int OutgoingSlot => 1 - _slot;

    /// <summary>True when something was dropped without fading (a change in the middle of a fade): the scene stops that slot now.</summary>
    public bool Cut { get; private set; }

    /// <summary>The fade length.</summary>
    public double Seconds => _seconds;

    /// <summary>Asks for <paramref name="key"/> (null for silence). False when it is already the current key.</summary>
    public bool Set(string? key, double now)
    {
        Cut = false;
        if (string.Equals(key, Current, StringComparison.Ordinal)) return false;
        if (Outgoing is not null) Cut = true; // still fading the one before: it goes now
        Outgoing = Current;
        Current = key;
        _slot = 1 - _slot;
        _fadeStart = now;
        return true;
    }

    /// <summary>The gains at <paramref name="now"/>. Once the fade is over the outgoing key is dropped, which <see cref="Outgoing"/> shows as null.</summary>
    public void Evaluate(double now, out double incoming, out double outgoing)
    {
        var t = _seconds <= 0 ? 1 : Math.Max(0, Math.Min(1, (now - _fadeStart) / _seconds));
        incoming = Current is null ? 0 : Math.Sin(t * Math.PI / 2);
        outgoing = Outgoing is null ? 0 : Math.Cos(t * Math.PI / 2);
        if (t >= 1 && Outgoing is not null) Outgoing = null;
    }
}
