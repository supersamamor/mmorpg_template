using Game.Client.Core.Audio;
using Godot;

namespace Game.Client.Scene.Audio;

/// <summary>
/// The two phone facts Godot 4.7.2 does not give C# (T-305, ADR-002 row 112): the iOS silent switch and Android audio focus. Both need native code, so this reads them
/// from an engine singleton named <c>GameAudioPlatform</c> <b>if a plugin provides one</b>, and does nothing otherwise. No such plugin exists in this repository yet; this
/// class is the seam it plugs into, and it has never run against a real one. The expected methods are <c>is_silent_switch_on() -> bool</c> (iOS) and
/// <c>has_audio_focus() -> bool</c> (Android); both are polled twice a second. The background pause does not need it: that is the engine's pause notification.
/// </summary>
public sealed class PlatformAudio
{
    private const string SingletonName = "GameAudioPlatform";
    private const double PollSeconds = 0.5;

    private readonly AppAudioState _state;
    private readonly GodotObject? _plugin;
    private double _next;

    /// <summary>Looks for the plugin.</summary>
    public PlatformAudio(AppAudioState state)
    {
        _state = state;
        if (Engine.HasSingleton(SingletonName)) _plugin = Engine.GetSingleton(SingletonName);
    }

    /// <summary>One line for the log: what is and is not handled.</summary>
    public string Describe() => _plugin is not null
        ? "platform plugin found (silent switch and audio focus polled; untested on a device)"
        : "no platform audio plugin: the iOS silent switch and Android audio focus are not handled (needs native code)";

    /// <summary>Reads the plugin, if any.</summary>
    public void Poll(double realNow)
    {
        if (_plugin is null || realNow < _next) return;
        _next = realNow + PollSeconds;
        if (_plugin.HasMethod("is_silent_switch_on")) _state.SetSilentSwitch(_plugin.Call("is_silent_switch_on").AsBool());
        if (_plugin.HasMethod("has_audio_focus")) _state.SetFocusLost(!_plugin.Call("has_audio_focus").AsBool(), realNow);
    }
}
