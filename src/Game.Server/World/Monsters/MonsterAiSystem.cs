using Game.Server.Events;
using Game.Shared.Definitions;
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
/// <para>Allocation after warm-up: only the intents and events it raises, and the per-monster state when a monster spawns.</para>
/// </remarks>
public sealed class MonsterAiSystem : ITickSystem
{
    private static readonly IReadOnlyCollection<Type> Owned = [];
    private static readonly int AspdStat = StatIndex.IndexOf(DerivedStats.Aspd);

    private readonly MonsterCatalog _catalog;
    private readonly AiBalanceDef _ai;
    private readonly int _bucketSize;
    private readonly int _viewRadius;
    private readonly Dictionary<long, Brain> _byId = new();
    private readonly List<Brain> _brains = new();     // sorted by entity id
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
        if (GridDistance.Chebyshev(entity.Cell, brain.Home) > brain.Def.Ai.ChaseRange)
        {
            GiveUp(context, brain, AggroReasons.Leash);
            return;
        }
        if (brain.Unreachable)
        {
            GiveUp(context, brain, AggroReasons.Unreachable);
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

    /// <summary>One <see cref="AttackRequested"/> per 1000 / aspd ms while in range; the first as soon as the last one's cooldown is over.</summary>
    private void Attack(TickContext context, Brain brain, Entity target, int range)
    {
        var aspd = brain.Entity.Stats?.Get(AspdStat) ?? 0;
        if (!(aspd > 0) || _nowMs < brain.NextAttackMs) return;
        // Late by less than a tick is the 100 ms tick rounding: count from the due time so the average rate is exactly aspd. Later than
        // that, it was not attacking (first attack, back in range): count from now, so no attacks are banked while away.
        var from = _nowMs - brain.NextAttackMs < TickRate.IntervalMs ? brain.NextAttackMs : _nowMs;
        brain.NextAttackMs = from + 1000.0 / aspd;
        context.Publish(new AttackRequested(brain.Entity.Id, target.Id, brain.Entity.Cell, target.Cell, range, _nowMs));
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
        var index = _brains.BinarySearch(brain, BrainOrder.Instance);
        _brains.Insert(index < 0 ? ~index : index, brain);
    }

    private void OnLeftMap(EntityLeftMap e)
    {
        // A same-map warp (the return timeout) leaves and re-enters: only forget a monster that is really gone from this instance.
        if (e.Kind != EntityKind.Monster || _instance!.TryGetEntity(e.Entity, out _) || !_byId.Remove(e.Entity.Value, out var brain)) return;
        _brains.RemoveAt(_brains.BinarySearch(brain, BrainOrder.Instance));
    }

    private void OnDamage(DamageDealt e)
    {
        if (_byId.TryGetValue(e.Target.Value, out var brain) && e.Source != e.Target) brain.Provoker = e.Source;
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

        /// <summary>
        /// A move intent sent this tick or the last one has not been answered yet: it is drained on the next tick and its movement event
        /// dispatched at the end of that tick, so it is known by the AI two ticks after it was sent.
        /// </summary>
        public bool MovePending(long tick) => MoveIssued && tick - MoveIssuedTick <= 1;
    }

    private sealed class BrainOrder : IComparer<Brain>
    {
        public static readonly BrainOrder Instance = new();

        public int Compare(Brain? x, Brain? y) => x!.Entity.Id.Value.CompareTo(y!.Entity.Id.Value);
    }
}
