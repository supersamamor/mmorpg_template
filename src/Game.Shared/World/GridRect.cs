namespace Game.Shared.World
{
    /// <summary>A rectangle of cells: <see cref="W"/> columns east and <see cref="H"/> rows south of <c>(X, Y)</c>, inclusive of that cell.</summary>
    public readonly struct GridRect
    {
        /// <summary>Creates a rectangle.</summary>
        public GridRect(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        /// <summary>Column of the north-west cell.</summary>
        public int X { get; init; }

        /// <summary>Row of the north-west cell.</summary>
        public int Y { get; init; }

        /// <summary>Width in cells.</summary>
        public int W { get; init; }

        /// <summary>Height in cells.</summary>
        public int H { get; init; }

        /// <summary>True if <paramref name="x"/>, <paramref name="y"/> lies inside the rectangle.</summary>
        public bool Contains(int x, int y) => x >= X && y >= Y && x - X < W && y - Y < H;

        /// <inheritdoc />
        public override string ToString() => "(" + X + ", " + Y + ", " + W + "x" + H + ")";
    }
}
