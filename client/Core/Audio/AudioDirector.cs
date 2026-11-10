using System;
using System.Globalization;
using Game.Shared.Audio;
using Game.Shared.Random;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Audio;

/// <summary>What the engine side does for the director: it owns the players and the buses and loads the files. Nothing here decides anything.</summary>
public interface IAudioSink
{
    /// <summary>The length in seconds of one file of a sound, loading it if needed; 0 or less when it cannot be loaded.</summary>
    double DurationOf(ResolvedSound sound, int fileIndex);

    /// <summary>Starts an effect on a voice, replacing what it played.</summary>
    void StartVoice(int voice, ResolvedSound sound, int fileIndex, double volumeDb, double pitch);

    /// <summary>Stops a voice.</summary>
    void StopVoice(int voice);

    /// <summary>Starts a looping stream in a channel's slot (silent until a gain is set).</summary>
    void StartStream(int channel, int slot, ResolvedSound sound, int fileIndex);

    /// <summary>Stops a channel's slot and lets go of its stream.</summary>
    void StopStream(int channel, int slot);

    /// <summary>Sets a slot's fade gain, 0–1 linear.</summary>
    void SetStreamGain(int channel, int slot, double gain);

    /// <summary>Sets an engine bus's volume in decibels.</summary>
    void SetBusDb(string bus, double db);

    /// <summary>Pauses or resumes all players.</summary>
    void SetPaused(bool paused);
}

/// <summary>Why a request did not play.</summary>
public sealed class AudioStats
{
    /// <summary>Effects started.</summary>
    public int Played { get; internal set; }

    /// <summary>Skipped by the per-key cooldown.</summary>
    public int Cooldown { get; internal set; }

    /// <summary>Skipped as too far from the camera.</summary>
    public int Distance { get; internal set; }

    /// <summary>Skipped because every voice held something at least as important.</summary>
    public int Full { get; internal set; }

    /// <summary>Voices cut off for a more important sound.</summary>
    public int Stolen { get; internal set; }

    /// <summary>Skipped because the bus was muted or at zero.</summary>
    public int Muted { get; internal set; }

    /// <summary>Skipped while the app was paused or the silent switch was on.</summary>
    public int Paused { get; internal set; }

    /// <summary>Requests that ended in silence (no sound found).</summary>
    public int Silent { get; internal set; }

    /// <summary>Files that could not be loaded.</summary>
    public int LoadFailed { get; internal set; }

    /// <summary>Music or ambience changes started.</summary>
    public int Crossfades { get; internal set; }
}

/// <summary>
/// The audio rules of ARCHITECTURE §7.8 in one pure class: it resolves a request through <see cref="AudioResolver"/> and the theme chain, picks a variation, volume
/// and pitch with an injected random source, asks the <see cref="VoicePool"/> for a voice, keeps music and ambience crossfading, and applies the player's bus
/// volumes, the pause and the silent switch. It tells an <see cref="IAudioSink"/> what to do, so tests run it without an engine and the scripted runs read its trace.
/// </summary>
public sealed class AudioDirector
{
    /// <summary>The music channel index.</summary>
    public const int MusicChannel = 0;

    /// <summary>The ambience channel index.</summary>
    public const int AmbientChannel = 1;

    /// <summary>Marks a source with no place in the world (interface, events, music): it is never skipped for distance.</summary>
    public const double Global = double.NaN;

