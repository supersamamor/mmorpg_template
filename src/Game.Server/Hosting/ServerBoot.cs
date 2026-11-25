using Game.Content.Registry;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Autopilot;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Monsters;
using Game.Server.World.Quests;

namespace Game.Server.Hosting;

/// <summary>
/// Command line of <c>Game.Server</c>: <c>[content-dir] [--theme name] [--seed n] [--duration seconds] [--environment name]
/// [--net-&lt;name&gt; value ...]</c>. The <c>--net-</c> options are collected for <see cref="Game.Server.Net.GatewayOptions"/>.
/// </summary>
public sealed record ServerOptions(string ContentDir, string Theme, ulong? Seed, double? DurationSeconds)
{
    /// <summary>Usage text.</summary>
    public const string Usage =
        "usage: Game.Server [content-dir] [--theme <name>] [--seed <n>] [--duration <seconds>] [--environment <Development|Production>] [--legacy-timer] [--migrate] [--net-<name> <value> ...]; the database connection string is the environment variable GAME_DB_CONNECTION";

    /// <summary>The environment name that enables development mode (plain ws:// on loopback, the dev login).</summary>
    public const string DevelopmentName = "Development";

    private static readonly IReadOnlyList<string> NoArgs = [];

    /// <summary>
    /// <c>--environment</c>, or null to use <c>DOTNET_ENVIRONMENT</c> (then <c>Production</c>). <see cref="DevelopmentName"/> means
    /// development; anything else is treated as production.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary><c>--legacy-timer</c>: keep the ordinary wait on Windows too, to measure what the high-resolution timer buys.</summary>
    public bool LegacyTimer { get; init; }

    /// <summary><c>--migrate</c>: apply the database migrations and exit. Development applies them at start; everywhere else this is how it is done (T-701).</summary>
    public bool Migrate { get; init; }

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
        var legacyTimer = false;
        var migrate = false;
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
                    case "--legacy-timer": legacyTimer = true; break;
                    case "--migrate": migrate = true; break;
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
            LegacyTimer = legacyTimer,
            Migrate = migrate,
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

/// <summary>Builds the systems every map instance runs, in tick order, from loaded content.</summary>
public static class ServerPipeline
{
    /// <summary>
    /// Order: movement first, so later systems see this tick's positions. Then the monster spawner (fills and refills the map's spawn
    /// slots; new monsters appear in phase 3) and the monster AI, which decides from this tick's positions: its move intents apply in the
    /// next tick's movement, and its attack requests are events. Combat comes after the AI: it resolves the attack requests raised in the
    /// previous tick (events are dispatched in phase 4, so this tick's arrive for the next one) and this tick's player attacks against the
    /// positions movement just produced, and a death it causes is seen by the spawner and the AI in phase 4 of the same tick. The dialogue system (NPCs and their talk) runs after the AI and before combat, and the inventory comes
    /// after combat: a drop combat makes this tick is added in phase 3 and can be picked up from the next, and a weapon put on counts from the next
    /// swing. The quest system (T-602) comes after the inventory: it reacts to the previous tick's events (kills, uses, talks, bag changes) and its turn-ins go to the
    /// inventory and combat as events, applied on their next tick. The autopilot (T-604) comes after all of them: it looks at what this tick made of
    /// the world and queues intents, which the systems get in the next tick like a player's. The AOI does
    /// its work in phase 5 (outbound), after every system, so its place in the list only orders its intents; it is last.
    /// </summary>
    /// <remarks>Player progression and inventories live in in-memory stores shared by every instance (persistence is T-702); pass them in to read
    /// them from elsewhere too (the gateway's own <c>exp</c> and <c>inventory</c> messages). <paramref name="clock"/> times item cooldowns.</remarks>
    public static SystemPipeline Create(ContentDatabase content, IPlayerProgressionStore? progressions = null, IInventoryStore? inventories = null,
        Game.Shared.Time.IClock? clock = null, ISavePointStore? savePoints = null)
    {
        var balance = content.Balance;
        var monsters = MonsterCatalog.From(content);
        MovementSystem.Create(balance);            // fails now, at boot, if the movement limits are unusable
        MonsterAiSystem.Create(monsters, balance); // likewise for the AI timings
        AoiSystem.Create(balance);                 // and the AOI numbers
        var combat = CombatContent.From(content, monsters);   // and the combat, death and loot numbers
        var items = InventoryContent.From(content);            // and the inventory numbers and item modifiers
        progressions ??= new InMemoryProgressionStore();
        inventories ??= new InMemoryInventoryStore();
        clock ??= new SystemClock();
        savePoints ??= new InMemorySavePointStore();
        var dialogue = DialogueContent.From(content);   // and the NPCs (a placement of an unknown NPC fails at the first tick)
        var quests = QuestContent.From(content);         // and the quest numbers
        var shops = ShopContent.From(content);           // and the shops with their prices resolved
        var autopilot = AutopilotContent.From(content);  // and the autopilot numbers, with every journey it may have to make worked out
        return new SystemPipeline()
            .Add(() => MovementSystem.Create(balance))
            .Add(() => new MonsterSpawnerSystem(monsters, balance.Death.CorpseMs))
            .Add(() => MonsterAiSystem.Create(monsters, balance))
            .Add(() => new DialogueSystem(dialogue, savePoints, progressions, inventories))
            .Add(() => new CombatSystem(combat, progressions, savePoints))
            .Add(() => new InventorySystem(items, inventories, clock, progressions, shops))
            .Add(() => new QuestSystem(quests, progressions, inventories))
            .Add(() => new AutopilotSystem(autopilot, progressions, clock))
            .Add(() => AoiSystem.Create(balance));
    }
}
