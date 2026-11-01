using Game.Content.Loading;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Checks every map's logic grid against the rest of the content (ARCHITECTURE §5.1): referenced monsters, NPCs and
/// target maps exist, warps/targets/NPCs stand on walkable cells, spawn and zone rectangles fit the grid, and the
/// vocabulary terms are known. Issues name the grid file and a path like <c>warps[2].target_cell</c>.
/// </summary>
public sealed class MapGridRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "map-grid";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;
        foreach (var map in db.Maps.All)
        {
            var file = ContentLayout.GridPath(map.GridFile);
            if (!db.Grids.TryGetValue(map.Id, out var grid))
            {
                context.Error(map.Id, "grid_file", ValidationCodes.RefMissing, $"{map.Id} has no loaded logic grid ('{map.GridFile}')");
                continue;
            }
            if (map.Respawn is { } respawn)
                RequireWalkable(grid, new GridCell(respawn.X, respawn.Y), "respawn", "respawn", (path, code, message) => context.Error(map.Id, path, code, message));
            Check(context, db, file, grid);
        }
    }

    private static void Check(ValidationContext context, Registry.ContentDatabase db, string file, MapGrid grid)
    {
        void Error(string path, string code, string message) => context.ErrorInFile(file, path, code, message);

        if (grid.UnknownFlagCells > 0)
            Error("cells", ValidationCodes.GridUnknownFlags,
                $"{grid.UnknownFlagCells} cell(s) have bits outside CellFlags ({string.Join(", ", CellFlags.All)}); first at {grid.FirstUnknownFlagCell}");

        for (var i = 0; i < grid.Spawns.Count; i++)
        {
            var s = grid.Spawns[i];
            var p = $"spawns[{i}]";
            if (!db.Monsters.Contains(s.MonsterId))
                Error(p + ".monster_id", ValidationCodes.RefMissing, $"{grid.MapId} spawns unknown monster '{s.MonsterId}'");
            if (!RectInBounds(grid, s.Area))
                Error(p + ".area", ValidationCodes.GridRect, $"spawn area {s.Area} is empty or outside the {grid.Width}x{grid.Height} grid");
            else if (!AnyWalkable(grid, s.Area))
                Error(p + ".area", ValidationCodes.GridRect, $"spawn area {s.Area} has no walkable cell");
            if (s.Count < 1)
                Error(p + ".count", ValidationCodes.GridValueRange, $"spawn count must be at least 1 (got {s.Count})");
            if (!(s.RespawnSeconds >= 0))
                Error(p + ".respawn_seconds", ValidationCodes.GridValueRange, $"respawn_seconds must be 0 or more (got {s.RespawnSeconds})");
        }

        var warpCells = new Dictionary<(int, int), int>();
        for (var i = 0; i < grid.Warps.Count; i++)
        {
            var w = grid.Warps[i];
            var p = $"warps[{i}]";
            RequireWalkable(grid, w.Cell, p + ".cell", "warp", Error);
            if (warpCells.TryGetValue((w.Cell.X, w.Cell.Y), out var first))
                Error(p + ".cell", ValidationCodes.GridWarpDuplicate, $"warps[{first}] is already on cell {w.Cell}");
            else
                warpCells.Add((w.Cell.X, w.Cell.Y), i);

            if (!db.Maps.Contains(w.TargetMapId))
            {
                Error(p + ".target_map_id", ValidationCodes.RefMissing, $"warp targets unknown map '{w.TargetMapId}'");
            }
            else if (db.Grids.TryGetValue(w.TargetMapId, out var target))
            {
                RequireWalkable(target, w.TargetCell, p + ".target_cell", $"warp target on {w.TargetMapId}", Error);
            }
        }

        for (var i = 0; i < grid.Npcs.Count; i++)
        {
            var n = grid.Npcs[i];
            var p = $"npcs[{i}]";
            if (!db.Npcs.Contains(n.NpcId))
                Error(p + ".npc_id", ValidationCodes.RefMissing, $"{grid.MapId} places unknown npc '{n.NpcId}'");
            RequireWalkable(grid, n.Cell, p + ".cell", "npc", Error);
            if (!Directions.All.Contains(n.Facing))
                Error(p + ".facing", ValidationCodes.VocabUnknown, $"facing '{n.Facing}' is not a direction ({string.Join(", ", Directions.All)})");
        }

        for (var i = 0; i < grid.Zones.Count; i++)
        {
            var z = grid.Zones[i];
            var p = $"zones[{i}]";
            if (!ZoneKinds.All.Contains(z.Kind))
                Error(p + ".kind", ValidationCodes.VocabUnknown, $"zone kind '{z.Kind}' is not one of {string.Join(", ", ZoneKinds.All)}");
            if (!RectInBounds(grid, z.Area))
                Error(p + ".area", ValidationCodes.GridRect, $"zone area {z.Area} is empty or outside the {grid.Width}x{grid.Height} grid");
        }
    }

    private static void RequireWalkable(MapGrid grid, GridCell cell, string path, string what, Action<string, string, string> error)
    {
        if (!grid.InBounds(cell.X, cell.Y))
            error(path, ValidationCodes.GridCellBlocked, $"{what} cell {cell} is outside the {grid.Width}x{grid.Height} grid of {grid.MapId}");
        else if (!grid.IsWalkable(cell.X, cell.Y))
            error(path, ValidationCodes.GridCellBlocked, $"{what} cell {cell} is not walkable on {grid.MapId}");
    }

    private static bool RectInBounds(MapGrid grid, GridRect r) =>
        r.W >= 1 && r.H >= 1 && grid.InBounds(r.X, r.Y) && grid.InBounds(r.X + r.W - 1, r.Y + r.H - 1);

    private static bool AnyWalkable(MapGrid grid, GridRect r)
    {
        for (var y = r.Y; y < r.Y + r.H; y++)
            for (var x = r.X; x < r.X + r.W; x++)
                if (grid.IsWalkable(x, y)) return true;
        return false;
    }
}
