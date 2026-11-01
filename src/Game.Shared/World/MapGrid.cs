using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Game.Shared.World
{
    /// <summary>
    /// The runtime logic grid of one map, built once from a <see cref="MapGridFile"/>. Immutable and thread-safe;
    /// the query methods do not allocate. Cell <c>(0, 0)</c> is the north-west corner, <c>+x</c> east, <c>+y</c> south.
    /// Anything outside the grid is not walkable and blocks line of sight.
    /// </summary>
    public sealed class MapGrid
    {
        private readonly byte[] _flags;
        private readonly byte _walkable = CellFlagBits.Walkable;
        private readonly byte _blocksLos = CellFlagBits.BlocksLos;

        private MapGrid(MapGridFile file, byte[] flags, int unknownFlagCells, GridCell firstUnknownFlagCell)
        {
            MapId = file.MapId;
            Width = file.Width;
            Height = file.Height;
            SourceSceneHash = file.SourceSceneHash ?? string.Empty;
            _flags = flags;
            UnknownFlagCells = unknownFlagCells;
            FirstUnknownFlagCell = firstUnknownFlagCell;
            Spawns = Copy(file.Spawns);
            Warps = Copy(file.Warps);
            Npcs = Copy(file.Npcs);
            Zones = Copy(file.Zones);
        }

        /// <summary>Id of the map this grid belongs to.</summary>
        public string MapId { get; }

        /// <summary>Columns.</summary>
        public int Width { get; }

        /// <summary>Rows.</summary>
        public int Height { get; }

        /// <summary>Scene hash recorded by the exporter (empty if none). Stored, not checked, until T-208.</summary>
        public string SourceSceneHash { get; }

        /// <summary>Monster spawn areas.</summary>
        public IReadOnlyList<GridSpawn> Spawns { get; }

        /// <summary>Warps.</summary>
        public IReadOnlyList<GridWarp> Warps { get; }

        /// <summary>NPC placements.</summary>
        public IReadOnlyList<GridNpc> Npcs { get; }

        /// <summary>Rule zones.</summary>
        public IReadOnlyList<GridZone> Zones { get; }

        /// <summary>Number of cells whose byte has bits outside <see cref="CellFlagBits.All"/>; the content validator reports these.</summary>
        public int UnknownFlagCells { get; }

        /// <summary>First such cell in row-major order; meaningful only when <see cref="UnknownFlagCells"/> is above 0.</summary>
        public GridCell FirstUnknownFlagCell { get; }

        /// <summary>True if the cell is inside the grid.</summary>
        public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

        /// <summary>True if the cell is inside the grid and walkable.</summary>
        public bool IsWalkable(int x, int y) => InBounds(x, y) && (_flags[y * Width + x] & _walkable) != 0;

        /// <summary>True if the cell blocks line of sight; cells outside the grid do.</summary>
        public bool BlocksLos(int x, int y) => !InBounds(x, y) || (_flags[y * Width + x] & _blocksLos) != 0;

        /// <summary>
        /// Builds a grid from a file. Returns false with a message when the format version is unknown, the size is out of
        /// range, <c>cells</c> is not base64, or its decoded length is not <c>width * height</c>. Cell bits outside
        /// <see cref="CellFlagBits.All"/> are not an error here (see <see cref="UnknownFlagCells"/>); queries ignore them.
        /// </summary>
        public static bool TryCreate(MapGridFile file, [NotNullWhen(true)] out MapGrid? grid, [NotNullWhen(false)] out string? error)
        {
            grid = null;
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (file.FormatVersion != MapGridFile.CurrentFormatVersion)
            {
                error = "unsupported format_version " + file.FormatVersion + " (this build reads " + MapGridFile.CurrentFormatVersion + ")";
                return false;
            }
            if (file.Width < 1 || file.Width > MapGridFile.MaxSize || file.Height < 1 || file.Height > MapGridFile.MaxSize)
            {
                error = "width and height must be between 1 and " + MapGridFile.MaxSize + " (got " + file.Width + "x" + file.Height + ")";
                return false;
            }

            byte[] flags;
            try
            {
                flags = Convert.FromBase64String(file.Cells ?? string.Empty);
            }
            catch (FormatException)
            {
                error = "cells is not valid base64";
                return false;
            }
            var expected = file.Width * file.Height;
            if (flags.Length != expected)
            {
                error = "cells decodes to " + flags.Length + " bytes, expected width * height = " + expected;
                return false;
            }

            var unknown = 0;
            var first = default(GridCell);
            var allowed = CellFlagBits.All;
            for (var i = 0; i < flags.Length; i++)
            {
                if ((flags[i] & ~allowed) == 0) continue;
                if (unknown++ == 0) first = new GridCell(i % file.Width, i / file.Width);
            }

            grid = new MapGrid(file, flags, unknown, first);
            error = null;
            return true;
        }

        /// <summary>Like <see cref="TryCreate"/> but throws <see cref="ArgumentException"/> on a bad file.</summary>
        public static MapGrid Create(MapGridFile file) =>
            TryCreate(file, out var grid, out var error) ? grid : throw new ArgumentException(error, nameof(file));

        private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? source)
        {
            if (source == null || source.Count == 0) return Array.Empty<T>();
            var copy = new T[source.Count];
            for (var i = 0; i < copy.Length; i++) copy[i] = source[i];
            return new ReadOnlyCollection<T>(copy);
        }
    }
}
