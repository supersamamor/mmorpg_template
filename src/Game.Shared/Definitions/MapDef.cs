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
    }
}
