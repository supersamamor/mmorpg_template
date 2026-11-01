using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// Map metadata (ARCHITECTURE §5.3). The logic grid (cells, spawns, warps, NPCs, zones) is a separate
    /// generated file named by <see cref="GridFile"/>; it is not a definition.
    /// </summary>
    public sealed record MapDef : IDefinition
    {
        /// <summary>Id namespace for maps.</summary>
        public const string IdPrefix = IdPrefixes.Map;

        /// <summary>Map id, e.g. <c>map.starter_town</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Godot visual scene (<c>scene.*</c>), e.g. <c>scene.map.starter_town</c>.</summary>
        public string SceneAssetKey { get; init; } = string.Empty;

        /// <summary>Background music (<c>music.*</c>).</summary>
        public string? MusicAssetKey { get; init; }

        /// <summary>Ambience loop (<c>ambience.*</c>), played under the music.</summary>
        public string? AmbienceAssetKey { get; init; }

        /// <summary>
        /// File name of the logic grid in <c>content/maps/</c>, e.g. <c>starter_town.grid.json</c>.
        /// A content file name, not an asset path.
        /// </summary>
        public string GridFile { get; init; } = string.Empty;

        /// <summary>
        /// Where a player who dies on this map comes back (a walkable cell of this map's grid). Optional: without it the player returns to
        /// the first warp of any map that arrives here, then to the <c>new_character</c> start (<c>balance.yaml</c>).
        /// </summary>
        public CellDef? Respawn { get; init; }
    }
}

namespace Game.Shared.Definitions
{
    /// <summary>A cell position in content (<c>x</c>, <c>y</c>; <c>(0, 0)</c> is the north-west corner of the map's grid).</summary>
    public sealed record CellDef
    {
        /// <summary>Column, counted from the west edge.</summary>
        public int X { get; init; }

        /// <summary>Row, counted from the north edge.</summary>
        public int Y { get; init; }
    }
}
