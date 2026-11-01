using Game.Shared.Vocabulary;

namespace Game.Shared.World
{
    /// <summary>
    /// Helpers between the 8 <see cref="Directions"/> terms and grid steps. Index order is the vocabulary order
    /// (n, ne, e, se, s, sw, w, nw); <c>+x</c> is east and <c>+y</c> is south (ARCHITECTURE §5.3).
    /// </summary>
    public static class GridDirection
    {
        /// <summary>Number of directions.</summary>
        public const int Count = 8;

        private static readonly string[] Names =
        {
            Directions.N, Directions.Ne, Directions.E, Directions.Se, Directions.S, Directions.Sw, Directions.W, Directions.Nw,
        };

        private static readonly int[] StepX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] StepY = { -1, -1, 0, 1, 1, 1, 0, -1 };

        // tan(22.5 degrees) = sqrt(2) - 1: the slope where "mostly along an axis" turns into "diagonal".
        private const double TanHalfOctant = 0.41421356237309503;

        /// <summary>The <see cref="Directions"/> term for index 0–7.</summary>
        public static string Name(int index) => Names[index];

        /// <summary>X step (−1, 0 or 1) of direction index 0–7.</summary>
        public static int StepXOf(int index) => StepX[index];

        /// <summary>Y step (−1, 0 or 1) of direction index 0–7.</summary>
        public static int StepYOf(int index) => StepY[index];

        /// <summary>Index of a <see cref="Directions"/> term, or −1 if it is not one.</summary>
        public static int IndexOf(string direction)
        {
            for (var i = 0; i < Names.Length; i++)
                if (Names[i] == direction) return i;
            return -1;
        }

        /// <summary>The <c>(dx, dy)</c> step of a direction term; false if the term is unknown.</summary>
        public static bool TryGetStep(string direction, out int dx, out int dy)
        {
            var i = IndexOf(direction);
            dx = i < 0 ? 0 : StepX[i];
            dy = i < 0 ? 0 : StepY[i];
            return i >= 0;
        }

        /// <summary>
        /// The direction term of a step, from the signs of <paramref name="dx"/> and <paramref name="dy"/> only.
        /// Returns null for (0, 0). Use <see cref="Facing"/> for the nearest direction to a longer vector.
        /// </summary>
        public static string? FromStep(int dx, int dy)
        {
            var sx = dx > 0 ? 1 : dx < 0 ? -1 : 0;
            var sy = dy > 0 ? 1 : dy < 0 ? -1 : 0;
            for (var i = 0; i < Names.Length; i++)
                if (StepX[i] == sx && StepY[i] == sy) return Names[i];
            return null;
        }

        /// <summary>
        /// Facing from one cell to another: the nearest of the 8 directions to the straight line between them (so a cell far to the
        /// east and slightly south is <c>e</c>). Returns null when both cells are the same.
        /// </summary>
        public static string? Facing(GridCell from, GridCell to)
        {
            long dx = (long)to.X - from.X;
            long dy = (long)to.Y - from.Y;
            if (dx == 0 && dy == 0) return null;
            var ax = dx < 0 ? -dx : dx;
            var ay = dy < 0 ? -dy : dy;
            var sx = dx > 0 ? 1 : dx < 0 ? -1 : 0;
            var sy = dy > 0 ? 1 : dy < 0 ? -1 : 0;
            if (ay <= ax * TanHalfOctant) sy = 0;
            else if (ax <= ay * TanHalfOctant) sx = 0;
            return FromStep(sx, sy);
        }
    }
}
