using System.CommandLine;
using Game.Content.Loading;
using Game.Content.Validation;
using Game.Tools.Validation;

namespace Game.Tools.Commands;

/// <summary>
/// <c>validate [--content &lt;dir&gt;] [--schemas &lt;dir&gt;] [--theme &lt;name&gt;] [--format text|json]</c>: loads the content, runs every
/// validation rule, checks every document against its JSON Schema (once the content loads) and prints the findings.
/// Exit code 0 when there are no errors (warnings allowed), 1 otherwise.
/// </summary>
internal static class ValidateCommand
{
    public static Command Create()
    {
        var contentOption = new Option<DirectoryInfo?>("--content") { Description = "Content directory (default: <repo>/content)" };
        var themeOption = new Option<string>("--theme") { Description = "Theme whose asset-manifest.json is checked", DefaultValueFactory = _ => "default" };
        var schemasOption = new Option<DirectoryInfo?>("--schemas") { Description = "Directory of *.schema.json files (default: schemas/ next to the content directory)" };
        var formatOption = new Option<string>("--format") { Description = "Output format: text or json", DefaultValueFactory = _ => "text" };
        formatOption.AcceptOnlyFromAmong("text", "json");

        var command = new Command("validate", "Validate all content and cross-references") { contentOption, schemasOption, themeOption, formatOption };
        command.SetAction(parse => Run(parse.GetValue(contentOption), parse.GetValue(schemasOption), parse.GetValue(themeOption)!, parse.GetValue(formatOption)!));
        return command;
    }

    private static int Run(DirectoryInfo? contentDir, DirectoryInfo? schemasDir, string theme, string format)
    {
        var content = contentDir?.FullName ?? Path.Combine(FindRepoRoot(), "content");
        var issues = Collect(content, schemasDir?.FullName ?? Path.GetFullPath(Path.Combine(content, "..", "schemas")), theme);
        Console.WriteLine(format == "json" ? ValidationReportFormatter.ToJson(issues) : ValidationReportFormatter.ToText(issues));
        return issues.Any(i => i.Severity == ValidationSeverity.Error) ? 1 : 0;
    }

    private static IReadOnlyList<ValidationIssue> Collect(string content, string schemas, string theme)
    {
        var load = ContentLoader.Load(content);
        if (load.Database is null) return ValidationReportFormatter.FromLoadErrors(load.Errors);

        // themes/ sits next to content/ in the repo layout.
        var manifest = AssetManifest.PathFor(Path.Combine(Path.GetFullPath(content), "..", "themes"), theme);
        IReadOnlySet<string> assetKeys;
        AudioManifest audio;
        try
        {
            assetKeys = AssetManifest.LoadKeys(manifest);
            audio = AudioManifest.Load(manifest, $"themes/{theme}/asset-manifest.json");
        }
        catch (InvalidDataException e)
        {
            return new[] { new ValidationIssue(ValidationSeverity.Error, "", 0, 0, "", ValidationCodes.ThemeManifest, e.Message) };
        }

        var issues = ContentValidator.Validate(load.Database, content, assetKeys, audio).Issues.ToList();
        issues.AddRange(SchemaCheck.Check(Path.GetFullPath(content), schemas));
        return issues;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return dir.FullName;
        throw new InvalidOperationException("Game.sln not found above the current directory; run from the repo or pass --content.");
    }
}
