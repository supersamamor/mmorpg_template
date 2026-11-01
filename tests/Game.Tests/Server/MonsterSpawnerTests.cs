using Game.Server.World;
using Game.Server.World.Monsters;
using Game.Shared.Random;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>T-301: spawns on instance start, respawn after simulated time, and determinism on the sample meadow.</summary>
public class MonsterSpawnerTests
{
    private const string Meadow = "map.starter_meadow";

    private static MonsterRig MeadowRig(ulong seed = 1) =>
        new(TestGrids.SampleGrid(Meadow), TestGrids.SampleContent.Monsters.All, TestGrids.SampleContent.Balance.Ai,
            TestGrids.SampleContent.Balance.Aoi.BucketSize, TestGrids.SampleContent.Balance.Aoi.ViewRadius, seed);

    [Fact]
    public void First_tick_spawns_every_entry_of_the_meadow_inside_its_area()
    {
        var rig = MeadowRig();
        rig.Ticks();
        var grid = rig.Instance.Grid;
        grid.Spawns.Count.ShouldBe(5);
        var spawned = rig.Of<EntitySpawned>();
        spawned.Count.ShouldBe(grid.Spawns.Sum(s => s.Count));
        rig.MonsterEntities.Count.ShouldBe(spawned.Count);
        rig.Ai.Count.ShouldBe(spawned.Count);

        for (var i = 0; i < grid.Spawns.Count; i++)
        {
            var spawn = grid.Spawns[i];
            rig.Spawner.LiveCountOf(i).ShouldBe(spawn.Count);
            var def = TestGrids.SampleContent.Monsters.Get(spawn.MonsterId);
            foreach (var e in spawned.Where(s => s.SpawnIndex == i))
            {
                e.DefinitionId.ShouldBe(spawn.MonsterId);
                spawn.Area.Contains(e.Cell.X, e.Cell.Y).ShouldBeTrue($"{e.Cell} in {spawn.Area}");
                grid.IsWalkable(e.Cell.X, e.Cell.Y).ShouldBeTrue();
                var entity = rig.Get(e.Entity);
                entity.Kind.ShouldBe(EntityKind.Monster);
                entity.DefinitionId.ShouldBe(spawn.MonsterId);
                entity.Cell.ShouldBe(e.Cell);
                entity.Stats!.Get(DerivedStats.MoveSpeed).ShouldBe(def.Stats[DerivedStats.MoveSpeed]);
                entity.Stats.GetInt(DerivedStats.MaxHp).ShouldBe((int)def.Stats[DerivedStats.MaxHp]);
                entity.Stats.Get(DerivedStats.Aspd).ShouldBeGreaterThan(0);
                rig.View(e.Entity).Home.ShouldBe(e.Cell);
                rig.State(e.Entity).ShouldBe(AiStates.Idle);
            }
        }
    }

    [Fact]
    public void Spawn_area_with_unwalkable_cells_uses_only_walkable_ones()
    {
        string[] rows = ["#.#.#", "#####", "#.#.#"];
        var def = TestMonsters.Passive;
        var rig = new MonsterRig(TestMonsters.Grid(rows, TestMonsters.Spawn(def, 0, 0, 5, 3, count: 12)), [def]);
        rig.Ticks();
        rig.MonsterEntities.Count.ShouldBe(12);
        rig.MonsterEntities.ShouldAllBe(m => m.Cell.X % 2 == 1 && m.Cell.Y % 2 == 0);   // monsters may share a cell (no blocking in v1)
    }

