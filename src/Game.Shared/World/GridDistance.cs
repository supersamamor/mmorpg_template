using System;

namespace Game.Shared.World
{
    /// <summary>Distances in cells. Ranges and AoE use Chebyshev distance (ARCHITECTURE §4).</summary>
    public static class GridDistance
    {
        /// <summary>Chebyshev distance: the number of 8-direction steps ignoring obstacles, <c>max(|dx|, |dy|)</c>.</summary>
        public static int Chebyshev(GridCell a, GridCell b)
        {
            var dx = Math.Abs((long)a.X - b.X);
            var dy = Math.Abs((long)a.Y - b.Y);
            var d = dx > dy ? dx : dy;
            return d > int.MaxValue ? int.MaxValue : (int)d;
        }

        /// <summary>True if <paramref name="b"/> is within <paramref name="range"/> cells (Chebyshev, inclusive) of <paramref name="a"/>.</summary>
        public static bool InRange(GridCell a, GridCell b, int range) => range >= 0 && Chebyshev(a, b) <= range;
    }
}
