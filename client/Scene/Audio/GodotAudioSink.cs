using Game.Client.Core;
using Game.Client.Core.Audio;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene.Audio;

/// <summary>
/// The engine side of the audio director (ARCHITECTURE §7.8): a fixed set of <see cref="AudioStreamPlayer"/> nodes made at start (the voice pool, and two slots for
/// each of music and ambience), the four buses, and the loading of files. Everything loads on the main thread: in the headless display server the dummy renderer is
/// not thread-safe (§7.7, T-218), and sound files are small enough that a synchronous load of one at first use does not hitch a frame. Effects are charged to a
/// <see cref="SoundBudget"/> and the least recently used are let go over the limit; music and ambience are held only while they play.
/// </summary>
public sealed class GodotAudioSink : IAudioSink
{
    private const int SampleRate = 44100;
    private const int Channels = 2;

    private readonly AudioStreamPlayer[] _voices;
    private readonly AudioStreamPlayer[][] _slots;
    private readonly double[][] _slotBaseDb = { new double[2], new double[2] };
    private readonly Dictionary<string, AudioStream> _effects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioStream> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringName> _busNames = new(StringComparer.Ordinal);
    private readonly SoundBudget _budget;
    private readonly List<string> _evicted = new();
    private readonly Action<string> _warn;
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    /// <summary>Makes the players under <paramref name="parent"/> and checks the buses.</summary>
    public GodotAudioSink(Node parent, ClientConfig config, Action<string> warn)
    {
        _warn = warn;
        _budget = new SoundBudget((long)(config.AudioEffectsBudgetMb * 1024 * 1024));
        foreach (var bus in BusMixer.Buses)
        {
            EnsureBus(bus);
            _busNames[bus] = new StringName(bus);
        }
        var root = new Node { Name = "Audio" };
        parent.AddChild(root);
        _voices = new AudioStreamPlayer[config.AudioMaxVoices];
        for (var i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer { Name = "Voice" + i, Bus = _busNames[AudioBuses.Sfx] };
            root.AddChild(_voices[i]);
        }
        _slots = new[] { new AudioStreamPlayer[2], new AudioStreamPlayer[2] };
        for (var c = 0; c < 2; c++)
            for (var s = 0; s < 2; s++)
            {
                _slots[c][s] = new AudioStreamPlayer { Name = (c == 0 ? "Music" : "Ambient") + s, Bus = _busNames[c == 0 ? AudioBuses.Music : AudioBuses.Ambient] };
                root.AddChild(_slots[c][s]);
            }
    }

    /// <summary>The effects budget (for the measuring run).</summary>
    public SoundBudget Budget => _budget;

    /// <summary>Effect files loaded now.</summary>
    public int LoadedEffects => _effects.Count;

    /// <summary>Total players made (voices plus the four music and ambience slots).</summary>
    public int PlayerCount => _voices.Length + 4;

    /// <summary>Voices whose player is playing right now (what the engine says, not the pool's bookkeeping).</summary>
    public int PlayingVoices()
    {
        var count = 0;
        for (var i = 0; i < _voices.Length; i++)
            if (_voices[i].Playing) count++;
        return count;
    }

    /// <summary>Loads an effect ahead of its first use (the sound keys the bundle names). Charges the budget.</summary>
    public void Preload(ResolvedSound sound)
    {
        for (var i = 0; i < sound.Paths.Length; i++)
            if (!sound.Loop) LoadEffect(sound.Paths[i]);
    }

    /// <inheritdoc />
    public double DurationOf(ResolvedSound sound, int fileIndex)
    {
        var stream = sound.Loop ? LoadStream(sound.Paths[fileIndex], true) : LoadEffect(sound.Paths[fileIndex]);
        return stream is null ? 0 : stream.GetLength();
    }

    /// <inheritdoc />
    public void StartVoice(int voice, ResolvedSound sound, int fileIndex, double volumeDb, double pitch)
    {
        var stream = sound.Loop ? LoadStream(sound.Paths[fileIndex], true) : LoadEffect(sound.Paths[fileIndex]);
        if (stream is null) return;
        var player = _voices[voice];
        player.Stop();
        player.Stream = stream;
        player.Bus = _busNames[sound.Bus];
        player.VolumeDb = (float)volumeDb;
        player.PitchScale = (float)pitch;
        player.Play();
    }

