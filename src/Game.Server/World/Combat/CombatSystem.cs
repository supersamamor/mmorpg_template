using Game.Server.Events;
using Game.Server.World.Monsters;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Progression;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Combat;

/// <summary>
/// The server's combat loop (ARCHITECTURE §6.7): resolves monster <see cref="AttackRequested"/> events and player auto-attacks through the
/// <see cref="CombatResolver"/>, keeps HP (<see cref="Entity.Vitals"/>), handles death and player respawn, splits EXP by damage share
/// through <see cref="ProgressionRules"/>, and rolls drop tables into ground items with loot priority and expiry. Owns
/// <see cref="AttackTarget"/>, <see cref="StopAttack"/> and <see cref="Respawn"/>. Runs after the monster AI.
/// </summary>
/// <remarks>
/// <para><b>Per tick</b>, in this order: the intents (in arrival order); vitals clamped to changed stat sheets; the monster attack requests
/// buffered from the last tick's events (in raise order; a ranged one also needs line of sight); player auto-attacks (in entity id order); ground item expiry. A killing hit
/// handles the death at once (<see cref="Entity.Dead"/>, <see cref="EntityDied"/>, EXP, drops), so later attacks in the same tick already
/// see the target dead. All randomness is the instance's <see cref="TickContext.Random"/>, through the resolver and the drop rolls.</para>
/// <para><b>Talks to other systems only through intents and events</b> (hard rule 7): walks players with <see cref="MoveTo"/> /
/// <see cref="StopMove"/> and follows the movement events; learns about entities from <see cref="EntityEnteredMap"/> /
/// <see cref="EntityLeftMap"/> and about monster attacks from <see cref="AttackRequested"/>. The spawner despawns dead monsters; movement
/// stops dead walkers (<c>MovementStopped</c> <c>died</c>).</para>
/// <para><b>Allocation</b> after warm-up: none per resolved hit (one reused <see cref="AttackContext"/>, pooled damage logs); only the events
/// it raises, the chase <see cref="MoveTo"/> intents, and on a kill the progression results and the ground item entities.</para>
/// </remarks>
public sealed class CombatSystem : ITickSystem
{
    private static readonly IReadOnlyCollection<Type> Owned = [typeof(AttackTarget), typeof(StopAttack), typeof(Respawn)];
    private static readonly int AspdStat = StatIndex.IndexOf(DerivedStats.Aspd);

    private readonly CombatContent _content;
    private readonly IPlayerProgressionStore _progressions;
    private readonly ProgressionRules _progression;
    private readonly Func<ProgressionState> _newCharacter;
    private readonly AttackContext _attack = new();
    private readonly FormulaContext _jobContext = new();
    private readonly Dictionary<long, Combatant> _combatants = new();
    private readonly List<Combatant> _ordered = new();               // sorted by entity id
    private readonly List<Chase> _chases = new();                     // sorted by entity id
    private readonly Dictionary<long, Chase> _chaseById = new();
    private readonly Stack<Chase> _chasePool = new();
    private readonly Stack<List<DamageShare>> _logPool = new();
    private readonly Dictionary<string, long> _refused = new(StringComparer.Ordinal);
    private readonly Queue<(EntityId Id, GroundItem Item)> _groundItems = new();
    private readonly List<RolledDrop> _rolled = new();
    private readonly List<Entity> _eligible = new();
    private long[] _damage = new long[8];
    private long[] _baseShares = new long[8];
    private long[] _jobShares = new long[8];
    private List<AttackRequested> _requests = new();
    private List<AttackRequested> _resolving = new();
    private CombatResolver? _resolver;
    private MapInstance? _instance;
    private bool[] _safe = [];
    private long _tick;
    private long _nowMs;

