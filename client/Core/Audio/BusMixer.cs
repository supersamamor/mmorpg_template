using System;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Audio;

/// <summary>
/// The player's volume per bus and the mute switch (T-305). Linear 0–1 per bus; the engine's bus volume is the decibel value of that. The values come from
/// <see cref="Ui.UserPreferences"/> and go back to it. The silent switch and an interruption by another app are <see cref="AppAudioState"/>, not choices.
/// </summary>
public sealed class BusMixer
{
    /// <summary>The floor in decibels: anything quieter is silence.</summary>
    public const double SilenceDb = -80;

    /// <summary>The buses, in the order the volume array uses.</summary>
    public static readonly string[] Buses = { AudioBuses.Music, AudioBuses.Sfx, AudioBuses.Ui, AudioBuses.Ambient };

    private readonly double[] _volume = { 0.7, 1, 1, 0.8 };

    /// <summary>True when the player muted everything.</summary>
    public bool Muted { get; set; }

    /// <summary>Index of a bus in <see cref="Buses"/>; -1 if unknown.</summary>
    public static int IndexOf(string bus)
    {
        for (var i = 0; i < Buses.Length; i++)
            if (Buses[i] == bus) return i;
        return -1;
    }

    /// <summary>The player's volume for a bus, 0–1.</summary>
    public double Volume(string bus) => IndexOf(bus) is var i and >= 0 ? _volume[i] : 1;

    /// <summary>Sets a bus volume, clamped to 0–1 (not a number counts as unchanged).</summary>
    public void SetVolume(string bus, double volume)
    {
        var i = IndexOf(bus);
        if (i < 0 || double.IsNaN(volume)) return;
        _volume[i] = Math.Max(0, Math.Min(1, volume));
    }

    /// <summary>The linear gain of a bus: 0 when muted.</summary>
    public double Gain(string bus) => Muted ? 0 : Volume(bus);

    /// <summary>The decibel value for the engine's bus: <see cref="SilenceDb"/> when the gain is 0.</summary>
    public double Db(string bus) => ToDb(Gain(bus));

    /// <summary>Linear gain to decibels, floored at <see cref="SilenceDb"/>.</summary>
    public static double ToDb(double gain) => gain <= 0.0001 ? SilenceDb : Math.Max(SilenceDb, 20 * Math.Log10(gain));
}
