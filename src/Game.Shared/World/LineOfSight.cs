namespace Game.Shared.World
{
    /// <summary>
    /// Line-of-sight test over cells flagged <c>blocks_los</c> (ARCHITECTURE §4).
    /// </summary>
    /// <remarks>
    /// The line runs between the two cell centres and every cell it touches is tested (a supercover walk), using exact integer
    /// arithmetic, so the result is the same on every platform and <c>a→b</c> equals <c>b→a</c>. The two end cells never block.
    /// Cells outside the grid block (<see cref="MapGrid.BlocksLos"/>).
    /// A line that passes exactly through a cell corner moves diagonally between the two cells that touch only at that corner. It is
    /// blocked only if <b>both</b> cells beside the corner block, or the cell it arrives in blocks: grazing one blocking corner is
    /// allowed, squeezing between two diagonal walls is not.
    /// </remarks>
    public static class LineOfSight
    {
        /// <summary>True if nothing flagged <c>blocks_los</c> lies between the two cells.</summary>
        public static bool HasLineOfSight(MapGrid grid, GridCell from, GridCell to)
        {
            long nx = to.X > from.X ? (long)to.X - from.X : (long)from.X - to.X;
            long ny = to.Y > from.Y ? (long)to.Y - from.Y : (long)from.Y - to.Y;
            var sx = to.X > from.X ? 1 : -1;
            var sy = to.Y > from.Y ? 1 : -1;
            var x = from.X;
            var y = from.Y;
            long ix = 0, iy = 0;

            while (ix < nx || iy < ny)
            {
                // Side of the next grid line the centre-to-centre segment passes on; 0 means exactly through a corner.
                var decision = (1 + 2 * ix) * ny - (1 + 2 * iy) * nx;
                if (decision == 0)
                {
                    if (grid.BlocksLos(x + sx, y) && grid.BlocksLos(x, y + sy)) return false;
                    x += sx;
                    y += sy;
                    ix++;
                    iy++;
                }
                else if (decision < 0)
                {
                    x += sx;
                    ix++;
                }
                else
                {
                    y += sy;
                    iy++;
                }

                if ((ix < nx || iy < ny) && grid.BlocksLos(x, y)) return false;
            }

            return true;
        }
    }
}
