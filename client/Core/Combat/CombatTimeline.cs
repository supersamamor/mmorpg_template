using System;
using System.Collections.Generic;
using Game.Client.Core.World;
using Game.Shared.Protocol;

namespace Game.Client.Core.Combat;

/// <summary>
/// Turns a delta's <c>hits</c> into timed presentation (ARCHITECTURE §7.6). A hit that arrives at view time <c>t</c> swings the attacker at <c>t + render delay</c>
/// (so it lines up with the movement on screen and never shows before the attacker has reached range) and lands at the swing plus the attacker's impact delay
/// (theme data, with a safe default). At the landing the target flinches (not on a miss) and <see cref="Impact"/> fires once. Hits that are late by more than
/// <c>StaleHitSeconds</c> when the clock gets to them (a stall, a resume) are dropped, never replayed, and a snapshot forgets everything pending (ADR-002 row 101).
/// Pending hits sit in a list that stops growing once warm, so nothing allocates per hit.
/// </summary>
public sealed class CombatTimeline
{
    private const int MaxPending = 256;

    private readonly WorldView _world;
    private readonly ClientConfig _config;
    private readonly List<Pending> _pending = new List<Pending>(64);
    private long _lastTick = long.MinValue;

    internal CombatTimeline(WorldView world, ClientConfig config)
    {
        _world = world;
        _config = config;
    }

    /// <summary>Fires once per hit at the moment it lands on screen. The one timing source for numbers, effects and audio.</summary>
    public event ImpactHandler? Impact;

    /// <summary>Hits waiting for their moment.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Hits dropped because they were stale, a duplicate tick, or the queue was full (for measuring).</summary>
    public long Dropped { get; private set; }

    /// <summary>How long the attacker's impact takes after the swing starts, in seconds. Set by the theme layer; null means the config default for everyone.</summary>
    public Func<EntityView, double>? ImpactDelay { get; set; }

    /// <summary>Queues this delta's hits. <paramref name="tick"/> below 0 means unknown; a tick not newer than the last one is a duplicate and ignored.</summary>
    internal void Apply(IReadOnlyList<HitRecord> hits, double now, long tick)
    {
        if (tick >= 0)
        {
            if (tick <= _lastTick)
            {
                Dropped += hits.Count;
                return;
            }
            _lastTick = tick;
        }
        var swingAt = now + _world.RenderDelay;
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var attacker = _world.Find(hit.Attacker);
            var target = _world.Find(hit.Target);
            if (attacker is null && target is null) continue;
            if (_pending.Count >= MaxPending)
            {
                _pending.RemoveAt(0);
                Dropped++;
            }
            var kind = HitKinds.Of(hit.Result);
            var delay = attacker is not null && ImpactDelay is { } source ? source(attacker) : _config.ImpactDelaySeconds;
            delay = Math.Max(0, Math.Min(_config.MaxImpactDelaySeconds, double.IsNaN(delay) ? _config.ImpactDelaySeconds : delay));
            if (kind == HitKind.Status) delay = 0;   // no swing to wait for
            _pending.Add(new Pending
            {
                Attacker = hit.Attacker,
                Target = hit.Target,
                Amount = hit.Amount,
                Kind = kind,
                SwingAt = swingAt,
                ImpactAt = swingAt + delay,
                X = target?.X ?? attacker!.X,
                Z = target?.Z ?? attacker!.Z,
            });
        }
    }

    /// <summary>The latest moment a pending hit lands on <paramref name="target"/>, or <paramref name="fallback"/> if none is later (a death waits for its killing blow).</summary>
    internal double LatestImpactOn(long target, double fallback)
    {
        var latest = fallback;
        for (var i = 0; i < _pending.Count; i++)
            if (_pending[i].Target == target && _pending[i].ImpactAt > latest) latest = _pending[i].ImpactAt;
        return latest;
    }

    /// <summary>A snapshot replaces state, not history: nothing pending survives it.</summary>
    internal void Clear(long tick)
    {
        _pending.Clear();
        _lastTick = tick;
    }

    /// <summary>Plays what is due at view time <paramref name="now"/>. Costs one comparison when nothing is pending.</summary>
    internal void Advance(double now)
    {
        if (_pending.Count == 0) return;
        var stale = _config.StaleHitSeconds;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var pending = _pending[i];
            if (now < pending.SwingAt) continue;
            if (!pending.Swung)
            {
                pending.Swung = true;
                _pending[i] = pending;
                if (now - pending.SwingAt <= stale && pending.Kind != HitKind.Status) _world.Find(pending.Attacker)?.MarkAttack();
            }
            if (now < pending.ImpactAt) continue;

            _pending[i] = _pending[_pending.Count - 1];
            _pending.RemoveAt(_pending.Count - 1);
            if (now - pending.ImpactAt > stale)
            {
                Dropped++;
                continue;
            }
            Land(in pending, now);
        }
    }

    private void Land(in Pending pending, double now)
    {
        var target = _world.Find(pending.Target);
        double x = pending.X, z = pending.Z;
        if (target is not null)
        {
            x = target.X;
            z = target.Z;
            if (pending.Kind is HitKind.Hit or HitKind.Crit) target.MarkHit();
        }
        var side = pending.Attacker == _world.OwnId ? HitSide.Dealt : pending.Target == _world.OwnId ? HitSide.Taken : HitSide.Other;
        var impact = new HitImpact(pending.Attacker, pending.Target, pending.Amount, pending.Kind, side, x, z, now);
        Impact?.Invoke(in impact);
    }

    private struct Pending
    {
        public long Attacker;
        public long Target;
        public int Amount;
        public HitKind Kind;
        public double SwingAt;
        public double ImpactAt;
        public double X;
        public double Z;
        public bool Swung;
    }
}
