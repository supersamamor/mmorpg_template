using System.Globalization;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Content.Loading;

/// <summary>
/// Stopgap until the Godot map_authoring exporter exists (T-208): turns an ASCII map with a small header into a
/// <see cref="MapGridFile"/>. Content ids are not checked here; the validator does that after loading.
/// <code>
/// // comment
/// map: map.starter_meadow
/// spawn: monster.gel_slime  x y w h  count  respawn_seconds
/// warp: x y  map.target  target_x target_y
/// npc: npc.guide  x y  facing
/// zone: safe  x y w h
/// ---
/// ####
/// #..#      . walkable    # blocks walking and line of sight    ~ blocks walking only (water)
/// ####
/// </code>
/// Header lines come before the first <c>---</c> line, the map rows after it. All rows must have the same length.
/// </summary>
public static class AsciiGridConverter
{
    /// <summary>The map characters and the flags each one sets (by <see cref="CellFlags"/> term).</summary>
    public static readonly IReadOnlyDictionary<char, string[]> Legend = new Dictionary<char, string[]>
    {
        ['.'] = new[] { CellFlags.Walkable },
        ['#'] = new[] { CellFlags.BlocksLos },
        ['~'] = Array.Empty<string>(),
    };

    /// <summary>Converts the text of an ASCII map file. Throws <see cref="FormatException"/> naming the line on any problem.</summary>
    public static MapGridFile Convert(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var separator = Array.IndexOf(lines, "---");
        if (separator < 0) throw new FormatException("Missing the '---' line between the header and the map rows.");

        string? mapId = null;
        var sceneHash = "";
        var spawns = new List<GridSpawn>();
        var warps = new List<GridWarp>();
        var npcs = new List<GridNpc>();
        var zones = new List<GridZone>();

        for (var i = 0; i < separator; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) throw Error(i, $"expected 'key: value', got '{line}'");
            var key = line.Substring(0, colon).Trim();
            var args = line.Substring(colon + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            switch (key)
            {
                case "map": mapId = Args(i, key, args, 1)[0]; break;
                case "scene_hash": sceneHash = args.Length == 0 ? "" : Args(i, key, args, 1)[0]; break;
                case MapObjectTypes.Spawn:
                    Args(i, key, args, 7);
                    spawns.Add(new GridSpawn
                    {
                        MonsterId = args[0], Area = new GridRect(Int(i, args[1]), Int(i, args[2]), Int(i, args[3]), Int(i, args[4])),
                        Count = Int(i, args[5]), RespawnSeconds = Double(i, args[6]),
                    });
                    break;
                case MapObjectTypes.Warp:
                    Args(i, key, args, 5);
                    warps.Add(new GridWarp
                    {
                        Cell = new GridCell(Int(i, args[0]), Int(i, args[1])), TargetMapId = args[2],
                        TargetCell = new GridCell(Int(i, args[3]), Int(i, args[4])),
                    });
                    break;
                case MapObjectTypes.Npc:
                    Args(i, key, args, 4);
                    npcs.Add(new GridNpc { NpcId = args[0], Cell = new GridCell(Int(i, args[1]), Int(i, args[2])), Facing = args[3] });
                    break;
                case MapObjectTypes.Zone:
                    Args(i, key, args, 5);
                    zones.Add(new GridZone { Kind = args[0], Area = new GridRect(Int(i, args[1]), Int(i, args[2]), Int(i, args[3]), Int(i, args[4])) });
                    break;
                default: throw Error(i, $"unknown header key '{key}'");
            }
        }
        if (mapId == null) throw new FormatException("Missing the 'map: <map id>' header line.");

        var rows = lines.Skip(separator + 1).Select(l => l.TrimEnd()).ToList();
        while (rows.Count > 0 && rows[rows.Count - 1].Length == 0) rows.RemoveAt(rows.Count - 1);
        if (rows.Count == 0) throw new FormatException("No map rows after the '---' line.");
        var width = rows[0].Length;
        var flags = new byte[width * rows.Count];
        for (var y = 0; y < rows.Count; y++)
        {
            var lineNo = separator + 1 + y;
            if (rows[y].Length != width) throw Error(lineNo, $"row has {rows[y].Length} characters, expected {width} like the first row");
            for (var x = 0; x < width; x++)
            {
                if (!Legend.TryGetValue(rows[y][x], out var set))
                    throw Error(lineNo, $"unknown map character '{rows[y][x]}' at column {x + 1} (use {string.Join(" ", Legend.Keys)})");
                byte b = 0;
                foreach (var flag in set) b |= CellFlagBits.Mask(flag);
                flags[y * width + x] = b;
            }
        }

        return new MapGridFile
        {
            MapId = mapId, Width = width, Height = rows.Count, Cells = GridFileJson.EncodeCells(flags), SourceSceneHash = sceneHash,
            Spawns = spawns, Warps = warps, Npcs = npcs, Zones = zones,
        };
    }

    private static string[] Args(int line, string key, string[] args, int count) =>
        args.Length == count ? args : throw Error(line, $"'{key}' takes {count} value(s), got {args.Length}");

    private static int Int(int line, string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw Error(line, $"'{s}' is not an integer");

    private static double Double(int line, string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw Error(line, $"'{s}' is not a number");

    private static FormatException Error(int zeroBasedLine, string message) => new($"line {zeroBasedLine + 1}: {message}");
}
