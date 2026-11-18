using Game.Server.Events;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Effects;
using Game.Server.World.Inventory;
using Game.Server.World.Monsters;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Random;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>Test skills and statuses for the effect runner (T-501), added to the sample content's.</summary>
public static class TestEffects
{
    public const string Poison = "status.t_poison";
    public const string Stack = "status.t_stack";
    public const string Ward = "status.t_ward";

    public static StatusDef PoisonDef => new()
    {
        Id = Poison, Name = "P", Debuff = true, Tags = ["poison"], MaxStacks = 1, TickIntervalMs = 1000,
        OnApply = [new ModifyStatEffect { Modifier = new ModifierDef { Stat = DerivedStats.Def, Op = ModifierOp.Percent, Value = "-50" } }],
        OnTick = [new DamageEffect { Multiplier = "0.5", Element = Elements.Poison }],
    };

    public static StatusDef StackDef => new()
    {
        Id = Stack, Name = "S", Tags = ["buff"], MaxStacks = 3,
        OnApply = [new ModifyStatEffect { Modifier = new ModifierDef { Stat = DerivedStats.Atk, Op = ModifierOp.Flat, Value = "5" } }],
    };

    public static StatusDef WardDef => new() { Id = Ward, Name = "W", Tags = ["buff"] };

    public static SkillDef Skill(string id, int range, params EffectDef[] effects) =>
        new() { Id = id, Name = id, Target = new SkillTargetDef { Range = range }, Effects = effects };

    public static SkillDef Friendly(string id, int range, params EffectDef[] effects) =>
        new() { Id = id, Name = id, Target = new SkillTargetDef { Range = range, Friendly = true }, Effects = effects };

    public static ApplyStatusEffect Apply(string status, string duration = "5000", string chance = "1") =>
        new() { StatusId = status, DurationMs = duration, Chance = chance };

    public static readonly SkillDef[] Skills =
    [
        Skill("skill.t_bolt", 3, new DamageEffect { Magic = true, Multiplier = "1" }),
        Skill("skill.t_poison", 3, Apply(Poison, "3000")),
        Skill("skill.t_stun", 3, Apply("status.stun", "2000")),
        Skill("skill.t_chance", 3, Apply(Poison, "3000", "0.5")),
        Skill("skill.t_push", 5, new KnockbackEffect { Cells = 3 }),
        Skill("skill.t_blast", 0, new AreaEffect { Radius = 2, Effects = [new DamageEffect { Magic = true, Multiplier = "1" }, new KnockbackEffect { Cells = 1 }] }),
        Friendly("skill.t_heal", 0, new HealEffect { Amount = "30" }, new RestoreSpEffect { Amount = "10 + level" }),
        Friendly("skill.t_mend", 0, new AreaEffect { Radius = 2, Friendly = true, Effects = [new HealEffect { Amount = "5" }] }),
        Friendly("skill.t_stack", 0, Apply(Stack, "5000")),
        Friendly("skill.t_ward", 0, Apply(Ward, "2000")),
        Friendly("skill.t_cleanse", 0, new RemoveStatusEffect { Tags = ["poison"] }),
        Friendly("skill.t_unward", 0, new RemoveStatusEffect { StatusIds = [Ward] }),
        Friendly("skill.t_call", 0, new SummonEffect { MonsterId = "monster.t_minion", Count = 2, DurationMs = 3000 }),
        Friendly("skill.t_blink", 0, new TeleportEffect { X = 8, Y = 1 }),
        Friendly("skill.t_away", 0, new TeleportEffect { MapId = "map.other", X = 2, Y = 2 }),
        Friendly("skill.t_home", 0, new TeleportEffect { ToSavePoint = true }),
        Friendly("skill.t_random", 0, new TeleportEffect()),
    ];

    public static readonly Lazy<EffectLibrary> Library = new(() =>
    {
        var sample = TestGrids.SampleContent;
        return new EffectLibrary(sample.Skills.All.Concat(Skills), sample.Items.All, sample.Statuses.All.Concat([PoisonDef, StackDef, WardDef]));
    });

    public static readonly MonsterDef Minion = TestCombat.Monster("monster.t_minion", maxHp: 30);
}

