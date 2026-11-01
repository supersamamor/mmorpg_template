using Game.Shared.Definitions;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>Asks the server to walk an entity to a cell (tap-to-move). The movement system validates it on the tick.</summary>
public sealed record MoveTo(EntityId Entity, GridCell Cell) : IIntent;

/// <summary>Asks the server to stop an entity where it is (the last cell it fully reached).</summary>
public sealed record StopMove(EntityId Entity) : IIntent;

/// <summary>
/// The reason terms of the movement events (VOCABULARY.md "Movement reasons"). Rejection terms go in
/// <c>MoveRejected</c>, stop terms in <c>MovementStopped</c>.
/// </summary>
public static class MovementReasons
{
    /// <summary>Rejected: the target is outside the map.</summary>
    public const string OutOfBounds = "out_of_bounds";

    /// <summary>Rejected: the target cell is not walkable.</summary>
    public const string NotWalkable = "not_walkable";

    /// <summary>Rejected: the entity stands on a cell that is not walkable, so no search can start.</summary>
    public const string StartNotWalkable = "start_not_walkable";

    /// <summary>Rejected: the target cannot be reached.</summary>
    public const string NoPath = "no_path";

    /// <summary>Rejected: a route exists but is longer than <c>balance.yaml</c> <c>movement.max_path_length</c>.</summary>
    public const string PathTooLong = "path_too_long";

    /// <summary>Rejected: the search hit <c>movement.max_nodes_expanded</c> before finding the target.</summary>
    public const string SearchLimit = "search_limit";

    /// <summary>Rejected, or stopped: the entity has no stat sheet or its <c>move_speed</c> is not above 0.</summary>
    public const string NoSpeed = "no_speed";

    /// <summary>Stopped: the last cell of the path was reached.</summary>
    public const string Arrived = "arrived";

    /// <summary>Stopped: a <see cref="StopMove"/>, or a <see cref="MoveTo"/> to the cell the entity stands on.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Stopped: the entity reached a warp cell and a warp was requested (a <c>WarpRejected</c> may follow; it then stays).</summary>
    public const string Warped = "warped";

    /// <summary>Stopped: the entity became dead.</summary>
    public const string Died = "died";
}

/// <summary>
/// Walks entities (ARCHITECTURE §4). Owns <see cref="MoveTo"/> and <see cref="StopMove"/>. On the tick it first handles the
/// intents (the last one per entity wins), then advances every moving entity by <see cref="TickContext.DeltaMs"/> at its
/// <c>move_speed</c> with a <see cref="GridMover"/>, in entity id order. One system object per instance, so the
/// non-thread-safe <see cref="Pathfinder"/> and the per-entity state need no locks.
/// </summary>
/// <remarks>
/// Speed comes from <see cref="Entity.Stats"/> and is read when a move starts and after every tick in which a cell was
/// reached, so a buff applies from the next cell on. Entities do not block each other. A rejected <see cref="MoveTo"/> leaves
/// any move in progress running. Allocation after warm-up: only the events (records); path lists and move state are pooled.
/// </remarks>
public sealed class MovementSystem : ITickSystem
{
    private static readonly int MoveSpeedStat = StatIndex.IndexOf(DerivedStats.MoveSpeed);
    private static readonly IReadOnlyCollection<Type> Owned = [typeof(MoveTo), typeof(StopMove)];

    private readonly PathLimits _limits;
    private readonly Pathfinder _pathfinder = new();
    private readonly Dictionary<EntityId, MoveState> _states = new();
    private readonly List<MoveState> _active = new();          // sorted by entity id
    private readonly Stack<MoveState> _pool = new();
    private readonly Dictionary<EntityId, int> _lastIntent = new();
    private readonly Dictionary<int, GridWarp> _warps = new();
    private MapGrid? _warpsOf;

