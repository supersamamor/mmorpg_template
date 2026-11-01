using Game.Server.Events;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Monsters;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Progression;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>T-308: regeneration, return home, corpse time, warps for players only, and the small T-302 fixes.</summary>
public class CombatPolishTests
{
    // ---- regeneration ---------------------------------------------------------------------------------------------------------------------

    private static (CombatRig Rig, EntityId Player) Hurt(double hpRegen, int maxHp = 1000, int hp = 500, int regenDelayMs = 3000, double spRegen = 0, int maxSp = 100)
    {
        var rig = new CombatRig(TestCombat.Open(12, 8), [], TestCombat.Balance(regenDelayMs: regenDelayMs));
        var player = rig.AddPlayer(new GridCell(4, 4), TestCombat.PlayerSheet(maxHp: maxHp, hpRegen: hpRegen, spRegen: spRegen, maxSp: maxSp));
        rig.Ticks(2);   // joins combat with full vitals
        rig.Get(player).Vitals!.Hp = hp;
        rig.Get(player).Vitals!.Sp = 0;
        return (rig, player);
    }

    [Fact]
    public void Hp_and_sp_regenerate_at_their_rates_in_simulated_time()
    {
        var (rig, player) = Hurt(hpRegen: 0.5, spRegen: 1.5);

        rig.Ticks(100);   // 10 simulated seconds

        rig.Get(player).Vitals!.Hp.ShouldBe(505);
        rig.Get(player).Vitals!.Sp.ShouldBe(15);
    }

    [Fact]
    public void A_slow_rate_carries_its_fractions_without_drift()
    {
        var (rig, player) = Hurt(hpRegen: 0.3, hp: 1);

        rig.Ticks(33);
        rig.Get(player).Vitals!.Hp.ShouldBe(1);   // 0.99 points so far: nothing yet
        rig.Ticks(1);
        rig.Get(player).Vitals!.Hp.ShouldBe(2);   // 1.02
        rig.Ticks(966);                           // 1000 ticks = 100 s = 30 points
        rig.Get(player).Vitals!.Hp.ShouldBe(31);
    }

    [Fact]
    public void Regeneration_never_goes_above_max()
    {
        var (rig, player) = Hurt(hpRegen: 5, maxHp: 100, hp: 97, spRegen: 5, maxSp: 10);

        for (var i = 0; i < 60; i++)
        {
            rig.Ticks();
            rig.Get(player).Vitals!.Hp.ShouldBeLessThanOrEqualTo(100);
            rig.Get(player).Vitals!.Sp.ShouldBeLessThanOrEqualTo(10);
        }
        rig.Get(player).Vitals!.Hp.ShouldBe(100);
        rig.Get(player).Vitals!.Sp.ShouldBe(10);
    }

    [Fact]
    public void A_full_entity_banks_nothing()
    {
        var (rig, player) = Hurt(hpRegen: 1, maxHp: 100, hp: 100);
        rig.Ticks(200);   // full for 20 s
        rig.Get(player).Vitals!.Hp = 90;

        rig.Ticks(9);   // 0.9 points: had it banked the full time it would be 100 at once
        rig.Get(player).Vitals!.Hp.ShouldBe(90);
    }

    [Fact]
    public void Taking_damage_pauses_regeneration_for_the_out_of_combat_delay()
    {
        var hitter = TestCombat.Monster("monster.t_hitter", maxHp: 100, atk: 10);
        var rig = new CombatRig(TestCombat.Open(12, 8), [hitter], TestCombat.Balance(regenDelayMs: 3000));
        var monster = rig.AddMonster(hitter, new GridCell(5, 4));
        var player = rig.AddPlayer(new GridCell(4, 4), TestCombat.PlayerSheet(maxHp: 1000, hpRegen: 2));
        rig.Ticks(2);

        rig.Request(monster, player);
        rig.TickUntil(() => rig.Of<DamageDealt>().Count == 1, 5, "hit");
        var hurt = rig.Hp(player);
        hurt.ShouldBe(rig.Of<DamageDealt>().Single().HpLeft);
        hurt.ShouldBeLessThan(1000);

        rig.Ticks(25);   // 2.5 s: still paused
        rig.Hp(player).ShouldBe(hurt);
        rig.Ticks(15);   // 4 s: the 3 s delay is over and it has regenerated
        rig.Hp(player).ShouldBeGreaterThan(hurt);
    }

