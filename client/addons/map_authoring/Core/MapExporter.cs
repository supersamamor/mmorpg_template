using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Client.MapAuthoring;

/// <summary>The metadata names of an authoring scene. VOCABULARY.md: "Map authoring metadata".</summary>
public static class MapMeta
{
    /// <summary>Root: the map's id (<c>map.*</c>).</summary>
    public const string MapId = "map_id";

    /// <summary>Root: columns.</summary>
    public const string Width = "map_width";

    /// <summary>Root: rows.</summary>
    public const string Height = "map_height";

    /// <summary>Root: the steepest walkable surface, in degrees. In the scene, so the hash covers it; the plugin's config only gives new maps their first value.</summary>
    public const string SlopeLimit = "map_slope_limit_degrees";

    /// <summary>Root: the painted cells (<see cref="CellOverrides"/>).</summary>
    public const string Overrides = "map_overrides";

    /// <summary>A collision body: false means its surface is not walkable.</summary>
    public const string SurfaceWalkable = "map_walkable";

    /// <summary>A collision body: true means cells whose ray hits it block line of sight.</summary>
    public const string SurfaceBlocksLos = "map_blocks_los";

    /// <summary>A marker node: its kind, a <see cref="MapObjectTypes"/> term.</summary>
    public const string Marker = "map_marker";

    /// <summary>Spawn: the monster.</summary>
    public const string MonsterId = "map_monster_id";

    /// <summary>Spawn, zone: the area's width in cells; the marker stands on its north-west cell.</summary>
    public const string AreaWidth = "map_w";

    /// <summary>Spawn, zone: the area's height in cells.</summary>
    public const string AreaHeight = "map_h";

    /// <summary>Spawn: how many monsters.</summary>
    public const string Count = "map_count";

    /// <summary>Spawn: seconds before a killed monster is replaced.</summary>
    public const string RespawnSeconds = "map_respawn_seconds";

    /// <summary>Warp: the destination map.</summary>
    public const string TargetMapId = "map_target_map_id";

    /// <summary>Warp: the destination column.</summary>
    public const string TargetX = "map_target_x";

    /// <summary>Warp: the destination row.</summary>
    public const string TargetY = "map_target_y";

    /// <summary>NPC: which one.</summary>
    public const string NpcId = "map_npc_id";

    /// <summary>NPC: the direction it faces (a <see cref="Directions"/> term).</summary>
    public const string Facing = "map_facing";

    /// <summary>Zone: its kind (a <see cref="ZoneKinds"/> term).</summary>
    public const string ZoneKind = "map_zone_kind";
}

/// <summary>What the export knows about the rest of the content: from the display bundle in the client, a fake in tests.</summary>
public interface IContentIds
{
    /// <summary>True when a definition has this id.</summary>
    bool Exists(string id);

    /// <summary>True when the cell is walkable on another map; false for an unknown map or a cell outside it.</summary>
    bool IsWalkable(string mapId, int x, int y);
}

/// <summary>A marker node as the export sees it: no engine types.</summary>
/// <param name="Path">Node path from the scene root, for messages.</param>
/// <param name="CellX">The column under the node's global position.</param>
/// <param name="CellY">The row under the node's global position.</param>
/// <param name="Meta">Its metadata: strings, longs and doubles.</param>
public sealed record MarkerNode(string Path, int CellX, int CellY, IReadOnlyDictionary<string, object?> Meta);

/// <summary>One thing wrong with a map. Shown in the editor's Map dock, printed by the headless export.</summary>
/// <param name="Path">The node to look at, or empty for the scene as a whole.</param>
/// <param name="Message">What is wrong.</param>
public sealed record AuthoringProblem(string Path, string Message);

/// <summary>The root's settings.</summary>
/// <param name="MapId">The map's id.</param>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
/// <param name="SlopeLimitDegrees">The bake's slope limit.</param>
/// <param name="Overrides">The painted cells.</param>
public sealed record MapSettings(string MapId, int Width, int Height, double SlopeLimitDegrees, CellOverrides Overrides);

