using System;
using System.Collections.Generic;

namespace Game.Shared.World
{
    /// <summary>What a journey costs: warps first, then the walk (T-604). Smaller is nearer.</summary>
    public readonly struct TravelCost : IComparable<TravelCost>, IEquatable<TravelCost>
    {
        /// <summary>No way at all.</summary>
        public static readonly TravelCost None = new TravelCost(int.MaxValue, long.MaxValue);

        /// <summary>Creates a cost.</summary>
        public TravelCost(int warps, long walk)
        {
            Warps = warps;
            Walk = walk;
        }

        /// <summary>Warps taken.</summary>
        public int Warps { get; }

        /// <summary>Walking cost over all maps (10 per orthogonal step, 14 per diagonal).</summary>
        public long Walk { get; }

        /// <summary>True when there is a way.</summary>
        public bool Reachable => Warps != int.MaxValue;

        /// <summary>This journey with <paramref name="warps"/> more warps and <paramref name="walk"/> more walking; none stays none.</summary>
        public TravelCost Plus(int warps, long walk) => Reachable ? new TravelCost(Warps + warps, Walk + walk) : None;

        /// <inheritdoc />
        public int CompareTo(TravelCost other) => Warps != other.Warps ? Warps.CompareTo(other.Warps) : Walk.CompareTo(other.Walk);

        /// <inheritdoc />
        public bool Equals(TravelCost other) => Warps == other.Warps && Walk == other.Walk;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is TravelCost other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (Warps * 397) ^ Walk.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => Reachable ? Warps + " warps, walk " + Walk : "no way";
    }

    /// <summary>A place to go to: goal cells on one map, and how far it is from the arrival side of every warp. Made by <see cref="TravelMap.AddTarget"/>.</summary>
    public sealed class TravelTarget
    {
        internal TravelTarget(string mapId, DistanceField field, TravelCost[] afterWarp)
        {
            MapId = mapId;
            Field = field;
            AfterWarp = afterWarp;
        }

        /// <summary>The map the place is on.</summary>
        public string MapId { get; }

        /// <summary>Walking distances to it on that map.</summary>
        public DistanceField Field { get; }

        /// <summary>By warp index: the rest of the journey from before that warp (its own warp counted).</summary>
        internal TravelCost[] AfterWarp { get; }
    }

    /// <summary>The next part of a journey: what the whole rest costs, and the field to walk along on the current map (to the place itself, or to a warp).</summary>
    public readonly struct TravelRoute
    {
        internal TravelRoute(TravelCost cost, DistanceField? field, bool toWarp)
        {
            Cost = cost;
            Field = field;
            ToWarp = toWarp;
        }

        /// <summary>The rest of the journey.</summary>
        public TravelCost Cost { get; }

        /// <summary>What to walk along now; null when there is no way.</summary>
        public DistanceField? Field { get; }

        /// <summary>True when that leads to a warp rather than to the place.</summary>
        public bool ToWarp { get; }
    }

    /// <summary>
    /// Every map with its warps, for journeys across maps (T-604, ARCHITECTURE §6.4.2): the nearest place is the one with the fewest warps on the way, then the shortest walk. All the
    /// searching is done when it is built and when a target is added (at boot); planning a route is then a few array reads per warp of the current map. Immutable after the last
    /// <see cref="AddTarget"/>, and then thread-safe.
    /// </summary>
    public sealed class TravelMap
    {
        private sealed class Node
        {
            public Node(MapGrid grid)
            {
                Grid = grid;
                Parts = Connect(grid);
            }

            public readonly MapGrid Grid;
            public readonly int[] Parts;   // per cell: which connected part of the map it is in, from 1; 0 for a cell nobody walks on
            public readonly List<int> Warps = new List<int>();
            public readonly List<GridCell> WarpCells = new List<GridCell>();
        }

        private sealed class Warp
        {
            public Warp(GridWarp def, DistanceField field)
            {
                Def = def;
                Field = field;
            }

            public readonly GridWarp Def;
            public readonly DistanceField Field;
        }