    [Fact]
    public void Dealing_damage_also_pauses_regeneration()
    {
        var attacker = TestCombat.Monster("monster.t_attacker", maxHp: 100, atk: 1, hpRegen: 2);
        var rig = new CombatRig(TestCombat.Open(12, 8), [attacker], TestCombat.Balance(regenDelayMs: 3000));
        var monster = rig.AddMonster(attacker, new GridCell(5, 4));
        var player = rig.AddPlayer(new GridCell(4, 4), TestCombat.PlayerSheet(maxHp: 100_000));
        rig.Ticks(2);
        rig.Get(monster).Vitals!.Hp = 50;

        rig.Request(monster, player);
        rig.TickUntil(() => rig.Of<DamageDealt>().Count == 1, 5, "monster attack");
        var before = rig.Hp(monster);

        rig.Ticks(25);
        rig.Hp(monster).ShouldBe(before);
        rig.Ticks(15);
        rig.Hp(monster).ShouldBeGreaterThan(before);
    }

    [Fact]
    public void A_miss_counts_as_combat()
    {
        var misser = TestCombat.Monster("monster.t_misser", maxHp: 100, atk: 10, hit: 1);
        var rig = new CombatRig(TestCombat.Open(12, 8), [misser], TestCombat.Balance(regenDelayMs: 3000));
        var monster = rig.AddMonster(misser, new GridCell(5, 4));
        var player = rig.AddPlayer(new GridCell(4, 4), TestCombat.PlayerSheet(maxHp: 1000, hpRegen: 2, flee: 100_000));
        rig.Ticks(2);
        rig.Get(player).Vitals!.Hp = 500;

        rig.Request(monster, player);
        rig.TickUntil(() => rig.Of<DamageDealt>().Count == 1, 5, "attack");
        rig.Of<DamageDealt>().Single().Result.ShouldBe(HitResults.Miss);

        rig.Ticks(25);
        rig.Hp(player).ShouldBe(500);   // paused although nothing was lost
    }

    [Fact]
    public void The_dead_never_regenerate()
    {
        var killer = TestCombat.Monster("monster.t_killer", maxHp: 100, atk: 100_000);
        var rig = new CombatRig(TestCombat.Open(12, 8), [killer], TestCombat.Balance(regenDelayMs: 0));
        var monster = rig.AddMonster(killer, new GridCell(5, 4));
        var player = rig.AddPlayer(new GridCell(4, 4), TestCombat.PlayerSheet(maxHp: 100, hpRegen: 50));
        rig.Ticks(2);
        rig.Request(monster, player);
        rig.TickUntil(() => rig.Get(player).Dead, 5, "death");

        rig.Ticks(100);

        rig.Hp(player).ShouldBe(0);
        rig.Get(player).Dead.ShouldBeTrue();
    }

    [Fact]
    public void A_zero_delay_means_regeneration_never_pauses()
    {
        var (rig, player) = Hurt(hpRegen: 1, regenDelayMs: 0);
        rig.Ticks(100);
        rig.Hp(player).ShouldBe(510);
    }

