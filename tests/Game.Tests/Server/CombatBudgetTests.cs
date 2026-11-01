using System.Diagnostics;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Random;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;
using Xunit.Abstractions;

namespace Game.Tests.Server;

/// <summary>
/// T-302 cost: the combat system on the sample meadow with its full spawn list (and the spawner and AI), ~20 players fighting monsters.
/// Logs wall time per tick; asserts only that allocation per tick stays bounded after warm-up and that a hit allocates nothing.
/// </summary>
public class CombatBudgetTests(ITestOutputHelper output)
{
    private sealed class Timed(ITickSystem inner) : ITickSystem, IWrappedSystem
    {
        public bool Measure;
        public double Micros;
        public double MaxMicros;
        public long Bytes;
        public long MaxBytes;

        public ITickSystem Inner => inner;

        public string Name => inner.Name;

        public IReadOnlyCollection<Type> IntentTypes => inner.IntentTypes;

        public void Tick(TickContext context)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            inner.Tick(context);
            var micros = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (!Measure) return;
            Micros += micros;
            MaxMicros = Math.Max(MaxMicros, micros);
            Bytes += bytes;
            MaxBytes = Math.Max(MaxBytes, bytes);
        }
    }

    private const int Warmup = 300;
    private const int Measured = 1000;

    [Fact]
    public void Meadow_with_twenty_players_fighting()
    {
        var content = TestGrids.SampleContent;
        var grid = content.GetGrid("map.starter_meadow");
        var timed = new List<Timed>();
        var balance = content.Balance with { NewCharacter = content.Balance.NewCharacter with { StartMapId = grid.MapId } };
        var rig = new CombatRig(grid, content.Monsters.All, balance, ai: true, seed: 11, aiBalance: content.Balance.Ai,
            bucketSize: content.Balance.Aoi.BucketSize, viewRadius: content.Balance.Aoi.ViewRadius, wrap: s =>
            {
                var t = new Timed(s);
                timed.Add(t);
                return t;
            });
        rig.Ticks();
        IRandom pick = new Pcg32Random(3, 9);
        var players = Enumerable.Range(0, 20).Select(_ =>
        {
            var sheet = TestCombat.PlayerSheet(aspd: 1.5, speed: 5, maxHp: 400);
            sheet.Modifiers.Add("test", DerivedStats.Atk, ModifierOp.Override, 12);
            return rig.AddPlayer(RandomCell(grid, pick), sheet);
        }).ToList();
        rig.Ticks(2);

        void Fight(int t)
        {
            // Each player picks a random live monster every 3 s (staggered), respawns when dead and walks out of the safe zone it respawns
            // in, so the map keeps fighting.
            var monsters = rig.Instance.Entities.Where(e => e.Kind == EntityKind.Monster && !e.Dead).ToList();
            for (var i = 0; i < players.Count; i++)
            {
                if ((t + i * 3) % 30 != 0 || !rig.Has(players[i])) continue;
                var player = rig.Get(players[i]);
                if (player.Dead) rig.Send(new Respawn(players[i]));
                else if (grid.Zones.Any(z => z.Kind == ZoneKinds.Safe && z.Area.Contains(player.Cell.X, player.Cell.Y)))
                    rig.Send(new MoveTo(players[i], RandomCell(grid, pick)));
                else if (monsters.Count > 0) rig.Send(new AttackTarget(players[i], monsters[pick.Next(monsters.Count)].Id));
            }
        }

        for (var t = 0; t < Warmup; t++)
        {
            Fight(t);
            rig.Ticks();
        }
        var resolved = rig.Combat.Resolved;
        var deaths = rig.Of<EntityDied>().Count;
        var drops = rig.Of<ItemDropped>().Count;
        foreach (var s in timed) s.Measure = true;
        for (var t = Warmup; t < Warmup + Measured; t++)
        {
            Fight(t);
            rig.Ticks();
        }

        output.WriteLine($"20 players fighting: {rig.Instance.Entities.Count(e => e.Kind == EntityKind.Monster)} monsters, {Measured} ticks");
        foreach (var s in timed)
            output.WriteLine($"  {s.Name}: {s.Micros / Measured:0.0} us/tick (max {s.MaxMicros:0} us), {(double)s.Bytes / Measured:0} B/tick (max {s.MaxBytes} B)");
        output.WriteLine($"  attacks resolved: {rig.Combat.Resolved - resolved}, deaths: {rig.Of<EntityDied>().Count - deaths}, " +
                         $"drops: {rig.Of<ItemDropped>().Count - drops}, attacking now: {rig.Combat.AttackingCount}, ground items: {rig.Combat.GroundItemCount}");

        (rig.Combat.Resolved - resolved).ShouldBeGreaterThan(100);
        (rig.Of<EntityDied>().Count - deaths).ShouldBeGreaterThan(0);
        // After warm-up combat allocates only the events and chase intents it raises and, on a kill, the progression results and ground items:
        // bounded by the number of fighters, never growing with time.
        var combat = timed.Single(s => s.Inner is CombatSystem);
        combat.MaxBytes.ShouldBeLessThan(40 * 4096L);
        (combat.Bytes / Measured).ShouldBeLessThan(40 * 512L);
    }

    [Fact]
    public void Regenerating_a_crowd_allocates_nothing_per_tick()
    {
        // 300 hurt players and 100 hurt monsters, all regenerating HP and SP every tick: the cost the regeneration adds to combat.
        var tank = TestCombat.Monster("monster.t_regen", maxHp: 100_000, hpRegen: 1);
        var timed = new List<Timed>();
        var rig = new CombatRig(TestCombat.Open(60, 40), [tank], wrap: s =>
        {
            var t = new Timed(s);
            timed.Add(t);
            return t;
        });
        var entities = new List<EntityId>();
        for (var i = 0; i < 300; i++)
            entities.Add(rig.AddPlayer(new GridCell(i % 60, i / 60), TestCombat.PlayerSheet(maxHp: 100_000, hpRegen: 1, spRegen: 1, maxSp: 100_000)));
        for (var i = 0; i < 100; i++) entities.Add(rig.AddMonster(tank, new GridCell(i % 60, 10 + i / 60)));
        rig.Ticks(2);
        foreach (var id in entities)
        {
            rig.Get(id).Vitals!.Hp = 1;
            rig.Get(id).Vitals!.Sp = 1;
        }
        rig.Ticks(50);
        var combat = timed.Single(s => s.Inner is CombatSystem);
        combat.Measure = true;
        const int ticks = 200;
        rig.Ticks(ticks);

        var hp = rig.Get(entities[0]).Vitals!.Hp;
        output.WriteLine($"regeneration of {entities.Count} hurt entities: combat {combat.Micros / ticks:0.0} us/tick (max {combat.MaxMicros:0} us), " +
                         $"{combat.Bytes} B over {ticks} ticks (max {combat.MaxBytes} B per tick)");
        hp.ShouldBeInRange(25, 27);   // it really regenerated: 1 HP/s for the 25 s since it was hurt
        combat.MaxBytes.ShouldBe(0);
    }

    [Fact]
    public void A_steady_auto_attack_allocates_only_its_event()
    {
        var tank = TestCombat.Monster("monster.t_tank", maxHp: 1_000_000, atk: 1);
        Timed? combat = null;
        var rig = new CombatRig(TestCombat.Open(12, 8), [tank], wrap: s => s is CombatSystem ? combat = new Timed(s) : s);
        var monster = rig.AddMonster(tank, new GridCell(6, 4));
        var player = rig.AddPlayer(new GridCell(5, 4), TestCombat.PlayerSheet(aspd: 2));
        rig.Ticks();
        rig.Send(new AttackTarget(player, monster));
        rig.Ticks(50);
        long hitMin = long.MaxValue, idleMin = long.MaxValue;
        for (var i = 0; i < 200; i++)
        {
            var hits = rig.Of<DamageDealt>().Count;
            combat!.Measure = true;
            combat.Bytes = 0;
            rig.Ticks();
            if (rig.Of<DamageDealt>().Count > hits) hitMin = Math.Min(hitMin, combat.Bytes);
            else idleMin = Math.Min(idleMin, combat.Bytes);
        }
        var resolver = ResolverBytesPerHit();
        output.WriteLine($"combat tick with a hit: {hitMin} B, without: {idleMin} B; the shared resolver alone: {resolver} B per hit");
        idleMin.ShouldBe(0);
        // Combat adds only the DamageDealt record (48 B); the resolver and its own path (context, vitals, damage log, timers, regeneration)
        // allocate nothing.
        resolver.ShouldBe(0);
        hitMin.ShouldBeLessThanOrEqualTo(64);
    }

    /// <summary>Bytes one <see cref="CombatResolver.Resolve"/> allocates with a reused context: 0 since T-308 cached the target-side stat slots.</summary>
    private static long ResolverBytesPerHit()
    {
        var content = TestGrids.SampleContent;
        var monster = content.Monsters.Get("monster.thornback_boar");
        var catalog = TestMonsters.Catalog(monster);
        var rules = CombatRules.Compile(content.Formulas, content.Elements, content.Balance.Combat);
        var resolver = new CombatResolver(rules, new Pcg32Random(1, 1));
        var attack = new AttackContext
        {
            Attacker = TestCombat.PlayerSheet(),
            Defender = catalog.CreateStats(monster),
            DefenderElement = monster.Element,
            DefenderRace = monster.Race,
            DefenderSize = monster.Size,
        };
        return AllocationProbe.MinAllocatedBytes(() =>
        {
            for (var i = 0; i < 100; i++) resolver.Resolve(attack);
        }) / 100;
    }

    [Fact]
    public void The_shared_resolver_allocation_per_hit_is_bounded()
    {
        var bytes = ResolverBytesPerHit();
        output.WriteLine($"CombatResolver.Resolve: {bytes} B per hit");
        bytes.ShouldBe(0);
    }

    private static GridCell RandomCell(MapGrid grid, IRandom random)
    {
        while (true)
        {
            var cell = new GridCell(random.Next(grid.Width), random.Next(grid.Height));
            if (grid.IsWalkable(cell.X, cell.Y)) return cell;
        }
    }
}
