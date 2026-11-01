using System;
using System.Collections.Generic;

namespace Game.Shared.World
{
    /// <summary>
    /// The on-disk logic grid of one map: <c>content/maps/&lt;name&gt;.grid.json</c> (ARCHITECTURE §5.3).
    /// Written by the Godot exporter (T-208) or the <c>grid from-ascii</c> tool; read by the content loader and
    /// turned into a <see cref="MapGrid"/>. JSON keys are the <c>snake_case</c> names of these members.
    /// </summary>
    public sealed record MapGridFile
    {
        /// <summary>The only <see cref="FormatVersion"/> this build reads.</summary>
        public const int CurrentFormatVersion = 1;

        /// <summary>Largest accepted <see cref="Width"/> or <see cref="Height"/>, in cells.</summary>
        public const int MaxSize = 4096;

        /// <summary>Format version; starts at 1. The loader rejects any version it does not know.</summary>
        public int FormatVersion { get; init; } = CurrentFormatVersion;

        /// <summary>Id of the map this grid belongs to (<c>map.*</c>); must equal the id of the <c>MapDef</c> that names the file.</summary>
        public string MapId { get; init; } = string.Empty;

        /// <summary>Columns, 1 to 4096.</summary>
        public int Width { get; init; }

        /// <summary>Rows, 1 to 4096.</summary>
        public int Height { get; init; }

        /// <summary>
        /// Base64 of one byte per cell, row-major (<c>index = y * width + x</c>). Bit <c>i</c> is the <c>i</c>-th term of
        /// <c>CellFlags.All</c> (bit 0 <c>walkable</c>, bit 1 <c>blocks_los</c>); other bits must be 0.
        /// </summary>
        public string Cells { get; init; } = string.Empty;

        /// <summary>Hash of the Godot scene the grid was exported from. Empty until the exporter exists (T-208); stored, not checked.</summary>
        public string SourceSceneHash { get; init; } = string.Empty;

        /// <summary>Monster spawn areas (map object type <c>spawn</c>).</summary>
        public IReadOnlyList<GridSpawn> Spawns { get; init; } = Array.Empty<GridSpawn>();

        /// <summary>Warps (map object type <c>warp</c>).</summary>
        public IReadOnlyList<GridWarp> Warps { get; init; } = Array.Empty<GridWarp>();

        /// <summary>NPC placements (map object type <c>npc</c>).</summary>
        public IReadOnlyList<GridNpc> Npcs { get; init; } = Array.Empty<GridNpc>();

        /// <summary>Rule zones (map object type <c>zone</c>).</summary>
        public IReadOnlyList<GridZone> Zones { get; init; } = Array.Empty<GridZone>();
    }

    /// <summary>A monster spawn area: <see cref="Count"/> monsters kept alive inside <see cref="Area"/>.</summary>
    public sealed record GridSpawn
    {
        /// <summary>Monster to spawn (<c>monster.*</c>).</summary>
        public string MonsterId { get; init; } = string.Empty;

        /// <summary>Cells monsters may spawn on (only walkable ones are used).</summary>
        public GridRect Area { get; init; }

        /// <summary>How many monsters the area holds; at least 1.</summary>
        public int Count { get; init; }

        /// <summary>Seconds before a killed monster is replaced; 0 or more.</summary>
        public double RespawnSeconds { get; init; }
    }

    /// <summary>A warp from <see cref="Cell"/> on this map to <see cref="TargetCell"/> on <see cref="TargetMapId"/> (which may be this map).</summary>
    public sealed record GridWarp
    {
        /// <summary>Cell that triggers the warp; must be walkable. At most one warp per cell.</summary>
        public GridCell Cell { get; init; }

        /// <summary>Destination map (<c>map.*</c>).</summary>
        public string TargetMapId { get; init; } = string.Empty;

        /// <summary>Arrival cell on the destination map; must be walkable there.</summary>
        public GridCell TargetCell { get; init; }
    }

    /// <summary>An NPC placement.</summary>
    public sealed record GridNpc
    {
        /// <summary>NPC definition (<c>npc.*</c>).</summary>
        public string NpcId { get; init; } = string.Empty;

        /// <summary>Cell the NPC stands on; must be walkable.</summary>
        public GridCell Cell { get; init; }

        /// <summary>Direction the NPC faces (a <c>Directions</c> term).</summary>
        public string Facing { get; init; } = string.Empty;
    }

    /// <summary>A rule zone.</summary>
    public sealed record GridZone
    {
        /// <summary>Zone kind (a <c>ZoneKinds</c> term).</summary>
        public string Kind { get; init; } = string.Empty;

        /// <summary>Cells covered.</summary>
        public GridRect Area { get; init; }
    }
}