/// <summary>What <see cref="MapExporter.Build"/> produced.</summary>
/// <param name="File">The grid, or null when <paramref name="Problems"/> is not empty.</param>
/// <param name="Problems">Everything wrong, in a fixed order.</param>
public sealed record MapExportResult(MapGridFile? File, IReadOnlyList<AuthoringProblem> Problems);

/// <summary>Turns the bake, the paint and the markers into a grid file, checking the markers on the way (ARCHITECTURE §5.3). Pure: the same input gives the same output.</summary>
public static class MapExporter
{
    /// <summary>Reads the root's metadata. Null with problems added when something required is missing.</summary>
    public static MapSettings? ReadSettings(IReadOnlyDictionary<string, object?> root, ICollection<AuthoringProblem> problems)
    {
        var errors = new List<string>();
        var reader = new MetaReader(root, errors);
        var id = reader.Text(MapMeta.MapId);
        var width = reader.Int(MapMeta.Width);
        var height = reader.Int(MapMeta.Height);
        var slope = reader.Number(MapMeta.SlopeLimit);
        if (width < 1 || width > MapGridFile.MaxSize || height < 1 || height > MapGridFile.MaxSize)
            errors.Add($"the map size {width}x{height} must be 1 to {MapGridFile.MaxSize} each way");
        if (slope <= 0 || slope > 90) errors.Add($"{MapMeta.SlopeLimit} must be above 0 and at most 90 (got {slope.ToString(CultureInfo.InvariantCulture)})");

        var lines = root.TryGetValue(MapMeta.Overrides, out var value) && value is IEnumerable<string> list ? list : Array.Empty<string>();
        var overrides = CellOverrides.Parse(lines, errors);
        foreach (var error in errors) problems.Add(new AuthoringProblem(string.Empty, error));
        return errors.Count > 0 ? null : new MapSettings(id, width, height, slope, overrides);
    }

    /// <summary>Builds the grid file.</summary>
    /// <param name="settings">The root's settings.</param>
    /// <param name="baked">The bake's flags, before the paint.</param>
    /// <param name="markers">The marker nodes, in any order: the lists are sorted, so moving a node in the tree changes nothing.</param>
    /// <param name="sceneHash">The value for <c>source_scene_hash</c>.</param>
    /// <param name="ids">The rest of the content.</param>
    public static MapExportResult Build(MapSettings settings, byte[] baked, IEnumerable<MarkerNode> markers, string sceneHash, IContentIds ids)
    {
        var problems = new List<AuthoringProblem>();
        var paintErrors = new List<string>();
        var flags = settings.Overrides.Apply(baked, settings.Width, settings.Height, paintErrors);
        foreach (var error in paintErrors) problems.Add(new AuthoringProblem(string.Empty, error));

        var spawns = new List<GridSpawn>();
        var warps = new List<GridWarp>();
        var npcs = new List<GridNpc>();
        var zones = new List<GridZone>();
        var context = new MarkerContext(settings, flags, ids);
        foreach (var marker in markers.OrderBy(m => m.Path, StringComparer.Ordinal))
        {
            var errors = new List<string>();
            var reader = new MetaReader(marker.Meta, errors);
            var kind = reader.Text(MapMeta.Marker);
            switch (kind)
            {
                case MapObjectTypes.Spawn:
                    context.Spawn(marker, reader, errors, spawns);
                    break;
                case MapObjectTypes.Warp:
                    context.Warp(marker, reader, errors, warps);
                    break;
                case MapObjectTypes.Npc:
                    context.Npc(marker, reader, errors, npcs);
                    break;
                case MapObjectTypes.Zone:
                    context.Zone(marker, reader, errors, zones);
                    break;
                default:
                    errors.Add($"marker kind '{kind}' is not one of {string.Join(", ", MapObjectTypes.All)}");
                    break;
            }
            foreach (var error in errors) problems.Add(new AuthoringProblem(marker.Path, error));
        }
        if (problems.Count > 0) return new MapExportResult(null, problems);

        return new MapExportResult(new MapGridFile
        {
            MapId = settings.MapId,
            Width = settings.Width,
            Height = settings.Height,
            Cells = Convert.ToBase64String(flags),
            SourceSceneHash = sceneHash,
            Spawns = spawns.OrderBy(s => s.MonsterId, StringComparer.Ordinal).ThenBy(s => s.Area.Y).ThenBy(s => s.Area.X).ThenBy(s => s.Area.W).ThenBy(s => s.Area.H).ThenBy(s => s.Count).ThenBy(s => s.RespawnSeconds).ToList(),
            Warps = warps.OrderBy(w => w.Cell.Y).ThenBy(w => w.Cell.X).ToList(),
            Npcs = npcs.OrderBy(n => n.NpcId, StringComparer.Ordinal).ThenBy(n => n.Cell.Y).ThenBy(n => n.Cell.X).ToList(),
            Zones = zones.OrderBy(z => z.Kind, StringComparer.Ordinal).ThenBy(z => z.Area.Y).ThenBy(z => z.Area.X).ThenBy(z => z.Area.W).ThenBy(z => z.Area.H).ToList(),
        }, problems);
    }

