using Game.Shared.Random;
using Game.Shared.World;
using Xunit.Abstractions;

namespace Game.Tests.World;

public class PathfinderTests
{
    private readonly ITestOutputHelper _output;

    public PathfinderTests(ITestOutputHelper output) => _output = output;

    private static GridCell C(int x, int y) => new(x, y);

    private static (PathStatus status, List<GridCell> path) Find(
        MapGrid g, GridCell start, GridCell goal, PathLimits? limits = null, PathOptions options = PathOptions.None)
    {
        var path = new List<GridCell>();
        var status = new Pathfinder().FindPath(g, start, goal, limits ?? TestGrids.Roomy, path, options);
        return (status, path);
    }

    private static string Show(List<GridCell> path) => string.Join(" ", path.Select(c => c.X + "," + c.Y));

    /// <summary>Cost in tenths (10 orthogonal, 14 diagonal); also asserts every step is legal.</summary>
    private static int Cost(MapGrid g, GridCell start, List<GridCell> path)
    {
        var cost = 0;
        var at = start;
        foreach (var c in path)
        {
            var dx = c.X - at.X;
            var dy = c.Y - at.Y;
            (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1 && (dx != 0 || dy != 0)).ShouldBeTrue("step " + at + " -> " + c);
            g.IsWalkable(c.X, c.Y).ShouldBeTrue("cell " + c + " must be walkable");
            if (dx != 0 && dy != 0)
            {
                (g.IsWalkable(at.X + dx, at.Y) && g.IsWalkable(at.X, at.Y + dy)).ShouldBeTrue("corner cut " + at + " -> " + c);
                cost += 14;
            }
            else cost += 10;
            at = c;
        }
        return cost;
    }

    [Fact]
    public void Straight_line()
    {
        var g = TestGrids.Parse(".....");
        var (status, path) = Find(g, C(0, 0), C(4, 0));
        status.ShouldBe(PathStatus.Found);
        Show(path).ShouldBe("1,0 2,0 3,0 4,0");
    }

    [Fact]
    public void Diagonal_line()
    {
        var g = TestGrids.Parse("....", "....", "....", "....");
        var (status, path) = Find(g, C(0, 0), C(3, 3));
        status.ShouldBe(PathStatus.Found);
        Show(path).ShouldBe("1,1 2,2 3,3");
    }

    [Fact]
    public void Start_equal_to_goal_is_an_empty_path()
    {
        var (status, path) = Find(TestGrids.Parse("..."), C(1, 0), C(1, 0));
        status.ShouldBe(PathStatus.Found);
        path.ShouldBeEmpty();
    }

    [Fact]
    public void Goes_around_a_wall()
    {
        var g = TestGrids.Parse(
            ".....",
            ".###.",
            ".....");
        var (status, path) = Find(g, C(2, 0), C(2, 2));
        status.ShouldBe(PathStatus.Found);
        path.Last().ShouldBe(C(2, 2));
        path.Count.ShouldBe(6);
        Cost(g, C(2, 0), path).ShouldBe(60);   // the diagonals beside the wall would cut its corners
    }

    [Fact]
    public void Does_not_cut_corners()
    {
        // Only a diagonal link between (0,0) and (1,1): both orthogonal neighbours are walls.
        Find(TestGrids.Parse(".#", "#."), C(0, 0), C(1, 1)).status.ShouldBe(PathStatus.NoPath);

        // One orthogonal neighbour is a wall: still no cutting, the walker goes round through the open one.
        var g = TestGrids.Parse(".#", "..");
        var (status, path) = Find(g, C(0, 0), C(1, 1));
        status.ShouldBe(PathStatus.Found);
        Show(path).ShouldBe("0,1 1,1");
    }

    [Fact]
    public void Sealed_goal_has_no_path()
    {
        var g = TestGrids.Parse(
            "..#..",
            "..#..",
            "..#..");
        Find(g, C(0, 0), C(4, 2)).status.ShouldBe(PathStatus.NoPath);
    }

    [Fact]
    public void Out_of_bounds_and_unwalkable_start()
    {
        var g = TestGrids.Parse("..~");
        Find(g, C(-1, 0), C(1, 0)).status.ShouldBe(PathStatus.OutOfBounds);
        Find(g, C(0, 0), C(3, 0)).status.ShouldBe(PathStatus.OutOfBounds);
        Find(g, C(2, 0), C(0, 0)).status.ShouldBe(PathStatus.StartNotWalkable);
    }

    [Fact]
    public void Unwalkable_goal_is_refused_by_default()
    {
        var g = TestGrids.Parse("...#.");
        var (status, path) = Find(g, C(0, 0), C(3, 0));
        status.ShouldBe(PathStatus.GoalNotWalkable);
        path.ShouldBeEmpty();
    }

