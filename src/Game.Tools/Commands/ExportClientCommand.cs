using System.CommandLine;
using Game.Content.ClientExport;
using Game.Content.Loading;

namespace Game.Tools.Commands;

/// <summary>
/// <c>export-client [out] [--content &lt;dir&gt;] [--check &lt;file&gt;]</c>: writes the client bundle (display data only, ARCHITECTURE §5.4) and
/// prints its content hash. <c>--check</c> writes nothing; it exits 1 when the file differs from what the current content produces.
/// </summary>
internal static class ExportClientCommand
{
    public const string DefaultOutput = "build/client-content.json";

    public static Command Create()
    {
        var outArgument = new Argument<FileInfo?>("out") { Description = $"Output file (default: {DefaultOutput})", Arity = ArgumentArity.ZeroOrOne };
        var contentOption = new Option<DirectoryInfo?>("--content") { Description = "Content directory (default: <repo>/content)" };
        var checkOption = new Option<FileInfo?>("--check") { Description = "Do not write; exit 1 if this bundle file is stale" };
        var command = new Command("export-client", "Write the client content bundle and its content hash") { outArgument, contentOption, checkOption };
        command.SetAction(parse => Run(parse.GetValue(outArgument), parse.GetValue(contentOption), parse.GetValue(checkOption)));
        return command;
    }

    private static int Run(FileInfo? output, DirectoryInfo? contentDir, FileInfo? check)
    {
        var content = contentDir?.FullName ?? Path.Combine(FindRepoRoot(), "content");
        var load = ContentLoader.Load(content);
        if (load.Database is null)
        {
            foreach (var error in load.Errors) Console.Error.WriteLine(error);
            Console.Error.WriteLine("Content has errors; run validate. Nothing exported.");
            return 1;
        }

        var file = ClientBundleExporter.Export(load.Database);
        if (check is not null)
        {
            if (!check.Exists)
            {
                Console.Error.WriteLine($"{check.FullName} does not exist; expected hash {file.Hash}.");
                return 1;
            }
            if (ClientBundleExporter.IsCurrent(load.Database, File.ReadAllBytes(check.FullName)))
            {
                Console.WriteLine($"up to date: {file.Hash}");
                return 0;
            }
            Console.Error.WriteLine($"{check.FullName} is stale; current hash is {file.Hash}.");
            return 1;
        }

        var target = output?.FullName ?? Path.Combine(FindRepoRoot(), DefaultOutput);
        var folder = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllBytes(target, file.FileBytes);
        Console.WriteLine($"wrote {target} ({file.FileBytes.Length} bytes)");
        Console.WriteLine($"content hash: {file.Hash}");
        return 0;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return dir.FullName;
        throw new InvalidOperationException("Game.sln not found above the current directory; run from the repo or pass paths.");
    }
}