    private sealed class MarkerContext
    {
        private readonly MapSettings _settings;
        private readonly byte[] _flags;
        private readonly IContentIds _ids;
        private readonly Dictionary<(int, int), string> _warpCells = new Dictionary<(int, int), string>();

        public MarkerContext(MapSettings settings, byte[] flags, IContentIds ids)
        {
            _settings = settings;
            _flags = flags;
            _ids = ids;
        }

        public void Spawn(MarkerNode marker, MetaReader meta, List<string> errors, List<GridSpawn> into)
        {
            var monster = meta.Text(MapMeta.MonsterId);
            var area = new GridRect(marker.CellX, marker.CellY, meta.Int(MapMeta.AreaWidth), meta.Int(MapMeta.AreaHeight));
            var count = meta.Int(MapMeta.Count);
            var respawn = meta.Number(MapMeta.RespawnSeconds);
            if (errors.Count > 0) return;
            if (!_ids.Exists(monster)) errors.Add($"unknown monster '{monster}'");
            if (!RectInBounds(area)) errors.Add($"spawn area {area} is empty or outside the {_settings.Width}x{_settings.Height} map");
            else if (!AnyWalkable(area)) errors.Add($"spawn area {area} has no walkable cell");
            if (count < 1) errors.Add($"{MapMeta.Count} must be at least 1 (got {count})");
            if (!(respawn >= 0)) errors.Add($"{MapMeta.RespawnSeconds} must be 0 or more (got {respawn.ToString(CultureInfo.InvariantCulture)})");
            into.Add(new GridSpawn { MonsterId = monster, Area = area, Count = count, RespawnSeconds = respawn });
        }

        public void Warp(MarkerNode marker, MetaReader meta, List<string> errors, List<GridWarp> into)
        {
            var map = meta.Text(MapMeta.TargetMapId);
            var target = new GridCell(meta.Int(MapMeta.TargetX), meta.Int(MapMeta.TargetY));
            if (errors.Count > 0) return;
            var cell = new GridCell(marker.CellX, marker.CellY);
            RequireWalkable(cell, "warp", errors);
            if (_warpCells.TryGetValue((cell.X, cell.Y), out var first)) errors.Add($"{first} is already a warp on cell {cell}");
            else _warpCells.Add((cell.X, cell.Y), marker.Path);

            if (map == _settings.MapId)
            {
                if (!IsWalkable(target.X, target.Y)) errors.Add($"warp target {target} is not walkable on this map");
            }
            else if (!_ids.Exists(map))
            {
                errors.Add($"unknown target map '{map}'");
            }
            else if (!_ids.IsWalkable(map, target.X, target.Y))
            {
                errors.Add($"warp target {target} is not walkable on {map}");
            }
            into.Add(new GridWarp { Cell = cell, TargetMapId = map, TargetCell = target });
        }

