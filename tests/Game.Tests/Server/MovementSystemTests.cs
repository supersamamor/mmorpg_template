using Game.Server.Events;
using Game.Server.World;
using Game.Shared.Definitions;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>
/// T-212: the movement system. Speed 5 cells/s at 10 Hz is exactly half a cell per tick (orthogonal), and agi 150 / 0 / 250 give the
/// sample formulas' 5 / 3.5 / 6 cells per second (<c>min(6, 3.5 + agi * 0.01)</c>).
/// </summary>
public class MovementSystemTests
{
    private const int Agi5 = 150;
    private const int Agi3Point5 = 0;
    private const int Agi6 = 250;

    private static readonly PathLimits Limits = TestGrids.SampleLimits;

    private static StatSheet Sheet(int agi)
    {
        var sheet = new StatSheet(DerivedStatFormulas.Compile(TestGrids.SampleContent.Formulas));
        sheet.Base.BaseLevel = 1;
        sheet.Base[PrimaryStats.Agi] = agi;
        return sheet;
    }

    /// <summary>One world, its instances' events as text lines (<c>tN Name details</c>), and helpers to spawn and drive entities.</summary>
    private sealed class Rig
    {
        public readonly GameWorld World;
        public readonly MapInstance A;
        public readonly List<string> Log = new();
        public readonly List<(long Tick, IGameEvent Event)> Events = new();

        public Rig(MapGrid grid, PathLimits? limits = null, params MapGrid[] others) : this(true, grid, limits, others)
        {
        }

        public Rig(bool record, MapGrid grid, PathLimits? limits, MapGrid[] others)
        {
            var pipeline = new SystemPipeline().Add(() => new MovementSystem(limits ?? Limits));
            World = new GameWorld(new[] { grid }.Concat(others), pipeline, 1, new ManualClock(), new ListLog());
            A = World.GetInstance(grid.MapId);
            foreach (var instance in World.Instances)
            {
                if (!record) continue;
                var captured = instance;
                instance.Events.SubscribeAll(e =>
                {
                    Events.Add((captured.TickNumber, e));
                    Log.Add($"{captured.MapId} t{captured.TickNumber} {Describe(e)}");
                });
            }
        }

        public EntityId Spawn(GridCell cell, int? agi = Agi5, string map = "")
        {
            var instance = map.Length == 0 ? A : World.GetInstance(map);
            var id = World.Spawn(EntityKind.Player, instance.MapId, cell, Directions.S);
            instance.Tick();
            instance.TryGetEntity(id, out var entity).ShouldBeTrue();
            if (agi is { } a) entity!.Stats = Sheet(a);
            Events.Clear();   // the spawn's own events are not what these tests look at
            Log.Clear();
            return id;
        }

        public Entity Get(EntityId id) => A.TryGetEntity(id, out var e) ? e : throw new InvalidOperationException("not on A");

        public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

        public void Ticks(int n)
        {
            for (var i = 0; i < n; i++) A.Tick();
        }

        public IEnumerable<T> Of<T>() where T : IGameEvent => Events.Select(e => e.Event).OfType<T>();

        public long TickOf<T>(Func<T, bool>? where = null) where T : IGameEvent =>
            Events.Where(e => e.Event is T t && (where?.Invoke(t) ?? true)).Select(e => e.Tick).First();
    }

    private static string Describe(IGameEvent e) => e switch
    {
        MovementStarted x => $"{x.Name} {x.Entity} {x.From}->{x.Goal} len {x.PathLength}",
        CellReached x => $"{x.Name} {x.Entity} {x.From}->{x.Cell} {x.Facing}",
        MovementStopped x => $"{x.Name} {x.Entity} {x.Cell} {x.Reason}",
        MoveRejected x => $"{x.Name} {x.Entity} {x.Target} {x.Reason}",
        EntityEnteredMap x => $"{x.Name} {x.Entity} {x.Cell} {x.Reason}",
        EntityLeftMap x => $"{x.Name} {x.Entity} {x.Cell} {x.Reason}",
        WarpRejected x => $"{x.Name} {x.Entity} {x.TargetMapId} {x.TargetCell} {x.Reason}",
        _ => e.Name,
    };

