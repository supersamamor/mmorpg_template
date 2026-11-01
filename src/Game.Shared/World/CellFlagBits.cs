using Game.Shared.Vocabulary;

namespace Game.Shared.World
{
    /// <summary>
    /// Bit assignment of the per-cell flag byte: bit <c>i</c> is the <c>i</c>-th term of <see cref="CellFlags.All"/>.
    /// Derived from the vocabulary so the file format and VOCABULARY.md cannot drift apart.
    /// </summary>
    public static class CellFlagBits
    {
        /// <summary>Mask of <c>walkable</c> (bit 0).</summary>
        public static readonly byte Walkable = Mask(CellFlags.Walkable);

        /// <summary>Mask of <c>blocks_los</c> (bit 1).</summary>
        public static readonly byte BlocksLos = Mask(CellFlags.BlocksLos);

        /// <summary>Mask of every defined flag; a cell byte with any other bit set is invalid.</summary>
        public static readonly byte All = (byte)((1 << CellFlags.All.Count) - 1);

        /// <summary>Mask of one flag term, e.g. <see cref="CellFlags.Walkable"/>.</summary>
        public static byte Mask(string flag)
        {
            for (var i = 0; i < CellFlags.All.Ordered.Count; i++)
                if (CellFlags.All.Ordered[i] == flag) return (byte)(1 << i);
            throw new System.ArgumentException("'" + flag + "' is not a cell flag.", nameof(flag));
        }
    }
}
