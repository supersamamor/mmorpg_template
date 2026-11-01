using Game.Content.Loading;
using Game.Content.Registry;
using Game.Content.Validation;
using Game.Server.World;
using Game.Server.World.Aoi;

namespace Game.Server.Hosting;

/// <summary>Command line of <c>Game.Server</c>: <c>[content-dir] [--theme name] [--seed n] [--duration seconds]</c>.</summary>
public sealed record ServerOptions(string ContentDir, string Theme, ulong? Seed, double? DurationSeconds)
{
    /// <summary>Usage text.</summary>
    public const string Usage = "usage: Game.Server [content-dir] [--theme <name>] [--seed <n>] [--duration <seconds>]";

    /// <summary>Parses the arguments; returns null with an error message when they are malformed.</summary>
    public static ServerOptions? Parse(string[] args, out string? error)
    {
        string? content = null;
        var theme = "default";
        ulong? seed = null;
        double? duration = null;
        error = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Length ? args[++i] : throw new FormatException($"{arg} needs a value");
            try
            {
                switch (arg)
                {
                    case "--theme": theme = Value(); break;
                    case "--seed": seed = ulong.Parse(Value()); break;
                    case "--duration": duration = double.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal) || content is not null) throw new FormatException($"unexpected argument '{arg}'");
                        content = arg;
                        break;
                }
            }
            catch (FormatException e)
            {
                error = e.Message;
                return null;
            }
        }
        return new ServerOptions(content ?? DefaultContentDir(), theme, seed, duration);
    }

    /// <summary><c>content/</c> under the current directory, else under the repo root (the folder holding Game.sln).</summary>
    private static string DefaultContentDir()
    {
        var local = Path.Combine(Directory.GetCurrentDirectory(), "content");
        if (Directory.Exists(local)) return local;
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return Path.Combine(dir.FullName, "content");
        return local;
    }
}

/// <summary>Loads and validates content the same way <c>Game.Tools validate</c> does.</summary>
public static class ContentBoot
{
    /// <summary>
    /// Loads <paramref name="contentDir"/> and validates it against the theme's asset manifest (<c>themes/</c> next to
    /// <c>content/</c>). Returns the database when there are no errors; <paramref name="issues"/> also holds warnings.
    /// </summary>
    public static ContentDatabase? Load(string contentDir, string theme, out IReadOnlyList<ValidationIssue> issues)
    {
        var load = ContentLoader.Load(contentDir);
        if (load.Database is null)
        {
            issues = ValidationReportFormatter.FromLoadErrors(load.Errors);
            return null;
        }

        var manifest = AssetManifest.PathFor(Path.Combine(Path.GetFullPath(contentDir), "..", "themes"), theme);
        IReadOnlySet<string> assetKeys;
        try
        {
            assetKeys = AssetManifest.LoadKeys(manifest);
        }
        catch (InvalidDataException e)
        {
            issues = [new ValidationIssue(ValidationSeverity.Error, "", 0, 0, "", ValidationCodes.ThemeManifest, e.Message)];
            return null;
        }

        issues = ContentValidator.Validate(load.Database, contentDir, assetKeys).Issues;
        return issues.Any(i => i.Severity == ValidationSeverity.Error) ? null : load.Database;
    }
}

/// <summary>Builds the systems every map instance runs, in tick order, from loaded content.</summary>
public static class ServerPipeline
{
    /// <summary>
    /// Movement is first, so later systems (combat) see this tick's positions. The AOI does its work in phase 5 (outbound), after every
    /// system, so its place in the list only orders its intents; it is last.
    /// </summary>
    public static SystemPipeline Create(ContentDatabase content)
    {
        var balance = content.Balance;
        MovementSystem.Create(balance); // fails now, at boot, if the movement limits are unusable
        AoiSystem.Create(balance);      // likewise for the AOI numbers
        return new SystemPipeline()
            .Add(() => MovementSystem.Create(balance))
            .Add(() => AoiSystem.Create(balance));
    }
}