    [Fact]
    public void A_monster_healed_to_full_by_regeneration_forgets_its_attackers()
    {
        var def = TestCombat.Monster("monster.t_regen_mob", maxHp: 100, atk: 1, hpRegen: 10);
        var rig = new CombatRig(TestCombat.Open(12, 8), [def], TestCombat.Balance(regenDelayMs: 1000));
        var monster = rig.AddMonster(def, new GridCell(5, 4));
        var player = rig.AddPlayer(new GridCell(4, 4), TestCombat.PlayerSheet(aspd: 1));
        rig.Ticks(2);
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Of<DamageDealt>().Any(d => d.Source == player && d.Amount > 0), 50, "first hit");
        rig.Send(new StopAttack(player));
        rig.Ticks(2);
        rig.Combat.DamageLog(monster).ShouldNotBeEmpty();

        rig.TickUntil(() => rig.Hp(monster) == rig.Get(monster).Vitals!.MaxHp, 200, "healed");

        rig.Combat.DamageLog(monster).ShouldBeEmpty();
    }

    // ---- monster returns home ----------------------------------------------------------------------------------------------------------------

    private static readonly GridCell Home = new(5, 4);

    /// <summary>
    /// A hurt monster whose attacker walked away far enough that it gave up and returned; ends the tick it is back home. A second player
    /// stands by for the next kill.
    /// </summary>
    private static (CombatRig Rig, EntityId Monster, EntityId First, EntityId Second) GaveUp(bool healOnReturn)
    {
        var def = TestCombat.Monster("monster.t_homebody", maxHp: 10_000, atk: 1, aspd: 1, chaseRange: 3);
        var balance = TestCombat.Balance(healOnReturn: healOnReturn);
        var spawn = TestMonsters.Spawn(def, Home.X, Home.Y);
        var rig = new CombatRig(TestCombat.Open(30, 8, [spawn]), [def], balance, ai: true, aiBalance: TestMonsters.Ai, viewRadius: 6);
        rig.Ticks(2);
        var monster = rig.Instance.Entities.Single(e => e.Kind == EntityKind.Monster).Id;
        var first = rig.AddPlayer(new GridCell(Home.X + 1, Home.Y), TestCombat.PlayerSheet(maxHp: 100_000, speed: 8));
        var second = rig.AddPlayer(new GridCell(Home.X - 1, Home.Y), TestCombat.PlayerSheet(maxHp: 100_000));
        rig.Ticks(2);
        rig.Send(new AttackTarget(first, monster));
        rig.TickUntil(() => rig.Hp(monster) < 10_000, 100, "first player hurts it");
        rig.Send(new MoveTo(first, new GridCell(26, Home.Y)));   // walks off with the monster after it
        rig.TickUntil(() => rig.Of<MonsterReturned>().Count > 0, 600, "monster gave up and came home");
        return (rig, monster, first, second);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_monster_that_returns_home_resets_its_damage_shares_and_heals_only_if_asked(bool healOnReturn)
    {
        var (rig, monster, first, _) = GaveUp(healOnReturn);

        rig.Of<AggroLost>().ShouldContain(a => a.Monster == monster && a.Reason == AggroReasons.Leash);
        rig.Get(monster).Cell.ShouldBe(Home);
        rig.Combat.DamageLog(monster).ShouldBeEmpty();
        rig.Of<MonsterReturned>().Single().ShouldBe(new MonsterReturned(monster, Home));
        var max = rig.Get(monster).Vitals!.MaxHp;
        if (healOnReturn) rig.Hp(monster).ShouldBe(max);
        else rig.Hp(monster).ShouldBeLessThan(max);
        rig.Get(first).Dead.ShouldBeFalse();
    }

    [Fact]
    public void Exp_from_a_kill_after_the_return_is_not_split_with_the_old_attacker()
    {
        var (rig, monster, first, second) = GaveUp(healOnReturn: false);
        rig.Get(monster).Vitals!.Hp = 1;
        rig.Send(new AttackTarget(second, monster));

        rig.TickUntil(() => rig.Of<EntityDied>().Count > 0, 100, "kill");

        var gains = rig.Of<ExpGained>();
        gains.Select(g => g.Player).ShouldBe([second]);
        gains.Single().BaseExp.ShouldBe(10);   // the whole of the monster's EXP
        gains.ShouldNotContain(g => g.Player == first);
    }

    // ---- corpse time -----------------------------------------------------------------------------------------------------------------------

    private static (CombatRig Rig, EntityId Monster, EntityId Player) Kill(int corpseMs, double respawnSeconds)
    {
        var def = TestCombat.Monster("monster.t_corpse", maxHp: 50, atk: 1);
        var spawn = TestMonsters.Spawn(def, 8, 4, respawnSeconds: respawnSeconds);
        var rig = new CombatRig(TestCombat.Open(14, 8, [spawn]), [def], TestCombat.Balance(corpseMs: corpseMs));
        rig.Ticks(2);
        var monster = rig.Instance.Entities.Single(e => e.Kind == EntityKind.Monster).Id;
        var player = rig.AddPlayer(new GridCell(7, 4), TestCombat.PlayerSheet(maxHp: 100_000));
        rig.Ticks(2);
        rig.Get(monster).Vitals!.Hp = 1;
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Of<EntityDied>().Count > 0, 100, "kill");
        return (rig, monster, player);
    }

    [Fact]
    public void A_dead_monster_lies_there_for_the_corpse_time_then_is_removed()
    {
        var (rig, monster, player) = Kill(corpseMs: 2000, respawnSeconds: 60);
        var died = rig.TimedOf<EntityDied>().Single().Tick;

        rig.Ticks(15);   // 1.5 s later it is still there, dead
        rig.Has(monster).ShouldBeTrue();
        rig.Get(monster).Dead.ShouldBeTrue();
        rig.Spawner.CorpseCount.ShouldBe(1);

        rig.Send(new AttackTarget(player, monster));   // not targetable
        rig.Ticks(2);
        rig.Of<AttackRefused>().ShouldContain(r => r.Target == monster && r.Reason == AttackReasons.TargetDead);

        rig.TickUntil(() => !rig.Has(monster), 40, "removed");
        var lingered = (rig.Instance.TickNumber - died) * TickRate.IntervalMs;
        lingered.ShouldBeInRange(2000, 2300);
        rig.Spawner.CorpseCount.ShouldBe(0);
    }

    [Fact]
    public void Corpse_time_zero_removes_the_monster_on_the_next_tick()
    {
        var (rig, monster, _) = Kill(corpseMs: 0, respawnSeconds: 60);
        rig.Ticks(2);
        rig.Has(monster).ShouldBeFalse();
    }

    [Fact]
    public void The_respawn_timer_counts_from_the_death_not_from_the_removal()
    {
        var (rig, monster, _) = Kill(corpseMs: 2000, respawnSeconds: 5);
        var died = rig.TimedOf<EntityDied>().Single().Tick;

        rig.TickUntil(() => rig.TimedOf<EntitySpawned>().Count == 2, 100, "respawn");

        var after = (rig.TimedOf<EntitySpawned>()[1].Tick - died) * TickRate.IntervalMs;
        after.ShouldBeInRange(5000, 5200);
        rig.Has(monster).ShouldBeFalse();   // the corpse was long gone
    }

    [Fact]
    public void A_respawn_faster_than_the_corpse_time_does_not_wait_for_the_corpse()
    {
        var (rig, monster, _) = Kill(corpseMs: 3000, respawnSeconds: 1);
        var died = rig.TimedOf<EntityDied>().Single().Tick;

        rig.TickUntil(() => rig.TimedOf<EntitySpawned>().Count == 2, 100, "respawn");

        (rig.TimedOf<EntitySpawned>()[1].Tick - died).ShouldBeInRange(10, 12);
        rig.Has(monster).ShouldBeTrue();   // the old corpse is still lying there
        rig.Get(monster).Dead.ShouldBeTrue();
        rig.Spawner.LiveCount.ShouldBe(1);
        rig.Spawner.CorpseCount.ShouldBe(1);
    }

    // ---- warps are for players ------------------------------------------------------------------------------------------------------------------

    private static readonly GridCell WarpCell = new(4, 1);

    private static MapGrid WarpGrid(string mapId, params GridSpawn[] spawns)
    {
        const int width = 9;
        var cells = Enumerable.Repeat(CellFlagBits.Walkable, width * 3).ToArray();
        return MapGrid.Create(new MapGridFile
        {
            MapId = mapId, Width = width, Height = 3, Cells = Convert.ToBase64String(cells), Spawns = spawns,
            Warps = mapId == "map.test" ? [new GridWarp { Cell = WarpCell, TargetMapId = "map.zother", TargetCell = new GridCell(1, 1) }] : [],
        });
    }

    private static (GameWorld World, MapInstance Instance, List<IGameEvent> Events) WarpWorld(Func<SystemPipeline, SystemPipeline> systems, params GridSpawn[] spawns)
    {
        var pipeline = systems(new SystemPipeline().Add(() => new MovementSystem(TestGrids.Roomy)));
        var world = new GameWorld([WarpGrid("map.test", spawns), WarpGrid("map.zother")], pipeline, 5, new ManualClock(), new ListLog());
        var instance = world.GetInstance("map.test");
        var events = new List<IGameEvent>();
        instance.Events.SubscribeAll(events.Add);
        return (world, instance, events);
    }

    [Fact]
    public void A_player_walking_over_a_warp_cell_warps()
    {
        var (world, instance, _) = WarpWorld(p => p);
        var player = world.Spawn(EntityKind.Player, "map.test", new GridCell(0, 1), Directions.E, e => e.Stats = TestCombat.PlayerSheet(speed: 8));
        instance.Tick();
        world.TryEnqueue(new MoveTo(player, new GridCell(8, 1))).ShouldBe(EnqueueResult.Accepted);

        for (var i = 0; i < 40 && world.LocationOf(player) == "map.test"; i++) instance.Tick();

        world.LocationOf(player).ShouldBe("map.zother");
    }

    [Fact]
    public void A_monster_walking_over_a_warp_cell_stays_on_its_map()
    {
        var def = TestCombat.Monster("monster.t_walker", speed: 8);
        var catalog = TestMonsters.Catalog(def);
        var (world, instance, events) = WarpWorld(p => p);
        var monster = world.Spawn(EntityKind.Monster, "map.test", new GridCell(0, 1), Directions.E, e =>
        {
            e.DefinitionId = def.Id;
            e.Stats = catalog.CreateStats(def);
        });
        instance.Tick();
        world.TryEnqueue(new MoveTo(monster, new GridCell(8, 1))).ShouldBe(EnqueueResult.Accepted);

        for (var i = 0; i < 40; i++) instance.Tick();

        world.LocationOf(monster).ShouldBe("map.test");
        instance.TryGetEntity(monster, out var entity).ShouldBeTrue();
        entity.Cell.ShouldBe(new GridCell(8, 1));
        events.OfType<CellReached>().ShouldContain(c => c.Entity == monster && c.Cell.X == WarpCell.X && c.Cell.Y == WarpCell.Y);
        events.OfType<EntityLeftMap>().ShouldNotContain(e => e.Entity == monster);
        events.OfType<WarpRejected>().ShouldBeEmpty();
    }

    [Fact]
    public void A_wandering_monster_crosses_a_warp_cell_without_leaving()
    {
        var def = TestMonsters.Def("monster.t_wanderer", speed: 6);
        var spawn = TestMonsters.Spawn(def, WarpCell.X, WarpCell.Y);   // its home is the warp cell itself
        var catalog = TestMonsters.Catalog(def);
        var (world, instance, events) = WarpWorld(p => p
            .Add(() => new MonsterSpawnerSystem(catalog))
            .Add(() => new MonsterAiSystem(catalog, TestMonsters.Ai with { WanderRadius = 3 }, 4, 2)), spawn);
        world.Spawn(EntityKind.Player, "map.test", new GridCell(0, 0), Directions.S, e => e.Stats = TestCombat.PlayerSheet());   // keeps it awake
        instance.Tick();
        var monster = instance.Entities.Single(e => e.Kind == EntityKind.Monster).Id;

        for (var i = 0; i < 1500; i++) instance.Tick();

        world.LocationOf(monster).ShouldBe("map.test");
        var visited = events.OfType<CellReached>().Where(c => c.Entity == monster).Select(c => (c.Cell.X, c.Cell.Y)).ToHashSet();
        visited.Count.ShouldBeGreaterThan(3);              // it really wandered
        visited.ShouldContain((WarpCell.X, WarpCell.Y));   // and came back over its home, the warp cell
        events.OfType<EntityLeftMap>().ShouldNotContain(e => e.Entity == monster);
    }

    // ---- the T-302 small fixes ---------------------------------------------------------------------------------------------------------

    private sealed class MovementSpy(ITickSystem inner) : ITickSystem, IWrappedSystem
    {
        public readonly List<IIntent> Seen = new();

        public ITickSystem Inner => inner;

        public string Name => inner.Name;

        public IReadOnlyCollection<Type> IntentTypes => inner.IntentTypes;

        public void Tick(TickContext context)
        {
            Seen.AddRange(context.Intents);
            inner.Tick(context);
        }
    }

    [Fact]
    public void A_monsters_first_move_is_not_mistaken_for_one_in_flight()
    {
        // A monster that never moved used to count as "move just issued" (tick - long.MinValue overflows), so engaging a target already in
        // range sent a StopMove for a walk that never started.
        var def = TestCombat.Monster("monster.t_still", maxHp: 1000, atk: 1, aggressive: true, aggroRange: 5);
        var spawn = TestMonsters.Spawn(def, 6, 4);
        MovementSpy? spy = null;
        var rig = new CombatRig(TestCombat.Open(14, 8, [spawn]), [def], ai: true, wrap: s => s is MovementSystem ? spy = new MovementSpy(s) : s);
        rig.Ticks(2);
        rig.AddPlayer(new GridCell(5, 4), TestCombat.PlayerSheet(maxHp: 100_000));

        rig.TickUntil(() => rig.Of<AttackRequested>().Count > 0, 50, "the monster attacks");
        rig.Ticks(5);

        rig.Of<AggroGained>().Count.ShouldBe(1);
        spy!.Seen.OfType<StopMove>().ShouldBeEmpty();
        spy.Seen.OfType<MoveTo>().ShouldBeEmpty();
    }

    [Fact]
    public void Target_stat_slots_are_written_without_allocating()
    {
        var sheet = TestCombat.PlayerSheet();
        var context = new FormulaContext();
        var bytes = AllocationProbe.MinAllocatedBytes(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                sheet.WriteTo(context, asTarget: true);
                sheet.WriteTo(context);
            }
        });
        bytes.ShouldBe(0);
    }

    [Fact]
    public void Target_stat_slots_hold_the_sheets_values()
    {
        var sheet = TestCombat.PlayerSheet();
        var context = new FormulaContext();

        sheet.WriteTo(context, asTarget: true);

        context[FormulaVariables.TargetPrefix + DerivedStats.Def].ShouldBe(sheet.Get(DerivedStats.Def));
        context[FormulaVariables.TargetPrefix + FormulaVariables.BaseLevel].ShouldBe(sheet.Base.BaseLevel);
        context[FormulaVariables.TargetPrefix + FormulaVariables.JobLevel].ShouldBe(sheet.Base.JobLevel);
        context[FormulaVariables.TargetLevel].ShouldBe(sheet.Base.BaseLevel);
    }

    // ---- LoseBaseExp (golden values: exp.base = 65, 194, 400, 687, ...) ---------------------------------------------------------------

    private static ProgressionRules Rules() =>
        new(TestGrids.SampleContent.Balance, new Game.Content.Simulation.ContentProgression(TestGrids.SampleContent));

    [Theory]
    [InlineData(1, 40, 10, 10, 30)]      // plain loss
    [InlineData(1, 40, 40, 40, 0)]       // exactly what it has
    [InlineData(1, 40, 500, 40, 0)]      // more than it has: only what it has, never below 0
    [InlineData(1, 0, 20, 0, 0)]         // nothing to lose at the start of a level
    [InlineData(3, 399, 1000, 399, 0)]   // never drops a level
    [InlineData(4, 100, 0, 0, 100)]      // zero loses nothing
    [InlineData(4, 100, -5, 0, 100)]     // negative loses nothing
    public void LoseBaseExp_stays_within_the_current_level(int level, long exp, long amount, long expectedLost, long expectedLeft)
    {
        var rules = Rules();
        var state = rules.NewCharacter("job.novice");
        state.BaseLevel = level;
        state.BaseExp = exp;

        var lost = rules.LoseBaseExp(state, amount);

        lost.ShouldBe(expectedLost);
        state.BaseExp.ShouldBe(expectedLeft);
        state.BaseLevel.ShouldBe(level);
    }

    [Fact]
    public void LoseBaseExp_at_the_max_level_loses_nothing()
    {
        var rules = Rules();
        var state = rules.NewCharacter("job.novice");
        state.BaseLevel = rules.MaxBaseLevel;
        state.BaseExp = 0;

        rules.LoseBaseExp(state, 1000).ShouldBe(0);
        state.BaseLevel.ShouldBe(rules.MaxBaseLevel);
    }

    // ---- per-map respawn point -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_player_respawns_at_the_maps_respawn_point()
    {
        var killer = TestCombat.Monster("monster.t_executioner", maxHp: 100, atk: 100_000);
        var respawns = new Dictionary<string, (string MapId, GridCell Cell)> { [TestCombat.MapId] = (TestCombat.MapId, new GridCell(9, 6)) };
        var rig = new CombatRig(TestCombat.Open(12, 8), [killer], respawns: respawns);
        var monster = rig.AddMonster(killer, new GridCell(5, 4));
        var player = rig.AddPlayer(new GridCell(4, 4), TestCombat.PlayerSheet(maxHp: 100));
        rig.Ticks(2);
        rig.Request(monster, player);
        rig.TickUntil(() => rig.Get(player).Dead, 5, "death");

        rig.Send(new Respawn(player));
        rig.Ticks(3);

        rig.Of<PlayerRespawned>().Single().Cell.ShouldBe(new GridCell(9, 6));
        rig.Get(player).Cell.ShouldBe(new GridCell(9, 6));
    }

    [Fact]
    public void Without_a_respawn_cell_the_first_arrival_warp_then_the_new_character_start_decide()
    {
        var content = TestGrids.SampleContent;
        var own = CombatContent.RespawnPoints(content.Maps.All, content.Grids);
        own["map.starter_meadow"].ShouldBe(("map.starter_meadow", new GridCell(12, 12)));
        own["map.thistle_hollow"].ShouldBe(("map.thistle_hollow", new GridCell(2, 7)));

        // With the maps' own cells removed, each map falls back to where the first warp onto it arrives.
        var stripped = CombatContent.RespawnPoints(content.Maps.All.Select(m => m with { Respawn = null }).ToList(), content.Grids);
        stripped["map.thistle_hollow"].ShouldBe(("map.thistle_hollow", new GridCell(2, 7)));     // the meadow's warp 46,16 -> hollow 2,7
        stripped["map.starter_meadow"].ShouldBe(("map.starter_meadow", new GridCell(45, 16)));   // the hollow's warp 1,7 -> meadow 45,16

        // A map nothing leads to uses the new_character start.
        var combat = TestCombat.Content(TestMonsters.Catalog(), TestCombat.Balance());
        combat.RespawnPointFor("map.nowhere").ShouldBe((TestCombat.MapId, new GridCell(1, 1)));
    }
}
