using System;
using System.Collections.Generic;
using Game.Client.Core.Combat;
using Game.Shared.Protocol;

namespace Game.Client.Core.World;

/// <summary>
/// The client's picture of the world: it applies snapshots and deltas on the main thread, and Godot nodes only read it (CLAUDE.md rule 1:
/// nothing here decides anything). Time comes in as a parameter, in seconds, from a clock the caller owns.
/// </summary>
public sealed class WorldView
{
    private readonly ClientConfig _config;
    private readonly Dictionary<long, EntityView> _byId = new Dictionary<long, EntityView>();
    private readonly List<EntityView> _entities = new List<EntityView>();
    private readonly List<EntityView> _removed = new List<EntityView>();
    private readonly Stack<EntityView> _pool = new Stack<EntityView>();
    private readonly HashSet<long> _seen = new HashSet<long>();
    private readonly List<CastBar> _casts = new List<CastBar>();
    private double _renderDelay;
    private double _lastAdvance = double.NaN;

    /// <summary>Creates an empty view. The render delay is 0 until <c>welcome</c> gives the tick rate.</summary>
    public WorldView(ClientConfig config)
    {
        _config = config;
        Combat = new CombatTimeline(this, config);
    }

    /// <summary>Hits turned into timed presentation. Subscribe to <see cref="CombatTimeline.Impact"/> for the moment each one lands.</summary>
    public CombatTimeline Combat { get; }

    /// <summary>The current map id, or null before the first <c>welcome</c>.</summary>
    public string? MapId { get; private set; }

    /// <summary>The client's own entity id.</summary>
    public long OwnId { get; private set; }

    /// <summary>The last tick received.</summary>
    public long Tick { get; private set; }

    /// <summary>How far behind the newest data the view renders, in seconds.</summary>
    public double RenderDelay => _renderDelay;

    /// <summary>Own exact HP and SP, or null before the first ones arrive.</summary>
    public VitalsRecord? Vitals { get; private set; }

    /// <summary>Own levels and EXP, or null before the first ones arrive.</summary>
    public ExpMessage? Exp { get; private set; }

    /// <summary>Counts changes to <see cref="Vitals"/> and <see cref="Exp"/>, so a HUD redraws only on a change.</summary>
    public int StatsVersion { get; private set; }

    /// <summary>Every entity in view, in no particular order. Index it in per-frame code, so nothing allocates an enumerator.</summary>
    public IReadOnlyList<EntityView> Entities => _entities;

    /// <summary>The cast bars open now (T-502): any caster in view, the own character included, in the order they started.</summary>
    public IReadOnlyList<CastBar> Casts => _casts;

    /// <summary>Counts changes to <see cref="Casts"/> (a bar opened or closed).</summary>
    public int CastVersion { get; private set; }

    /// <summary>Entities that left the view since <see cref="ClearRemoved"/>; the presenter releases their visuals.</summary>
    public IReadOnlyList<EntityView> Removed => _removed;

    /// <summary>The client's own entity, or null if not in view yet.</summary>
    public EntityView? Own => _byId.TryGetValue(OwnId, out var own) ? own : null;

    /// <summary>One entity in view, or null.</summary>
    public EntityView? Find(long id) => _byId.TryGetValue(id, out var entity) ? entity : null;

    /// <summary><c>welcome</c>: own id, the map (§10 step 3: load it if it changed) and the render delay from <c>tick_rate</c>.</summary>
    public void ApplyWelcome(WelcomeMessage welcome)
    {
        OwnId = welcome.Id;
        MapId = welcome.MapId;
        _renderDelay = welcome.TickRate > 0 ? _config.RenderDelayTicks / welcome.TickRate : 0;
    }

    /// <summary><c>snapshot</c>: replaces the view. A new map id means the character changed maps.</summary>
    public void ApplySnapshot(SnapshotMessage snapshot, double now)
    {
        Tick = snapshot.Tick;
        OwnId = snapshot.Id;
        MapId = snapshot.MapId;
        Combat.Clear(snapshot.Tick); // a snapshot is state, not history (ADR-002 row 101): pending hits are not replayed after it
        _seen.Clear();
        for (var i = 0; i < snapshot.Entities.Count; i++)
        {
            var record = snapshot.Entities[i];
            _seen.Add(record.Id);
            AddOrReset(record, now);
        }
        for (var i = _entities.Count - 1; i >= 0; i--)
            if (!_seen.Contains(_entities[i].Id)) Remove(_entities[i].Id);
        if (_casts.Count > 0)
        {
            _casts.Clear();   // a snapshot is state, not history: casts under way are not told again
            CastVersion++;
        }
        if (snapshot.Vitals is { } vitals) SetVitals(vitals);
    }

