using System;
using System.Collections.Generic;

namespace Game.Shared.World
{
    /// <summary>
    /// Deterministic 8-direction A* over a <see cref="MapGrid"/> (ARCHITECTURE §4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rules.</b> Neighbours are visited in the order n, ne, e, se, s, sw, w, nw. A diagonal step needs both orthogonal neighbours
    /// walkable (no corner cutting). Costs are the integers 10 (orthogonal) and 14 (diagonal), an approximation of 1 and √2 with
    /// no rounding on any runtime, and the heuristic is the octile distance <c>10·(dx + dy) − 6·min(dx, dy)</c> in the same units,
    /// so it is consistent and the path is optimal under those costs. Integers mean two routes with the same cost compare equal
    /// exactly, never "equal on one CPU, different on another". Among equal total cost the node with the lower heuristic (nearer
    /// the goal) is expanded first, then the lower cell index (<c>y * width + x</c>), so the same query gives the same path everywhere.
    /// </para>
    /// <para>
    /// <b>Memory.</b> An instance owns its search buffers (sized to the largest grid it has seen) and reuses them, so after the first
    /// search on a grid no allocation happens. An instance is not thread-safe: use one per thread (or per map instance).
    /// </para>
    /// </remarks>
    public sealed class Pathfinder
    {
        private const int OrthogonalCost = 10;
        private const int DiagonalCost = 14;
        private const int KeyHeuristicBits = 20;

        private struct HeapEntry
        {
            public long Key;   // (f << KeyHeuristicBits) | h
            public int Cell;
        }

        private int[] _g = Array.Empty<int>();
        private int[] _parent = Array.Empty<int>();
        private int[] _seen = Array.Empty<int>();    // == _generation: _g and _parent are valid for this search
        private int[] _closed = Array.Empty<int>();  // == _generation: already expanded
        private HeapEntry[] _heap = new HeapEntry[64];
        private int _heapCount;
        private int _generation;

        /// <summary>Nodes expanded by the last search; for tests and tuning.</summary>
        public int LastNodesExpanded { get; private set; }

        /// <summary>
        /// Finds the cheapest path from <paramref name="start"/> to <paramref name="goal"/>. On <see cref="PathStatus.Found"/>,
        /// <paramref name="path"/> holds the cells to walk in order, excluding the start and including the goal (empty if the start already is
        /// the goal, or touches it when <see cref="PathOptions.PathToNeighbourIfGoalBlocked"/> applies). On any other status it is empty.
        /// </summary>
        /// <param name="grid">The map.</param>
        /// <param name="start">Where the walker is; must be walkable.</param>
        /// <param name="goal">Where it wants to go.</param>
        /// <param name="limits">Search bounds (<c>balance.yaml</c> <c>movement</c>).</param>
        /// <param name="path">Caller-owned list that receives the path; cleared first.</param>
        /// <param name="options">Optional behaviour; off by default.</param>
        public PathStatus FindPath(MapGrid grid, GridCell start, GridCell goal, PathLimits limits, List<GridCell> path, PathOptions options = PathOptions.None)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (path == null) throw new ArgumentNullException(nameof(path));
            path.Clear();
            LastNodesExpanded = 0;

            if (!grid.InBounds(start.X, start.Y) || !grid.InBounds(goal.X, goal.Y)) return PathStatus.OutOfBounds;
            if (!grid.IsWalkable(start.X, start.Y)) return PathStatus.StartNotWalkable;

            var toNeighbour = false;
            if (!grid.IsWalkable(goal.X, goal.Y))
            {
                if (options != PathOptions.PathToNeighbourIfGoalBlocked) return PathStatus.GoalNotWalkable;
                toNeighbour = true;
            }

            var goalReach = toNeighbour ? 1 : 0;
            var distance = GridDistance.Chebyshev(start, goal);
            if (distance <= goalReach) return PathStatus.Found;
            if (distance - goalReach > limits.MaxPathLength) return PathStatus.TooLong;

            var width = grid.Width;
            Prepare(width * grid.Height);

            var startIndex = start.Y * width + start.X;
            _g[startIndex] = 0;
            _parent[startIndex] = -1;
            _seen[startIndex] = _generation;
            _heapCount = 0;
            var startH = Heuristic(start.X, start.Y, goal, toNeighbour);
            Push(startIndex, startH, startH);

