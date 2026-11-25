using System;
using System.Collections.Generic;

namespace Game.Shared.World
{
    /// <summary>
    /// How far every cell of a map is from a fixed set of goal cells, on foot: the pathfinder's costs (10 orthogonal, 14 diagonal, no corner cutting), whole map, no limits.
    /// Built once (at boot) and then read with array lookups only, so a journey needs no path search of its own. Immutable and thread-safe.
    /// </summary>
    /// <remarks>
    /// Cells named as <c>avoid</c> are never walked through (a warp cell on the way to somewhere else would take the walker away), but they have a distance of their own:
    /// a walker that stands on one can leave it. Among equally short ways the first direction in the order n, ne, e, se, s, sw, w, nw wins, so a way is always the same.
    /// </remarks>
    public sealed class DistanceField
    {
        /// <summary>The distance of a cell that cannot reach any goal.</summary>
        public const int Unreachable = int.MaxValue;

        private const byte NoStep = 255;
        private const int OrthogonalCost = 10;
        private const int DiagonalCost = 14;

        private readonly int[] _cost;
        private readonly byte[] _step;   // the direction of the next step toward the goal; NoStep at a goal and where there is no way

        private DistanceField(MapGrid grid, int[] cost, byte[] step)
        {
            Grid = grid;
            _cost = cost;
            _step = step;
        }

        /// <summary>The map.</summary>
        public MapGrid Grid { get; }

        /// <summary>
        /// Builds the field. Goals that are not walkable are left out (with none left every cell is <see cref="Unreachable"/>). A goal may be an avoided cell: it is then
        /// walked to, but not through.
        /// </summary>
        public static DistanceField Build(MapGrid grid, IEnumerable<GridCell> goals, IEnumerable<GridCell>? avoid = null)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (goals == null) throw new ArgumentNullException(nameof(goals));
            var width = grid.Width;
            var count = width * grid.Height;
            var cost = new int[count];
            var step = new byte[count];
            for (var i = 0; i < count; i++)
            {
                cost[i] = Unreachable;
                step[i] = NoStep;
            }
            var avoided = new bool[count];
            if (avoid != null)
                foreach (var cell in avoid)
                    if (grid.InBounds(cell.X, cell.Y)) avoided[cell.Y * width + cell.X] = true;

            // Dial's algorithm: every step costs 10 or 14, so fifteen buckets of cells, taken in cost order, replace a heap.
            var buckets = new List<int>[DiagonalCost + 1];
            for (var i = 0; i < buckets.Length; i++) buckets[i] = new List<int>();
            var pending = 0;
            foreach (var goal in goals)
            {
                if (!grid.IsWalkable(goal.X, goal.Y)) continue;
                var index = goal.Y * width + goal.X;
                if (cost[index] == 0) continue;
                cost[index] = 0;
                buckets[0].Add(index);
                pending++;
            }

            for (var distance = 0; pending > 0; distance++)
            {
                var bucket = buckets[distance % buckets.Length];
                if (bucket.Count == 0) continue;
                bucket.Sort();   // lower cell index first: the same way on every machine
                for (var b = 0; b < bucket.Count; b++)
                {
                    var current = bucket[b];
                    pending--;
                    if (cost[current] != distance) continue;   // reached more cheaply meanwhile
                    if (avoided[current] && distance > 0) continue;
                    var cx = current % width;
                    var cy = current / width;
                    for (var d = 0; d < GridDirection.Count; d++)
                    {
                        var dx = GridDirection.StepXOf(d);
                        var dy = GridDirection.StepYOf(d);
                        if (!grid.IsWalkable(cx + dx, cy + dy)) continue;
                        var diagonal = dx != 0 && dy != 0;
                        if (diagonal && !(grid.IsWalkable(cx + dx, cy) && grid.IsWalkable(cx, cy + dy))) continue;
                        var next = (cy + dy) * width + cx + dx;
                        var nextCost = distance + (diagonal ? DiagonalCost : OrthogonalCost);
                        if (nextCost > cost[next]) continue;
                        var back = (byte)((d + GridDirection.Count / 2) % GridDirection.Count);   // the walker steps the other way
                        if (nextCost == cost[next])
                        {
                            if (back < step[next]) step[next] = back;
                            continue;
                        }
                        cost[next] = nextCost;
                        step[next] = back;
                        buckets[nextCost % buckets.Length].Add(next);
                        pending++;
                    }
                }
                bucket.Clear();
            }
            return new DistanceField(grid, cost, step);
        }

        /// <summary>The walking cost from <paramref name="cell"/> to the nearest goal (10 per orthogonal step, 14 per diagonal), 0 at a goal, <see cref="Unreachable"/> without a way.</summary>
        public int At(GridCell cell) => Grid.InBounds(cell.X, cell.Y) ? _cost[cell.Y * Grid.Width + cell.X] : Unreachable;

        /// <summary>
        /// The cell <paramref name="steps"/> steps along the way from <paramref name="from"/> to the nearest goal (the goal itself when it is nearer); <paramref name="from"/> when it
        /// is a goal or there is no way.
        /// </summary>
        public GridCell Along(GridCell from, int steps)
        {
            if (At(from) == Unreachable) return from;
            var cell = from;
            for (var i = 0; i < steps; i++)
            {
                var direction = _step[cell.Y * Grid.Width + cell.X];
                if (direction == NoStep) break;
                cell = new GridCell(cell.X + GridDirection.StepXOf(direction), cell.Y + GridDirection.StepYOf(direction));
            }
            return cell;
        }

        /// <summary>The pathfinder's estimate of a walk between two cells on open ground: a walk never costs less.</summary>
        public static int Octile(GridCell a, GridCell b)
        {
            var dx = Math.Abs(a.X - b.X);
            var dy = Math.Abs(a.Y - b.Y);
            return OrthogonalCost * (dx + dy) - (2 * OrthogonalCost - DiagonalCost) * Math.Min(dx, dy);
        }
    }
}