    [Fact]
    public void Unwalkable_goal_can_path_to_a_neighbour()
    {
        var g = TestGrids.Parse(
            ".....",
            "..#..",
            ".....");
        var (status, path) = Find(g, C(0, 1), C(2, 1), options: PathOptions.PathToNeighbourIfGoalBlocked);
        status.ShouldBe(PathStatus.Found);
        Show(path).ShouldBe("1,1");   // beside the goal after one step

        // From the far side the nearest touching cell is the one the walker reaches first.
        var (s2, p2) = Find(g, C(4, 0), C(2, 1), options: PathOptions.PathToNeighbourIfGoalBlocked);
        s2.ShouldBe(PathStatus.Found);
        Show(p2).ShouldBe("3,0");
        GridDistance.Chebyshev(p2.Last(), C(2, 1)).ShouldBe(1);
    }

    [Fact]
    public void Neighbour_mode_with_start_already_adjacent_is_an_empty_path()
    {
        var g = TestGrids.Parse("...", ".#.", "...");
        var (status, path) = Find(g, C(0, 0), C(1, 1), options: PathOptions.PathToNeighbourIfGoalBlocked);
        status.ShouldBe(PathStatus.Found);
        path.ShouldBeEmpty();
    }

    [Fact]
    public void Neighbour_mode_fails_when_every_neighbour_is_cut_off()
    {
        var g = TestGrids.Parse(
            "..~~~",
            "..~#~",
            "..~~~");
        Find(g, C(0, 0), C(3, 1), options: PathOptions.PathToNeighbourIfGoalBlocked).status.ShouldBe(PathStatus.NoPath);
    }

    [Fact]
    public void Neighbour_mode_leaves_a_walkable_goal_alone()
    {
        var g = TestGrids.Parse("....");
        var (status, path) = Find(g, C(0, 0), C(3, 0), options: PathOptions.PathToNeighbourIfGoalBlocked);
        status.ShouldBe(PathStatus.Found);
        Show(path).ShouldBe("1,0 2,0 3,0");
    }

    [Fact]
    public void Max_path_length_rejects_long_paths()
    {
        var g = TestGrids.Parse("..........");
        Find(g, C(0, 0), C(9, 0), new PathLimits(9, 1000)).status.ShouldBe(PathStatus.Found);
        Find(g, C(0, 0), C(9, 0), new PathLimits(8, 1000)).status.ShouldBe(PathStatus.TooLong);
    }

    [Fact]
    public void Max_path_length_counts_a_detour()
    {
        // Straight-line distance is 2 but the way round is 10 steps.
        var g = TestGrids.Parse(
            ".#.",
            ".#.",
            ".#.",
            ".#.",
            "...");
        var (status, path) = Find(g, C(0, 0), C(2, 0));
        status.ShouldBe(PathStatus.Found);
        path.Count.ShouldBe(10);
        var (tooLong, empty) = Find(g, C(0, 0), C(2, 0), new PathLimits(9, 1000));
        tooLong.ShouldBe(PathStatus.TooLong);
        empty.ShouldBeEmpty();
    }

    [Fact]
    public void Node_limit_stops_the_search()
    {
        var g = TestGrids.Parse(
            "...............",
            "...............",
            "...............",
            "...............");
        var finder = new Pathfinder();
        var path = new List<GridCell>();
        finder.FindPath(g, C(0, 0), C(14, 3), new PathLimits(100, 3), path).ShouldBe(PathStatus.NodeLimit);
        finder.LastNodesExpanded.ShouldBe(3);
        path.ShouldBeEmpty();
        finder.FindPath(g, C(0, 0), C(14, 3), new PathLimits(100, 100), path).ShouldBe(PathStatus.Found);
    }

    [Fact]
    public void Path_list_is_cleared_between_searches()
    {
        var g = TestGrids.Parse("....");
        var finder = new Pathfinder();
        var path = new List<GridCell>();
        finder.FindPath(g, C(0, 0), C(3, 0), TestGrids.Roomy, path).ShouldBe(PathStatus.Found);
        finder.FindPath(g, C(0, 0), C(9, 0), TestGrids.Roomy, path).ShouldBe(PathStatus.OutOfBounds);
        path.ShouldBeEmpty();
    }

