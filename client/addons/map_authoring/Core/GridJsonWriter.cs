using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Game.Shared.World;

namespace Game.Client.MapAuthoring;

/// <summary>
/// Writes a grid file as canonical text: the same text <c>GridFileJson.Write</c> produces in <c>Game.Content</c> (a test holds them together), which the client cannot reference.
/// By hand, so line endings and number formats do not depend on the runtime or the machine: LF, two-space indent, one final line break, UTF-8 without BOM.
/// </summary>
public static class GridJsonWriter
{
    /// <summary>The file's text.</summary>
    public static string Write(MapGridFile file)
    {
        var o = new StringBuilder();
        o.Append("{\n");
        o.Append("  \"format_version\": ").Append(Int(file.FormatVersion)).Append(",\n");
        o.Append("  \"map_id\": ").Append(Text(file.MapId)).Append(",\n");
        o.Append("  \"width\": ").Append(Int(file.Width)).Append(",\n");
        o.Append("  \"height\": ").Append(Int(file.Height)).Append(",\n");
        o.Append("  \"cells\": ").Append(Text(file.Cells)).Append(",\n");
        o.Append("  \"source_scene_hash\": ").Append(Text(file.SourceSceneHash)).Append(",\n");
        List(o, "spawns", file.Spawns, (s, b) =>
        {
            b.Append("      \"monster_id\": ").Append(Text(s.MonsterId)).Append(",\n");
            Rect(b, s.Area);
            b.Append(",\n      \"count\": ").Append(Int(s.Count)).Append(",\n");
            b.Append("      \"respawn_seconds\": ").Append(s.RespawnSeconds.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        });
        o.Append(",\n");
        List(o, "warps", file.Warps, (w, b) =>
        {
            Cell(b, "cell", w.Cell);
            b.Append(",\n      \"target_map_id\": ").Append(Text(w.TargetMapId)).Append(",\n");
            Cell(b, "target_cell", w.TargetCell);
            b.Append('\n');
        });
        o.Append(",\n");
        List(o, "npcs", file.Npcs, (n, b) =>
        {
            b.Append("      \"npc_id\": ").Append(Text(n.NpcId)).Append(",\n");
            Cell(b, "cell", n.Cell);
            b.Append(",\n      \"facing\": ").Append(Text(n.Facing)).Append('\n');
        });
        o.Append(",\n");
        List(o, "zones", file.Zones, (z, b) =>
        {
            b.Append("      \"kind\": ").Append(Text(z.Kind)).Append(",\n");
            Rect(b, z.Area);
            b.Append('\n');
        });
        o.Append("\n}\n");
        return o.ToString();
    }

    /// <summary>The file's bytes.</summary>
    public static byte[] WriteBytes(MapGridFile file) => new UTF8Encoding(false).GetBytes(Write(file));

    private static void List<T>(StringBuilder o, string name, IReadOnlyList<T> items, System.Action<T, StringBuilder> writeItem)
    {
        o.Append("  \"").Append(name).Append("\": [");
        if (items.Count == 0)
        {
            o.Append(']');
            return;
        }
        o.Append('\n');
        for (var i = 0; i < items.Count; i++)
        {
            o.Append("    {\n");
            writeItem(items[i], o);
            o.Append("    }").Append(i + 1 < items.Count ? ",\n" : "\n");
        }
        o.Append("  ]");
    }

    private static void Rect(StringBuilder o, GridRect r)
    {
        o.Append("      \"area\": {\n");
        o.Append("        \"x\": ").Append(Int(r.X)).Append(",\n");
        o.Append("        \"y\": ").Append(Int(r.Y)).Append(",\n");
        o.Append("        \"w\": ").Append(Int(r.W)).Append(",\n");
        o.Append("        \"h\": ").Append(Int(r.H)).Append("\n      }");
    }

    private static void Cell(StringBuilder o, string name, GridCell c)
    {
        o.Append("      \"").Append(name).Append("\": {\n");
        o.Append("        \"x\": ").Append(Int(c.X)).Append(",\n");
        o.Append("        \"y\": ").Append(Int(c.Y)).Append("\n      }");
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(string value) => "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
}