    /// <inheritdoc />
    public void StopVoice(int voice) => _voices[voice].Stop();

    /// <inheritdoc />
    public void StartStream(int channel, int slot, ResolvedSound sound, int fileIndex)
    {
        var stream = LoadStream(sound.Paths[fileIndex], sound.Loop);
        if (stream is null) return;
        var player = _slots[channel][slot];
        player.Stop();
        player.Stream = stream;
        _slotBaseDb[channel][slot] = sound.VolumeDb;
        player.VolumeDb = (float)BusMixer.SilenceDb;
        player.Play();
    }

    /// <inheritdoc />
    public void StopStream(int channel, int slot)
    {
        var player = _slots[channel][slot];
        if (player.Stream is null) return;
        player.Stop();
        player.Stream = null;
        ReleaseUnusedStreams();
    }

    /// <inheritdoc />
    public void SetStreamGain(int channel, int slot, double gain) =>
        _slots[channel][slot].VolumeDb = (float)(BusMixer.ToDb(gain) + (gain <= 0 ? 0 : _slotBaseDb[channel][slot]));

    /// <inheritdoc />
    public void SetBusDb(string bus, double db)
    {
        var index = AudioServer.GetBusIndex(bus);
        if (index < 0) return;
        AudioServer.SetBusVolumeDb(index, (float)db);
        AudioServer.SetBusMute(index, db <= BusMixer.SilenceDb);
    }

    /// <inheritdoc />
    public void SetPaused(bool paused)
    {
        for (var i = 0; i < _voices.Length; i++) _voices[i].StreamPaused = paused;
        for (var c = 0; c < 2; c++)
            for (var s = 0; s < 2; s++) _slots[c][s].StreamPaused = paused;
    }

    private static void EnsureBus(string name)
    {
        if (AudioServer.GetBusIndex(name) >= 0) return;
        var index = AudioServer.BusCount;
        AudioServer.AddBus(index);
        AudioServer.SetBusName(index, name);
        AudioServer.SetBusSend(index, "Master");
    }

    private AudioStream? LoadEffect(string path)
    {
        if (_effects.TryGetValue(path, out var cached))
        {
            _budget.Touch(path);
            return cached;
        }
        var stream = Load(path);
        if (stream is null) return null;
        _effects[path] = stream;
        _budget.Add(path, SoundBudget.DecodedBytes(stream.GetLength(), SampleRate, Channels), _evicted);
        for (var i = 0; i < _evicted.Count; i++)
            if (!InUse(_effects[_evicted[i]])) _effects.Remove(_evicted[i]);
            else _budget.Touch(_evicted[i]); // playing right now: it stays, charged again on the next load round
        return stream;
    }

    private AudioStream? LoadStream(string path, bool loop)
    {
        if (!_streams.TryGetValue(path, out var stream))
        {
            stream = Load(path);
            if (stream is null) return null;
            _streams[path] = stream;
        }
        if (stream is AudioStreamOggVorbis ogg) ogg.Loop = loop;
        return stream;
    }

    private AudioStream? Load(string path)
    {
        var resource = ResourceLoader.Exists("res://" + path) ? ResourceLoader.Load<AudioStream>("res://" + path) : null;
        if (resource is null && _failed.Add(path)) _warn("[audio] cannot load " + path + " as an audio stream; that sound is skipped");
        return resource;
    }

    private bool InUse(AudioStream stream)
    {
        for (var i = 0; i < _voices.Length; i++)
            if (_voices[i].Stream == stream && _voices[i].Playing) return true;
        return false;
    }

    private void ReleaseUnusedStreams()
    {
        List<string>? gone = null;
        foreach (var pair in _streams)
        {
            var used = false;
            for (var c = 0; c < 2 && !used; c++)
                for (var s = 0; s < 2; s++)
                    if (_slots[c][s].Stream == pair.Value) used = true;
            if (!used) (gone ??= new List<string>()).Add(pair.Key);
        }
        if (gone is not null)
            foreach (var path in gone) _streams.Remove(path);
    }
}
