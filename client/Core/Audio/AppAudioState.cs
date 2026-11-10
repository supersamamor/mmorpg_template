using System;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Audio;

/// <summary>
/// What the phone does to the game's audio, as pure state (T-305): paused while the app is in the background or another app took audio focus, and on iOS the
/// silent switch silencing effects (not music). It also keeps the <b>audio clock</b>: real time minus the time spent paused, so cooldowns, voice ends and fades
/// stand still while paused and carry on after. Where the facts come from (the engine's pause notification; a platform plugin for the switch and the focus) is the scene's business.
/// </summary>
public sealed class AppAudioState
{
    private double _pausedSince = double.NaN;
    private double _pausedTotal;

    /// <summary>The app is in the background.</summary>
    public bool Backgrounded { get; private set; }

    /// <summary>Another app holds audio focus (Android) or an interruption is going on (iOS call, Siri).</summary>
    public bool FocusLost { get; private set; }

    /// <summary>The iOS silent switch is on.</summary>
    public bool SilentSwitch { get; private set; }

    /// <summary>True while nothing may play.</summary>
    public bool Paused => Backgrounded || FocusLost;

    /// <summary>The app went to the background or came back. Returns true when <see cref="Paused"/> changed.</summary>
    public bool SetBackgrounded(bool value, double realNow)
    {
        var before = Paused;
        Backgrounded = value;
        return Settle(before, realNow);
    }

    /// <summary>Another app took or gave back audio focus. Returns true when <see cref="Paused"/> changed.</summary>
    public bool SetFocusLost(bool value, double realNow)
    {
        var before = Paused;
        FocusLost = value;
        return Settle(before, realNow);
    }

    /// <summary>The silent switch changed.</summary>
    public void SetSilentSwitch(bool on) => SilentSwitch = on;

    /// <summary>The audio clock for a real time in seconds: frozen while paused.</summary>
    public double Clock(double realNow) => (double.IsNaN(_pausedSince) ? realNow : _pausedSince) - _pausedTotal;

    /// <summary>True when a bus is silenced by the silent switch: effects, interface and ambience follow it, music is the player's choice.</summary>
    public bool SilencedBySwitch(string bus) => SilentSwitch && bus != AudioBuses.Music;

    private bool Settle(bool before, double realNow)
    {
        var after = Paused;
        if (after == before) return false;
        if (after)
        {
            _pausedSince = realNow;
        }
        else
        {
            _pausedTotal += Math.Max(0, realNow - _pausedSince);
            _pausedSince = double.NaN;
        }
        return true;
    }
}