    /// <summary>Creates the combat system for one instance; <paramref name="progressions"/> is shared by every instance.</summary>
    public CombatSystem(CombatContent content, IPlayerProgressionStore progressions)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _progressions = progressions ?? throw new ArgumentNullException(nameof(progressions));
        _progression = new ProgressionRules(content.Balance, content.Progression);
        var startJob = content.Balance.NewCharacter.StartJobId;
        _newCharacter = () => _progression.NewCharacter(startJob);
        foreach (var reason in AttackReasons.All.Ordered) _refused[reason] = 0;
    }

    /// <inheritdoc />
    public string Name => "combat";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>Entities with vitals that combat tracks on this instance.</summary>
    public int CombatantCount => _combatants.Count;

    /// <summary>Players currently auto-attacking.</summary>
    public int AttackingCount => _chases.Count;

    /// <summary>Attacks resolved so far (hits and misses).</summary>
    public long Resolved { get; private set; }

    /// <summary>Ground items waiting to expire (some may already have been picked up).</summary>
    public int GroundItemCount => _groundItems.Count;

    /// <summary>Attacks and attack intents dropped so far for an <see cref="AttackReasons"/> term.</summary>
    public long RefusedCount(string reason) => _refused.TryGetValue(reason, out var n) ? n : 0;

    /// <summary>The player's auto-attack target, or null when it is not attacking.</summary>
    public EntityId? TargetOf(EntityId player) => _chaseById.TryGetValue(player.Value, out var chase) ? chase.Target : null;

    /// <summary>The players who damaged a monster since its last full-HP moment and how much HP each took, in first-hit order.</summary>
    public IReadOnlyList<(EntityId Attacker, long Damage)> DamageLog(EntityId monster)
    {
        if (!_combatants.TryGetValue(monster.Value, out var c) || c.Damage is null) return [];
        return c.Damage.Select(d => (d.Attacker, d.Damage)).ToList();
    }

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        var instance = context.Instance;
        if (!ReferenceEquals(_instance, instance)) Setup(context);
        _tick = context.TickNumber;
        _nowMs = context.SimTimeMs;

        HandleIntents(context);
        for (var i = 0; i < _ordered.Count; i++)
        {
            var entity = _ordered[i].Entity;
            if (entity.Stats is { } stats) entity.Vitals?.Sync(stats);
        }
        ResolveRequests(context);
        RunChases(context);
        ExpireItems(context);
    }

    // ---- setup and bookkeeping -----------------------------------------------------------------------------------------------------

    private void Setup(TickContext context)
    {
        var instance = context.Instance;
        _instance = instance;
        _resolver = new CombatResolver(_content.Rules, context.Random);
        var grid = instance.Grid;
        _safe = new bool[grid.Width * grid.Height];
        foreach (var zone in grid.Zones)
        {
            if (zone.Kind != ZoneKinds.Safe) continue;
            for (var y = Math.Max(0, zone.Area.Y); y < Math.Min(grid.Height, zone.Area.Y + zone.Area.H); y++)
            for (var x = Math.Max(0, zone.Area.X); x < Math.Min(grid.Width, zone.Area.X + zone.Area.W); x++)
                _safe[y * grid.Width + x] = true;
        }

        var events = instance.Events;
        events.Subscribe<EntityEnteredMap>(OnEntered);
        events.Subscribe<EntityLeftMap>(OnLeft);
        events.Subscribe<AttackRequested>(e => _requests.Add(e));
        events.Subscribe<MovementStarted>(OnMovementStarted);
        events.Subscribe<MovementStopped>(OnMovementStopped);
        events.Subscribe<MoveRejected>(OnMoveRejected);

        // Entities that were there before combat first ran (tests, a system added late) join now.
        foreach (var entity in instance.Entities) Track(entity);
    }

    /// <summary>Melee (range 1 or less) needs no line of sight; a ranged attack needs it from cell to cell (ARCHITECTURE §4).</summary>
    private bool InSight(GridCell from, GridCell to, int range) => range <= 1 || LineOfSight.HasLineOfSight(_instance!.Grid, from, to);

    private bool InSafeZone(GridCell cell)
    {
        var grid = _instance!.Grid;
        return grid.InBounds(cell.X, cell.Y) && _safe[cell.Y * grid.Width + cell.X];
    }

    private void OnEntered(EntityEnteredMap e)
    {
        if (_instance!.TryGetEntity(e.Entity, out var entity)) Track(entity);
    }

    /// <summary>Starts tracking a player or monster with stats: vitals full if it has none yet (they travel with it across warps).</summary>
    private void Track(Entity entity)
    {
        if (entity.Kind is not (EntityKind.Player or EntityKind.Monster) || entity.Stats is null) return;
        if (_combatants.ContainsKey(entity.Id.Value)) return;
        entity.Vitals ??= new Vitals(entity.Stats);
        MonsterDef? def = null;
        if (entity.Kind == EntityKind.Monster && entity.DefinitionId is { } id) _content.Monsters.TryGet(id, out def);
        var combatant = new Combatant(entity, def);
        _combatants.Add(entity.Id.Value, combatant);
        var index = _ordered.BinarySearch(combatant, CombatantOrder.Instance);
        _ordered.Insert(index < 0 ? ~index : index, combatant);
        if (entity.Kind == EntityKind.Player) _progressions.GetOrCreate(entity.Id, _newCharacter);
    }

    private void OnLeft(EntityLeftMap e)
    {
        // A same-map warp leaves and re-enters at once: only forget an entity that is really gone from this instance.
        if (_instance!.TryGetEntity(e.Entity, out _)) return;
        if (_combatants.Remove(e.Entity.Value, out var combatant))
        {
            _ordered.RemoveAt(_ordered.BinarySearch(combatant, CombatantOrder.Instance));
            ReleaseLog(combatant);
        }
        if (_chaseById.TryGetValue(e.Entity.Value, out var chase)) Drop(chase);
        if (e.Kind == EntityKind.Player && _instance.World.LocationOf(e.Entity) is null) _progressions.Release(e.Entity);
    }

    // ---- intents ---------------------------------------------------------------------------------------------------------------------

    private void HandleIntents(TickContext context)
    {
        var intents = context.Intents;
        for (var i = 0; i < intents.Count; i++)
        {
            if (!_instance!.TryGetEntity(intents[i].Entity, out var entity)) continue;   // the router already vetted this
            switch (intents[i])
            {
                case AttackTarget attack: Start(context, entity, attack.Target); break;
                case StopAttack:
                    if (_chaseById.TryGetValue(entity.Id.Value, out var chase)) End(context, chase, AttackReasons.Cancelled);
                    break;
                case Respawn: Revive(context, entity); break;
            }
        }
    }

    private void Start(TickContext context, Entity player, EntityId targetId)
    {
        var reason = CanStart(player, targetId);
        if (reason is not null)
        {
            Refuse(context, player.Id, targetId, reason);
            return;
        }
        if (!_chaseById.TryGetValue(player.Id.Value, out var chase))
        {
            chase = _chasePool.Count > 0 ? _chasePool.Pop() : new Chase();
            chase.Reset(player);
            _chaseById.Add(player.Id.Value, chase);
            var index = _chases.BinarySearch(chase, ChaseOrder.Instance);
            _chases.Insert(index < 0 ? ~index : index, chase);
        }
        chase.Target = targetId;
        chase.EndReason = null;
        chase.NextRepathMs = _nowMs;
    }

    private string? CanStart(Entity player, EntityId targetId)
    {
        if (player.Kind != EntityKind.Player || !_combatants.ContainsKey(player.Id.Value)) return AttackReasons.InvalidTarget;
        if (targetId == player.Id) return AttackReasons.InvalidTarget;
        if (!_instance!.TryGetEntity(targetId, out var target)) return AttackReasons.TargetGone;
        if (target.Kind != EntityKind.Monster || !_combatants.ContainsKey(targetId.Value)) return AttackReasons.InvalidTarget;   // no PvP in v1
        if (target.Dead) return AttackReasons.TargetDead;
        if (InSafeZone(player.Cell)) return AttackReasons.SafeZone;
        return null;
    }

    /// <summary>A dead player comes back at the respawn point with <c>death.respawn_hp_percent</c> of its max HP.</summary>
    private void Revive(TickContext context, Entity player)
    {
        if (player.Kind != EntityKind.Player || !player.Dead || player.Vitals is not { } vitals || player.Stats is not { } stats) return;
        vitals.Sync(stats);
        var hp = (int)Math.Max(1, (long)vitals.MaxHp * _content.Balance.Death.RespawnHpPercent / 100);
        vitals.Hp = Math.Min(hp, Math.Max(1, vitals.MaxHp));
        player.Dead = false;
        var (mapId, cell) = _content.RespawnPoint;
        context.Warp(player.Id, mapId, cell);
        context.Publish(new PlayerRespawned(player.Id, mapId, cell, vitals.Hp));
    }

    // ---- monster attack requests ----------------------------------------------------------------------------------------------------

    private void ResolveRequests(TickContext context)
    {
        (_requests, _resolving) = (_resolving, _requests);
        for (var i = 0; i < _resolving.Count; i++)
        {
            var request = _resolving[i];
            var reason = Check(request, out var attacker, out var target);
            if (reason is null) Strike(context, attacker!, target!);
            else Refuse(context, request.Attacker, request.Target, reason);
        }
        _resolving.Clear();
    }

    /// <summary>
    /// Re-checks a monster's request on the tick it is resolved: both alive here, the target hittable, within the request's range, and for a
    /// ranged attack with line of sight.
    /// </summary>
    private string? Check(AttackRequested request, out Combatant? attacker, out Combatant? target)
    {
        target = null;
        if (!_combatants.TryGetValue(request.Attacker.Value, out attacker) || attacker.Entity.Dead) return AttackReasons.AttackerDead;
        if (!_combatants.TryGetValue(request.Target.Value, out target)) return AttackReasons.TargetGone;
        if (target.Entity.Dead) return AttackReasons.TargetDead;
        if (target.Entity == attacker.Entity) return AttackReasons.InvalidTarget;
        if (target.Entity.Kind == EntityKind.Player && InSafeZone(target.Entity.Cell)) return AttackReasons.SafeZone;
        if (GridDistance.Chebyshev(attacker.Entity.Cell, target.Entity.Cell) > request.Range) return AttackReasons.OutOfRange;
        if (!InSight(attacker.Entity.Cell, target.Entity.Cell, request.Range)) return AttackReasons.NoLineOfSight;
        return null;
    }

    // ---- player auto-attack -----------------------------------------------------------------------------------------------------------

    private void RunChases(TickContext context)
    {
        // End() removes from the list, so walk a snapshot of the count backwards-safe: index only advances when the chase survives.
        for (var i = 0; i < _chases.Count;)
        {
            var chase = _chases[i];
            if (Pursue(context, chase)) i++;
        }
    }

    /// <summary>One player's auto-attack for one tick. False when it ended (and was removed from the list).</summary>
    private bool Pursue(TickContext context, Chase chase)
    {
        var player = chase.Player;
        if (player.Dead) return End(context, chase, AttackReasons.AttackerDead);
        if (chase.EndReason is { } ended) return End(context, chase, ended);
        if (!_combatants.TryGetValue(chase.Target.Value, out var target)) return End(context, chase, AttackReasons.TargetGone);
        if (target.Entity.Dead) return End(context, chase, AttackReasons.TargetDead);
        if (!_combatants.TryGetValue(player.Id.Value, out var self)) return End(context, chase, AttackReasons.AttackerDead);

        var range = _content.Balance.Combat.PlayerAttackRange;
        // Out of range or, for a ranged attack, out of sight: walk closer (toward the target's cell) the same way.
        if (GridDistance.Chebyshev(player.Cell, target.Entity.Cell) <= range && InSight(player.Cell, target.Entity.Cell, range))
        {
            if (chase.Goal is not null && (chase.Walking || chase.MovePending(_tick))) Stop(context, chase);
            player.Facing = GridDirection.Facing(player.Cell, target.Entity.Cell) ?? player.Facing;
            // In a safe zone it neither hits nor can be hit; it keeps the target and swings once it steps out.
            if (!InSafeZone(player.Cell) && Ready(self)) Strike(context, self, target);
            return true;
        }

        if (_nowMs < chase.NextRepathMs) return true;
        var walkingThere = chase.Goal is { } goal && Same(goal, target.Entity.Cell) && (chase.Walking || chase.MovePending(_tick));
        if (walkingThere) return true;
        Move(context, chase, target.Entity.Cell);
        chase.NextRepathMs = _nowMs + _content.Balance.Combat.PlayerRepathIntervalMs;
        return true;
    }

    /// <summary>True when the attacker's next auto-attack is due at its aspd (attacks per second); schedules the one after it.</summary>
    private bool Ready(Combatant attacker)
    {
        var aspd = attacker.Entity.Stats?.Get(AspdStat) ?? 0;
        if (!(aspd > 0) || _nowMs < attacker.NextAttackMs) return false;
        // As in the monster AI: late by less than a tick is tick rounding, so count from the due time and the average rate is exactly aspd;
        // later than that it was not attacking, so count from now and nothing is banked.
        var from = _nowMs - attacker.NextAttackMs < TickRate.IntervalMs ? attacker.NextAttackMs : _nowMs;
        attacker.NextAttackMs = from + 1000.0 / aspd;
        return true;
    }

    private void Move(TickContext context, Chase chase, GridCell cell)
    {
        chase.PreviousGoal = chase.Goal;
        chase.Goal = cell;
        chase.MoveIssuedTick = _tick;
        context.Instance.TryEnqueue(new MoveTo(chase.Player.Id, cell));
    }

    private void Stop(TickContext context, Chase chase)
    {
        chase.PreviousGoal = chase.Goal;
        chase.Goal = null;
        chase.MoveIssuedTick = _tick;
        chase.StopIssuedTick = _tick;
        context.Instance.TryEnqueue(new StopMove(chase.Player.Id));
    }

    /// <summary>Ends an auto-attack with <see cref="AttackStopped"/>; always returns false (for <see cref="Pursue"/>).</summary>
    private bool End(TickContext context, Chase chase, string reason)
    {
        context.Publish(new AttackStopped(chase.Player.Id, chase.Target, reason));
        Drop(chase);
        return false;
    }

    private void Drop(Chase chase)
    {
        _chaseById.Remove(chase.Player.Id.Value);
        _chases.RemoveAt(_chases.BinarySearch(chase, ChaseOrder.Instance));
        chase.Reset(null);
        _chasePool.Push(chase);
    }

    // The movement events tell combat whether a player's walk is its own chase or the player's new order (which ends the attack). A move
    // combat sent is known two ticks later (drained next tick, its event dispatched at the end of that tick), so the last two goals count.

    private void OnMovementStarted(MovementStarted e)
    {
        if (!_chaseById.TryGetValue(e.Entity.Value, out var chase)) return;
        if (Same(chase.Goal, e.Goal) || (chase.MovePending(_tick) && Same(chase.PreviousGoal, e.Goal))) chase.Walking = true;
        else chase.EndReason ??= AttackReasons.Cancelled;   // the player walked somewhere else
    }

    private void OnMovementStopped(MovementStopped e)
    {
        if (!_chaseById.TryGetValue(e.Entity.Value, out var chase)) return;
        chase.Walking = false;
        if (e.Reason == MovementReasons.Cancelled && _tick - chase.StopIssuedTick > 1) chase.EndReason ??= AttackReasons.Cancelled;
    }

    private void OnMoveRejected(MoveRejected e)
    {
        if (_chaseById.TryGetValue(e.Entity.Value, out var chase) && Same(chase.Goal, e.Target)) chase.EndReason ??= AttackReasons.Unreachable;
    }

    // ---- hits and death ---------------------------------------------------------------------------------------------------------------

    /// <summary>Resolves one auto-attack through the resolver and applies it. Allocates nothing but the event.</summary>
    private void Strike(TickContext context, Combatant attacker, Combatant target)
    {
        var a = _attack;
        a.Attacker = attacker.Entity.Stats!;
        a.Defender = target.Entity.Stats!;
        a.WeaponAtk = 0;   // weapons arrive with T-401
        a.Refine = 0;
        a.Effect = null;
        a.SkillLevel = 0;
        a.AttackerElement = ElementOf(attacker.Def);
        a.DefenderElement = ElementOf(target.Def);
        a.DefenderRace = target.Def is { Race.Length: > 0 } race ? race.Race : null;
        a.DefenderSize = target.Def is { Size.Length: > 0 } size ? size.Size : null;
        var result = _resolver!.Resolve(a);
        Resolved++;

        var vitals = target.Entity.Vitals!;
        var before = vitals.Hp;
        vitals.Hp = (int)Math.Clamp((long)before - result.Damage, 0, Math.Max(before, vitals.MaxHp));
        if (target.Entity.Kind == EntityKind.Monster)
        {
            if (vitals.Hp >= vitals.MaxHp) ReleaseLog(target);                                    // back to full: the damage race restarts
            else if (attacker.Entity.Kind == EntityKind.Player && before > vitals.Hp) Credit(target, attacker.Entity.Id, before - vitals.Hp);
        }
        var outcome = !result.Hit ? HitResults.Miss : result.Crit ? HitResults.Crit : HitResults.Hit;
        context.Publish(new DamageDealt(attacker.Entity.Id, target.Entity.Id, result.Damage, outcome, vitals.Hp));
        if (vitals.Hp == 0) Die(context, target, attacker.Entity.Id);
    }

    private static string? ElementOf(MonsterDef? def) => def is { Element.Length: > 0 } ? def.Element : null;

    private void Credit(Combatant monster, EntityId attacker, long damage)
    {
        var log = monster.Damage ??= _logPool.Count > 0 ? _logPool.Pop() : new List<DamageShare>();
        for (var i = 0; i < log.Count; i++)
        {
            if (log[i].Attacker != attacker) continue;
            log[i] = new DamageShare(attacker, log[i].Damage + damage);
            return;
        }
        log.Add(new DamageShare(attacker, damage));
    }

    private void ReleaseLog(Combatant combatant)
    {
        if (combatant.Damage is not { } log) return;
        log.Clear();
        _logPool.Push(log);
        combatant.Damage = null;
    }

    private void Die(TickContext context, Combatant dead, EntityId killer)
    {
        var entity = dead.Entity;
        entity.Dead = true;   // movement stops it (MovementStopped "died"); the AI and the router ignore it from now on
        context.Publish(new EntityDied(entity.Id, killer, entity.Kind, entity.Kind == EntityKind.Player ? null : entity.DefinitionId, entity.Cell));
        if (entity.Kind == EntityKind.Player)
        {
            Penalize(context, entity);
            return;
        }
        if (dead.Def is { } def) Reward(context, dead, def);
        ReleaseLog(dead);
    }

    /// <summary>The death penalty: <c>death.exp_penalty_percent</c> of the current level's requirement, never more than the EXP it has.</summary>
    private void Penalize(TickContext context, Entity player)
    {
        var percent = _content.Balance.Death.ExpPenaltyPercent;
        if (percent <= 0 || !_progressions.TryGet(player.Id, out var state)) return;
        var table = _content.BaseExpTable.Exp;
        if (state.BaseLevel < 1 || state.BaseLevel > table.Count) return;   // at the max level EXP is always 0
        var loss = Math.Min(state.BaseExp, table[state.BaseLevel - 1] * percent / 100);
        if (loss <= 0) return;
        // ProgressionRules has no "lose EXP" operation yet; this keeps its invariant (0 <= BaseExp < requirement, level unchanged).
        state.BaseExp -= loss;
        context.Publish(new ExpLost(player.Id, loss));
    }

    // ---- EXP and drops ----------------------------------------------------------------------------------------------------------------

    /// <summary>Splits the monster's EXP between the players who damaged it and are still on the map, then rolls its drops.</summary>
    private void Reward(TickContext context, Combatant monster, MonsterDef def)
    {
        _eligible.Clear();
        var log = monster.Damage;
        var count = 0;
        if (log is not null)
        {
            Grow(log.Count);
            for (var i = 0; i < log.Count; i++)
            {
                if (!_instance!.TryGetEntity(log[i].Attacker, out var player) || player.Kind != EntityKind.Player) continue;
                _eligible.Add(player);
                _damage[count++] = log[i].Damage;
            }
        }

        EntityId? owner = null;
        if (count > 0)
        {
            var damage = _damage.AsSpan(0, count);
            ExpSplit.Split(def.BaseExp, damage, _baseShares.AsSpan(0, count));
            ExpSplit.Split(def.JobExp, damage, _jobShares.AsSpan(0, count));
            var top = 0;
            for (var i = 0; i < count; i++)
            {
                if (damage[i] > damage[top]) top = i;   // ties: the first hitter
                Grant(context, _eligible[i], monster.Entity, def, damage[i], _baseShares[i], _jobShares[i]);
            }
            owner = _eligible[top].Id;
        }
        _eligible.Clear();
        DropLoot(context, monster.Entity, def, owner);
    }

    private void Grow(int count)
    {
        if (_damage.Length >= count) return;
        var size = Math.Max(count, _damage.Length * 2);
        _damage = new long[size];
        _baseShares = new long[size];
        _jobShares = new long[size];
    }

    private void Grant(TickContext context, Entity player, Entity monster, MonsterDef def, long damage, long baseExp, long jobExp)
    {
        var state = _progressions.GetOrCreate(player.Id, _newCharacter);
        var jobLevel = state.JobLevel;
        var baseResult = _progression.GainBaseExp(state, baseExp);
        var jobResult = _progression.GainJobExp(state, jobExp);
        context.Publish(new ExpGained(player.Id, monster.Id, def.Id, damage, baseExp, jobExp, state.BaseLevel, state.JobLevel));
        foreach (var change in baseResult.Events) context.Publish(new PlayerProgressed(player.Id, change));
        foreach (var change in jobResult.Events) context.Publish(new PlayerProgressed(player.Id, change));
        if (baseResult.LevelsGained > 0 || jobResult.LevelsGained > 0) Restat(player, state, jobLevel != state.JobLevel);
    }

    /// <summary>
    /// Brings the player's sheet to its new levels (ProgressionRules remarks): base and job level, and the job's stat bonuses re-added with the
    /// new job level. Its vitals then clamp to the new max (a higher max leaves current HP as it is).
    /// </summary>
    private void Restat(Entity player, ProgressionState state, bool jobLevelChanged)
    {
        if (player.Stats is not { } sheet) return;
        sheet.Base.BaseLevel = state.BaseLevel;
        sheet.Base.JobLevel = state.JobLevel;
        if (jobLevelChanged)
        {
            var source = "job:" + state.JobId;
            sheet.Modifiers.RemoveBySource(source);
            _jobContext[FormulaVariables.BaseLevel] = state.BaseLevel;
            _jobContext[FormulaVariables.JobLevel] = state.JobLevel;
            foreach (var bonus in _content.JobBonuses(state.JobId)) sheet.Modifiers.Add(source, bonus, _jobContext);
        }
        player.Vitals?.Sync(sheet);
    }

    /// <summary>Rolls every drop table of the monster and puts each drop on a walkable cell near the death cell.</summary>
    private void DropLoot(TickContext context, Entity monster, MonsterDef def, EntityId? owner)
    {
        _rolled.Clear();
        var tables = def.DropTableIds;
        for (var i = 0; i < tables.Count; i++)
            if (_content.TryGetDropTable(tables[i], out var table)) DropRoller.Roll(table, context.Random, _rolled);
        if (_rolled.Count == 0) return;

        var loot = _content.Balance.Loot;
        var grid = context.Instance.Grid;
        for (var i = 0; i < _rolled.Count; i++)
        {
            var drop = _rolled[i];
            if (!DropRoller.TryPlace(grid, monster.Cell, loot.SpreadRadius, context.Random, out var cell))
            {
                context.Log.Warn($"[{grid.MapId}] no walkable cell near {monster.Cell} for a drop of {drop.ItemId}; it is lost.");
                continue;
            }
            var item = new GroundItem(drop.ItemId, drop.Quantity, monster.Id, owner, _nowMs, _nowMs + loot.PriorityMs, _nowMs + loot.LifetimeMs);
            var id = context.Spawn(EntityKind.Item, cell, Directions.S, e =>
            {
                e.DefinitionId = item.ItemId;
                e.GroundItem = item;
            });
            _groundItems.Enqueue((id, item));
            context.Publish(new ItemDropped(id, item.ItemId, item.Quantity, cell, monster.Id, owner, item.OwnerUntilMs, item.ExpiresAtMs));
        }
        _rolled.Clear();
    }

    /// <summary>Despawns ground items whose lifetime is over. All share one lifetime, so the queue is in expiry order.</summary>
    private void ExpireItems(TickContext context)
    {
        while (_groundItems.Count > 0 && _groundItems.Peek().Item.ExpiresAtMs <= _nowMs)
        {
            var (id, item) = _groundItems.Dequeue();
            // Picked up (T-401) or otherwise removed meanwhile: nothing to do.
            if (!_instance!.TryGetEntity(id, out var entity) || !ReferenceEquals(entity.GroundItem, item)) continue;
            context.Despawn(id);
            context.Publish(new ItemExpired(id, item.ItemId, item.Quantity, entity.Cell));
        }
    }

    private void Refuse(TickContext context, EntityId attacker, EntityId target, string reason)
    {
        _refused[reason]++;
        context.Publish(new AttackRefused(attacker, target, reason));
    }

    private static bool Same(GridCell a, GridCell b) => a.X == b.X && a.Y == b.Y;

    private static bool Same(GridCell? a, GridCell b) => a is { } cell && Same(cell, b);

    private readonly record struct DamageShare(EntityId Attacker, long Damage);

    private sealed class Combatant(Entity entity, MonsterDef? def)
    {
        public readonly Entity Entity = entity;
        public readonly MonsterDef? Def = def;   // null for players
        public double NextAttackMs;              // players' auto-attack timer (monsters are paced by the AI)
        public List<DamageShare>? Damage;        // monsters: HP taken per player since the last full-HP moment, in first-hit order
    }

    private sealed class Chase
    {
        private const long Never = -1_000_000;   // a tick long past (not long.MinValue: "tick - Never" must not overflow)

        public Entity Player = null!;
        public EntityId Target;
        public string? EndReason;          // set by the movement handlers: the player moved, or no path leads to the target
        public GridCell? Goal;             // where combat's last MoveTo went (null after a StopMove)
        public GridCell? PreviousGoal;
        public bool Walking;
        public long MoveIssuedTick = Never;
        public long StopIssuedTick = Never;
        public long NextRepathMs;

        public bool MovePending(long tick) => tick - MoveIssuedTick <= 1;

        public void Reset(Entity? player)
        {
            Player = player!;
            Target = default;
            EndReason = null;
            Goal = null;
            PreviousGoal = null;
            Walking = false;
            MoveIssuedTick = Never;
            StopIssuedTick = Never;
            NextRepathMs = 0;
        }
    }

    private sealed class CombatantOrder : IComparer<Combatant>
    {
        public static readonly CombatantOrder Instance = new();

        public int Compare(Combatant? x, Combatant? y) => x!.Entity.Id.Value.CompareTo(y!.Entity.Id.Value);
    }

    private sealed class ChaseOrder : IComparer<Chase>
    {
        public static readonly ChaseOrder Instance = new();

        public int Compare(Chase? x, Chase? y) => x!.Player.Id.Value.CompareTo(y!.Player.Id.Value);
    }
}