public class EffectTests
{
    private static readonly MonsterDef Dummy = TestCombat.Monster("monster.dummy", maxHp: 1000, aspd: 0.001);

    private static CombatRig Rig(MapGrid? grid = null, bool ai = false, MonsterDef[]? monsters = null, MapGrid[]? more = null,
        IReadOnlyDictionary<string, (string MapId, GridCell Cell)>? respawns = null, ulong seed = 1) =>
        new(grid ?? TestCombat.Open(12, 8), monsters ?? [Dummy, TestEffects.Minion], ai: ai, effects: TestEffects.Library.Value, moreMaps: more,
            respawns: respawns, seed: seed);

    private static void Cast(CombatRig rig, EntityId caster, string skill, EntityId? target = null, GridCell? cell = null, int level = 1) =>
        rig.Instance.Events.Publish(new SkillRequested(caster, skill, level, target, cell, rig.Now));

    /// <summary>Publishes a request and runs two ticks: dispatched at the end of the first, resolved by combat in the second.</summary>
    private static void CastNow(CombatRig rig, EntityId caster, string skill, EntityId? target = null, GridCell? cell = null, int level = 1)
    {
        Cast(rig, caster, skill, target, cell, level);
        rig.Ticks(2);
    }

    private static (CombatRig Rig, EntityId Player, EntityId Monster) Duo(GridCell? monsterCell = null)
    {
        var rig = Rig();
        var player = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0, spRegen: 0, maxSp: 100));
        var monster = rig.AddMonster(Dummy, monsterCell ?? new GridCell(3, 2));
        rig.Ticks();
        return (rig, player, monster);
    }

    // ---- damage, heal, restore_sp ----

    [Fact]
    public void Damage_goes_through_the_resolver_and_raises_DamageDealt_with_EXP_credit()
    {
        var (rig, player, monster) = Duo();
        CastNow(rig, player, "skill.t_bolt", monster);
        var hit = rig.Of<DamageDealt>().Single();
        hit.Source.ShouldBe(player);
        hit.Result.ShouldBe(HitResults.Hit);   // magic always lands
        hit.Amount.ShouldBeGreaterThan(0);
        rig.Hp(monster).ShouldBe(1000 - hit.Amount);
        rig.Combat.DamageLog(monster).Single().ShouldBe((player, (long)hit.Amount));   // the same EXP-share log as auto-attacks
        rig.Of<SkillUsed>().Single().SkillId.ShouldBe("skill.t_bolt");
    }

    [Fact]
    public void Heal_and_restore_sp_use_their_formulas_and_stop_at_max()
    {
        var (rig, player, _) = Duo();
        var vitals = rig.Get(player).Vitals!;
        vitals.Hp = 480;
        vitals.Sp = 0;
        CastNow(rig, player, "skill.t_heal", level: 3);
        vitals.Hp.ShouldBe(500);
        vitals.Sp.ShouldBe(13);   // 10 + level
        rig.Of<HpRestored>().Single().Amount.ShouldBe(20);
        rig.Of<SpRestored>().Single().Amount.ShouldBe(13);
    }

    // ---- statuses ----

    [Fact]
    public void A_status_applies_its_modifier_ticks_on_the_tick_clock_and_expires_cleanly()
    {
        var (rig, player, monster) = Duo();
        var sheet = rig.Get(monster).Stats!;
        var def = sheet.Get(DerivedStats.Def);
        rig.Get(player).Stats!.Modifiers.Add("test", DerivedStats.Atk, ModifierOp.Override, 40);
        CastNow(rig, player, "skill.t_poison", monster);
        var applied = rig.Of<StatusApplied>().Single();
        applied.Change.ShouldBe(StatusChange.Applied);
        sheet.Modifiers.HasSource("status." + "t_poison").ShouldBeFalse();
        sheet.Modifiers.HasSource("status:" + TestEffects.Poison).ShouldBeTrue();
        sheet.Get(DerivedStats.Def).ShouldBe(def * 0.5, 1e-9);

        rig.Ticks(40);
        var ticks = rig.Of<DamageDealt>().Where(d => d.Result == HitResults.Status).ToList();
        ticks.Count.ShouldBe(3);   // 3000 ms at 1000 ms: one at each second, the last at the end
        ticks.ShouldAllBe(t => t.Source == player);
        rig.Of<StatusExpired>().Single().StatusId.ShouldBe(TestEffects.Poison);
        sheet.Modifiers.HasSource("status:" + TestEffects.Poison).ShouldBeFalse();
        sheet.Get(DerivedStats.Def).ShouldBe(def, 1e-9);
        rig.Get(monster).Statuses!.IsEmpty.ShouldBeTrue();
        var timed = rig.TimedOf<DamageDealt>().Where(d => d.Event.Result == HitResults.Status).Select(d => d.Tick).ToList();
        (timed[1] - timed[0]).ShouldBe(10);   // 1000 ms of simulated time = 10 ticks
    }

    [Fact]
    public void Stacks_add_up_to_max_stacks_then_refresh_and_never_shorten()
    {
        var (rig, player, _) = Duo();
        var sheet = rig.Get(player).Stats!;
        var atk = sheet.Get(DerivedStats.Atk);
        for (var i = 0; i < 4; i++) CastNow(rig, player, "skill.t_stack");
        rig.Of<StatusApplied>().Select(a => (a.Change, a.Stacks)).ShouldBe([
            (StatusChange.Applied, 1), (StatusChange.Stacked, 2), (StatusChange.Stacked, 3), (StatusChange.Refreshed, 3)]);
        sheet.Get(DerivedStats.Atk).ShouldBe(atk + 15, 1e-9);   // on_apply once per stack
        var active = rig.Get(player).Statuses!.Find(TestEffects.Stack)!;
        active.EndsAtMs.ShouldBe(rig.Now - TickRate.IntervalMs + 5000);   // refreshed by the last cast (resolved on the last tick)
        rig.TickUntil(() => rig.Of<StatusExpired>().Count > 0);
        sheet.Get(DerivedStats.Atk).ShouldBe(atk, 1e-9);
    }

    [Fact]
    public void Remove_status_cleanses_by_tag_and_by_id_and_takes_the_modifiers_off()
    {
        var (rig, player, _) = Duo();
        var sheet = rig.Get(player).Stats!;
        var def = sheet.Get(DerivedStats.Def);
        CastNow(rig, player, "skill.t_ward");
        // A monster poisons the player (a hostile skill from the monster's side).
        var caster = rig.AddMonster(Dummy, new GridCell(2, 3));
        rig.Ticks();
        CastNow(rig, caster, "skill.t_poison", player);
        rig.Get(player).Statuses!.Active.Select(a => a.Id).ShouldBe([TestEffects.Ward, TestEffects.Poison]);
        sheet.Get(DerivedStats.Def).ShouldBe(def * 0.5, 1e-9);

        CastNow(rig, player, "skill.t_cleanse");   // tag poison
        rig.Get(player).Statuses!.Active.Select(a => a.Id).ShouldBe([TestEffects.Ward]);
        sheet.Get(DerivedStats.Def).ShouldBe(def, 1e-9);
        CastNow(rig, player, "skill.t_unward");    // by id
        rig.Get(player).Statuses!.IsEmpty.ShouldBeTrue();
        rig.Of<StatusRemoved>().Select(r => (r.StatusId, r.Reason)).ShouldBe([
            (TestEffects.Poison, StatusEndReasons.Removed), (TestEffects.Ward, StatusEndReasons.Removed)]);
    }

    [Fact]
    public void Statuses_end_on_death_with_their_modifiers()
    {
        var (rig, player, monster) = Duo();
        CastNow(rig, player, "skill.t_poison", monster);
        rig.Get(monster).Vitals!.Hp = 1;
        rig.TickUntil(() => rig.Get(monster).Dead, 30, "the poison kills it");
        rig.Of<StatusRemoved>().Single().ShouldBe(new StatusRemoved(monster, TestEffects.Poison, StatusEndReasons.Died));
        rig.Get(monster).Stats!.Modifiers.HasSource("status:" + TestEffects.Poison).ShouldBeFalse();
        rig.Of<EntityDied>().Single().Killer.ShouldBe(player);   // the applier gets the kill
        rig.Of<ExpGained>().Single().Player.ShouldBe(player);
    }

    [Fact]
    public void A_chance_below_one_rolls_the_instance_random_and_is_the_same_for_a_seed()
    {
        static int Applied(ulong seed)
        {
            var rig = Rig(seed: seed);
            var player = rig.AddPlayer(new GridCell(2, 2));
            var monster = rig.AddMonster(Dummy, new GridCell(3, 2));
            rig.Ticks();
            for (var i = 0; i < 40; i++)
            {
                Cast(rig, player, "skill.t_chance", monster);
                rig.Ticks(40);   // let it expire between tries
            }
            return rig.Of<StatusApplied>().Count;
        }

        var a = Applied(7);
        a.ShouldBeInRange(10, 30);   // about half of 40
        Applied(7).ShouldBe(a);
    }

    // ---- prevents flags ----

    [Fact]
    public void A_stun_refuses_moves_attacks_and_skills_with_their_reasons_and_stops_a_walker()
    {
        var (rig, player, monster) = Duo();
        var stunner = rig.AddMonster(Dummy, new GridCell(2, 3));
        rig.Ticks();
        rig.Send(new MoveTo(player, new GridCell(10, 6)));
        rig.Ticks(2);
        CastNow(rig, stunner, "skill.t_stun", player);
        rig.Ticks();
        rig.Of<MovementStopped>().Last().Reason.ShouldBe(MovementReasons.CannotMove);

        rig.Send(new MoveTo(player, new GridCell(1, 1)));
        rig.Send(new AttackTarget(player, monster));
        Cast(rig, player, "skill.t_bolt", monster);
        rig.Ticks(2);
        rig.Of<MoveRejected>().Single().Reason.ShouldBe(MovementReasons.CannotMove);
        rig.Of<AttackRefused>().Single().Reason.ShouldBe(AttackReasons.CannotAttack);
        rig.Of<SkillRefused>().Single().Reason.ShouldBe(SkillReasons.CannotUseSkills);

        rig.TickUntil(() => rig.Of<StatusExpired>().Count > 0, 40);
        rig.Send(new MoveTo(player, new GridCell(1, 1)));
        rig.Ticks(2);
        rig.Of<MovementStarted>().Last().Entity.ShouldBe(player);
    }

    // ---- knockback, area, summon, teleport ----

    [Fact]
    public void Knockback_pushes_away_from_the_caster_and_stops_before_a_wall()
    {
        var grid = TestCombat.Grid([
            "............",
            "............",
            "......#.....",
            "............",
        ]);
        var rig = Rig(grid);
        var player = rig.AddPlayer(new GridCell(2, 2));
        var monster = rig.AddMonster(Dummy, new GridCell(4, 2));
        var free = rig.AddMonster(Dummy, new GridCell(5, 1));
        rig.Ticks();
        CastNow(rig, player, "skill.t_push", monster);
        rig.Get(monster).Cell.ShouldBe(new GridCell(5, 2));   // 3 cells asked, the wall at (6, 2) stops it after one
        rig.Of<KnockedBack>().Single().ShouldBe(new KnockedBack(player, monster, new GridCell(4, 2), new GridCell(5, 2)));
        CastNow(rig, player, "skill.t_push", free);
        rig.Get(free).Cell.ShouldBe(new GridCell(8, 1));   // (3, -1) away: the nearest direction is e, and all 3 cells are free
    }

    [Fact]
    public void An_area_hits_the_other_side_in_its_square_by_distance_then_id_and_a_friendly_one_hits_allies()
    {
        var rig = Rig(TestCombat.Open(12, 8));
        var player = rig.AddPlayer(new GridCell(5, 4), TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0));
        var ally = rig.AddPlayer(new GridCell(6, 5), TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0));
        var far = rig.AddMonster(Dummy, new GridCell(7, 4));      // distance 2
        var near = rig.AddMonster(Dummy, new GridCell(4, 4));     // distance 1
        var near2 = rig.AddMonster(Dummy, new GridCell(6, 3));    // distance 1, later id
        var outside = rig.AddMonster(Dummy, new GridCell(8, 4));  // distance 3
        rig.Ticks();
        CastNow(rig, player, "skill.t_blast");
        rig.Of<DamageDealt>().Select(d => d.Target).ShouldBe([near, near2, far]);
        rig.Hp(outside).ShouldBe(1000);
        rig.Of<AreaApplied>().Single().Targets.ShouldBe(3);
        rig.Get(near).Cell.ShouldBe(new GridCell(3, 4));          // pushed away from the center

        rig.Get(player).Vitals!.Hp = 100;
        rig.Get(ally).Vitals!.Hp = 100;
        CastNow(rig, player, "skill.t_mend");
        rig.Of<HpRestored>().Select(h => h.Target).ShouldBe([player, ally]);
    }

    [Fact]
    public void A_summon_spawns_owned_monsters_up_to_the_effects_count_and_dismisses_them_after_their_time()
    {
        var (rig, player, _) = Duo();
        CastNow(rig, player, "skill.t_call");
        rig.Ticks();
        var summoned = rig.Of<MonsterSummoned>();
        summoned.Count.ShouldBe(2);
        summoned.ShouldAllBe(s => s.Owner == player && s.MonsterId == "monster.t_minion");
        rig.Get(summoned[0].Monster).Owner.ShouldBe(player);
        CastNow(rig, player, "skill.t_call");   // the cap: two live already
        rig.Ticks();
        rig.Of<MonsterSummoned>().Count.ShouldBe(2);
        rig.Spawner.SummonCount.ShouldBe(2);
        rig.Ticks(32);
        rig.Has(summoned[0].Monster).ShouldBeFalse();
        rig.Spawner.SummonCount.ShouldBe(0);
    }

    [Fact]
    public void Teleport_goes_to_a_cell_a_random_walkable_cell_or_the_save_point_through_the_warp_path()
    {
        var respawns = new Dictionary<string, (string MapId, GridCell Cell)> { [TestCombat.MapId] = (TestCombat.MapId, new GridCell(1, 6)) };
        var rig = Rig(respawns: respawns);
        var player = rig.AddPlayer(new GridCell(2, 2));
        rig.Ticks();
        CastNow(rig, player, "skill.t_blink");
        rig.Get(player).Cell.ShouldBe(new GridCell(8, 1));
        CastNow(rig, player, "skill.t_home");
        rig.Get(player).Cell.ShouldBe(new GridCell(1, 6));
        CastNow(rig, player, "skill.t_random");
        rig.Of<Teleported>().Count.ShouldBe(3);
        rig.Instance.Grid.IsWalkable(rig.Get(player).Cell.X, rig.Get(player).Cell.Y).ShouldBeTrue();
        rig.Of<EntityEnteredMap>().Count(e => e.Reason == MapChangeReason.Warp).ShouldBe(3);
    }

    [Fact]
    public void Statuses_end_when_their_bearer_arrives_on_another_map_and_stay_through_a_same_map_warp()
    {
        var other = MapGrid.Create(new MapGridFile
        {
            MapId = "map.other", Width = 6, Height = 6, Cells = Convert.ToBase64String(Enumerable.Repeat(CellFlagBits.Walkable, 36).ToArray()),
        });
        var rig = Rig(more: [other]);
        var removed = new List<StatusRemoved>();
        Other(rig).Events.Subscribe<StatusRemoved>(removed.Add);
        var player = rig.AddPlayer(new GridCell(2, 2));
        rig.Ticks();
        CastNow(rig, player, "skill.t_stack");
        CastNow(rig, player, "skill.t_blink");   // same map
        rig.Get(player).Statuses!.IsEmpty.ShouldBeFalse();
        var sheet = rig.Get(player).Stats!;
        CastNow(rig, player, "skill.t_away");
        Other(rig).Tick();   // arrives; combat ends its statuses in phase 4
        Other(rig).Tick();   // and the event published there is dispatched now
        removed.Single().ShouldBe(new StatusRemoved(player, TestEffects.Stack, StatusEndReasons.Warped));
        sheet.Modifiers.HasSource("status:" + TestEffects.Stack).ShouldBeFalse();
    }

    private static MapInstance Other(CombatRig rig) => rig.World.Instances.First(i => i.MapId == "map.other");

    // ---- consumables ----

    [Fact]
    public void The_antidote_and_the_swift_elixir_work_through_the_runner()
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet(aspd: null, speed: null));
        rig.Ticks();
        var elixir = rig.Give(player, "item.swift_elixir");
        var antidote = rig.Give(player, "item.antidote");
        var sheet = rig.Get(player).Stats!;
        var speed = sheet.Get(DerivedStats.MoveSpeed);
        rig.Send(new UseItem(player, elixir));
        rig.Ticks(2);
        rig.Of<StatusApplied>().Single().StatusId.ShouldBe("status.swift");
        sheet.Get(DerivedStats.MoveSpeed).ShouldNotBe(speed);
        rig.State(player).Find(elixir).ShouldBeNull();   // used up, nothing refused

        // Poison the player the way the wasp does, then cure it.
        var library = TestMonsters.Effects.Value;
        library.TryGetStatus("status.poison", out var poison).ShouldBeTrue();
        var actor = new EffectActor(player.Value, sheet, new GridCell(2, 2), Directions.S, false, true);
        rig.Get(player).Statuses!.Apply(poison, actor, 1, rig.Now, 8000, out _);
        rig.Send(new UseItem(player, antidote));
        rig.Ticks(2);
        rig.Get(player).Statuses!.Find("status.poison").ShouldBeNull();
        rig.Of<ItemRefused>().ShouldBeEmpty();
    }

    // ---- monster skills ----

    [Fact]
    public void A_monster_casts_its_skill_with_its_cast_time_and_cooldown_and_combat_resolves_it()
    {
        var caster = TestCombat.Monster("monster.t_caster", maxHp: 500, aspd: 2, aggressive: true, aggroRange: 5) with
        {
            Ai = new MonsterAiDef { Aggressive = true, AggroRange = 5, ChaseRange = 30, Skills = [new MonsterSkillDef { SkillId = "skill.venom_sting", Chance = 1 }] },
        };
        var rig = Rig(ai: true, monsters: [caster]);
        var player = rig.AddPlayer(new GridCell(4, 4), TestCombat.PlayerSheet(maxHp: 5000, hpRegen: 0, flee: 0));
        rig.AddMonster(caster, new GridCell(5, 4));
        rig.Ticks();
        rig.Instance.Events.Publish(new EntitySpawned(rig.Instance.Entities.First(e => e.Kind == EntityKind.Monster).Id, EntityKind.Monster,
            caster.Id, new GridCell(5, 4), -1));
        rig.TickUntil(() => rig.Of<SkillUsed>().Count > 0, 100, "a venom sting");
        var started = rig.TimedOf<SkillCastStarted>().First();
        started.Event.SkillId.ShouldBe("skill.venom_sting");
        started.Event.CastMs.ShouldBeInRange(1, 300);   // 300 × cast_time_mod
        rig.Of<SkillCastEnded>().First().ShouldBe(new SkillCastEnded(started.Event.Caster, "skill.venom_sting", Game.Shared.Protocol.CastResults.Done));
        var requested = rig.TimedOf<SkillRequested>().First();
        (requested.Tick - started.Tick).ShouldBeInRange(2, 4);   // the cast time passes on the tick clock
        rig.Of<DamageDealt>().ShouldContain(d => d.Target == player);
        // Cooldown 5000 ms: the next cast is at least 50 ticks after the first request.
        rig.TickUntil(() => rig.Of<SkillRequested>().Count > 1, 200, "a second sting");
        var requests = rig.TimedOf<SkillRequested>();
        (requests[1].Tick - requests[0].Tick).ShouldBeGreaterThanOrEqualTo(50);
        rig.Of<AttackRequested>().ShouldNotBeEmpty();   // auto-attacks in between
    }

    // ---- golden values ----

    /// <summary>
    /// One Venom Sting and one poison tick by the real Sting Wasp on a new character, seed 1, against values worked out from formulas.yaml:
    /// physical_damage = max(1, floor((atk + weapon_atk + refine*2) * 100 / (100 + t_def*3))), × the skill multiplier × poison vs neutral.
    /// </summary>
    [Fact]
    public void Golden_venom_sting_and_poison_tick()
    {
        var sample = TestGrids.SampleContent;
        var wasp = sample.Monsters.Get("monster.sting_wasp");
        var catalog = TestMonsters.Catalog(wasp);
        var waspSheet = catalog.CreateStats(wasp);
        var player = TestCombat.PlayerSheet(aspd: null, speed: null);
        var rules = CombatRules.Compile(sample.Formulas, sample.Elements, sample.Balance.Combat);
        var world = new GoldenWorld(waspSheet, wasp, player);
        var runner = new EffectRunner(rules, catalog.Effects, world, new Pcg32Random(1));
        catalog.Effects.TryGetSkill("skill.venom_sting", out var sting).ShouldBeTrue();
        runner.Run(sting.Effects, world.Actor(2), 1, null, 1);

        var atk = waspSheet.Get(DerivedStats.Atk);
        var def = player.Get(DerivedStats.Def);
        var poisonVsNeutral = sample.Elements.Multipliers[Elements.Poison][Elements.Neutral];
        var physical = Math.Max(1, Math.Floor(atk * 100 / (100 + def * 3)));
        world.Hits.Count.ShouldBeGreaterThanOrEqualTo(1);
        var sting1 = world.Hits[0];
        if (sting1.Hit) sting1.Damage.ShouldBe((int)Math.Floor(physical * 1.2 * poisonVsNeutral + 1e-9));
        sting1.Hit.ShouldBeTrue();
        sting1.Damage.ShouldBe(12);   // golden: the wasp's atk vs a new character's def, × 1.2, poison vs neutral

        // One poison tick on a poisoned new character (DEF −25% while poisoned), always landing.
        catalog.Effects.TryGetStatus("status.poison", out var poison).ShouldBeTrue();
        var set = world.StatusesOf(1, true)!;
        if (set.Find("status.poison") is null)
        {
            set.Apply(poison, world.Actor(2), 1, 0, 8000, out _);
            player.Modifiers.Add(poison.ModifierSource, CompiledModifier.Compile(((ModifyStatEffect)poison.Def.OnApply[0]).Modifier), new Game.Shared.Formulas.FormulaContext());
        }
        world.Hits.Clear();
        world.NowMs = 1000;
        runner.TickStatuses(1);
        var tick = world.Hits.Single();
        tick.Hit.ShouldBeTrue();
        var poisonedDef = player.Get(DerivedStats.Def);
        tick.Damage.ShouldBe((int)Math.Max(1, Math.Floor(Math.Max(1, Math.Floor(atk * 100 / (100 + poisonedDef * 3))) * 0.15 * poisonVsNeutral + 1e-9)));
        tick.Damage.ShouldBe(1);   // golden
    }

    private sealed class GoldenWorld : IEffectWorld
    {
        private readonly EffectActor _player;
        private readonly EffectActor _wasp;
        private readonly StatusSet _statuses = new();

        public GoldenWorld(StatSheet wasp, MonsterDef def, StatSheet player)
        {
            _player = new EffectActor(1, player, new GridCell(0, 0), Directions.E, false, true);
            _wasp = new EffectActor(2, wasp, new GridCell(1, 0), Directions.W, false, false, def.Element, def.Element, def.Race, def.Size);
        }

        public readonly List<AttackResult> Hits = new();

        public long NowMs { get; set; }

        public MapGrid? Grid => null;

        public EffectActor Actor(long id) => id == 1 ? _player : _wasp;

        public bool TryGetActor(long id, out EffectActor actor)
        {
            actor = Actor(id);
            return id is 1 or 2;
        }

        public StatusSet? StatusesOf(long id, bool create) => id == 1 ? _statuses : null;

        public void CollectArea(in EffectActor caster, GridCell center, int radius, bool friendly, List<long> into) => into.Clear();

        public void Damage(in EffectActor caster, long target, AttackResult result, bool fromStatus) => Hits.Add(result);

        public void Heal(long caster, long target, int amount) { }

        public void RestoreSp(long caster, long target, int amount) { }

        public void StatusStarted(long caster, long target, ActiveStatus status, StatusChange change) { }

        public void StatusEnded(long target, ActiveStatus status, string? reason) { }

        public void Knockback(long caster, long target, GridCell from, GridCell to) { }

        public void Teleport(long caster, TeleportEffect effect) { }

        public void Summon(long caster, SummonEffect effect, GridCell near) { }

        public void ResetStats(long caster, long target) { }

        public void ResetSkills(long caster, long target) { }

        public void AreaApplied(long caster, GridCell center, int radius, int targets) { }
    }
}
