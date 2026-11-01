namespace Game.Shared.World
{
    /// <summary>
    /// A cell of a map logic grid. <c>(0, 0)</c> is the north-west corner, <c>+x</c> is east, <c>+y</c> is south
    /// (ARCHITECTURE §5.3). Cell <c>(x, y)</c> is centred at world <c>(x + 0.5, y + 0.5)</c> on the ground plane (§4).
    /// </summary>
    public readonly struct GridCell
    {
        /// <summary>Creates a cell.</summary>
        public GridCell(int x, int y)
        {
            X = x;
            Y = y;
        }

        /// <summary>Column, counted from the west edge.</summary>
        public int X { get; init; }

        /// <summary>Row, counted from the north edge.</summary>
        public int Y { get; init; }

        /// <inheritdoc />
        public override string ToString() => "(" + X + ", " + Y + ")";
    }
}
