using System.Diagnostics;
using Game.Content.Registry;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Autopilot;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Monsters;
using Game.Server.World.Quests;
using Game.Shared.Quests;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Server;
using Game.Tests.World;
using Xunit.Abstractions;

namespace Game.Tests.Autopilot;

/// <summary>
/// T-604 cost: the autopilot system's share of a tick on the sample meadow, with 1 and with 100 runs (a kill quest without end), the rest of the pipeline running for real. Logs
/// the wall time per tick and per run; asserts generous bounds, that searches stay within the per-tick budget, and that waiting runs allocate next to nothing.
/// </summary>
[Collection(TimingCollection.Name)]
public class AutopilotBudgetTests(ITestOutputHelper output)
{
    private sealed class Timed(AutopilotSystem inner) : ITickSystem
    {
        public readonly List<double> Micros = new();
        public long Bytes;
        public long MaxSearches;
        public bool Measure;

        public string Name => inner.Name;

        public IReadOnlyCollection<Type> IntentTypes => inner.IntentTypes;

        public void Tick(TickContext context)
        {
            var searches = inner.Searches;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            inner.Tick(context);
            var micros = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            if (!Measure) return;
            Bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            Micros.Add(micros);
            MaxSearches = Math.Max(MaxSearches, inner.Searches - searches);
        }
    }

    private const string Meadow = "map.starter_meadow";
    private const int Warmup = 400;
    private const int Measured = 1500;

    /// <param name="runs">Players on autopilot.</param>
    /// <param name="monster">What they hunt.</param>
    /// <param name="start">Where they start.</param>
    /// <param name="content">The content.</param>
    /// <param name="keepAt">Put the players back there every tick, so they never get nearer.</param>
    /// <param name="battle">An auto-battle filter (T-811): the players fight around <paramref name="start"/> instead of playing the quest.</param>
    private (Timed Timed, MapInstance Instance) Run(int runs, string monster, GridCell start, ContentDatabase content, GridCell? keepAt = null, string? battle = null)
    {
        var quest = AutopilotContentKit.Quest("quest.endless", new Game.Shared.Definitions.KillObjective { MonsterId = monster, Count = 1_000_000 });
        var db = new ContentDatabase(content.Jobs, content.Skills, content.Items, content.Monsters, content.Statuses, new Registry<Game.Shared.Definitions.QuestDef>([quest]), content.Npcs,
            content.Dialogue, content.Drops, content.Maps, content.ExpTables, content.Formulas, content.Elements, content.Balance, content.Appearance, grids: content.Grids, refine: content.Refine);
        var progressions = new InMemoryProgressionStore();
        var inventories = new InMemoryInventoryStore();
        var savePoints = new InMemorySavePointStore();
        var clock = new ManualClock();
        var monsters = MonsterCatalog.From(db);
        var autopilot = AutopilotContent.From(db);
        Timed? timed = null;
        var pipeline = new SystemPipeline()
            .Add(() => MovementSystem.Create(db.Balance))
            .Add(() => new MonsterSpawnerSystem(monsters, db.Balance.Death.CorpseMs))
            .Add(() => MonsterAiSystem.Create(monsters, db.Balance))
            .Add(() => new DialogueSystem(DialogueContent.From(db), savePoints, progressions, inventories))
            .Add(() => new CombatSystem(CombatContent.From(db, monsters), progressions, savePoints))
            .Add(() => new InventorySystem(InventoryContent.From(db), inventories, clock, progressions))
            .Add(() => new QuestSystem(QuestContent.From(db), progressions, inventories))
            .Add(() => timed = new Timed(new AutopilotSystem(autopilot, progressions, clock)));
        var world = new GameWorld([db.GetGrid(Meadow)], pipeline, 604, clock, new ListLog());
        var instance = world.GetInstance(Meadow);
        var fresh = new NewCharacters(db);
        var players = Enumerable.Range(0, runs).Select(i => world.Spawn(EntityKind.Player, Meadow, start, Directions.S, e =>
        {
            e.Stats = fresh.CreateStats();
            e.Stats.Modifiers.Add("test", "max_hp", Game.Shared.Definitions.ModifierOp.Flat, 1_000_000);
            e.Stats.Modifiers.Add("test", "hit", Game.Shared.Definitions.ModifierOp.Flat, 5000);
            e.DefinitionId = fresh.JobId;
        })).ToList();
        instance.Tick();
        instance.Tick();
        foreach (var player in players)
        {
            progressions.TryGet(player, out var state).ShouldBeTrue();
            state.ActiveQuests[quest.Id] = new QuestProgress(quest.Id, 1);
            world.TryEnqueue(battle is null ? new StartAutopilot(player, [quest.Id], []) : new StartAutopilot(player, [], [], battle));
        }
        for (var i = 0; i < Warmup; i++) instance.Tick();
        timed!.Measure = true;
        for (var i = 0; i < Measured; i++)
        {
            instance.Tick();
            if (keepAt is { } cell)
                foreach (var player in instance.Players) player.Cell = cell;
        }
        instance.Players.Count(p => p.Autopilot is { Active: true }).ShouldBe(runs);
        return (timed, instance);
    }

