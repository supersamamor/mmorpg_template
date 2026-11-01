using Game.Content.Loading;
using Game.Content.Registry;
using Game.Content.Validation;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Combat;
using Game.Server.World.Monsters;

namespace Game.Server.Hosting;

/// <summary>
/// Command line of <c>Game.Server</c>: <c>[content-dir] [--theme name] [--seed n] [--duration seconds] [--environment name]
/// [--net-&lt;name&gt; value ...]</c>. The <c>--net-</c> options are collected for <see cref="Game.Server.Net.GatewayOptions"/>.
/// </summary>
public sealed record ServerOptions(string ContentDir, string Theme, ulong? Seed, double? DurationSeconds)
{
    /// <summary>Usage text.</summary>
    public const string Usage =
        "usage: Game.Server [content-dir] [--theme <name>] [--seed <n>] [--duration <seconds>] [--environment <Development|Production>] [--net-<name> <value> ...]";

    /// <summary>The environment name that enables development mode (plain ws:// on loopback, the dev login).</summary>
    public const string DevelopmentName = "Development";

    private static readonly IReadOnlyList<string> NoArgs = [];

    /// <summary>
    /// <c>--environment</c>, or null to use <c>DOTNET_ENVIRONMENT</c> (then <c>Production</c>). <see cref="DevelopmentName"/> means
    /// development; anything else is treated as production.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>The <c>--net-</c> options and their values, in order, for <see cref="Game.Server.Net.GatewayOptions.Parse"/>.</summary>
    public IReadOnlyList<string> NetArgs { get; init; } = NoArgs;

    /// <summary>The environment name in effect: <see cref="Environment"/>, else <paramref name="environmentVariable"/>, else <c>Production</c>.</summary>
    public string EnvironmentName(string? environmentVariable) =>
        Environment ?? (string.IsNullOrWhiteSpace(environmentVariable) ? "Production" : environmentVariable);

    /// <summary>Parses the arguments; returns null with an error message when they are malformed.</summary>
    public static ServerOptions? Parse(string[] args, out string? error)
    {
        string? content = null;
        var theme = "default";
        ulong? seed = null;
        double? duration = null;
        string? environment = null;
        var net = new List<string>();
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
                    case "--environment": environment = Value(); break;
                    case var _ when arg.StartsWith(Game.Server.Net.GatewayOptions.ArgPrefix, StringComparison.Ordinal):
                        net.Add(arg);
                        net.Add(Value());
                        break;
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
        return new ServerOptions(content ?? DefaultContentDir(), theme, seed, duration)
        {
            Environment = environment,
            NetArgs = net.Count == 0 ? NoArgs : net,
        };
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
    /// Order: movement first, so later systems see this tick's positions. Then the monster spawner (fills and refills the map's spawn
    /// slots; new monsters appear in phase 3) and the monster AI, which decides from this tick's positions: its move intents apply in the
    /// next tick's movement, and its attack requests are events. Combat comes after the AI: it resolves the attack requests raised in the
    /// previous tick (events are dispatched in phase 4, so this tick's arrive for the next one) and this tick's player attacks against the
    /// positions movement just produced, and a death it causes is seen by the spawner and the AI in phase 4 of the same tick. The AOI does
    /// its work in phase 5 (outbound), after every system, so its place in the list only orders its intents; it is last.
    /// </summary>
    /// <remarks>Player progression lives in one in-memory store shared by every instance (persistence is T-702); pass it in to read it from
    /// elsewhere too (the gateway's own <c>exp</c> messages).</remarks>
    public static SystemPipeline Create(ContentDatabase content, IPlayerProgressionStore? progressions = null)
    {
        var balance = content.Balance;
        var monsters = MonsterCatalog.From(content);
        MovementSystem.Create(balance);            // fails now, at boot, if the movement limits are unusable
        MonsterAiSystem.Create(monsters, balance); // likewise for the AI timings
        AoiSystem.Create(balance);                 // and the AOI numbers
        var combat = CombatContent.From(content, monsters);   // and the combat, death and loot numbers
        progressions ??= new InMemoryProgressionStore();
        return new SystemPipeline()
            .Add(() => MovementSystem.Create(balance))
            .Add(() => new MonsterSpawnerSystem(monsters))
            .Add(() => MonsterAiSystem.Create(monsters, balance))
            .Add(() => new CombatSystem(combat, progressions))
            .Add(() => AoiSystem.Create(balance));
    }
}