    /// <summary>Creates a movement system with the given search limits (positive numbers).</summary>
    public MovementSystem(PathLimits limits)
    {
        if (limits.MaxPathLength < 1) throw new ArgumentOutOfRangeException(nameof(limits), "MaxPathLength must be at least 1.");
        if (limits.MaxNodesExpanded < 1) throw new ArgumentOutOfRangeException(nameof(limits), "MaxNodesExpanded must be at least 1.");
        _limits = limits;
    }

    /// <summary>Creates a movement system with the limits of <c>balance.yaml</c> <c>movement</c>.</summary>
    public static MovementSystem Create(BalanceDef balance) =>
        new(new PathLimits(balance.Movement.MaxPathLength, balance.Movement.MaxNodesExpanded));

    /// <inheritdoc />
    public string Name => "movement";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>Entities currently walking.</summary>
    public int MovingCount => _states.Count;

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        var grid = context.Instance.Grid;
        if (!ReferenceEquals(grid, _warpsOf)) IndexWarps(grid);

        HandleIntents(context, grid);
        Advance(context);
    }

    private void IndexWarps(MapGrid grid)
    {
        _warps.Clear();
        foreach (var warp in grid.Warps) _warps[warp.Cell.Y * grid.Width + warp.Cell.X] = warp;
        _warpsOf = grid;
    }

    private void HandleIntents(TickContext context, MapGrid grid)
    {
        var intents = context.Intents;
        if (intents.Count == 0) return;

        _lastIntent.Clear();
        for (var i = 0; i < intents.Count; i++) _lastIntent[intents[i].Entity] = i;

        for (var i = 0; i < intents.Count; i++)
        {
            var intent = intents[i];
            if (_lastIntent[intent.Entity] != i) continue;                                  // a later intent replaces this one
            if (!context.Instance.TryGetEntity(intent.Entity, out var entity)) continue;     // the router already vetted this
            if (intent is MoveTo move) StartMove(context, grid, entity, move.Cell);
            else if (_states.TryGetValue(entity.Id, out var state)) Stop(context, state, MovementReasons.Cancelled);
        }
    }

    private void StartMove(TickContext context, MapGrid grid, Entity entity, GridCell goal)
    {
        _states.TryGetValue(entity.Id, out var state);

        if (goal.X == entity.Cell.X && goal.Y == entity.Cell.Y)
        {
            if (state != null) Stop(context, state, MovementReasons.Cancelled);   // idle: nothing to do
            return;
        }

        var speed = SpeedOf(entity);
        if (!(speed > 0))
        {
            Reject(context, entity, goal, MovementReasons.NoSpeed);
            return;
        }

        var fresh = state == null;
        state ??= Rent();
        var status = _pathfinder.FindPath(grid, entity.Cell, goal, _limits, state.Spare);
        if (status != PathStatus.Found)
        {
            if (fresh) Return(state);
            Reject(context, entity, goal, ReasonFor(status));
            return;
        }

        // The mover keeps a reference to its path list, so the new path goes into the spare list and the lists swap.
        (state.Path, state.Spare) = (state.Spare, state.Path);
        state.Consumed = 0;
        state.Speed = speed;
        if (fresh)
        {
            state.Entity = entity;
            state.Mover = new GridMover(entity.Cell, entity.Facing);
            _states.Add(entity.Id, state);
            InsertSorted(state);
        }
        state.Mover.SetPath(state.Path);
        state.Spare.Clear();   // only now: SetPath still reads the old path to keep the step in progress
        entity.Facing = state.Mover.Facing;
        context.Publish(new MovementStarted(entity.Id, entity.Cell, goal, state.Path.Count));
    }

    private void Advance(TickContext context)
    {
        var kept = 0;
        for (var i = 0; i < _active.Count; i++)
        {
            var state = _active[i];
            if (!state.Done) Step(context, state);

            if (state.Done) Return(state);
            else _active[kept++] = state;
        }
        _active.RemoveRange(kept, _active.Count - kept);
    }

    private void Step(TickContext context, MoveState state)
    {
        var entity = state.Entity!;
        if (entity.Dead)
        {
            Stop(context, state, MovementReasons.Died);
            return;
        }
        if (!context.Instance.TryGetEntity(entity.Id, out var current) || !ReferenceEquals(current, entity))
        {
            Discard(state);   // despawned or warped away by someone else: nobody to tell
            return;
        }

        var reached = state.Mover.Advance(state.Speed, context.DeltaMs);
        for (var k = 0; k < reached; k++)
        {
            var from = entity.Cell;
            var cell = state.Path[state.Consumed + k];
            var facing = GridDirection.Facing(from, cell) ?? entity.Facing;
            entity.Cell = cell;
            entity.Facing = facing;
            context.Publish(new CellReached(entity.Id, from, cell, facing));

            // Only players take warps; anything else walks over a warp cell like any other walkable cell.
            if (entity.Kind == EntityKind.Player && _warps.TryGetValue(cell.Y * context.Instance.Grid.Width + cell.X, out var warp))
            {
                Stop(context, state, MovementReasons.Warped);
                context.Warp(entity.Id, warp.TargetMapId, warp.TargetCell);
                return;
            }
        }
        state.Consumed += reached;
        entity.Facing = state.Mover.Facing;

        if (state.Mover.Arrived)
        {
            Stop(context, state, MovementReasons.Arrived);
        }
        else if (reached > 0)
        {
            state.Speed = SpeedOf(entity);
            if (!(state.Speed > 0)) Stop(context, state, MovementReasons.NoSpeed);
        }
    }

    private static double SpeedOf(Entity entity) => entity.Stats?.Get(MoveSpeedStat) ?? 0;

    private void Stop(TickContext context, MoveState state, string reason)
    {
        var entity = state.Entity!;
        context.Publish(new MovementStopped(entity.Id, entity.Cell, reason));
        Discard(state);
    }

    /// <summary>Forgets the state; <see cref="Advance"/> returns it to the pool when it compacts the active list.</summary>
    private void Discard(MoveState state)
    {
        state.Mover.Stop();
        state.Done = true;
        _states.Remove(state.Entity!.Id);
    }

    private static void Reject(TickContext context, Entity entity, GridCell target, string reason) =>
        context.Publish(new MoveRejected(entity.Id, target, reason));

    private static string ReasonFor(PathStatus status) => status switch
    {
        PathStatus.OutOfBounds => MovementReasons.OutOfBounds,
        PathStatus.StartNotWalkable => MovementReasons.StartNotWalkable,
        PathStatus.GoalNotWalkable => MovementReasons.NotWalkable,
        PathStatus.NoPath => MovementReasons.NoPath,
        PathStatus.NodeLimit => MovementReasons.SearchLimit,
        PathStatus.TooLong => MovementReasons.PathTooLong,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a failure status."),
    };

    private void InsertSorted(MoveState state)
    {
        var index = _active.BinarySearch(state, MoveStateOrder.Instance);
        _active.Insert(index < 0 ? ~index : index, state);
    }

    private MoveState Rent() => _pool.Count > 0 ? _pool.Pop() : new MoveState();

    private void Return(MoveState state)
    {
        state.Entity = null;
        state.Mover = default;
        state.Path.Clear();
        state.Spare.Clear();
        state.Done = false;
        _pool.Push(state);
    }

    private sealed class MoveState
    {
        public Entity? Entity;
        public GridMover Mover;
        public List<GridCell> Path = new();
        public List<GridCell> Spare = new();
        public int Consumed;        // cells of Path the mover has already reached
        public double Speed;
        public bool Done;
    }

    private sealed class MoveStateOrder : IComparer<MoveState>
    {
        public static readonly MoveStateOrder Instance = new();

        public int Compare(MoveState? x, MoveState? y) => x!.Entity!.Id.Value.CompareTo(y!.Entity!.Id.Value);
    }
}
