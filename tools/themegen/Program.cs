using Game.ThemeGen;

// T-207: rebuilds the generated placeholder theme pack under themes/ from themes/default/asset-manifest.json.
//   dotnet run --project tools/themegen                  write the pack (and delete files it no longer produces)
//   dotnet run --project tools/themegen -- --check       write nothing; exit 1 if the files on disk differ from what the generator produces
//   dotnet run --project tools/themegen -- --themes dir  use another themes directory
//   dotnet run --project tools/themegen -- --content dir  read item weapon types (for the placeholder weapon shapes) from another content directory
//   dotnet run --project tools/themegen -- --textured name [--other key,key]   T-711: write themes/<name>, a textured copy of default's character parts for tests and
//                                                         measuring (one atlas image in every file; the keys after --other get another image). Never committed.
var check = args.Contains("--check");
var themesAt = Array.IndexOf(args, "--themes");
var themes = themesAt >= 0 && themesAt + 1 < args.Length ? args[themesAt + 1] : Path.Combine(RepoRoot(), "themes");

var texturedAt = Array.IndexOf(args, "--textured");
if (texturedAt >= 0 && texturedAt + 1 < args.Length)
{
    var otherAt = Array.IndexOf(args, "--other");
    var other = otherAt >= 0 && otherAt + 1 < args.Length ? args[otherAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
    Console.WriteLine($"textured theme '{args[texturedAt + 1]}': {TexturedTheme.Write(themes, args[texturedAt + 1], other)} file(s) written");
    return 0;
}

var contentAt = Array.IndexOf(args, "--content");
var content = contentAt >= 0 && contentAt + 1 < args.Length ? args[contentAt + 1] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(themes))!, "content");
var files = Pack.Generate(themes, content);
var problems = 0;
foreach (var (relative, bytes) in files)
{
    var path = Path.Combine(themes, relative.Replace('/', Path.DirectorySeparatorChar));
    if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
    problems++;
    if (check)
    {
        Console.WriteLine("differs: " + relative);
        continue;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
    Console.WriteLine("wrote " + relative);
}

foreach (var theme in new[] { Pack.DefaultTheme, Pack.TestTheme })
{
    foreach (var folder in Pack.OwnedFolders)
    {
        var directory = Path.Combine(themes, theme, folder);
        if (!Directory.Exists(directory)) continue;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(themes, file).Replace('\\', '/');
            if (files.ContainsKey(relative) || relative.EndsWith(".import", StringComparison.Ordinal)) continue; // .import: the engine's, gitignored
            problems++;
            if (check)
            {
                Console.WriteLine("stale: " + relative);
            }
            else
            {
                File.Delete(file);
                Console.WriteLine("deleted " + relative);
            }
        }
    }
}

Console.WriteLine(problems == 0 ? $"pack is up to date ({files.Count} files)" : check ? $"{problems} difference(s)" : $"{problems} change(s)");
return check && problems > 0 ? 1 : 0;

static string RepoRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Game.sln"))) directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("Run from inside the repository, or pass --themes.");
}