        public void Npc(MarkerNode marker, MetaReader meta, List<string> errors, List<GridNpc> into)
        {
            var npc = meta.Text(MapMeta.NpcId);
            var facing = meta.Text(MapMeta.Facing);
            if (errors.Count > 0) return;
            var cell = new GridCell(marker.CellX, marker.CellY);
            if (!_ids.Exists(npc)) errors.Add($"unknown npc '{npc}'");
            RequireWalkable(cell, "npc", errors);
            if (!Directions.All.Contains(facing)) errors.Add($"facing '{facing}' is not one of {string.Join(", ", Directions.All)}");
            into.Add(new GridNpc { NpcId = npc, Cell = cell, Facing = facing });
        }

        public void Zone(MarkerNode marker, MetaReader meta, List<string> errors, List<GridZone> into)
        {
            var kind = meta.Text(MapMeta.ZoneKind);
            var area = new GridRect(marker.CellX, marker.CellY, meta.Int(MapMeta.AreaWidth), meta.Int(MapMeta.AreaHeight));
            if (errors.Count > 0) return;
            if (!ZoneKinds.All.Contains(kind)) errors.Add($"zone kind '{kind}' is not one of {string.Join(", ", ZoneKinds.All)}");
            if (!RectInBounds(area)) errors.Add($"zone area {area} is empty or outside the {_settings.Width}x{_settings.Height} map");
            into.Add(new GridZone { Kind = kind, Area = area });
        }

        private bool Inside(int x, int y) => x >= 0 && y >= 0 && x < _settings.Width && y < _settings.Height;

        private bool IsWalkable(int x, int y) => Inside(x, y) && (_flags[y * _settings.Width + x] & CellFlagBits.Walkable) != 0;

        private void RequireWalkable(GridCell cell, string what, List<string> errors)
        {
            if (!Inside(cell.X, cell.Y)) errors.Add($"{what} cell {cell} is outside the {_settings.Width}x{_settings.Height} map");
            else if (!IsWalkable(cell.X, cell.Y)) errors.Add($"{what} cell {cell} is not walkable");
        }

        private bool RectInBounds(GridRect r) => r.W >= 1 && r.H >= 1 && Inside(r.X, r.Y) && Inside(r.X + r.W - 1, r.Y + r.H - 1);

        private bool AnyWalkable(GridRect r)
        {
            for (var y = r.Y; y < r.Y + r.H; y++)
                for (var x = r.X; x < r.X + r.W; x++)
                    if (IsWalkable(x, y)) return true;
            return false;
        }
    }

    /// <summary>Typed access to metadata; a missing or mistyped value adds a message and returns a neutral value.</summary>
    private sealed class MetaReader
    {
        private readonly IReadOnlyDictionary<string, object?> _meta;
        private readonly ICollection<string> _errors;

        public MetaReader(IReadOnlyDictionary<string, object?> meta, ICollection<string> errors)
        {
            _meta = meta;
            _errors = errors;
        }

        public string Text(string name)
        {
            if (_meta.TryGetValue(name, out var value) && value is string { Length: > 0 } text) return text;
            _errors.Add($"metadata '{name}' must be a text");
            return string.Empty;
        }

        public int Int(string name)
        {
            if (_meta.TryGetValue(name, out var value))
            {
                if (value is long whole && whole >= int.MinValue && whole <= int.MaxValue) return (int)whole;
                if (value is double d && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue) return (int)d;
            }
            _errors.Add($"metadata '{name}' must be a whole number");
            return 0;
        }

        public double Number(string name)
        {
            if (_meta.TryGetValue(name, out var value))
            {
                if (value is long whole) return whole;
                if (value is double d) return d;
            }
            _errors.Add($"metadata '{name}' must be a number");
            return 0;
        }
    }
}