        private readonly Dictionary<string, Node> _nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        private readonly List<Warp> _warps = new List<Warp>();

        /// <summary>Reads every grid and its warps. A warp to a map that is not there, or from a cell that cannot be walked on, leads nowhere.</summary>
        public TravelMap(IEnumerable<MapGrid> grids)
        {
            if (grids == null) throw new ArgumentNullException(nameof(grids));
            var ordered = new List<MapGrid>(grids);
            ordered.Sort((a, b) => string.CompareOrdinal(a.MapId, b.MapId));
            foreach (var grid in ordered)
            {
                var node = new Node(grid);
                _nodes[grid.MapId] = node;
                foreach (var warp in grid.Warps) node.WarpCells.Add(warp.Cell);
            }
            foreach (var grid in ordered)
            {
                var node = _nodes[grid.MapId];
                foreach (var warp in grid.Warps)
                {
                    if (!_nodes.ContainsKey(warp.TargetMapId) || !grid.IsWalkable(warp.Cell.X, warp.Cell.Y)) continue;
                    node.Warps.Add(_warps.Count);
                    _warps.Add(new Warp(warp, DistanceField.Build(grid, new[] { warp.Cell }, node.WarpCells)));
                }
            }
        }

        /// <summary>Warps that lead somewhere, over all maps.</summary>
        public int WarpCount => _warps.Count;

        /// <summary>True when some walk on the map, however long, leads from one cell to the other: no search, so what cannot be walked to costs nothing to rule out.</summary>
        public bool Connected(string mapId, GridCell a, GridCell b)
        {
            if (!_nodes.TryGetValue(mapId, out var node) || !node.Grid.InBounds(a.X, a.Y) || !node.Grid.InBounds(b.X, b.Y)) return false;
            var part = node.Parts[a.Y * node.Grid.Width + a.X];
            return part != 0 && part == node.Parts[b.Y * node.Grid.Width + b.X];
        }

        /// <summary>Numbers the connected parts of a map. Orthogonal steps are enough: a diagonal step needs both orthogonal neighbours, so it never joins two parts.</summary>
        private static int[] Connect(MapGrid grid)
        {
            var parts = new int[grid.Width * grid.Height];
            var open = new Stack<int>();
            var part = 0;
            for (var start = 0; start < parts.Length; start++)
            {
                if (parts[start] != 0 || !grid.IsWalkable(start % grid.Width, start / grid.Width)) continue;
                parts[start] = ++part;
                open.Push(start);
                while (open.Count > 0)
                {
                    var current = open.Pop();
                    for (var d = 0; d < GridDirection.Count; d += 2)
                    {
                        var x = current % grid.Width + GridDirection.StepXOf(d);
                        var y = current / grid.Width + GridDirection.StepYOf(d);
                        if (!grid.IsWalkable(x, y) || parts[y * grid.Width + x] != 0) continue;
                        parts[y * grid.Width + x] = part;
                        open.Push(y * grid.Width + x);
                    }
                }
            }
            return parts;
        }

        /// <summary>The map's warp cells.</summary>
        public IReadOnlyList<GridCell> WarpCells(string mapId) => _nodes.TryGetValue(mapId, out var node) ? node.WarpCells : (IReadOnlyList<GridCell>)Array.Empty<GridCell>();

        /// <summary>
        /// A place made of <paramref name="goals"/> on a map (throws for an unknown map). Warp cells among them are left out: one does not stay on one.
        /// </summary>
        public TravelTarget AddTarget(string mapId, IEnumerable<GridCell> goals)
        {
            if (goals == null) throw new ArgumentNullException(nameof(goals));
            if (!_nodes.TryGetValue(mapId, out var node)) throw new ArgumentException("No map '" + mapId + "'.", nameof(mapId));
            var kept = new List<GridCell>();
            foreach (var goal in goals)
                if (!IsWarp(node.WarpCells, goal)) kept.Add(goal);
            var field = DistanceField.Build(node.Grid, kept, node.WarpCells);

            // From the place backwards over the warps, until nothing gets nearer (each round settles at least one more warp).
            var after = new TravelCost[_warps.Count];
            for (var i = 0; i < after.Length; i++) after[i] = TravelCost.None;
            for (var changed = true; changed;)
            {
                changed = false;
                for (var i = 0; i < after.Length; i++)
                {
                    var warp = _warps[i].Def;
                    var best = Best(warp.TargetMapId, warp.TargetCell, mapId, field, after, out _).Plus(1, 0);
                    if (best.CompareTo(after[i]) >= 0) continue;
                    after[i] = best;
                    changed = true;
                }
            }
            return new TravelTarget(mapId, field, after);
        }

