using Game.Content.Simulation;
using Game.Server.Events;
using Game.Server.World;
using Game.Server.World.Monsters;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>Test monsters, small grids with spawns, and a world running movement + spawner + AI on one map.</summary>
public static class TestMonsters
{
    /// <summary>Test timings: scans every tick, short wander pauses, re-path at most every 300 ms.</summary>
    public static readonly AiBalanceDef Ai = new()
    {
        ThinkIntervalMs = 100,
        WanderPauseMinMs = 1000,
        WanderPauseMaxMs = 2000,
        WanderRadius = 3,
        RepathIntervalMs = 300,
        ReturnTimeoutMs = 20000,
    };

    /// <summary>A monster with fixed speed and aspd overrides (so tests do not depend on the derived formulas).</summary>
    public static MonsterDef Def(string id, bool aggressive = false, int aggroRange = 0, int chaseRange = 10, double speed = 5,
        double aspd = 1, int attackRange = 1) => new()
    {
        Id = id,
        Name = id,
        Level = 1,
        Stats = new Dictionary<string, double> { [PrimaryStats.Agi] = 1, [DerivedStats.MoveSpeed] = speed, [DerivedStats.Aspd] = aspd },
        AttackRange = attackRange,
        Ai = new MonsterAiDef { Aggressive = aggressive, AggroRange = aggroRange, ChaseRange = chaseRange },
    };

    public static readonly MonsterDef Passive = Def("monster.passive");
    public static readonly MonsterDef Aggressive = Def("monster.aggressive", aggressive: true, aggroRange: 5);

    private static readonly Lazy<BalanceSimulator> Simulator = new(() => new BalanceSimulator(TestGrids.SampleContent));

    public static MonsterCatalog Catalog(params MonsterDef[] defs) => new(defs, Simulator.Value.Monster);

    public static GridSpawn Spawn(MonsterDef def, int x, int y, int w = 1, int h = 1, int count = 1, double respawnSeconds = 5) =>
        new() { MonsterId = def.Id, Area = new GridRect(x, y, w, h), Count = count, RespawnSeconds = respawnSeconds };

    /// <summary>A grid from rows (<c>.</c> walkable, <c>#</c> wall that blocks line of sight) with spawns.</summary>
    public static MapGrid Grid(string[] rows, params GridSpawn[] spawns)
    {
        var width = rows[0].Length;
        var cells = new byte[width * rows.Length];
        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < width; x++)
            cells[y * width + x] = rows[y][x] == '.' ? CellFlagBits.Walkable : CellFlagBits.BlocksLos;
        return MapGrid.Create(new MapGridFile
        {
            MapId = "map.test", Width = width, Height = rows.Length, Cells = Convert.ToBase64String(cells), Spawns = spawns,
        });
    }

    /// <summary>An open walkable grid.</summary>
    public static MapGrid Open(int width, int height, params GridSpawn[] spawns) =>
        Grid(Enumerable.Repeat(new string('.', width), height).ToArray(), spawns);
}

/// <summary>A test decorator around a system; the rig looks through it.</summary>
public interface IWrappedSystem
{
    ITickSystem Inner { get; }
}

/// <summary>
/// One map running movement, the monster spawner and the AI (no AOI: dormancy only needs its numbers). Every event is recorded with
/// its tick. Players are added with <see cref="AddPlayer"/> and join on the next tick.
/// </summary>
public sealed class MonsterRig
{
    public readonly GameWorld World;
    public readonly ListLog Log = new();
    public readonly List<(long Tick, IGameEvent Event)> Events = new();

    public MonsterRig(MapGrid grid, IEnumerable<MonsterDef> defs, AiBalanceDef? ai = null, int bucketSize = 4, int viewRadius = 1, ulong seed = 1,
        Func<ITickSystem, ITickSystem>? wrap = null)
    {
        var catalog = TestMonsters.Catalog(defs.ToArray());
        var balance = ai ?? TestMonsters.Ai;
        wrap ??= s => s;
        var pipeline = new SystemPipeline()
            .Add(() => new MovementSystem(TestGrids.Roomy))
            .Add(() => wrap(new MonsterSpawnerSystem(catalog)))
            .Add(() => wrap(new MonsterAiSystem(catalog, balance, bucketSize, viewRadius)));
        World = new GameWorld([grid], pipeline, seed, new ManualClock(), Log);
        Instance.Events.SubscribeAll(e => Events.Add((Instance.TickNumber, e)));
    }

    public MapInstance Instance => World.Instances[0];

    private IEnumerable<ITickSystem> Unwrapped => Instance.Systems.Select(s => s is IWrappedSystem w ? w.Inner : s);

    public MonsterSpawnerSystem Spawner => Unwrapped.OfType<MonsterSpawnerSystem>().Single();

    public MonsterAiSystem Ai => Unwrapped.OfType<MonsterAiSystem>().Single();

    public List<Entity> MonsterEntities => Instance.Entities.Where(e => e.Kind == EntityKind.Monster).ToList();

    public Entity Monster => MonsterEntities.Single();

    public MonsterAiView View(EntityId id) => Ai.TryGetView(id, out var view) ? view : throw new InvalidOperationException($"no AI for {id}");

    public string State(EntityId id) => View(id).State;

    public EntityId AddPlayer(GridCell cell, int agi = AoiRig.Agi5) =>
        World.Spawn(EntityKind.Player, Instance.MapId, cell, Directions.S, e => e.Stats = AoiRig.Sheet(agi));

    public Entity Get(EntityId id) => Instance.TryGetEntity(id, out var e) ? e : throw new InvalidOperationException($"{id} is gone");

    public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++) Instance.Tick();
    }

    /// <summary>Ticks until <paramref name="done"/> holds (checked after each tick); fails after <paramref name="max"/> ticks.</summary>
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