    [Fact]
    public void Dead_monster_is_despawned_and_respawns_after_the_respawn_time_never_above_count()
    {
        var def = TestMonsters.Passive;
        var rig = new MonsterRig(TestMonsters.Open(12, 12, TestMonsters.Spawn(def, 2, 2, 3, 3, count: 2, respawnSeconds: 5)), [def]);
        rig.Ticks();
        var first = rig.MonsterEntities.Select(m => m.Id).ToList();
        first.Count.ShouldBe(2);

        var deathTick = rig.Instance.TickNumber;
        rig.Instance.Events.Publish(new EntityDied(first[0], null));
        rig.Instance.Events.Publish(new EntityDied(first[0], null));             // a duplicate
        rig.Instance.Events.Publish(new EntityDied(new EntityId(9999), null));   // not ours
        rig.Ticks(2);   // handled in the first tick, despawned in the second
        rig.MonsterEntities.Select(m => m.Id).ShouldBe([first[1]]);
        rig.Ai.Count.ShouldBe(1);
        rig.Spawner.LiveCount.ShouldBe(1);

        // Due 5 s of simulated time after the tick the death was raised in.
        while (rig.Instance.TickNumber < deathTick + 50)
        {
            rig.Ticks();
            rig.MonsterEntities.Count.ShouldBe(1);
        }
        rig.Ticks();   // the tick whose simulated time is the death's + 5000 ms
        rig.MonsterEntities.Count.ShouldBe(2);
        var respawned = rig.TimedOf<EntitySpawned>().Last();
        ((respawned.Tick - deathTick) * TickRate.IntervalMs).ShouldBe(5000);
        var fresh = rig.Get(respawned.Event.Entity);
        fresh.Id.ShouldNotBe(first[0]);
        new GridRect(2, 2, 3, 3).Contains(fresh.Cell.X, fresh.Cell.Y).ShouldBeTrue();
        rig.Ai.Count.ShouldBe(2);

        rig.Ticks(200);
        rig.MonsterEntities.Count.ShouldBe(2);
        rig.Spawner.Spawned.ShouldBe(3);
    }

    [Fact]
    public void Zero_respawn_time_refills_on_the_next_tick()
    {
        var def = TestMonsters.Passive;
        var rig = new MonsterRig(TestMonsters.Open(8, 8, TestMonsters.Spawn(def, 1, 1, 2, 2, respawnSeconds: 0)), [def]);
        rig.Ticks();
        rig.Instance.Events.Publish(new EntityDied(rig.Monster.Id, null));
        rig.Ticks(2);
        rig.MonsterEntities.Count.ShouldBe(1);
        rig.Spawner.Spawned.ShouldBe(2);
    }

    [Fact]
    public void Monster_removed_by_someone_else_frees_its_slot_too()
    {
        var def = TestMonsters.Passive;
        var rig = new MonsterRig(TestMonsters.Open(8, 8, TestMonsters.Spawn(def, 1, 1, 2, 2, respawnSeconds: 1)), [def]);
        rig.Ticks();
        rig.World.Despawn(rig.Monster.Id).ShouldBeTrue();
        rig.Ticks(12);
        rig.MonsterEntities.Count.ShouldBe(1);
        rig.Spawner.Spawned.ShouldBe(2);
    }

    [Fact]
    public void Same_seed_gives_the_same_spawns_and_decisions()
    {
        var a = Run(42);
        var b = Run(42);
        a.ShouldBe(b);
        Run(43).ShouldNotBe(a);
    }

    private static GridCell NearestWalkable(MapGrid grid, GridCell cell)
    {
        for (var r = 0; ; r++)
        for (var y = cell.Y - r; y <= cell.Y + r; y++)
        for (var x = cell.X - r; x <= cell.X + r; x++)
            if (grid.IsWalkable(x, y)) return new GridCell(x, y);
    }

    /// <summary>The meadow with three players walking a fixed route through the monster areas for 60 s; returns every event and the final cells.</summary>
    private static List<string> Run(ulong seed)
    {
        var rig = MeadowRig(seed);
        var players = new[] { new GridCell(12, 12), new GridCell(30, 8), new GridCell(36, 24) }.Select(c => rig.AddPlayer(NearestWalkable(rig.Instance.Grid, c))).ToList();
        rig.Ticks(2);
        IRandom route = new Pcg32Random(7, 1);
        var grid = rig.Instance.Grid;
        for (var t = 0; t < 600; t++)
        {
            if (t % 40 == 0)
                foreach (var p in players)
                {
                    GridCell target;
                    do target = new GridCell(route.Next(grid.Width), route.Next(grid.Height));
                    while (!grid.IsWalkable(target.X, target.Y));
                    rig.Send(new MoveTo(p, target));
                }
            rig.Ticks();
        }
        rig.Of<AggroGained>().Count.ShouldBeGreaterThan(0);   // the route does provoke the wasps
        return rig.Events.Select(e => $"{e.Tick} {e.Event}")
            .Concat(rig.Instance.Entities.Select(e => $"{e.Id} {e.Kind} {e.DefinitionId} {e.Cell} {e.Facing}"))
            .ToList();
    }
}
