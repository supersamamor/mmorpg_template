using Game.Server.Events;
using Game.Server.World.Effects;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Formulas;
using Game.Shared.Protocol;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Monsters;

/// <summary>What the AI currently thinks of one monster (for tests, admin and logs).</summary>
/// <param name="State">An <see cref="AiStates"/> term.</param>
/// <param name="Target">The entity it chases or attacks, or null.</param>
/// <param name="Home">Its spawn cell; wandering stays near it and giving up walks back to it.</param>
public readonly record struct MonsterAiView(string State, EntityId? Target, GridCell Home);

/// <summary>
/// The monster AI state machine (ARCHITECTURE §6.6): <c>idle</c> → <c>wander</c> near home, <c>chase</c> a target, <c>attack</c> it when in
/// range, <c>return</c> home when the target is gone or the monster is too far from home. Per-monster numbers come from
/// <see cref="MonsterDef"/> (<c>ai.aggressive</c>, <c>ai.aggro_range</c>, <c>ai.chase_range</c>, <c>attack_range</c>) and the monster's stat
/// sheet (<c>aspd</c>); shared timings from <c>balance.yaml</c> <c>ai</c>. Monsters are processed in entity id order.
/// </summary>
/// <remarks>
/// <para>The AI only decides. It walks by enqueueing <see cref="MoveTo"/> / <see cref="StopMove"/> for the movement system (they apply on
/// the next tick) and learns where moves went from the movement events; it announces attacks with <see cref="AttackRequested"/> for
/// combat (T-302) and target changes with <see cref="AggroGained"/> / <see cref="AggroLost"/>. It learns about new monsters from
/// <see cref="EntitySpawned"/> and about hits from <see cref="DamageDealt"/>. It never calls another system (hard rule 7).</para>
/// <para><b>Dormancy:</b> an <c>idle</c> or <c>wander</c> monster that no player could see (no player's AOI window of
/// <c>aoi.view_radius</c> buckets covers its bucket) does nothing: no scan, no path, no wander. Chasing, attacking and returning monsters
/// always run, so they finish what they started. Players come from <see cref="MapInstance.Players"/>, bucketed once per tick
/// (<see cref="PlayerGrid"/>), so the cost is O(players + awake monsters × nearby players), not O(entities).</para>
/// <para><b>Skills</b> (T-501): when an auto-attack is due, the monster first looks at its <c>ai.skills</c> entries in order and casts the first
/// one that is off cooldown, whose target is within the skill's <c>target.range</c> (a self skill always is; a ground skill aims at the target's
/// cell) and whose <c>chance</c> roll succeeds (a roll is drawn only for a chance strictly between 0 and 1); that cast takes the attack's place.
/// Casting: <see cref="SkillCastStarted"/> with the cast time (the skill's <c>cast_time_ms</c> at the entry's <c>level</c> × the monster's
/// <c>cast_time_mod</c>, floored), the monster stands and neither attacks nor walks, and when the time is up it raises
/// <see cref="SkillRequested"/> for combat to resolve; the cooldown (<c>cooldown_ms</c>) starts then. A cast of 0 ms requests at once. Losing the
/// target or a <c>prevents_skills</c> status drops the cast.</para>
/// <para><b>Statuses:</b> a monster held by <c>prevents_move</c> sends no moves (it keeps its state and retries later), one held by
/// <c>prevents_attack</c> requests no attacks, and one held by <c>prevents_skills</c> casts nothing.</para>
/// <para>Allocation after warm-up: only the intents and events it raises, and the per-monster state when a monster spawns.</para>
/// </remarks>
public sealed class MonsterAiSystem : ITickSystem, Reload.IContentReloadable
{
    private static readonly IReadOnlyCollection<Type> Owned = [];
    private static readonly int AspdStat = StatIndex.IndexOf(DerivedStats.Aspd);
    private static readonly int CastTimeModStat = StatIndex.IndexOf(DerivedStats.CastTimeMod);
    private static readonly int LevelSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.SkillLevel);

    private MonsterCatalog _catalog;
    private AiBalanceDef _ai;
    private readonly int _bucketSize;
    private readonly int _viewRadius;
    private readonly FormulaContext _formula = new(FormulaSymbols.Default);
    private readonly Dictionary<long, Brain> _byId = new();
    private readonly List<Brain> _brains = new();     // sorted by entity id
    private readonly List<Brain> _allies = new();     // the brains of players' summons
    private PlayerGrid? _players;
    private MapInstance? _instance;
    private long _tick = -1;
    private long _nowMs;

    /// <summary>Creates the AI with shared timings <paramref name="ai"/> and the AOI's bucket size and view radius (for dormancy).</summary>
    public MonsterAiSystem(MonsterCatalog catalog, AiBalanceDef ai, int bucketSize, int viewRadius)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        Positive(ai.ThinkIntervalMs, nameof(ai.ThinkIntervalMs));
        Positive(ai.WanderPauseMinMs, nameof(ai.WanderPauseMinMs));
        Positive(ai.WanderRadius, nameof(ai.WanderRadius));
        Positive(ai.RepathIntervalMs, nameof(ai.RepathIntervalMs));
        Positive(ai.ReturnTimeoutMs, nameof(ai.ReturnTimeoutMs));
        if (ai.WanderPauseMaxMs < ai.WanderPauseMinMs)
            throw new ArgumentOutOfRangeException(nameof(ai), "WanderPauseMaxMs must not be below WanderPauseMinMs.");
        Positive(bucketSize, nameof(bucketSize));
        Positive(viewRadius, nameof(viewRadius));
        _bucketSize = bucketSize;
        _viewRadius = viewRadius;
    }

    /// <summary>Creates the AI with <c>balance.yaml</c> <c>ai</c> and <c>aoi</c>.</summary>
    public static MonsterAiSystem Create(MonsterCatalog catalog, BalanceDef balance) =>
        new(catalog, balance.Ai, balance.Aoi.BucketSize, balance.Aoi.ViewRadius);

    /// <summary>T-801: new monsters and timings. Every monster was removed before this (its brain went with its <c>EntityLeftMap</c>); the new ones get brains as they spawn.</summary>
    public void Reload(Reload.ServerContent next)
    {
        var ai = next.Balance.Ai;
        Positive(ai.ThinkIntervalMs, nameof(ai.ThinkIntervalMs));
        Positive(ai.WanderPauseMinMs, nameof(ai.WanderPauseMinMs));
        Positive(ai.WanderRadius, nameof(ai.WanderRadius));
        Positive(ai.RepathIntervalMs, nameof(ai.RepathIntervalMs));
        Positive(ai.ReturnTimeoutMs, nameof(ai.ReturnTimeoutMs));
        if (ai.WanderPauseMaxMs < ai.WanderPauseMinMs) throw new ArgumentOutOfRangeException(nameof(next), "WanderPauseMaxMs must not be below WanderPauseMinMs.");
        _catalog = next.Monsters;
        _ai = ai;
    }

    /// <inheritdoc />
    public string Name => "monster_ai";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>Monsters the AI runs.</summary>
    public int Count => _brains.Count;

    /// <summary>Monsters that ran their state machine in the last tick.</summary>
    public int AwakeCount { get; private set; }

    /// <summary>Monsters skipped as dormant in the last tick.</summary>
    public int DormantCount { get; private set; }

    /// <summary>Skill casts started so far (monster <c>ai.skills</c>).</summary>
    public long Casts { get; private set; }

    /// <summary>Aggro scans done so far (each looks only at the player buckets the aggro range touches).</summary>
    public long Scans { get; private set; }

    /// <summary>The AI's view of a monster, or false when it does not run it.</summary>
    public bool TryGetView(EntityId id, out MonsterAiView view)
    {
        if (_byId.TryGetValue(id.Value, out var brain))
        {
            view = new MonsterAiView(brain.State, brain.Target, brain.Home);
            return true;
        }
        view = default;
        return false;
    }

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        var instance = context.Instance;
        Subscribe(instance);
        _tick = context.TickNumber;
        _nowMs = context.SimTimeMs;
        if (_players is null || !ReferenceEquals(_players.Grid, instance.Grid)) _players = new PlayerGrid(instance.Grid, _bucketSize, _viewRadius);
        _players.Rebuild(instance.Players);

        var awake = 0;
        var dormant = 0;
        foreach (var brain in _brains)
        {
            if (brain.Entity.Dead) continue;   // combat owns death; the spawner removes it
            if (Think(context, brain)) awake++;
            else dormant++;
        }
        AwakeCount = awake;
        DormantCount = dormant;
    }

    /// <summary>Runs one monster for one tick; false when it was dormant.</summary>
    private bool Think(TickContext context, Brain brain)
    {
        var provoker = brain.Provoker;
        brain.Provoker = null;

        if (brain.Entity.SummonedByPlayer && brain.Entity.Owner is { } owner)
        {
            Ally(context, brain, owner, provoker);
            return true;
        }

        if (brain.State == AiStates.Return)
        {
            Return(context, brain);
            return true;
        }

        if (brain.State is AiStates.Idle or AiStates.Wander)
        {
            if (provoker is { } source && Alive(context, source, out _))
            {
                Engage(context, brain, source, AggroReasons.Attacked);
            }
            else
            {
                if (!_players!.IsAwake(brain.Entity.Cell)) return false;
                if (!Scan(context, brain)) Stroll(context, brain);
                if (brain.State is AiStates.Idle or AiStates.Wander) return true;
            }
        }

        Pursue(context, brain);
        return true;
    }

    /// <summary>An aggressive monster's look around, every <c>think_interval_ms</c>. True when it found a target.</summary>
    private bool Scan(TickContext context, Brain brain)
    {
        var def = brain.Def.Ai;
        if (!def.Aggressive || def.AggroRange <= 0 || _nowMs < brain.NextThinkMs) return false;
        brain.NextThinkMs = _nowMs + _ai.ThinkIntervalMs;
        Scans++;
        var player = _players!.Nearest(brain.Entity.Cell, def.AggroRange);
        if (player is null) return false;
        Engage(context, brain, player.Id, AggroReasons.Sighted);
        return true;
    }

    /// <summary>Idle: wait a random pause, then walk to a random walkable cell within <c>wander_radius</c> of home. Wander: wait to arrive.</summary>
    private void Stroll(TickContext context, Brain brain)
    {
        if (brain.State == AiStates.Wander)
        {
            if (brain.Walking || brain.MovePending(_tick)) return;
            brain.State = AiStates.Idle;
            brain.NextWanderMs = _nowMs + Pause(context);
            return;
        }

        if (brain.NextWanderMs < 0) brain.NextWanderMs = _nowMs + Pause(context);
        if (_nowMs < brain.NextWanderMs) return;

        var r = _ai.WanderRadius;
        var cell = new GridCell(brain.Home.X + context.Random.Next(2 * r + 1) - r, brain.Home.Y + context.Random.Next(2 * r + 1) - r);
        if (!Same(cell, brain.Entity.Cell) && context.Instance.Grid.IsWalkable(cell.X, cell.Y))
        {
            Move(context, brain, cell);
            brain.State = AiStates.Wander;
        }
        else
        {
            brain.NextWanderMs = _nowMs + Pause(context);   // a blocked pick: try again after another pause
        }
    }

    /// <summary>Chase and attack: keep after the target until it is in range, then request attacks at the monster's aspd.</summary>
    private void Pursue(TickContext context, Brain brain)
    {
        var entity = brain.Entity;
        if (brain.Target is not { } targetId || !Alive(context, targetId, out var target))
        {
            GiveUp(context, brain, AggroReasons.TargetGone);
            return;
        }
        if (!entity.SummonedByPlayer && GridDistance.Chebyshev(entity.Cell, brain.Home) > brain.Def.Ai.ChaseRange)
        {
            GiveUp(context, brain, AggroReasons.Leash);
            return;
        }
        if (brain.Unreachable)
        {
            GiveUp(context, brain, AggroReasons.Unreachable);
            return;
        }
        if (brain.Casting is not null)
        {
            entity.Facing = GridDirection.Facing(entity.Cell, target.Cell) ?? entity.Facing;
            Cast(context, brain);
            return;
        }

        var range = Math.Max(0, brain.Def.AttackRange);
        if (GridDistance.Chebyshev(entity.Cell, target.Cell) <= range)
        {
            if (brain.State != AiStates.Attack)
            {
                brain.State = AiStates.Attack;
                if (brain.Walking || brain.MovePending(_tick)) Stop(context, brain);
            }
            entity.Facing = GridDirection.Facing(entity.Cell, target.Cell) ?? entity.Facing;
            Attack(context, brain, target, range);
            return;
        }

        brain.State = AiStates.Chase;
        if (_nowMs < brain.NextRepathMs) return;
        var walkingThere = (brain.Walking || brain.MovePending(_tick)) && Same(brain.Goal, target.Cell);
        if (walkingThere) return;
        Move(context, brain, target.Cell);
        brain.NextRepathMs = _nowMs + _ai.RepathIntervalMs;
    }

    /// <summary>
    /// One <see cref="AttackRequested"/> per 1000 / aspd ms while in range; the first as soon as the last one's cooldown is over. When one is due a
    /// ready skill may take its place (class remarks).
    /// </summary>
    private void Attack(TickContext context, Brain brain, Entity target, int range)
    {
        var aspd = brain.Entity.Stats?.Get(AspdStat) ?? 0;
        if (!(aspd > 0) || _nowMs < brain.NextAttackMs) return;
        // Late by less than a tick is the 100 ms tick rounding: count from the due time so the average rate is exactly aspd. Later than
        // that, it was not attacking (first attack, back in range): count from now, so no attacks are banked while away.
        var from = _nowMs - brain.NextAttackMs < TickRate.IntervalMs ? brain.NextAttackMs : _nowMs;
        brain.NextAttackMs = from + 1000.0 / aspd;
        if (TryStartCast(context, brain, target)) return;
        if (brain.Entity.CannotAttack) return;
        context.Publish(new AttackRequested(brain.Entity.Id, target.Id, brain.Entity.Cell, target.Cell, range, _nowMs));
    }

    /// <summary>The first <c>ai.skills</c> entry that is ready, in range and wins its chance roll starts casting. False when none does.</summary>
    private bool TryStartCast(TickContext context, Brain brain, Entity target)
    {
        var entries = brain.Def.Ai.Skills;
        if (entries.Count == 0 || brain.Entity.CannotUseSkills || brain.Entity.Stats is not { } stats) return false;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (_nowMs < brain.SkillReadyMs[i] || !_catalog.Effects.TryGetSkill(entry.SkillId, out var skill)) continue;
            var aim = skill.Def.Target;
            var self = !aim.Ground && aim.Range <= 0;
            if (!self && GridDistance.Chebyshev(brain.Entity.Cell, target.Cell) > aim.Range) continue;
            if (!(entry.Chance > 0) || (entry.Chance < 1 && context.Random.NextDouble() >= entry.Chance)) continue;

            var level = Math.Max(1, entry.Level);
            WriteCaster(stats, level);
            var castMs = (int)Math.Max(0, Math.Floor(skill.CastTimeMs.Evaluate(_formula) * stats.Get(CastTimeModStat) + 1e-9));
            var cast = brain.Casting = new CastState(i, skill, level, aim.Ground || self ? null : target.Id, aim.Ground ? target.Cell : null, _nowMs + castMs, castMs);
            Casts++;
            if (castMs > 0)
            {
                if (brain.Walking || brain.MovePending(_tick)) Stop(context, brain);
                context.Publish(new SkillCastStarted(brain.Entity.Id, skill.Id, level, cast.Target, cast.Cell, castMs));
            }
            Cast(context, brain);
            return true;
        }
        return false;
    }

    /// <summary>A cast in progress: dropped by a <c>prevents_skills</c> status, requested when its time is up (the cooldown starts then).</summary>
    private void Cast(TickContext context, Brain brain)
    {
        var cast = brain.Casting!;
        if (brain.Entity.CannotUseSkills)
        {
            EndCast(context, brain, CastResults.Interrupted);
            return;
        }
        if (_nowMs < cast.EndsAtMs) return;
        EndCast(context, brain, CastResults.Done);
        WriteCaster(brain.Entity.Stats!, cast.Level);
        var cooldown = Math.Max(0, cast.Skill.CooldownMs.Evaluate(_formula));
        brain.SkillReadyMs[cast.Entry] = _nowMs + (long)Math.Floor(cooldown + 1e-9);
        context.Publish(new SkillRequested(brain.Entity.Id, cast.Skill.Id, cast.Level, cast.Target, cast.Cell, _nowMs));
    }

    /// <summary>Clears the cast in progress and, when it had a cast time, tells observers how it ended (<c>SkillCastEnded</c>).</summary>
    private static void EndCast(TickContext context, Brain brain, string result)
    {
        if (brain.Casting is not { } cast) return;
        brain.Casting = null;
        if (cast.CastMs > 0) context.Publish(new SkillCastEnded(brain.Entity.Id, cast.Skill.Id, result));
    }

    // ---- a player's summon (T-502) ----------------------------------------------------------------------------------------------------
    //
    // The rule: a summon is on its owner's side. It never scans for players and never targets one; it attacks the nearest living monster that is not
    // a player's summon, within chase_range of itself and of its owner, or whoever just hit its owner or itself. With nothing to fight it follows its owner
    // to within balance ai.wander_radius cells. It has no home and no leash. It earns nobody EXP or loot (credit goes to players only).

    private void Ally(TickContext context, Brain brain, EntityId ownerId, EntityId? provoker)
    {
        var entity = brain.Entity;
        if (!Alive(context, ownerId, out var owner))
        {
            if (brain.Walking || brain.MovePending(_tick)) Stop(context, brain);
            return;
        }
        var range = brain.Def.Ai.ChaseRange;
        if (brain.Target is { } current)
        {
            if (brain.Unreachable || !Alive(context, current, out var enemy) || enemy.OnPlayerSide || GridDistance.Chebyshev(owner.Cell, enemy.Cell) > range)
            {
                context.Publish(new AggroLost(entity.Id, current, AggroReasons.TargetGone));
                EndCast(context, brain, CastResults.Interrupted);
                brain.Target = null;
                brain.Unreachable = false;
            }
        }
        if (brain.Target is null)
        {
            if (provoker is { } source && Alive(context, source, out var attacker) && !attacker.OnPlayerSide) Engage(context, brain, source, AggroReasons.Attacked);
            else if (_nowMs >= brain.NextThinkMs)
            {
                brain.NextThinkMs = _nowMs + _ai.ThinkIntervalMs;
                Scans++;
                if (NearestEnemy(entity, owner, range) is { } found) Engage(context, brain, found, AggroReasons.Sighted);
            }
        }
        if (brain.Target is not null)
        {
            Pursue(context, brain);
            return;
        }

        brain.State = AiStates.Idle;
        var follow = GridDistance.Chebyshev(entity.Cell, owner.Cell) > _ai.WanderRadius;
        if (!follow)
        {
            if (brain.Walking || brain.MovePending(_tick)) Stop(context, brain);
            return;
        }
        if (brain.Walking || brain.MovePending(_tick) || _nowMs < brain.NextRepathMs) return;
        Move(context, brain, owner.Cell);
        brain.NextRepathMs = _nowMs + _ai.RepathIntervalMs;
    }

    /// <summary>The nearest living monster of the wild side within <paramref name="range"/> of the summon and of its owner (ties: the lower id).</summary>
    private EntityId? NearestEnemy(Entity summon, Entity owner, int range)
    {
        EntityId? best = null;
        var bestDistance = int.MaxValue;
        foreach (var other in _brains)   // sorted by id, so the first of equal distances wins
        {
            var e = other.Entity;
            if (e.Dead || e.OnPlayerSide) continue;
            var distance = GridDistance.Chebyshev(summon.Cell, e.Cell);
            if (distance > range || distance >= bestDistance || GridDistance.Chebyshev(owner.Cell, e.Cell) > range) continue;
            best = e.Id;
            bestDistance = distance;
        }
        return best;
    }

    private void WriteCaster(StatSheet stats, int level)
    {
        _formula.Clear();
        stats.WriteTo(_formula);
        _formula[LevelSlot] = level;
    }

    /// <summary>Return: walk home ignoring players and hits; past <c>return_timeout_ms</c>, put it back on its home cell.</summary>
    private void Return(TickContext context, Brain brain)
    {
        var entity = brain.Entity;
        if (Same(entity.Cell, brain.Home))
        {
            brain.State = AiStates.Idle;
            brain.NextWanderMs = _nowMs + Pause(context);
            context.Publish(new MonsterReturned(entity.Id, entity.Cell));
            return;
        }
        if (_nowMs >= brain.ReturnDeadlineMs)
        {
            if (brain.Walking || brain.MovePending(_tick)) Stop(context, brain);
            context.Warp(entity.Id, context.Instance.MapId, brain.Home);
            brain.ReturnDeadlineMs = long.MaxValue;   // once; the next tick finds it home
            return;
        }
        if (brain.Walking || brain.MovePending(_tick) || _nowMs < brain.NextRepathMs) return;
        Move(context, brain, brain.Home);
        brain.NextRepathMs = _nowMs + _ai.RepathIntervalMs;
    }

    private void Engage(TickContext context, Brain brain, EntityId target, string reason)
    {
        brain.Target = target;
        brain.State = AiStates.Chase;
        brain.Unreachable = false;
        brain.NextRepathMs = _nowMs;
        context.Publish(new AggroGained(brain.Entity.Id, target, reason));
    }

    private void GiveUp(TickContext context, Brain brain, string reason)
    {
        if (brain.Target is { } target) context.Publish(new AggroLost(brain.Entity.Id, target, reason));
        brain.Target = null;
        brain.Unreachable = false;
        EndCast(context, brain, CastResults.Interrupted);
        brain.State = AiStates.Return;
        brain.ReturnDeadlineMs = _nowMs + _ai.ReturnTimeoutMs;
        if (Same(brain.Entity.Cell, brain.Home))
        {
            Return(context, brain);   // already home: straight to idle
            return;
        }
        Move(context, brain, brain.Home);   // also replaces a chase path it may still be walking
        brain.NextRepathMs = _nowMs + _ai.RepathIntervalMs;
    }

    private void Move(TickContext context, Brain brain, GridCell cell)
    {
        if (brain.Entity.CannotMove) return;   // held by a status: it tries again on its next re-path or wander
        brain.Goal = cell;
        brain.MoveIssuedTick = _tick;
        brain.MoveIssued = true;
        context.Instance.TryEnqueue(new MoveTo(brain.Entity.Id, cell));
    }

    private void Stop(TickContext context, Brain brain)
    {
        brain.Goal = null;
        brain.MoveIssuedTick = _tick;
        brain.MoveIssued = true;
        context.Instance.TryEnqueue(new StopMove(brain.Entity.Id));
    }

    private int Pause(TickContext context) =>
        _ai.WanderPauseMinMs + context.Random.Next(_ai.WanderPauseMaxMs - _ai.WanderPauseMinMs + 1);

    private static bool Alive(TickContext context, EntityId id, out Entity entity) =>
        context.Instance.TryGetEntity(id, out entity) && !entity.Dead;

    private void Subscribe(MapInstance instance)
    {
        if (ReferenceEquals(_instance, instance)) return;
        _instance = instance;
        var events = instance.Events;
        events.Subscribe<EntitySpawned>(OnSpawned);
        events.Subscribe<EntityLeftMap>(OnLeftMap);
        events.Subscribe<DamageDealt>(OnDamage);
        events.Subscribe<MovementStarted>(e => Walking(e.Entity, true));
        events.Subscribe<MovementStopped>(e => Walking(e.Entity, false));
        events.Subscribe<MoveRejected>(OnMoveRejected);
    }

    private void OnSpawned(EntitySpawned e)
    {
        if (e.Kind != EntityKind.Monster || _byId.ContainsKey(e.Entity.Value)) return;
        if (!_instance!.TryGetEntity(e.Entity, out var entity) || !_catalog.TryGet(e.DefinitionId, out var def)) return;
        var brain = new Brain(entity, def, e.Cell)
        {
            // Spread the aggressive monsters' scans over the think interval by id, without spending random numbers.
            NextThinkMs = _nowMs + e.Entity.Value % Math.Max(1, _ai.ThinkIntervalMs / TickRate.IntervalMs) * TickRate.IntervalMs,
        };
        _byId.Add(e.Entity.Value, brain);
        if (entity.SummonedByPlayer) _allies.Add(brain);
        var index = _brains.BinarySearch(brain, BrainOrder.Instance);
        _brains.Insert(index < 0 ? ~index : index, brain);
    }

    private void OnLeftMap(EntityLeftMap e)
    {
        // A same-map warp (the return timeout) leaves and re-enters: only forget a monster that is really gone from this instance.
        if (e.Kind != EntityKind.Monster || _instance!.TryGetEntity(e.Entity, out _) || !_byId.Remove(e.Entity.Value, out var brain)) return;
        _brains.RemoveAt(_brains.BinarySearch(brain, BrainOrder.Instance));
        _allies.Remove(brain);
    }

    private void OnDamage(DamageDealt e)
    {
        if (e.Source == e.Target) return;
        if (_byId.TryGetValue(e.Target.Value, out var brain)) brain.Provoker = e.Source;
        // Whoever hits a player is also set on by that player's summons.
        foreach (var ally in _allies)
            if (ally.Entity.Owner == e.Target && ally.Provoker is null && ally.Target is null) ally.Provoker = e.Source;
    }

    private void Walking(EntityId id, bool walking)
    {
        if (_byId.TryGetValue(id.Value, out var brain)) brain.Walking = walking;
    }

    private void OnMoveRejected(MoveRejected e)
    {
        if (!_byId.TryGetValue(e.Entity.Value, out var brain) || !Same(brain.Goal, e.Target)) return;
        brain.Goal = null;
        if (brain.State is AiStates.Chase) brain.Unreachable = true;
    }

    private static bool Same(GridCell a, GridCell b) => a.X == b.X && a.Y == b.Y;

    private static bool Same(GridCell? a, GridCell b) => a is { } cell && Same(cell, b);

    private static void Positive(int value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name, value, name + " must be above 0.");
    }

    private sealed class Brain(Entity entity, MonsterDef def, GridCell home)
    {
        public readonly Entity Entity = entity;
        public readonly MonsterDef Def = def;
        public readonly GridCell Home = home;
        public string State = AiStates.Idle;
        public EntityId? Target;
        public EntityId? Provoker;        // the last entity that damaged it since the last tick
        public GridCell? Goal;            // where the last MoveTo went, if it may still be walking there
        public bool Walking;              // from MovementStarted / MovementStopped
        public bool Unreachable;          // the chase's last MoveTo was refused
        public bool MoveIssued;             // false until the first move: there is no "tick of the last move" to compare with
        public long MoveIssuedTick;
        public long NextThinkMs;
        public long NextWanderMs = -1;    // < 0: not scheduled yet
        public long NextRepathMs;
        public double NextAttackMs;
        public long ReturnDeadlineMs;
        public readonly long[] SkillReadyMs = new long[def.Ai.Skills.Count];   // per ai.skills entry: when its cooldown is over
        public CastState? Casting;             // the skill being cast, if any

        /// <summary>
        /// A move intent sent this tick or the last one has not been answered yet: it is drained on the next tick and its movement event
        /// dispatched at the end of that tick, so it is known by the AI two ticks after it was sent.
        /// </summary>
        public bool MovePending(long tick) => MoveIssued && tick - MoveIssuedTick <= 1;
    }

    private sealed record CastState(int Entry, CompiledSkill Skill, int Level, EntityId? Target, GridCell? Cell, long EndsAtMs, int CastMs);

    private sealed class BrainOrder : IComparer<Brain>
    {
        public static readonly BrainOrder Instance = new();

        public int Compare(Brain? x, Brain? y) => x!.Entity.Id.Value.CompareTo(y!.Entity.Id.Value);
    }
}
