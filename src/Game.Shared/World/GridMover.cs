using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.World
{
    /// <summary>
    /// Advances one entity along a path over time (ARCHITECTURE §4). A small value type: it holds the current cell, how far the entity is
    /// towards the next cell, and its facing. It is pure arithmetic: it never reads the map, so the path must already be validated
    /// (for example by <see cref="Pathfinder"/>), and each step must go to one of the 8 neighbouring cells.
    /// </summary>
    /// <remarks>
    /// Distance is measured in cells: an orthogonal step is 1 and a diagonal step is √2, so a diagonal takes √2 times as long. Time that is
    /// left over after finishing a step carries into the next one, and progress carries between <see cref="Advance"/> calls, so many
    /// short calls reach a cell at the same moment (to within floating-point rounding) as one long call. The mover keeps a reference to
    /// the path list; do not change the list while it is in use, pass a new one to <see cref="SetPath"/> instead.
    /// </remarks>
    public struct GridMover
    {
        /// <summary>Length of a diagonal step in cells (√2).</summary>
        public const double DiagonalLength = 1.4142135623730951;

        // Distances within this of a step's end count as reaching it, so 0.1 + 0.9 style rounding cannot strand a mover 1e-16 short of a cell.
        private const double ArrivalEpsilon = 1e-9;

        private IReadOnlyList<GridCell>? _path;
        private int _next;
        private double _travelled;   // cells covered towards _path[_next]

        /// <summary>Creates a mover standing on a cell.</summary>
        public GridMover(GridCell cell, string facing = Directions.S)
        {
            Cell = cell;
            Facing = facing;
            _path = null;
            _next = 0;
            _travelled = 0;
            Arrived = false;
        }

        /// <summary>The cell the entity has fully reached. While <see cref="IsMoving"/>, the entity is between this cell and <see cref="NextCell"/>.</summary>
        public GridCell Cell { get; private set; }

        /// <summary>Current facing (a <see cref="Directions"/> term): the direction of the step in progress, or of the last one.</summary>
        public string Facing { get; private set; }

        /// <summary>True when a path is set and cells remain on it.</summary>
        public bool IsMoving => _path != null && _next < _path.Count;

        /// <summary>True once the last cell of a path was reached. Cleared by <see cref="SetPath"/> and <see cref="Stop"/>.</summary>
        public bool Arrived { get; private set; }

        /// <summary>The cell being walked to; the same as <see cref="Cell"/> when not moving.</summary>
        public GridCell NextCell => IsMoving ? _path![_next] : Cell;

        /// <summary>Cells left on the path, including the one being walked to.</summary>
        public int RemainingSteps => _path == null ? 0 : _path.Count - _next;

        /// <summary>How far along the current step the entity is, from 0 (at <see cref="Cell"/>) up to but excluding 1. 0 when not moving.</summary>
        public double Progress => IsMoving ? _travelled / StepLength(Cell, _path![_next]) : 0;

        /// <summary>
        /// Starts walking <paramref name="path"/> from <see cref="Cell"/> (the path excludes the current cell, as <see cref="Pathfinder"/> returns it).
        /// Also the way to re-path: the current cell is kept, and progress towards the next cell is kept when the new path's first cell is the cell
        /// already being walked to; otherwise progress restarts from the current cell. An empty path means "already there" and sets
        /// <see cref="Arrived"/>.
        /// </summary>
        public void SetPath(IReadOnlyList<GridCell> path)
        {
            var keepProgress = IsMoving && path.Count > 0 && Same(path[0], _path![_next]);
            _path = path;
            _next = 0;
            if (!keepProgress) _travelled = 0;
            Arrived = path.Count == 0;
            FaceNext();
        }

        /// <summary>Stops where it is: drops the path and any progress towards the next cell. <see cref="Arrived"/> is cleared.</summary>
        public void Stop()
        {
            _path = null;
            _next = 0;
            _travelled = 0;
            Arrived = false;
        }

        /// <summary>
        /// Moves along the path for <paramref name="elapsedMs"/> at <paramref name="moveSpeed"/> cells per second. Returns the number of
        /// cells newly reached. Does nothing when not moving, or when speed or time is not positive. Time left over once the last
        /// cell is reached is discarded.
        /// </summary>
        public int Advance(double moveSpeed, int elapsedMs)
        {
            if (!IsMoving || !(moveSpeed > 0) || elapsedMs <= 0) return 0;

            var budget = moveSpeed * elapsedMs / 1000.0;
            var reached = 0;
            while (_next < _path!.Count)
            {
                var target = _path[_next];
                var length = StepLength(Cell, target);
                var need = length - _travelled;
                if (budget + ArrivalEpsilon < need)
                {
                    _travelled += budget;
                    break;
                }

                budget = budget > need ? budget - need : 0;
                _travelled = 0;
                Cell = target;
                _next++;
                reached++;
                FaceNext();
            }

            if (_next >= _path.Count) Arrived = true;
            return reached;
        }

        private void FaceNext()
        {
            if (_path == null || _next >= _path.Count) return;
            var facing = GridDirection.Facing(Cell, _path[_next]);
            if (facing != null) Facing = facing;
        }

        private static bool Same(GridCell a, GridCell b) => a.X == b.X && a.Y == b.Y;

        private static double StepLength(GridCell from, GridCell to) => from.X != to.X && from.Y != to.Y ? DiagonalLength : 1.0;
    }
}
