using Game.Shared.World;
using Xunit.Abstractions;

namespace Game.Tests.World;

/// <summary>
/// T-203 budget (CLAUDE.md rule 12): a worst-case search on the largest sample map. Allocations are asserted (they are
/// deterministic); time is only logged because wall clock flakes when test classes run in parallel.
/// </summary>
public class PathfinderBudgetTests
{
    private readonly ITestOutputHelper _output;

    public PathfinderBudgetTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Worst_case_search_on_the_largest_sample_map_allocates_nothing_after_warm_up()
    {
        var maps = TestGrids.SampleContent.Grids.Values;
        var map = maps.OrderByDescending(m => m.Width * m.Height).First();
        var limits = TestGrids.SampleLimits;

        // Worst case: the goal is walkable but walled in, so the search expands every cell the start can reach.
        var start = FindWalkable(map, 1, 1, +1);
        var goal = FindWalkable(map, map.Width / 2, map.Height / 2, +1);
        var sealedMap = Seal(map, goal);

        var finder = new Pathfinder();
        var path = new List<GridCell>(256);

        // A long successful search as well: the other expensive shape.
        var far = FindWalkable(map, map.Width - 2, map.Height - 2, -1);

        var expandedWorst = 0;
        var expandedFar = 0;
        for (var i = 0; i < 3; i++)   // warm-up: JIT, buffer growth, list capacity
        {
            finder.FindPath(sealedMap, start, goal, limits, path).ShouldBe(PathStatus.NoPath);
            expandedWorst = finder.LastNodesExpanded;
            finder.FindPath(map, start, far, limits, path);
            expandedFar = finder.LastNodesExpanded;
        }

        const int runs = 200;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < runs; i++)
            if (finder.FindPath(sealedMap, start, goal, limits, path) != PathStatus.NoPath) throw new InvalidOperationException("expected no path");
        watch.Stop();
        var allocated = AllocationProbe.MinAllocatedBytes(() =>
        {
            for (var i = 0; i < runs; i++) finder.FindPath(sealedMap, start, goal, limits, path);
        });

        var watch2 = System.Diagnostics.Stopwatch.StartNew();
        PathStatus farStatus = default;
        for (var i = 0; i < runs; i++) farStatus = finder.FindPath(map, start, far, limits, path);
        watch2.Stop();

        _output.WriteLine($"map {map.MapId} {map.Width}x{map.Height} ({map.Width * map.Height} cells), limits {limits.MaxPathLength} steps / {limits.MaxNodesExpanded} nodes");
        _output.WriteLine($"worst case (sealed goal): {expandedWorst} nodes expanded, {watch.Elapsed.TotalMilliseconds / runs * 1000:F0} us per search over {runs} runs, {allocated} bytes allocated in total");
        _output.WriteLine($"far corner ({farStatus}): {expandedFar} nodes expanded, {watch2.Elapsed.TotalMilliseconds / runs * 1000:F0} us per search");

        allocated.ShouldBe(0);
        expandedWorst.ShouldBeLessThan(limits.MaxNodesExpanded);
    }

    private static GridCell FindWalkable(MapGrid map, int x0, int y0, int direction)
    {
        for (var i = 0; i < map.Width * map.Height; i++)
        {
            var idx = ((y0 * map.Width + x0) + direction * i) % (map.Width * map.Height);
            if (idx < 0) idx += map.Width * map.Height;
            var x = idx % map.Width;
            var y = idx / map.Width;
            if (map.IsWalkable(x, y)) return new GridCell(x, y);
        }
        throw new InvalidOperationException("map has no walkable cell");
    }

    /// <summary>A copy of the map with every neighbour of <paramref name="goal"/> made unwalkable.</summary>
    private static MapGrid Seal(MapGrid map, GridCell goal)
    {
        var bytes = new byte[map.Width * map.Height];
        for (var y = 0; y < map.Height; y++)
        for (var x = 0; x < map.Width; x++)
        {
            var ring = Math.Max(Math.Abs(x - goal.X), Math.Abs(y - goal.Y)) == 1;
            if (map.IsWalkable(x, y) && !ring) bytes[y * map.Width + x] |= CellFlagBits.Walkable;
            if (map.BlocksLos(x, y)) bytes[y * map.Width + x] |= CellFlagBits.BlocksLos;
        }
        return TestGrids.FromBytes(map.Width, map.Height, bytes);
    }
}