    private readonly SoundCatalog _catalog;
    private readonly AudioResolver _resolver;
    private readonly IRandom _random;
    private readonly IAudioSink _sink;
    private readonly VoicePool _pool;
    private readonly CrossfadeChannel[] _channels;
    private readonly double[][] _lastGain = { new[] { -1.0, -1.0 }, new[] { -1.0, -1.0 } };
    private readonly double[] _lastBusDb = { double.NaN, double.NaN, double.NaN, double.NaN };
    private readonly System.Collections.Generic.HashSet<string> _silentTraced = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
    private readonly System.Collections.Generic.Dictionary<string, string[]> _weaponCategories = new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.Ordinal);
    private readonly double _cullSquared;
    private bool _sinkPaused;
    private double _now;

    /// <summary>Creates the director.</summary>
    public AudioDirector(SoundCatalog catalog, IRandom random, IAudioSink sink, ClientConfig config, Action<string>? trace = null)
    {
        _catalog = catalog;
        _resolver = new AudioResolver(catalog.Defaults, catalog.Events);
        _random = random;
        _sink = sink;
        Trace = trace;
        _pool = new VoicePool(config.AudioMaxVoices, config.AudioCooldownSeconds);
        _channels = new[] { new CrossfadeChannel(config.AudioCrossfadeSeconds), new CrossfadeChannel(config.AudioCrossfadeSeconds) };
        _cullSquared = config.AudioCullDistance * config.AudioCullDistance;
    }

    /// <summary>The player's volumes and mute.</summary>
    public BusMixer Mixer { get; } = new BusMixer();

    /// <summary>The phone's influence: background, focus, silent switch, and the audio clock.</summary>
    public AppAudioState App { get; } = new AppAudioState();

    /// <summary>Counters of what was played and skipped.</summary>
    public AudioStats Stats { get; } = new AudioStats();

    /// <summary>The debug trace: one line per play, skip, steal and crossfade. Null when off (nothing is formatted then).</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>The voice pool (read by tests and the measuring runs).</summary>
    public VoicePool Pool => _pool;

    /// <summary>The key a channel plays now (fading in or full), or null.</summary>
    public string? Playing(int channel) => _channels[channel].Current;

    /// <summary>The key a channel is fading out, or null.</summary>
    public string? FadingOut(int channel) => _channels[channel].Outgoing;

    // ---- requests ----

    /// <summary>Plays the sound of a category: the key content named (may be null), else the theme's default, else silence.</summary>
    /// <param name="contentKey">The key content names, or null.</param>
    /// <param name="category">An <see cref="AudioCategories"/> term.</param>
    /// <param name="priority">An <see cref="AudioPriority"/> value.</param>
    /// <param name="distanceSquared">Squared distance in world units from the camera focus, or <see cref="Global"/>.</param>
    public void PlayCategory(string? contentKey, string category, int priority, double distanceSquared = Global)
    {
        var key = _resolver.Resolve(contentKey, category);
        if (key is not null && _catalog.TryResolve(key, out var sound))
        {
            Play(sound, priority, distanceSquared);
            return;
        }
        // The content's key has no usable files: the theme default for the category is next (ARCHITECTURE §7.8).
        var fallback = contentKey is null ? null : _resolver.Resolve(null, category);
        if (fallback is not null && fallback != key && _catalog.TryResolve(fallback, out var fallbackSound))
        {
            if (Trace is not null) Trace("[audio] fallback key=" + key + " -> " + fallback);
            Play(fallbackSound, priority, distanceSquared);
            return;
        }
        Silent(key ?? category);
    }

    /// <summary>Plays the swing sound of a weapon type (theme default).</summary>
    public void PlayWeaponSwing(string weaponType, int priority, double distanceSquared) =>
        PlayCategory(null, WeaponCategory(weaponType, 0), priority, distanceSquared);

    /// <summary>Plays the impact sound of a weapon type (theme default).</summary>
    public void PlayWeaponImpact(string weaponType, int priority, double distanceSquared) =>
        PlayCategory(null, WeaponCategory(weaponType, 1), priority, distanceSquared);

    private string WeaponCategory(string weaponType, int which)
    {
        if (!_weaponCategories.TryGetValue(weaponType, out var names))
            _weaponCategories[weaponType] = names = new[] { AudioCategories.WeaponSwing(weaponType), AudioCategories.WeaponImpact(weaponType) }; // built once per weapon type
        return names[which];
    }

    /// <summary>Plays the sound of a game event (<see cref="EventNames"/>) from the theme's table.</summary>
    public void PlayEvent(string eventName, int priority = AudioPriority.Critical)
    {
        var key = _resolver.ForEvent(eventName);
        if (key is not null && _catalog.TryResolve(key, out var sound)) Play(sound, priority, Global);
        else Silent(key ?? eventName);
    }

    /// <summary>Plays an interface sound (<see cref="AudioCategories.UiTap"/> and the like).</summary>
    public void PlayUi(string category) => PlayCategory(null, category, AudioPriority.Critical);

    /// <summary>Plays one sound by key (no fallback; a missing key is silence with one warning).</summary>
    public void PlayKey(string key, int priority, double distanceSquared = Global)
    {
        if (_catalog.TryResolve(key, out var sound)) Play(sound, priority, distanceSquared);
        else Silent(key);
    }

    /// <summary>Sets the map's music and ambience (null for none); a change crossfades. Call on every map load, warp included.</summary>
    public void SetMap(string? musicKey, string? ambienceKey)
    {
        SetChannel(MusicChannel, musicKey);
        SetChannel(AmbientChannel, ambienceKey);
    }

    // ---- the frame ----

    /// <summary>Once a frame, with real time in seconds: moves the audio clock, the fades and the engine's bus volumes and pause.</summary>
    public void Tick(double realNow)
    {
        _now = App.Clock(realNow);
        if (_sinkPaused != App.Paused)
        {
            _sinkPaused = App.Paused;
            _sink.SetPaused(_sinkPaused);
            var paused = _sinkPaused;
            if (Trace is not null) Trace(paused ? "[audio] pause (background or focus lost)" : "[audio] resume");
        }
        for (var i = 0; i < BusMixer.Buses.Length; i++)
        {
            var bus = BusMixer.Buses[i];
            var db = App.SilencedBySwitch(bus) ? BusMixer.SilenceDb : Mixer.Db(bus);
            if (_lastBusDb[i] == db) continue;
            _lastBusDb[i] = db;
            _sink.SetBusDb(bus, db);
            if (Trace is not null) Trace("[audio] bus " + bus + " " + db.ToString("0.0", CultureInfo.InvariantCulture) + " dB");
        }
        for (var c = 0; c < _channels.Length; c++) Fade(c);
    }

    // ---- inside ----

    private void Play(ResolvedSound sound, int priority, double distanceSquared)
    {
        var key = sound.Key;
        if (App.Paused || App.SilencedBySwitch(sound.Bus))
        {
            Stats.Paused++;
            Skip(key, App.Paused ? "paused" : "silent_switch");
            return;
        }
        if (Mixer.Gain(sound.Bus) <= 0)
        {
            Stats.Muted++;
            Skip(key, "muted");
            return;
        }
        if (!double.IsNaN(distanceSquared) && distanceSquared > _cullSquared)
        {
            Stats.Distance++;
            Skip(key, "distance");
            return;
        }
        if (_pool.OnCooldown(key, _now))
        {
            Stats.Cooldown++;
            Skip(key, "cooldown");
            return;
        }

        var file = sound.Paths.Length > 1 ? _random.Next(sound.Paths.Length) : 0;
        var pitch = sound.PitchMax > sound.PitchMin ? sound.PitchMin + (sound.PitchMax - sound.PitchMin) * _random.NextDouble() : sound.PitchMin;
        var length = _sink.DurationOf(sound, file);
        if (length <= 0)
        {
            Stats.LoadFailed++;
            Skip(key, "load_failed");
            return;
        }
        var duration = sound.Loop ? double.PositiveInfinity : length / pitch;
        var outcome = _pool.Request(key, priority, duration, _now, out var voice, out var victim);
        if (outcome == VoiceOutcome.SkippedFull)
        {
            Stats.Full++;
            Skip(key, "full");
            return;
        }
        if (outcome == VoiceOutcome.Stole)
        {
            Stats.Stolen++;
            if (Trace is not null) Trace("[audio] steal voice=" + voice + " victim=" + victim + " for=" + key);
        }
        Stats.Played++;
        _sink.StartVoice(voice, sound, file, sound.VolumeDb, pitch);
        if (Trace is not null) Trace("[audio] play key=" + key + " bus=" + sound.Bus + " vol=" + sound.VolumeDb.ToString("0.0", CultureInfo.InvariantCulture) + "dB pitch=" +
                  pitch.ToString("0.000", CultureInfo.InvariantCulture) + " voice=" + voice + " prio=" + priority + " file=" + sound.Paths[file]);
    }

    private void SetChannel(int channel, string? key)
    {
        ResolvedSound? sound = null;
        if (key is not null && !_catalog.TryResolve(key, out sound!))
        {
            Silent(key);
            key = null;
            sound = null;
        }
        var state = _channels[channel];
        var previous = state.Current;
        if (!state.Set(key, _now)) return;
        Stats.Crossfades++;
        var name = channel == MusicChannel ? "music" : "ambient";
        var seconds = state.Seconds;
        var to = key;
        if (Trace is not null) Trace("[audio] crossfade channel=" + name + " from=" + (previous ?? "none") + " to=" + (to ?? "none") + " seconds=" + seconds.ToString("0.0", CultureInfo.InvariantCulture) +
                  (state.Cut ? " (cut the one before)" : string.Empty));
        _lastGain[channel][state.CurrentSlot] = -1;
        _sink.StopStream(channel, state.CurrentSlot); // this slot held the sound before the one now fading out, if any
        if (sound is not null)
        {
            var file = sound.Paths.Length > 1 ? _random.Next(sound.Paths.Length) : 0;
            _sink.StartStream(channel, state.CurrentSlot, sound, file);
        }
        Fade(channel);
    }

    private void Fade(int channel)
    {
        var state = _channels[channel];
        var hadOutgoing = state.Outgoing is not null;
        var outgoingSlot = state.OutgoingSlot;
        state.Evaluate(_now, out var incoming, out var outgoing);
        Send(channel, state.CurrentSlot, incoming);
        if (hadOutgoing)
        {
            if (state.Outgoing is null)
            {
                _sink.StopStream(channel, outgoingSlot);
                _lastGain[channel][outgoingSlot] = -1;
                var name = channel == MusicChannel ? "music" : "ambient";
                if (Trace is not null) Trace("[audio] crossfade done channel=" + name);
            }
            else
            {
                Send(channel, outgoingSlot, outgoing);
            }
        }
    }

    private void Send(int channel, int slot, double gain)
    {
        if (Math.Abs(_lastGain[channel][slot] - gain) < 0.0005) return;
        _lastGain[channel][slot] = gain;
        _sink.SetStreamGain(channel, slot, gain);
    }

    private void Skip(string key, string reason)
    {
        if (Trace is not null) Trace("[audio] skip key=" + key + " reason=" + reason);
    }

    private void Silent(string what)
    {
        Stats.Silent++;
        if (_silentTraced.Add(what) && Trace is not null) Trace("[audio] silence for " + what + " (no sound)");
    }
}
