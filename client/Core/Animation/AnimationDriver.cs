using System;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Animation;

/// <summary>The clips every animated model is expected to have (VOCABULARY "Animation ids"; a theme maps each to a clip name in its own library).</summary>
public enum AnimClip
{
    /// <summary>Standing.</summary>
    Idle = 0,

    /// <summary>Walking.</summary>
    Walk = 1,

    /// <summary>An attack, once.</summary>
    Attack = 2,

    /// <summary>Being hit, once.</summary>
    Hit = 3,

    /// <summary>Dying, once; the last pose holds.</summary>
    Die = 4,
}

/// <summary>Facts about <see cref="AnimClip"/>.</summary>
public static class AnimClips
{
    /// <summary>Number of clips.</summary>
    public const int Count = 5;

    /// <summary>The animation id (VOCABULARY) of a clip.</summary>
    public static string IdOf(AnimClip clip)
    {
        switch (clip)
        {
            case AnimClip.Walk: return AnimationIds.Walk;
            case AnimClip.Attack: return AnimationIds.Attack;
            case AnimClip.Hit: return AnimationIds.Hit;
            case AnimClip.Die: return AnimationIds.Die;
            default: return AnimationIds.Idle;
        }
    }

    /// <summary>True for the clips that repeat until something else replaces them.</summary>
    public static bool Loops(AnimClip clip) => clip == AnimClip.Idle || clip == AnimClip.Walk;
}

/// <summary>
/// Decides which clip an entity plays from what the view model says: dead, walking, and counters that go up on every attack or hit it is part of.
/// Dying beats everything and holds; a hit interrupts an attack; an attack waits for a hit that is playing; walking and idling resume when a one-shot
/// ends. Pure: the Godot layer asks it, plays the clip, and calls <see cref="OnFinished"/> when a one-shot ends.
/// </summary>
public sealed class AnimationDriver
{
    private AnimClip _current;
    private bool _hasCurrent;
    private bool _oneShot;
    private int _seenAttacks;
    private int _seenHits;

    /// <summary>The clip being played, once one has started.</summary>
    public AnimClip Current => _current;

    /// <summary>True while a one-shot (attack, hit or die) plays.</summary>
    public bool OneShotActive => _oneShot;

    /// <summary>Forgets everything; the counters given are taken as already seen (a pooled visual meeting a new entity).</summary>
    public void Reset(int attackCount, int hitCount)
    {
        _hasCurrent = false;
        _oneShot = false;
        _seenAttacks = attackCount;
        _seenHits = hitCount;
    }

    /// <summary>One look at the entity. True when a new clip must start: it is in <paramref name="clip"/>.</summary>
    public bool Update(bool dead, bool walking, int attackCount, int hitCount, out AnimClip clip)
    {
        var attacked = attackCount != _seenAttacks;
        var hit = hitCount != _seenHits;
        _seenAttacks = attackCount;
        _seenHits = hitCount;

        if (dead)
        {
            if (_hasCurrent && _current == AnimClip.Die)
            {
                clip = AnimClip.Die;
                return false;
            }
            return Start(AnimClip.Die, true, out clip);
        }
        if (_hasCurrent && _current == AnimClip.Die) _oneShot = false; // it came back to life

        if (hit) return Start(AnimClip.Hit, true, out clip);
        if (attacked && !(_oneShot && _current == AnimClip.Hit)) return Start(AnimClip.Attack, true, out clip);
        if (!_oneShot)
        {
            var locomotion = walking ? AnimClip.Walk : AnimClip.Idle;
            if (!_hasCurrent || _current != locomotion) return Start(locomotion, false, out clip);
        }
        clip = _current;
        return false;
    }

    /// <summary>The engine says the clip that was playing ended. A one-shot hands back to walking or idling; dying holds.</summary>
    public void OnFinished()
    {
        if (!_hasCurrent || _current == AnimClip.Die) return;
        if (_oneShot)
        {
            _oneShot = false;
            _hasCurrent = false; // the next Update picks walking or idling
        }
    }

    private bool Start(AnimClip clip, bool oneShot, out AnimClip started)
    {
        _current = clip;
        _hasCurrent = true;
        _oneShot = oneShot;
        started = clip;
        return true;
    }
}