            var expanded = 0;
            while (_heapCount > 0)
            {
                var current = Pop();
                if (_closed[current] == _generation) continue;   // stale duplicate from a later, cheaper push
                _closed[current] = _generation;
                var cx = current % width;
                var cy = current / width;

                if (Math.Max(Math.Abs(cx - goal.X), Math.Abs(cy - goal.Y)) <= goalReach)
                {
                    LastNodesExpanded = expanded;
                    return Build(current, width, limits, path);
                }

                if (expanded >= limits.MaxNodesExpanded)
                {
                    LastNodesExpanded = expanded;
                    return PathStatus.NodeLimit;
                }
                expanded++;

                var g = _g[current];
                for (var d = 0; d < GridDirection.Count; d++)
                {
                    var dx = GridDirection.StepXOf(d);
                    var dy = GridDirection.StepYOf(d);
                    var nx = cx + dx;
                    var ny = cy + dy;
                    if (!grid.IsWalkable(nx, ny)) continue;
                    var diagonal = dx != 0 && dy != 0;
                    if (diagonal && !(grid.IsWalkable(cx + dx, cy) && grid.IsWalkable(cx, cy + dy))) continue;

                    var next = ny * width + nx;
                    if (_closed[next] == _generation) continue;
                    var ng = g + (diagonal ? DiagonalCost : OrthogonalCost);
                    if (_seen[next] == _generation && ng >= _g[next]) continue;

                    _seen[next] = _generation;
                    _g[next] = ng;
                    _parent[next] = current;
                    var h = Heuristic(nx, ny, goal, toNeighbour);
                    Push(next, ng + h, h);
                }
            }

            LastNodesExpanded = expanded;
            return PathStatus.NoPath;
        }

        private PathStatus Build(int goalIndex, int width, PathLimits limits, List<GridCell> path)
        {
            var steps = 0;
            for (var i = goalIndex; _parent[i] >= 0; i = _parent[i]) steps++;
            if (steps > limits.MaxPathLength) return PathStatus.TooLong;

            for (var i = goalIndex; _parent[i] >= 0; i = _parent[i]) path.Add(new GridCell(i % width, i / width));
            path.Reverse();
            return PathStatus.Found;
        }

        private void Prepare(int cells)
        {
            if (_g.Length < cells)
            {
                _g = new int[cells];
                _parent = new int[cells];
                _seen = new int[cells];
                _closed = new int[cells];
                _generation = 0;
            }
            if (++_generation == int.MaxValue)
            {
                Array.Clear(_seen, 0, _seen.Length);
                Array.Clear(_closed, 0, _closed.Length);
                _generation = 1;
            }
        }

        // Octile distance in cost units. Heading for a blocked goal, the target is any neighbour of it, which can be up to one
        // diagonal step closer than the goal cell itself, so subtract that to stay admissible.
        private static int Heuristic(int x, int y, GridCell goal, bool toNeighbour)
        {
            var dx = Math.Abs(x - goal.X);
            var dy = Math.Abs(y - goal.Y);
            var h = OrthogonalCost * (dx + dy) - 6 * Math.Min(dx, dy);
            if (!toNeighbour) return h;
            h -= DiagonalCost;
            return h < 0 ? 0 : h;
        }

        private void Push(int cell, int f, int h)
        {
            if (_heapCount == _heap.Length) Array.Resize(ref _heap, _heap.Length * 2);
            var entry = new HeapEntry { Key = ((long)f << KeyHeuristicBits) | (uint)h, Cell = cell };
            var i = _heapCount++;
            while (i > 0)
            {
                var parent = (i - 1) >> 1;
                if (!Less(entry, _heap[parent])) break;
                _heap[i] = _heap[parent];
                i = parent;
            }
            _heap[i] = entry;
        }

        private int Pop()
        {
            var top = _heap[0].Cell;
            var last = _heap[--_heapCount];
            if (_heapCount > 0)
            {
                var i = 0;
                while (true)
                {
                    var child = 2 * i + 1;
                    if (child >= _heapCount) break;
                    if (child + 1 < _heapCount && Less(_heap[child + 1], _heap[child])) child++;
                    if (!Less(_heap[child], last)) break;
                    _heap[i] = _heap[child];
                    i = child;
                }
                _heap[i] = last;
            }
            return top;
        }

        private static bool Less(HeapEntry a, HeapEntry b) => a.Key < b.Key || (a.Key == b.Key && a.Cell < b.Cell);
    }
}