    /// <summary><c>delta</c>: one tick's changes. Appeared, then changed, then disappeared, so an id in both lists ends up gone. A change to an unknown id is ignored.</summary>
    public void ApplyDelta(DeltaMessage delta, double now)
    {
        Tick = delta.Tick;
        for (var i = 0; i < delta.Appeared.Count; i++) AddOrReset(delta.Appeared[i], now);
        for (var i = 0; i < delta.Changed.Count; i++)
        {
            var change = delta.Changed[i];
            if (_byId.TryGetValue(change.Id, out var entity))
            {
                entity.Apply(change, now, _renderDelay, _config.SnapDistanceCells, _config.GlideCellsPerSecond);
                if ((change.Fields & ChangedFields.Dead) != 0 && entity.Dead) CloseCast(change.Id);
                if ((change.Fields & ChangedFields.Dead) != 0 && entity.Dead) entity.DelayDeath(Combat.LatestImpactOn(entity.Id, 0)); // the death waits for the killing blow
            }
        }
        for (var i = 0; i < delta.Disappeared.Count; i++) Remove(delta.Disappeared[i]);
        if (delta.Casts is { Count: > 0 } casts)
            for (var i = 0; i < casts.Count; i++) OpenCast(casts[i], now);
        if (delta.CastEnds is { Count: > 0 } ends)
            for (var i = 0; i < ends.Count; i++) CloseCast(ends[i].Caster);
        if (delta.Vitals is { } vitals) SetVitals(vitals);
    }

    /// <summary>
    /// A delta's <c>hits</c>: each swings its attacker when render time reaches this moment and lands after the attacker's impact delay (<see cref="Combat"/>).
    /// Visual only; the outcome was decided by the server. Entities that are not in view are ignored. <paramref name="tick"/> is the delta's tick; a tick that is
    /// not newer than the last is a duplicate and shows nothing. Below 0 means unknown.
    /// </summary>
    public void ApplyHits(IReadOnlyList<HitRecord> hits, double now, long tick = -1) => Combat.Apply(hits, now, tick);

    /// <summary><c>exp</c>: own levels and EXP.</summary>
    public void ApplyExp(ExpMessage exp)
    {
        Exp = exp;
        StatsVersion++;
    }

    /// <summary>Moves every drawn entity to the render time. Allocates nothing. True if anything moved.</summary>
    public bool Advance(double now)
    {
        var dt = double.IsNaN(_lastAdvance) ? 0 : Math.Max(0, now - _lastAdvance);
        _lastAdvance = now;
        var renderTime = now - _renderDelay;
        var turnRate = _config.TurnDegreesPerSecond * Math.PI / 180;
        var moved = false;
        for (var i = 0; i < _entities.Count; i++)
        {
            var entity = _entities[i];
            entity.Advance(now, renderTime, dt, turnRate);
            moved |= entity.PoseDirty;
        }
        Combat.Advance(now);
        return moved;
    }

    /// <summary>Hands the removed entities back to the pool once the presenter has released their visuals.</summary>
    public void ClearRemoved()
    {
        for (var i = 0; i < _removed.Count; i++)
        {
            _removed[i].Visual = null;
            _pool.Push(_removed[i]);
        }
        _removed.Clear();
    }

    /// <summary>Forgets everything (a new login).</summary>
    public void Reset()
    {
        for (var i = _entities.Count - 1; i >= 0; i--) Remove(_entities[i].Id);
        _casts.Clear();
        CastVersion++;
        MapId = null;
        OwnId = 0;
        Tick = 0;
        Vitals = null;
        Exp = null;
        StatsVersion++;
    }

    private void AddOrReset(EntityRecord record, double now)
    {
        if (!_byId.TryGetValue(record.Id, out var entity))
        {
            entity = _pool.Count > 0 ? _pool.Pop() : new EntityView();
            _byId[record.Id] = entity;
            _entities.Add(entity);
        }
        entity.Reset(record, now, _renderDelay);
    }

    private void Remove(long id)
    {
        if (!_byId.TryGetValue(id, out var entity)) return;
        CloseCast(id);
        _byId.Remove(id);
        var index = _entities.IndexOf(entity);
        _entities[index] = _entities[_entities.Count - 1];
        _entities.RemoveAt(_entities.Count - 1);
        _removed.Add(entity);
    }

    private void OpenCast(CastRecord record, double now)
    {
        CloseCast(record.Caster);
        _casts.Add(new CastBar(record.Caster, record.Skill, now, record.Ms / 1000.0));
        CastVersion++;
    }

    private void CloseCast(long caster)
    {
        for (var i = 0; i < _casts.Count; i++)
        {
            if (_casts[i].Caster != caster) continue;
            _casts.RemoveAt(i);
            CastVersion++;
            return;
        }
    }

    private void SetVitals(VitalsRecord vitals)
    {
        Vitals = vitals;
        StatsVersion++;
    }
}

/// <summary>A cast under way (T-502): who, which skill, when it began on the view clock and how long it takes.</summary>
public sealed class CastBar
{
    /// <summary>Creates a bar that began at <paramref name="start"/> and lasts <paramref name="seconds"/>.</summary>
    public CastBar(long caster, string skill, double start, double seconds)
    {
        Caster = caster;
        Skill = skill;
        Start = start;
        Seconds = seconds;
    }

    /// <summary>The caster's entity id.</summary>
    public long Caster { get; }

    /// <summary>The skill being cast.</summary>
    public string Skill { get; }

    /// <summary>When the cast began, on the view clock.</summary>
    public double Start { get; }

    /// <summary>The cast time in seconds (the server's, so it already includes the caster's cast time modifier).</summary>
    public double Seconds { get; }

    /// <summary>How far along it is at view time <paramref name="now"/>, 0 to 1.</summary>
    public double Fraction(double now) => Seconds <= 0 ? 1 : Math.Clamp((now - Start) / Seconds, 0, 1);
}