    private double Report(string what, Timed timed, int runs)
    {
        var sorted = timed.Micros.Order().ToList();
        var mean = sorted.Average();
        output.WriteLine($"{what}, {runs} run(s): mean {mean:0.00} us per tick ({mean / runs:0.000} us per run), p50 {sorted[sorted.Count / 2]:0.00}, p99 {sorted[sorted.Count * 99 / 100]:0.00}, " +
                         $"max {sorted[^1]:0.00} us; {timed.Bytes / (double)timed.Micros.Count:0} bytes per tick; at most {timed.MaxSearches} searches in a tick");
        return mean;
    }

    [Fact]
    public void One_run_and_a_hundred_hunting()
    {
        var content = TestGrids.SampleContent;
        var (one, _) = Run(1, "monster.gel_slime", new GridCell(12, 12), content);
        Report("hunting", one, 1).ShouldBeLessThan(500);
        var (hundred, instance) = Run(100, "monster.gel_slime", new GridCell(12, 12), content);
        Report("hunting", hundred, 100).ShouldBeLessThan(5_000);   // a twentieth of the 100 ms tick, on any machine
        hundred.MaxSearches.ShouldBeLessThanOrEqualTo(content.Balance.Autopilot.SearchesPerTick);
        instance.Counters.SystemErrors.ShouldBe(0);
    }