        /// <summary>The way from <paramref name="cell"/> of <paramref name="mapId"/> to <paramref name="target"/>.</summary>
        public TravelRoute Plan(string mapId, GridCell cell, TravelTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var cost = Best(mapId, cell, target.MapId, target.Field, target.AfterWarp, out var via);
            if (!cost.Reachable) return new TravelRoute(TravelCost.None, null, false);
            return via < 0 ? new TravelRoute(cost, target.Field, false) : new TravelRoute(cost, _warps[via].Field, true);
        }

        private TravelCost Best(string mapId, GridCell cell, string targetMapId, DistanceField field, TravelCost[] after, out int via)
        {
            via = -1;
            var best = TravelCost.None;
            if (string.Equals(mapId, targetMapId, StringComparison.Ordinal))
            {
                var walk = field.At(cell);
                if (walk != DistanceField.Unreachable) best = new TravelCost(0, walk);
            }
            if (!_nodes.TryGetValue(mapId, out var node)) return best;
            for (var i = 0; i < node.Warps.Count; i++)
            {
                var index = node.Warps[i];
                var walk = _warps[index].Field.At(cell);
                if (walk == DistanceField.Unreachable) continue;
                var cost = after[index].Plus(0, walk);
                if (cost.CompareTo(best) >= 0) continue;
                best = cost;
                via = index;
            }
            return best;
        }

        /// <summary>
        /// The cell to walk to now: at most <paramref name="maxSteps"/> along <paramref name="field"/> from <paramref name="from"/>, shortened (down to one step) while a shortest walk
        /// there could step on a warp cell that is not the end of it. Standing on the goal already (a warp one arrived on), it is a free neighbour, so the goal can be stepped on again.
        /// </summary>
        public GridCell NextStop(string mapId, GridCell from, DistanceField field, int maxSteps)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            var warps = WarpCells(mapId);
            if (field.At(from) == 0)
            {
                for (var d = 0; d < GridDirection.Count; d += 2)   // orthogonal neighbours: no corner to cut
                {
                    var cell = new GridCell(from.X + GridDirection.StepXOf(d), from.Y + GridDirection.StepYOf(d));
                    if (field.Grid.IsWalkable(cell.X, cell.Y) && !IsWarp(warps, cell)) return cell;
                }
                return from;
            }
            for (var steps = Math.Max(1, maxSteps); ; steps /= 2)
            {
                var stop = field.Along(from, steps);
                if (steps <= 1 || !CrossesWarp(warps, from, stop, field.At(from) - field.At(stop))) return stop;
            }
        }

        /// <summary>A walk of cost <paramref name="walk"/> can pass a cell only when the two parts of the way around it fit into that cost.</summary>
        private static bool CrossesWarp(IReadOnlyList<GridCell> warps, GridCell from, GridCell to, int walk)
        {
            for (var i = 0; i < warps.Count; i++)
            {
                var w = warps[i];
                if (Same(w, to) || Same(w, from)) continue;
                if (DistanceField.Octile(from, w) + DistanceField.Octile(w, to) <= walk) return true;
            }
            return false;
        }

        private static bool IsWarp(IReadOnlyList<GridCell> warps, GridCell cell)
        {
            for (var i = 0; i < warps.Count; i++)
                if (Same(warps[i], cell)) return true;
            return false;
        }

        private static bool Same(GridCell a, GridCell b) => a.X == b.X && a.Y == b.Y;
    }
}
