using Game.Content.Simulation;
using Game.Server.Events;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Monsters;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>Test monsters, balance and grids for the combat loop (T-302).</summary>
public static class TestCombat
{
    public const string MapId = "map.test";

    private static readonly Lazy<BalanceSimulator> Simulator = new(() => new BalanceSimulator(TestGrids.SampleContent));

    /// <summary>
    /// The sample balance with test numbers: range 1, re-path every 300 ms, respawn with 50% HP at (1, 1) of the test map, a 10% death
    /// penalty, 3 s loot priority, 10 s item lifetime, spread radius 1.
    /// </summary>
    public static BalanceDef Balance(int penaltyPercent = 10, int spreadRadius = 1, int priorityMs = 3000, int lifetimeMs = 10000)
    {
        var sample = TestGrids.SampleContent.Balance;
        return sample with
        {
            Combat = sample.Combat with { PlayerAttackRange = 1, PlayerRepathIntervalMs = 300 },
            Death = new DeathBalanceDef { RespawnHpPercent = 50, ExpPenaltyPercent = penaltyPercent },
            Loot = new LootBalanceDef { PriorityMs = priorityMs, LifetimeMs = lifetimeMs, SpreadRadius = spreadRadius },
            NewCharacter = sample.NewCharacter with { StartMapId = MapId, StartX = 1, StartY = 1 },
        };
    }

    /// <summary>A monster with fixed combat stats as overrides, so tests do not depend on the derived formulas.</summary>
    public static MonsterDef Monster(string id, int maxHp = 50, int atk = 10, int def = 0, int hit = 1000, int flee = 0, double aspd = 1,
        double speed = 4, long baseExp = 10, long jobExp = 6, string[]? drops = null, bool aggressive = false, int aggroRange = 0,
        int attackRange = 1, string element = Elements.Neutral) => new()
    {
        Id = id,
        Name = id,
        Level = 1,
        Race = MonsterRaces.Formless,
        Size = MonsterSizes.Small,
        Element = element,
        Stats = new Dictionary<string, double>
        {
            [PrimaryStats.Agi] = 1, [DerivedStats.MaxHp] = maxHp, [DerivedStats.Atk] = atk, [DerivedStats.Def] = def, [DerivedStats.Hit] = hit,
            [DerivedStats.Flee] = flee, [DerivedStats.Aspd] = aspd, [DerivedStats.MoveSpeed] = speed, [DerivedStats.Crit] = 0,
        },
        AttackRange = attackRange,
        BaseExp = baseExp,
        JobExp = jobExp,
        DropTableIds = drops ?? [],
        Ai = new MonsterAiDef { Aggressive = aggressive, AggroRange = aggroRange, ChaseRange = 30 },
    };

    /// <summary>A drop table of the given entries.</summary>
    public static DropTableDef Drops(string id, params DropEntryDef[] entries) => new() { Id = id, Entries = entries };

    /// <summary>
    /// A new character's sheet (base/job level 1, start stats, start job bonuses), the way the gateway builds it, with test overrides for
    /// aspd and move speed (null keeps the formula) and optional extra overrides.
    /// </summary>
    public static StatSheet PlayerSheet(double? aspd = 2, double? speed = 5, int? flee = null, int? maxHp = null)
    {
        var simulator = Simulator.Value;
        StatSheet sheet;
        lock (simulator)
        {
            var job = TestGrids.SampleContent.Balance.NewCharacter.StartJobId;
            var state = simulator.Progression.NewCharacter(job);
            sheet = simulator.Player(job, state.BaseLevel, state.JobLevel, state.Stats, state.StatPoints, weapon: null).Sheet;
        }
        if (aspd is { } a) sheet.Modifiers.Add("test", DerivedStats.Aspd, ModifierOp.Override, a);
        if (speed is { } s) sheet.Modifiers.Add("test", DerivedStats.MoveSpeed, ModifierOp.Override, s);
        if (flee is { } f) sheet.Modifiers.Add("test", DerivedStats.Flee, ModifierOp.Override, f);
        if (maxHp is { } h) sheet.Modifiers.Add("test", DerivedStats.MaxHp, ModifierOp.Override, h);
        return sheet;
    }

    /// <summary>A grid from rows (<c>.</c> walkable, <c>#</c> wall) with spawns and safe zones.</summary>
    public static MapGrid Grid(string[] rows, GridSpawn[]? spawns = null, GridRect[]? safe = null)
    {
        var width = rows[0].Length;
        var cells = new byte[width * rows.Length];
        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < width; x++)
            cells[y * width + x] = rows[y][x] == '.' ? CellFlagBits.Walkable : CellFlagBits.BlocksLos;
        return MapGrid.Create(new MapGridFile
        {
            MapId = MapId, Width = width, Height = rows.Length, Cells = Convert.ToBase64String(cells), Spawns = spawns ?? [],
            Zones = (safe ?? []).Select(r => new GridZone { Kind = ZoneKinds.Safe, Area = r }).ToArray(),
        });
    }

    public static MapGrid Open(int width, int height, GridSpawn[]? spawns = null, GridRect[]? safe = null) =>
        Grid(Enumerable.Repeat(new string('.', width), height).ToArray(), spawns, safe);

    public static CombatContent Content(MonsterCatalog catalog, BalanceDef balance, IEnumerable<DropTableDef>? drops = null)
    {
        var sample = TestGrids.SampleContent;
        var tables = sample.Drops.All.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var d in drops ?? []) tables[d.Id] = d;
        return new CombatContent(CombatRules.Compile(sample.Formulas, sample.Elements, balance.Combat), catalog, balance, tables,
            new ContentProgression(sample));
    }
}