    [Fact]
    public void Same_query_gives_the_same_path_on_fresh_and_reused_finders()
    {
        var g = TestGrids.SampleGrid("map.starter_meadow");
        var a = new List<GridCell>();
        var b = new List<GridCell>();
        var reused = new Pathfinder();
        reused.FindPath(g, C(3, 12), C(40, 10), TestGrids.SampleLimits, new List<GridCell>());   // dirty the buffers
        reused.FindPath(g, C(40, 10), C(3, 12), TestGrids.SampleLimits, new List<GridCell>());
        reused.FindPath(g, C(3, 12), C(40, 10), TestGrids.SampleLimits, a).ShouldBe(PathStatus.Found);
        new Pathfinder().FindPath(g, C(3, 12), C(40, 10), TestGrids.SampleLimits, b).ShouldBe(PathStatus.Found);
        Show(a).ShouldBe(Show(b));
    }

    [Fact]
    public void Equal_cost_ties_break_the_same_way_every_time()
    {
        // (0,0)->(2,1) can go e,se or se,e (both 24). Equal f and h at the first choice, so the lower cell index wins.
        var g = TestGrids.Parse("...", "...");
        for (var i = 0; i < 3; i++)
            Show(Find(g, C(0, 0), C(2, 1)).path).ShouldBe("1,1 2,1");
    }

    [Fact]
    public void Golden_path_across_the_starter_meadow()
    {
        var g = TestGrids.SampleGrid("map.starter_meadow");
        var (status, path) = Find(g, C(3, 12), C(40, 10), TestGrids.SampleLimits);
        _output.WriteLine(TestGrids.Draw(g, path, C(3, 12)));
        status.ShouldBe(PathStatus.Found);
        Show(path).ShouldBe("4,12 5,12 6,12 7,12 8,12 9,12 10,12 11,12 12,12 13,12 14,12 15,12 16,12 17,12 18,12 19,12 20,12 21,12 22,12 23,12 24,12 25,12 26,12 27,12 28,12 29,13 30,13 31,13 32,12 33,11 34,10 35,10 36,10 37,10 38,10 39,10 40,10");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Matches_brute_force_dijkstra_on_random_grids(int seed)
    {
        var rng = new Pcg32Random((ulong)seed, 11);
        var finder = new Pathfinder();
        var path = new List<GridCell>();
        var found = 0;
        for (var round = 0; round < 150; round++)
        {
            var w = 3 + rng.Next(10);
            var h = 3 + rng.Next(10);
            var wallPercent = 10 + rng.Next(40);
            var bytes = new byte[w * h];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = rng.Next(100) < wallPercent ? (byte)0 : CellFlagBits.Walkable;
            var g = TestGrids.FromBytes(w, h, bytes);
            var start = C(rng.Next(w), rng.Next(h));
            var goal = C(rng.Next(w), rng.Next(h));
            if (!g.IsWalkable(start.X, start.Y) || !g.IsWalkable(goal.X, goal.Y)) continue;

            var expected = BruteForceCost(g, start, goal);
            var status = finder.FindPath(g, start, goal, TestGrids.Roomy, path);
            if (expected < 0)
            {
                status.ShouldBe(PathStatus.NoPath, "round " + round);
                continue;
            }
            status.ShouldBe(PathStatus.Found, "round " + round);
            found++;
            if (start.X == goal.X && start.Y == goal.Y) path.ShouldBeEmpty();
            else path.Last().ShouldBe(goal);
            Cost(g, start, path).ShouldBe(expected, "round " + round + " " + start + " -> " + goal + "\n" + TestGrids.Draw(g, path, start));
        }
        found.ShouldBeGreaterThan(50);
    }

    // Plain O(n^2) Dijkstra over the same rules, written separately from the pathfinder.
    private static int BruteForceCost(MapGrid g, GridCell start, GridCell goal)
    {
        var n = g.Width * g.Height;
        var dist = new int[n];
        var done = new bool[n];
        Array.Fill(dist, int.MaxValue);
        dist[start.Y * g.Width + start.X] = 0;
        while (true)
        {
            var best = -1;
            for (var i = 0; i < n; i++)
                if (!done[i] && dist[i] != int.MaxValue && (best < 0 || dist[i] < dist[best])) best = i;
            if (best < 0) break;
            done[best] = true;
            var x = best % g.Width;
            var y = best / g.Width;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                if (!g.IsWalkable(x + dx, y + dy)) continue;
                if (dx != 0 && dy != 0 && (!g.IsWalkable(x + dx, y) || !g.IsWalkable(x, y + dy))) continue;
                var to = (y + dy) * g.Width + x + dx;
                var d = dist[best] + (dx != 0 && dy != 0 ? 14 : 10);
                if (d < dist[to]) dist[to] = d;
            }
        }
        var result = dist[goal.Y * g.Width + goal.X];
        return result == int.MaxValue ? -1 : result;
    }
}
