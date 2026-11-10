using System;
using System.Collections.Generic;

namespace Game.Client.Core.Audio;

/// <summary>How the pool answered a play request.</summary>
public enum VoiceOutcome
{
    /// <summary>A free voice was taken.</summary>
    Played,

    /// <summary>All voices were busy; the least important, oldest one was cut off.</summary>
    Stole,

    /// <summary>The same key played too recently.</summary>
    SkippedCooldown,

    /// <summary>All voices are busy with sounds at least as important.</summary>
    SkippedFull,
}

/// <summary>Importance of a sound when voices run out. A higher number is kept.</summary>
public static class AudioPriority
{
    /// <summary>Chatter that can be dropped first (other characters' hits).</summary>
    public const int Low = 0;

    /// <summary>Ordinary effects.</summary>
    public const int Normal = 1;

    /// <summary>Own actions and deaths.</summary>
    public const int High = 2;

    /// <summary>Interface sounds and events (level up): never cut for a combat sound.</summary>
    public const int Critical = 3;
}

/// <summary>
/// The bookkeeping of the voice pool (ARCHITECTURE §9: at most 24 voices): which voice plays what, the per-key cooldown, and who gets cut off when all are busy.
/// Pure and allocation-free after warm-up (the cooldown table grows to the number of distinct keys, once). The scene owns the players; this decides which index to use.
/// Time is the audio clock in seconds (it stops while the app is in the background, <see cref="AppAudioState"/>).
/// </summary>
public sealed class VoicePool
{
    private readonly string?[] _key;
    private readonly int[] _priority;
    private readonly double[] _start;
    private readonly double[] _end;
    private readonly Dictionary<string, double> _lastPlayed = new Dictionary<string, double>(StringComparer.Ordinal);
    private readonly double _cooldown;

    /// <summary>Creates a pool.</summary>
    /// <param name="capacity">Number of voices.</param>
    /// <param name="cooldownSeconds">Minimum time between two plays of the same key.</param>
    public VoicePool(int capacity, double cooldownSeconds)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _key = new string?[capacity];
        _priority = new int[capacity];
        _start = new double[capacity];
        _end = new double[capacity];
        _cooldown = cooldownSeconds;
    }

    /// <summary>Number of voices.</summary>
    public int Capacity => _key.Length;

    /// <summary>Voices busy at <paramref name="now"/>.</summary>
    public int Active(double now)
    {
        var count = 0;
        for (var i = 0; i < _key.Length; i++)
            if (_key[i] is not null && _end[i] > now) count++;
        return count;
    }

    /// <summary>True when <paramref name="key"/> played less than the cooldown ago.</summary>
    public bool OnCooldown(string key, double now) => _cooldown > 0 && _lastPlayed.TryGetValue(key, out var last) && now - last < _cooldown;

    /// <summary>The key a voice is playing at <paramref name="now"/>, or null.</summary>
    public string? KeyOf(int voice, double now) => _key[voice] is not null && _end[voice] > now ? _key[voice] : null;

    /// <summary>
    /// Asks for a voice. Cooldown first (a skipped sound does not touch any voice), then a free voice, then the cheapest one to cut: lowest priority, then oldest.
    /// A sound is never cut for a less important one.
    /// </summary>
    /// <param name="key">The sound key.</param>
    /// <param name="priority">An <see cref="AudioPriority"/> value.</param>
    /// <param name="duration">How long it plays, in seconds (infinity for a loop).</param>
    /// <param name="now">Audio clock.</param>
    /// <param name="voice">The voice to use, when played or stole.</param>
    /// <param name="victim">The key that was cut off, when stole.</param>
    public VoiceOutcome Request(string key, int priority, double duration, double now, out int voice, out string? victim)
    {
        voice = -1;
        victim = null;
        if (_cooldown > 0 && _lastPlayed.TryGetValue(key, out var last) && now - last < _cooldown) return VoiceOutcome.SkippedCooldown;

        var cheapest = -1;
        for (var i = 0; i < _key.Length; i++)
        {
            if (_key[i] is null || _end[i] <= now)
            {
                voice = i;
                Take(i, key, priority, duration, now);
                return VoiceOutcome.Played;
            }
            if (cheapest < 0 || _priority[i] < _priority[cheapest] || (_priority[i] == _priority[cheapest] && _start[i] < _start[cheapest])) cheapest = i;
        }
        if (_priority[cheapest] > priority) return VoiceOutcome.SkippedFull;
        victim = _key[cheapest];
        voice = cheapest;
        Take(cheapest, key, priority, duration, now);
        return VoiceOutcome.Stole;
    }

    /// <summary>Frees a voice before its time (a stopped loop).</summary>
    public void Release(int voice) => _key[voice] = null;

    private void Take(int voice, string key, int priority, double duration, double now)
    {
        _key[voice] = key;
        _priority[voice] = priority;
        _start[voice] = now;
        _end[voice] = now + duration;
        _lastPlayed[key] = now;
    }
}
