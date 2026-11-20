using Game.ThemeImport;

// T-224: imports an art pack into a theme folder from a mapping file (docs/THEMES.md "Import mapping"), then checks it against the theme contract.
//   dotnet run --project tools/themeimport -- --mapping my-theme.yaml                  write themes/<theme>/ and report
//   dotnet run --project tools/themeimport -- --mapping my-theme.yaml --check          write nothing; exit 1 if a file would change
//   --pack <dir>      the pack folder (default: the mapping's 'pack', relative to the mapping file)
//   --themes <dir>    the themes folder (default: <repo>/themes); content is read from <themes>/../content unless --content says otherwise
// Exit code: 0 when the theme meets the contract (warnings allowed) and, with --check, nothing would change; 1 otherwise; 2 for a mapping that cannot be used.
string? Value(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

var mapping = Value("--mapping");
if (mapping is null)
{
    Console.Error.WriteLine("usage: themeimport --mapping <file.yaml> [--pack <dir>] [--themes <dir>] [--content <dir>] [--check]");
    return 2;
}
var themes = Path.GetFullPath(Value("--themes") ?? Path.Combine(RepoRoot(), "themes"));
var content = Path.GetFullPath(Value("--content") ?? Path.Combine(themes, "..", "content"));
var check = args.Contains("--check");
try
{
    var result = Importer.Run(new ImportOptions(Path.GetFullPath(mapping), themes, content, Value("--pack") is { } pack ? Path.GetFullPath(pack) : null, check));
    Console.WriteLine(result.Report);
    return result.Failed || (check && result.Changed) ? 1 : 0;
}
catch (MappingException e)
{
    Console.Error.WriteLine("themeimport: " + e.Message);
    return 2;
}

static string RepoRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Game.sln"))) directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("Run from inside the repository, or pass --themes.");
}
