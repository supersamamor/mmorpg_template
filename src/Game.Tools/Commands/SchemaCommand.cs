using System.CommandLine;
using System.Text;
using Game.Content.Schema;
using Game.Shared.Definitions;

namespace Game.Tools.Commands;

/// <summary><c>schema</c>: writes one JSON Schema per definition type into <c>/schemas</c>.</summary>
internal static class SchemaCommand
{
    public static Command Create()
    {
        var outOption = new Option<DirectoryInfo?>("--out") { Description = "Output directory (default: <repo>/schemas)" };
        var command = new Command("schema", "Generate JSON Schemas from the C# definitions") { outOption };
        command.SetAction(parse => Run(parse.GetValue(outOption)));
        return command;
    }

    private static int Run(DirectoryInfo? outDir)
    {
        var target = outDir?.FullName ?? Path.Combine(FindRepoRoot(), "schemas");
        Directory.CreateDirectory(target);

        var generator = new SchemaGenerator(XmlDocReader.ForAssembly(typeof(ItemDef).Assembly));
        foreach (var (fileName, json) in generator.GenerateAll())
        {
            File.WriteAllText(Path.Combine(target, fileName), json, new UTF8Encoding(false));
            Console.WriteLine($"wrote {fileName}");
        }

        return 0;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return dir.FullName;
        throw new InvalidOperationException("Game.sln not found above the current directory; run from the repo or pass --out.");
    }
}