    private static MapGrid Corridor(int length, string id = "map.a") => Grids.Make(id, new string('.', length));

    // ---- timing ----

    [Fact]
    public void Orthogonal_walk_arrives_when_distance_over_speed_says()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(4, 0)));
        rig.Ticks(12);

        // 4 cells at 5 cells/s = 0.8 s = 8 ticks; the move is handled and advanced in the first of them (tick 1: tick 0 spawned).
        rig.TickOf<MovementStopped>().ShouldBe(8);
        rig.Of<CellReached>().Select(c => c.Cell.X).ShouldBe([1, 2, 3, 4]);
        rig.Of<CellReached>().Select(c => c.Facing).Distinct().ShouldBe([Directions.E]);
        rig.Of<MovementStarted>().Single().PathLength.ShouldBe(4);
        rig.Of<MovementStopped>().Single().Reason.ShouldBe(MovementReasons.Arrived);
        rig.Get(id).Cell.ShouldBe(new GridCell(4, 0));
        rig.Get(id).Facing.ShouldBe(Directions.E);
    }

    [Fact]
    public void Diagonal_walk_takes_root_two_longer()
    {
        var rig = new Rig(Grids.Make("map.a", ".....", ".....", ".....", "....."));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(3, 3)));
        rig.Ticks(12);

        // 3 diagonal steps = 4.243 cells = 8.49 ticks of 0.5 cells -> reached during the 9th tick (ticks 1..9).
        rig.TickOf<MovementStopped>().ShouldBe(9);
        rig.Of<CellReached>().Select(c => c.Facing).Distinct().ShouldBe([Directions.Se]);
        rig.Get(id).Cell.ShouldBe(new GridCell(3, 3));
    }

    [Fact]
    public void Cells_are_reached_in_order_and_each_step_is_adjacent()
    {
        var rig = new Rig(Grids.Make("map.a", "......", ".####.", ".#....", ".#.##.", "...#.."));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(5, 4)));
        rig.Ticks(60);

        var steps = rig.Of<CellReached>().ToList();
        steps.ShouldNotBeEmpty();
        var at = new GridCell(0, 0);
        foreach (var s in steps)
        {
            (s.From.X, s.From.Y).ShouldBe((at.X, at.Y));
            Math.Max(Math.Abs(s.Cell.X - s.From.X), Math.Abs(s.Cell.Y - s.From.Y)).ShouldBe(1);
            rig.A.Grid.IsWalkable(s.Cell.X, s.Cell.Y).ShouldBeTrue();
            at = s.Cell;
        }
        rig.Get(id).Cell.ShouldBe(new GridCell(5, 4));
    }

    [Fact]
    public void Walks_across_the_sample_meadow()
    {
        var grid = TestGrids.SampleGrid("map.starter_meadow");
        var rig = new Rig(grid, Limits, TestGrids.SampleGrid("map.thistle_hollow"));
        GridCell Walkable(int fromX, int fromY)
        {
            for (var y = fromY; y < grid.Height; y++)
            for (var x = fromX; x < grid.Width; x++)
                if (grid.IsWalkable(x, y)) return new GridCell(x, y);
            throw new InvalidOperationException("no walkable cell");
        }

        var start = Walkable(0, 0);
        var goal = Walkable(start.X + 8, start.Y + 6);
        var id = rig.Spawn(start);
        rig.Send(new MoveTo(id, goal));
        rig.Ticks(100);

        rig.Of<MovementStopped>().Single().Reason.ShouldBe(MovementReasons.Arrived);
        rig.Get(id).Cell.ShouldBe(goal);
    }

    // ---- intents ----

    [Fact]
    public void Last_move_to_in_a_tick_wins()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(9, 0)));
        rig.Send(new MoveTo(id, new GridCell(2, 0)));
        rig.Send(new MoveTo(id, new GridCell(6, 0)));
        rig.Ticks(20);

        rig.Of<MovementStarted>().Single().Goal.ShouldBe(new GridCell(6, 0));
        rig.Get(id).Cell.ShouldBe(new GridCell(6, 0));
    }

    [Fact]
    public void Last_intent_wins_even_when_it_is_a_stop()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(9, 0)));
        rig.Send(new StopMove(id));
        rig.Ticks(5);

        rig.Of<MovementStarted>().ShouldBeEmpty();
        rig.Get(id).Cell.ShouldBe(new GridCell(0, 0));
    }

    [Fact]
    public void Stop_move_stops_on_the_last_reached_cell()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(9, 0)));
        rig.Ticks(3);           // 1.5 cells: (1, 0) reached, half way to (2, 0)
        rig.Send(new StopMove(id));
        rig.Ticks(5);

        var stop = rig.Of<MovementStopped>().Single();
        (stop.Cell, stop.Reason).ShouldBe((new GridCell(1, 0), MovementReasons.Cancelled));
        rig.Get(id).Cell.ShouldBe(new GridCell(1, 0));
        rig.Of<CellReached>().Count().ShouldBe(1);
    }

    [Fact]
    public void Stop_move_while_idle_does_nothing()
    {
        var rig = new Rig(Corridor(5));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new StopMove(id));
        rig.Ticks(2);
        rig.Events.ShouldBeEmpty();
    }

    [Fact]
    public void Move_to_the_current_cell_while_idle_does_nothing_and_while_moving_cancels()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(2, 0));
        rig.Send(new MoveTo(id, new GridCell(2, 0)));
        rig.Ticks(2);
        rig.Events.ShouldBeEmpty();

        rig.Send(new MoveTo(id, new GridCell(9, 0)));
        rig.Ticks(2);
        rig.Send(new MoveTo(id, rig.Get(id).Cell));
        rig.Ticks(3);
        rig.Of<MovementStopped>().Single().Reason.ShouldBe(MovementReasons.Cancelled);
    }

    [Fact]
    public void Re_path_in_the_middle_of_a_step_keeps_the_progress()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(4, 0)));
        rig.Ticks(1);           // half way to (1, 0)
        rig.Send(new MoveTo(id, new GridCell(3, 0)));
        rig.Ticks(8);

        // 3 cells from where it started is 6 ticks (1..6). Throwing the half step away would make it 7.
        rig.TickOf<MovementStopped>().ShouldBe(6);
        rig.Of<MovementStarted>().Select(s => s.PathLength).ShouldBe([4, 3]);
        rig.Get(id).Cell.ShouldBe(new GridCell(3, 0));
    }

    [Fact]
    public void Re_path_back_the_other_way_walks_from_the_last_reached_cell()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(3, 0));
        rig.Send(new MoveTo(id, new GridCell(8, 0)));
        rig.Ticks(3);           // (4, 0) reached, half way to (5, 0)
        rig.Send(new MoveTo(id, new GridCell(0, 0)));
        rig.Ticks(20);

        rig.Of<MovementStopped>().Last().Reason.ShouldBe(MovementReasons.Arrived);
        rig.Get(id).Cell.ShouldBe(new GridCell(0, 0));
        rig.Of<CellReached>().Skip(1).First().From.ShouldBe(new GridCell(4, 0));
    }

    // ---- rejections ----

    private static void ShouldReject(Rig rig, EntityId id, GridCell target, string reason)
    {
        rig.Events.Clear();
        rig.Send(new MoveTo(id, target));
        rig.Ticks(1);
        var rejected = rig.Of<MoveRejected>().Single();
        (rejected.Entity, rejected.Target, rejected.Reason).ShouldBe((id, target, reason));
        rig.Of<MovementStarted>().ShouldBeEmpty();
    }

    [Fact]
    public void Out_of_bounds_target_is_rejected()
    {
        var rig = new Rig(Grids.Room("map.a"));
        var id = rig.Spawn(new GridCell(0, 0));
        ShouldReject(rig, id, new GridCell(5, 0), MovementReasons.OutOfBounds);
        ShouldReject(rig, id, new GridCell(-1, 2), MovementReasons.OutOfBounds);
    }

    [Fact]
    public void Unwalkable_target_is_rejected_without_a_nearest_cell_fallback()
    {
        var rig = new Rig(Grids.Room("map.a"));
        var id = rig.Spawn(new GridCell(0, 0));
        ShouldReject(rig, id, Grids.Wall, MovementReasons.NotWalkable);
        rig.Get(id).Cell.ShouldBe(new GridCell(0, 0));
    }

    [Fact]
    public void Unreachable_target_is_rejected()
    {
        var rig = new Rig(Grids.Make("map.a", "..#..", "..#..", "..#.."));
        var id = rig.Spawn(new GridCell(0, 0));
        ShouldReject(rig, id, new GridCell(4, 1), MovementReasons.NoPath);
    }

    [Fact]
    public void Path_longer_than_the_limit_is_rejected()
    {
        var rig = new Rig(Corridor(10), new PathLimits(5, 1000));
        var id = rig.Spawn(new GridCell(0, 0));
        ShouldReject(rig, id, new GridCell(9, 0), MovementReasons.PathTooLong);
        rig.Events.Clear();
        rig.Send(new MoveTo(id, new GridCell(5, 0)));       // exactly the limit is fine
        rig.Ticks(1);
        rig.Of<MovementStarted>().Single().PathLength.ShouldBe(5);
    }

    [Fact]
    public void Search_that_hits_the_node_limit_is_rejected()
    {
        var rig = new Rig(Corridor(10), new PathLimits(100, 2));
        var id = rig.Spawn(new GridCell(0, 0));
        ShouldReject(rig, id, new GridCell(9, 0), MovementReasons.SearchLimit);
    }

    [Fact]
    public void Entity_without_stats_cannot_move()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0), agi: null);
        ShouldReject(rig, id, new GridCell(5, 0), MovementReasons.NoSpeed);
        rig.Ticks(3);
        rig.Get(id).Cell.ShouldBe(new GridCell(0, 0));
    }

    [Fact]
    public void Entity_whose_speed_is_zero_cannot_move()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0));
        var sheet = rig.Get(id).Stats!;
        sheet.Modifiers.Add("test:root", DerivedStats.MoveSpeed, ModifierOp.Override, 0);
        ShouldReject(rig, id, new GridCell(5, 0), MovementReasons.NoSpeed);
    }

    [Fact]
    public void A_rejected_move_leaves_the_current_one_running()
    {
        var rig = new Rig(Grids.Room("map.a"));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(4, 0)));
        rig.Ticks(1);
        rig.Send(new MoveTo(id, Grids.Wall));
        rig.Ticks(10);

        rig.Of<MoveRejected>().Single().Reason.ShouldBe(MovementReasons.NotWalkable);
        rig.Get(id).Cell.ShouldBe(new GridCell(4, 0));
    }

    [Fact]
    public void Intents_for_unknown_entities_are_dropped_by_the_router_and_never_reach_the_system()
    {
        var rig = new Rig(Corridor(5));
        rig.A.TryEnqueue(new MoveTo(new EntityId(999), new GridCell(1, 0)));
        rig.Ticks(1);
        rig.Events.ShouldBeEmpty();
        rig.A.Counters.IntentsDroppedUnknownEntity.ShouldBe(1);
    }

    [Fact]
    public void Dead_entities_cannot_be_told_to_move_and_a_dying_walker_stops()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(9, 0)));
        rig.Ticks(3);
        rig.Get(id).Dead = true;
        rig.Send(new MoveTo(id, new GridCell(2, 0)));
        rig.Ticks(3);

        rig.A.Counters.IntentsDroppedDeadEntity.ShouldBe(1);
        rig.Of<MovementStopped>().Single().Reason.ShouldBe(MovementReasons.Died);
        rig.Get(id).Cell.ShouldBe(new GridCell(1, 0));
    }

    // ---- speed ----

    [Fact]
    public void Speed_change_takes_effect_at_the_next_cell_reached()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0), Agi3Point5);   // 0.35 cells per tick
        rig.Send(new MoveTo(id, new GridCell(8, 0)));
        rig.Ticks(1);
        rig.Get(id).Stats!.Base[PrimaryStats.Agi] = Agi6;       // now 0.6 cells per tick, but only read at the next cell
        rig.Ticks(12);

        var start = rig.TickOf<MovementStarted>();
        // Old speed until (1, 0): 1 / 0.35 -> 3 ticks. Then 0.05 cell carried, 0.6 per tick: (2, 0) after 2 more ticks.
        // Reading the speed every tick would reach (2, 0) one tick earlier; never changing it, one tick later.
        rig.TickOf<CellReached>(c => c.Cell.X == 1).ShouldBe(start + 2);
        rig.TickOf<CellReached>(c => c.Cell.X == 2).ShouldBe(start + 4);
    }

    [Fact]
    public void Losing_all_speed_mid_path_stops_at_the_next_cell()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(8, 0)));
        rig.Ticks(1);
        rig.Get(id).Stats = null;
        rig.Ticks(6);

        var stop = rig.Of<MovementStopped>().Single();
        (stop.Cell, stop.Reason).ShouldBe((new GridCell(1, 0), MovementReasons.NoSpeed));
    }

    // ---- warps ----

    private static MapGrid WithWarp(string id, string row, GridCell at, string target, GridCell targetCell)
    {
        var width = row.Length;
        var cells = row.Select(c => c == '.' ? CellFlagBits.Walkable : (byte)0).ToArray();
        return MapGrid.Create(new MapGridFile
        {
            MapId = id, Width = width, Height = 1, Cells = Convert.ToBase64String(cells),
            Warps = [new GridWarp { Cell = at, TargetMapId = target, TargetCell = targetCell }],
        });
    }

    [Fact]
    public void Walking_onto_a_warp_cell_warps_the_entity()
    {
        var a = WithWarp("map.a", "........", new GridCell(3, 0), "map.b", new GridCell(1, 0));
        var rig = new Rig(a, Limits, Corridor(4, "map.b"));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(7, 0)));
        rig.Ticks(10);

        rig.Of<MovementStopped>().Single().Reason.ShouldBe(MovementReasons.Warped);
        rig.Of<CellReached>().Last().Cell.ShouldBe(new GridCell(3, 0));
        rig.A.TryGetEntity(id, out _).ShouldBeFalse();
        var b = rig.World.GetInstance("map.b");
        b.Tick();
        b.TryGetEntity(id, out var entity).ShouldBeTrue();
        entity.Cell.ShouldBe(new GridCell(1, 0));
        rig.Log.ShouldContain(l => l.Contains(EventNames.EntityLeftMap) && l.Contains("Warp"));
    }

    [Fact]
    public void A_warp_in_the_middle_of_a_fast_tick_is_not_skipped()
    {
        // 6 cells/s with a long diagonal-free path still passes at most one cell a tick, so give the entity a path that
        // crosses the warp as the 1st of several cells reached in one call by using a huge speed override.
        var a = WithWarp("map.a", "........", new GridCell(2, 0), "map.b", new GridCell(1, 0));
        var rig = new Rig(a, Limits, Corridor(4, "map.b"));
        var id = rig.Spawn(new GridCell(0, 0));
        var sheet = rig.Get(id).Stats!;
        sheet.Modifiers.Add("test:haste", DerivedStats.MoveSpeed, ModifierOp.Override, 40);
        rig.Send(new MoveTo(id, new GridCell(7, 0)));
        rig.Ticks(3);

        rig.Of<CellReached>().Select(c => c.Cell.X).ShouldBe([1, 2]);
        rig.Of<MovementStopped>().Single().Cell.ShouldBe(new GridCell(2, 0));
        rig.World.LocationOf(id).ShouldBe("map.b");
    }

    [Fact]
    public void A_rejected_warp_leaves_the_entity_stopped_on_the_warp_cell()
    {
        var a = WithWarp("map.a", "........", new GridCell(3, 0), "map.b", new GridCell(1, 0));
        var rig = new Rig(a, Limits, Grids.Make("map.b", "..#."));   // (1, 0) is walkable; use a blocked arrival below
        var blocked = WithWarp("map.a", "........", new GridCell(3, 0), "map.b", new GridCell(2, 0));
        rig = new Rig(blocked, Limits, Grids.Make("map.b", "..#."));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(7, 0)));
        rig.Ticks(10);

        rig.Of<WarpRejected>().Single().Reason.ShouldBe("target cell not walkable");
        rig.Of<MovementStopped>().Single().Reason.ShouldBe(MovementReasons.Warped);
        rig.Get(id).Cell.ShouldBe(new GridCell(3, 0));
        rig.Of<MovementStarted>().Count().ShouldBe(1);
    }

    [Fact]
    public void Spawning_on_a_warp_cell_does_not_warp_but_walking_back_onto_it_does()
    {
        var a = WithWarp("map.a", "........", new GridCell(3, 0), "map.b", new GridCell(1, 0));
        var rig = new Rig(a, Limits, Corridor(4, "map.b"));
        var id = rig.Spawn(new GridCell(3, 0));
        rig.Ticks(5);
        rig.Get(id).Cell.ShouldBe(new GridCell(3, 0));
        rig.World.LocationOf(id).ShouldBe("map.a");

        rig.Send(new MoveTo(id, new GridCell(5, 0)));
        rig.Ticks(6);
        rig.Get(id).Cell.ShouldBe(new GridCell(5, 0));      // leaving the cell is not a warp

        rig.Send(new MoveTo(id, new GridCell(3, 0)));
        rig.Ticks(6);
        rig.World.LocationOf(id).ShouldBe("map.b");
    }

    // ---- structure ----

    [Fact]
    public void Despawned_walkers_are_forgotten_quietly()
    {
        var rig = new Rig(Corridor(10));
        var id = rig.Spawn(new GridCell(0, 0));
        rig.Send(new MoveTo(id, new GridCell(9, 0)));
        rig.Ticks(2);
        rig.World.Despawn(id);
        rig.Ticks(3);
        rig.Of<MovementStopped>().ShouldBeEmpty();
        rig.A.Systems.OfType<MovementSystem>().Single().MovingCount.ShouldBe(0);
    }

    [Fact]
    public void Unusable_limits_fail_at_boot_not_on_a_tick()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new MovementSystem(new PathLimits(0, 10)));
        Should.Throw<ArgumentOutOfRangeException>(() => new MovementSystem(new PathLimits(10, 0)));
        var content = TestGrids.SampleContent;
        Game.Server.Hosting.ServerPipeline.Create(content).Build().OfType<MovementSystem>().Count().ShouldBe(1);   // T-204: other systems join the pipeline
    }

    // ---- determinism ----

    private static List<string> RunScripted()
    {
        var meadow = TestGrids.SampleGrid("map.starter_meadow");
        var rig = new Rig(meadow, Limits, TestGrids.SampleGrid("map.thistle_hollow"));
        var walkable = new List<GridCell>();
        for (var y = 0; y < meadow.Height; y++)
        for (var x = 0; x < meadow.Width; x++)
            if (meadow.IsWalkable(x, y)) walkable.Add(new GridCell(x, y));

        var seed = 12345u;
        int Next(int n)
        {
            seed = seed * 1664525u + 1013904223u;
            return (int)((seed >> 8) % (uint)n);
        }

        var ids = new List<EntityId>();
        for (var i = 0; i < 30; i++) ids.Add(rig.Spawn(walkable[Next(walkable.Count)], 100 + i * 5));
        for (var tick = 0; tick < 150; tick++)
        {
            if (tick % 10 == 0)
                foreach (var id in ids)
                    if (Next(3) != 0) rig.Send(Next(5) == 0 ? new StopMove(id) : new MoveTo(id, walkable[Next(walkable.Count)]));
            rig.A.Tick();
            rig.World.GetInstance("map.thistle_hollow").Tick();
        }
        rig.Log.AddRange(ids.Select(id => rig.A.TryGetEntity(id, out var e) ? $"final {id} {e.Cell} {e.Facing}" : $"final {id} gone"));
        return rig.Log;
    }

    [Fact]
    public void Two_instances_given_the_same_intents_end_the_same_with_the_same_events()
    {
        var first = RunScripted();
        var second = RunScripted();
        first.Count(l => l.Contains(EventNames.CellReached)).ShouldBeGreaterThan(100);
        second.ShouldBe(first);
    }

    // ---- cost ----

    [Fact]
    public void Cost_of_a_tick_with_200_walkers_on_the_sample_meadow()
    {
        var meadow = TestGrids.SampleGrid("map.starter_meadow");
        var rig = new Rig(false, meadow, Limits, [TestGrids.SampleGrid("map.thistle_hollow")]);
        var walkable = new List<GridCell>();
        for (var y = 0; y < meadow.Height; y++)
        for (var x = 0; x < meadow.Width; x++)
            if (meadow.IsWalkable(x, y) && !meadow.Warps.Any(w => w.Cell.X == x && w.Cell.Y == y)) walkable.Add(new GridCell(x, y));

        var seed = 987654321u;
        int Next(int n)
        {
            seed = seed * 1664525u + 1013904223u;
            return (int)((seed >> 8) % (uint)n);
        }

        var ids = new List<EntityId>();
        for (var i = 0; i < 200; i++) ids.Add(rig.Spawn(walkable[Next(walkable.Count)], 100 + Next(150)));
        var idle = new List<EntityId>(ids);
        rig.A.Events.Subscribe<MovementStopped>(e => idle.Add(e.Entity));
        rig.A.Events.Subscribe<MoveRejected>(e => idle.Add(e.Entity));
        var cellEvents = 0;
        rig.A.Events.Subscribe<CellReached>(_ => cellEvents++);
        rig.Events.Clear();
        rig.Log.Clear();

        // Count events without keeping them: the Rig's own subscription stores every event, which would hide the real allocations.
        void Step()
        {
            foreach (var id in idle) rig.Send(new MoveTo(id, walkable[Next(walkable.Count)]));
            idle.Clear();
            rig.A.Tick();
            rig.Events.Clear();
            rig.Log.Clear();
        }

        for (var i = 0; i < 100; i++) Step();   // warm-up: JIT, pools, lists reach their working size

        const int measured = 300;
        var times = new List<double>(measured);
        long allocated = 0, events = 0, tickBytesMax = 0;
        for (var i = 0; i < measured; i++)
        {
            foreach (var id in idle) rig.Send(new MoveTo(id, walkable[Next(walkable.Count)]));
            idle.Clear();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var cellsBefore = cellEvents;
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            rig.A.Tick();
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            times.Add(elapsed.TotalMilliseconds);
            allocated += bytes;
            events += cellEvents - cellsBefore;
            tickBytesMax = Math.Max(tickBytesMax, bytes);
            rig.Events.Clear();
            rig.Log.Clear();
        }

        times.Sort();
        Console.WriteLine($"[T-212 cost] 200 walkers on map.starter_meadow, {measured} ticks after 100 warm-up ticks (Release would be faster):");
        Console.WriteLine($"[T-212 cost] full tick: mean {times.Average():F3} ms, median {times[times.Count / 2]:F3} ms, p99 {times[(int)(times.Count * 0.99)]:F3} ms, max {times[^1]:F3} ms (budget {TickRate.IntervalMs} ms)");
        Console.WriteLine($"[T-212 cost] allocations: {allocated / (double)measured:F0} bytes/tick on average, max {tickBytesMax} bytes; {events / (double)measured:F1} CellReached events/tick");
        Console.WriteLine($"[T-212 cost] moving at the end: {rig.A.Systems.OfType<MovementSystem>().Single().MovingCount}");

        events.ShouldBeGreaterThan(0);
        // The rig's own subscriptions store events (Events and Log lists), so only bound what the system itself needs: the event
        // records (CellReached is 48 bytes) and their pending-list growth. Path lists, move state and search buffers are pooled.
        (allocated / (double)measured).ShouldBeLessThan(events / (double)measured * 512 + 20_000);
    }
}