    [Fact]
    public void A_hundred_that_wait_or_cannot_reach_anything()
    {
        // Nothing of the kind is alive: every thought looks at every entity and finds nobody.
        var content = TestGrids.SampleContent;
        var grid = content.GetGrid(Meadow);
        ContentDatabase Placed(MapGrid changed, Game.Shared.Definitions.BalanceDef? balance = null) => new(content.Jobs, content.Skills, content.Items, content.Monsters, content.Statuses, content.Quests, content.Npcs, content.Dialogue, content.Drops,
            content.Maps, content.ExpTables, content.Formulas, content.Elements, balance ?? content.Balance, content.Appearance,
            grids: content.Grids.ToDictionary(p => p.Key, p => p.Key == Meadow ? changed : p.Value), refine: content.Refine);
        MapGrid With(byte[] cells, params GridSpawn[] spawns) => MapGrid.Create(new MapGridFile
        {
            MapId = Meadow, Width = grid.Width, Height = grid.Height, Cells = Convert.ToBase64String(cells), Spawns = spawns, Npcs = grid.Npcs, Zones = grid.Zones,
        });
        var cells = new byte[grid.Width * grid.Height];
        for (var i = 0; i < cells.Length; i++) cells[i] = grid.IsWalkable(i % grid.Width, i / grid.Width) ? CellFlagBits.Walkable : (byte)0;

        var (waiting, _) = Run(100, "monster.gel_slime", new GridCell(18, 18), Placed(With(cells, new GridSpawn { MonsterId = "monster.gel_slime", Area = new GridRect(16, 16, 10, 8), Count = 0 })));
        Report("waiting", waiting, 100).ShouldBeLessThan(2_000);
        (waiting.Bytes / (double)waiting.Micros.Count).ShouldBeLessThan(64);   // a hundred runs together; what is left are the runtime's own one-offs (tiered JIT)

        // The only one alive stands behind walls: ruled out without a search.
        var walled = (byte[])cells.Clone();
        foreach (var (x, y) in from x in Enumerable.Range(39, 5) from y in Enumerable.Range(25, 5) where x is 39 or 43 || y is 25 or 29 select (x, y)) walled[y * grid.Width + x] = 0;
        var (blocked, _) = Run(100, "monster.gel_slime", new GridCell(18, 18), Placed(With(walled,
            new GridSpawn { MonsterId = "monster.gel_slime", Area = new GridRect(16, 16, 10, 8), Count = 0 }, new GridSpawn { MonsterId = "monster.gel_slime", Area = new GridRect(41, 27, 1, 1), Count = 1 })));
        Report("walled off", blocked, 100).ShouldBeLessThan(2_000);
        blocked.MaxSearches.ShouldBe(0);

        // The worst a search can be: the only one alive is near and connected, but the walk around a long wall is longer than a path may be, so every search covers its whole side and fails.
        var fence = (byte[])cells.Clone();
        for (var y = 0; y < grid.Height - 2; y++) fence[y * grid.Width + 27] = 0;
        var (far, _) = Run(100, "monster.gel_slime", new GridCell(24, 18), Placed(With(fence,
            new GridSpawn { MonsterId = "monster.gel_slime", Area = new GridRect(16, 16, 10, 8), Count = 0 }, new GridSpawn { MonsterId = "monster.gel_slime", Area = new GridRect(33, 14, 1, 1), Count = 1 }),
            content.Balance with { Movement = content.Balance.Movement with { MaxPathLength = 20 }, Autopilot = content.Balance.Autopilot with { LegCells = 12, NoProgressMs = int.MaxValue } }), keepAt: new GridCell(24, 18));
        Report("around a wall", far, 100).ShouldBeLessThan(20_000);
        far.MaxSearches.ShouldBe(content.Balance.Autopilot.SearchesPerTick);
    }

    [Fact]
    public void One_and_a_hundred_auto_battle_runs_in_a_crowd()
    {
        // T-811: a hundred characters on one spot of the slimes' meadow, all fighting around the same anchor: most of them wait for what the others killed to respawn.
        var content = TestGrids.SampleContent;
        var (one, _) = Run(1, "monster.gel_slime", new GridCell(20, 20), content, battle: AutoBattleFilters.Nearby);
        Report("auto-battle", one, 1).ShouldBeLessThan(500);
        var (hundred, instance) = Run(100, "monster.gel_slime", new GridCell(20, 20), content, battle: AutoBattleFilters.Nearby);
        Report("auto-battle", hundred, 100).ShouldBeLessThan(5_000);
        hundred.MaxSearches.ShouldBeLessThanOrEqualTo(content.Balance.Autopilot.SearchesPerTick);
        instance.Counters.SystemErrors.ShouldBe(0);
        instance.Players.ShouldAllBe(p => GridDistance.Chebyshev(p.Cell, new GridCell(20, 20)) <= content.Balance.AutoBattle.RadiusCells + content.Balance.AutoBattle.RetaliateExtraCells + 1);
    }

    [Fact]
    public void Working_the_journeys_out_at_boot_is_quick()
    {
        var content = TestGrids.SampleContent;   // loaded before the watch starts
        var watch = Stopwatch.StartNew();
        AutopilotContent.From(content);
        output.WriteLine($"autopilot content of the sample: {watch.Elapsed.TotalMilliseconds:0.0} ms");
        watch.Elapsed.TotalSeconds.ShouldBeLessThan(5);
    }
}