/// <summary>
/// One map running movement, the monster spawner, optionally the monster AI, and combat, with every event recorded with its tick.
/// Monsters and players are added with <see cref="AddMonster"/> / <see cref="AddPlayer"/> and join on the next tick (with full vitals).
/// </summary>
public sealed class CombatRig
{
    public readonly GameWorld World;
    public readonly ListLog Log = new();
    public readonly List<(long Tick, IGameEvent Event)> Events = new();
    public readonly InMemoryProgressionStore Progressions = new();
    public readonly MonsterCatalog Catalog;
    public readonly CombatContent Content;

    public CombatRig(MapGrid grid, IEnumerable<MonsterDef> monsters, BalanceDef? balance = null, IEnumerable<DropTableDef>? drops = null,
        bool ai = false, ulong seed = 1, AiBalanceDef? aiBalance = null, int bucketSize = 4, int viewRadius = 1,
        Func<ITickSystem, ITickSystem>? wrap = null)
    {
        Catalog = TestMonsters.Catalog(monsters.ToArray());
        Content = TestCombat.Content(Catalog, balance ?? TestCombat.Balance(), drops);
        wrap ??= s => s;
        var timings = aiBalance ?? TestMonsters.Ai;
        var pipeline = new SystemPipeline()
            .Add(() => wrap(new MovementSystem(TestGrids.Roomy)))
            .Add(() => wrap(new MonsterSpawnerSystem(Catalog)));
        if (ai) pipeline.Add(() => wrap(new MonsterAiSystem(Catalog, timings, bucketSize, viewRadius)));
        pipeline.Add(() => wrap(new CombatSystem(Content, Progressions)));
        World = new GameWorld([grid], pipeline, seed, new ManualClock(), Log);
        Instance.Events.SubscribeAll(e => Events.Add((Instance.TickNumber, e)));
    }

    public MapInstance Instance => World.Instances[0];

    private IEnumerable<ITickSystem> Unwrapped => Instance.Systems.Select(s => s is IWrappedSystem w ? w.Inner : s);

    public CombatSystem Combat => Unwrapped.OfType<CombatSystem>().Single();

    public MonsterSpawnerSystem Spawner => Unwrapped.OfType<MonsterSpawnerSystem>().Single();

    public long Now => Instance.TickNumber * TickRate.IntervalMs;

    /// <summary>Adds a monster that no spawner owns (it stays dead until the test removes it).</summary>
    public EntityId AddMonster(MonsterDef def, GridCell cell) =>
        World.Spawn(EntityKind.Monster, Instance.MapId, cell, Directions.S, e =>
        {
            e.DefinitionId = def.Id;
            e.Stats = Catalog.CreateStats(def);
        });

    public EntityId AddPlayer(GridCell cell, StatSheet? sheet = null)
    {
        var stats = sheet ?? TestCombat.PlayerSheet();
        return World.Spawn(EntityKind.Player, Instance.MapId, cell, Directions.S, e => e.Stats = stats);
    }

    public Entity Get(EntityId id) => Instance.TryGetEntity(id, out var e) ? e : throw new InvalidOperationException($"{id} is gone");

    public bool Has(EntityId id) => Instance.TryGetEntity(id, out _);

    public int Hp(EntityId id) => Get(id).Vitals!.Hp;

    public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

    /// <summary>Publishes a monster attack request as the AI would (dispatched at the end of the next tick, resolved in the one after).</summary>
    public void Request(EntityId monster, EntityId target, int range = 1) =>
        Instance.Events.Publish(new AttackRequested(monster, target, Get(monster).Cell, Get(target).Cell, range, Now));

    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++) Instance.Tick();
    }

    public void TickUntil(Func<bool> done, int max = 1000, string? what = null)
    {
        for (var i = 0; i < max; i++)
        {
            Instance.Tick();
            if (done()) return;
        }
        throw new ShouldAssertException($"not reached after {max} ticks: {what}");
    }

    public List<T> Of<T>() where T : IGameEvent => Events.Select(e => e.Event).OfType<T>().ToList();

    public List<(long Tick, T Event)> TimedOf<T>() where T : IGameEvent =>
        Events.Where(e => e.Event is T).Select(e => (e.Tick, (T)e.Event)).ToList();
}
