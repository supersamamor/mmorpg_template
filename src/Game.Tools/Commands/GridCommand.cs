using System.CommandLine;
using System.Text;
using Game.Content.Loading;

namespace Game.Tools.Commands;

/// <summary>
/// <c>grid from-ascii &lt;in.txt&gt; &lt;out.grid.json&gt;</c>: a stopgap that builds a logic grid from ASCII art until the
/// Godot exporter (T-208) writes real ones. Format: see <see cref="AsciiGridConverter"/> and content/README.md.
/// </summary>
internal static class GridCommand
{
    public static Command Create()
    {
        var input = new Argument<FileInfo>("in.txt") { Description = "ASCII map: header lines, a '---' line, then map rows" };
        var output = new Argument<FileInfo>("out.grid.json") { Description = "Grid file to write" };
        var fromAscii = new Command("from-ascii",
            "STOPGAP until the Godot map exporter (T-208): convert an ASCII map (. walkable, # blocks walking and LOS, ~ blocks walking only) " +
            "plus a header (map, spawn, warp, npc, zone lines) into a .grid.json") { input, output };
        fromAscii.SetAction(parse => Run(parse.GetValue(input)!, parse.GetValue(output)!));

        return new Command("grid", "Map logic grid tools") { fromAscii };
    }

    private static int Run(FileInfo input, FileInfo output)
    {
        try
        {
            var file = AsciiGridConverter.Convert(File.ReadAllText(input.FullName));
            File.WriteAllText(output.FullName, GridFileJson.Write(file), new UTF8Encoding(false));
            Console.WriteLine($"wrote {output.Name} ({file.Width}x{file.Height}, {file.Spawns.Count} spawns, {file.Warps.Count} warps, {file.Npcs.Count} npcs, {file.Zones.Count} zones)");
            return 0;
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{input.Name}: {e.Message}");
            return 1;
        }
    }
}
