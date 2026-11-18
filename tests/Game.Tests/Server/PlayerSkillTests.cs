using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Effects;
using Game.Server.World.Inventory;
using Game.Server.World.Monsters;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Skills;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>Player skills (T-502): learning, job change, casting with cost, cast time and cooldown, passives, and allied summons.</summary>
public class PlayerSkillTests
{
    private static readonly MonsterDef Dummy = TestCombat.Monster("monster.dummy", maxHp: 1000, aspd: 0.001);

    private static readonly MonsterDef Weak = TestCombat.Monster("monster.weak", maxHp: 40, aspd: 0.001);

    private static SkillDef Skill(string id, int range, string sp = "0", string cast = "0", string cooldown = "0", bool friendly = false, bool ground = false,
        string[]? weapons = null, params EffectDef[] effects) => new()
    {
        Id = id, Name = id, MaxLevel = 5, SpCost = sp, CastTimeMs = cast, CooldownMs = cooldown, WeaponTypes = weapons ?? [],
        Target = new SkillTargetDef { Range = range, Friendly = friendly, Ground = ground }, Effects = effects,
    };

    private static readonly SkillDef[] Skills =
    [
        Skill("skill.p_strike", 1, sp: "5 + level", cooldown: "2000", effects: new DamageEffect { Multiplier = "1" }),
        Skill("skill.p_fire", 5, sp: "10", cast: "1000", cooldown: "3000", effects: new DamageEffect { Magic = true, Multiplier = "1" }),
        Skill("skill.p_instant", 5, effects: new DamageEffect { Magic = true, Multiplier = "1" }),
        Skill("skill.p_staff", 5, weapons: ["staff"], effects: new DamageEffect { Magic = true, Multiplier = "1" }),
        Skill("skill.p_mend", 0, friendly: true, effects: new HealEffect { Amount = "10" }),
        Skill("skill.p_ally", 5, friendly: true, effects: new HealEffect { Amount = "10" }),
        Skill("skill.p_ground", 5, ground: true, cast: "500", effects: new AreaEffect { Radius = 1, Effects = [new DamageEffect { Magic = true, Multiplier = "1" }] }),
        Skill("skill.p_slam", 0, effects: new AreaEffect { Radius = 2, Effects = [new DamageEffect { Magic = true, Multiplier = "1" }] }),
        new SkillDef { Id = "skill.p_passive", Name = "P", MaxLevel = 5, Passive = true, PassiveModifiers = [new ModifierDef { Stat = DerivedStats.Atk, Op = ModifierOp.Flat, Value = "level * 3" }] },
    ];

    private static readonly Lazy<EffectLibrary> Library = new(() =>
    {
        var sample = TestGrids.SampleContent;
        return new EffectLibrary(sample.Skills.All.Concat(Skills), sample.Items.All, sample.Statuses.All);
    });

    private static CombatRig Rig(MapGrid? grid = null, bool ai = false, MonsterDef[]? monsters = null, ulong seed = 1) =>
        new(grid ?? TestCombat.Open(14, 8), monsters ?? [Dummy, TestEffects.Minion], ai: ai, effects: Library.Value, seed: seed);

    private static (CombatRig Rig, EntityId Player, EntityId Monster, ProgressionState State) Duo(int sp = 100, GridCell? monsterCell = null, MapGrid? grid = null)
    {
        var rig = Rig(grid);
        var player = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0, spRegen: 0, maxSp: sp));
        var monster = rig.AddMonster(Dummy, monsterCell ?? new GridCell(3, 2));
        rig.Ticks();
        rig.Progressions.TryGet(player, out var state).ShouldBeTrue();
        foreach (var skill in Skills) state.Skills[skill.Id] = 3;
        return (rig, player, monster, state);
    }

    private static void Use(CombatRig rig, EntityId player, string skill, EntityId? target = null, GridCell? cell = null, int level = 0) =>
        rig.Send(new UseSkill(player, skill, level, target, cell));

    private static string? Refusal(CombatRig rig) => rig.Of<SkillRefused>().LastOrDefault()?.Reason;

    // ---- learning ----

    [Fact]
    public void Learn_spends_one_point_and_raises_the_level_by_one()
    {
        var (rig, player, _, state) = Duo();
        state.Skills.Clear();
        state.SkillPoints = 2;
        rig.Send(new LearnSkill(player, "skill.second_wind"));
        rig.Ticks(2);
        state.SkillLevel("skill.second_wind").ShouldBe(1);
        state.SkillPoints.ShouldBe(1);
        rig.Of<PlayerProgressed>().Select(p => p.Change).OfType<SkillLearned>().Single().ShouldBe(new SkillLearned("skill.second_wind", 1));
        rig.Send(new LearnSkill(player, "skill.second_wind"));
        rig.Ticks(2);
        state.SkillLevel("skill.second_wind").ShouldBe(2);
        state.SkillPoints.ShouldBe(0);
    }

    [Theory]
    [InlineData("skill.second_wind", 0, "job.wanderer", SkillReasons.NotEnoughPoints)]
    [InlineData("skill.twin_strike", 3, "job.wanderer", SkillReasons.NotInTree)]
    [InlineData("skill.nothing", 3, "job.wanderer", SkillReasons.UnknownSkill)]
    [InlineData("skill.mana_font", 3, "job.spellbinder", SkillReasons.PrerequisiteNotMet)]
    public void Learn_refusals_each_have_a_reason(string skill, int points, string job, string reason)
    {
        var (rig, player, _, state) = Duo();
        state.Skills.Clear();
        state.JobId = job;
        state.SkillPoints = points;
        rig.Send(new LearnSkill(player, skill));
        rig.Ticks(2);
        rig.Of<SkillLearnRefused>().Single().Reason.ShouldBe(reason);
        state.SkillPoints.ShouldBe(points);
        state.Skills.ShouldBeEmpty();
    }

    [Fact]
    public void Learn_stops_at_the_trees_max_level_and_a_met_prerequisite_unlocks_the_next_skill()
    {
        var (rig, player, _, state) = Duo();
        state.Skills.Clear();
        state.JobId = "job.spellbinder";
        state.SkillPoints = 20;
        for (var i = 0; i < 2; i++) { rig.Send(new LearnSkill(player, "skill.ember_bolt")); rig.Ticks(2); }
        rig.Send(new LearnSkill(player, "skill.mana_font"));
        rig.Ticks(2);
        state.SkillLevel("skill.mana_font").ShouldBe(1);   // ember_bolt 2 is the requirement
        state.Skills["skill.cleanse"] = 0;
        rig.Send(new LearnSkill(player, "skill.cleanse"));
        rig.Ticks(2);
        rig.Send(new LearnSkill(player, "skill.cleanse"));
        rig.Ticks(2);
        rig.Of<SkillLearnRefused>().Single().Reason.ShouldBe(SkillReasons.AtMaxLevel);   // cleanse has max level 1
    }

    // ---- job change ----

    [Fact]
    public void Job_change_swaps_the_job_keeps_skills_and_points_and_restarts_the_job_level()
    {
        var (rig, player, _, state) = Duo();
        state.BaseLevel = 5;
        state.JobLevel = 6;
        state.JobExp = 40;
        state.SkillPoints = 3;
        state.Skills.Clear();
        state.Skills["skill.second_wind"] = 2;
        rig.Send(new ChangeJob(player, "job.spellbinder"));
        rig.Ticks(2);

        state.JobId.ShouldBe("job.spellbinder");
        (state.JobLevel, state.JobExp).ShouldBe((1, 0L));
        state.SkillPoints.ShouldBe(3);
        state.SkillLevel("skill.second_wind").ShouldBe(2);
        state.BaseLevel.ShouldBe(5);
        var entity = rig.Get(player);
        entity.DefinitionId.ShouldBe("job.spellbinder");
        entity.Stats!.Modifiers.HasSource("job:job.spellbinder").ShouldBeTrue();
        entity.Stats.Modifiers.HasSource("job:job.wanderer").ShouldBeFalse();
        rig.Of<PlayerProgressed>().Select(p => p.Change).OfType<JobChanged>().Single().ShouldBe(new JobChanged("job.wanderer", "job.spellbinder"));
    }

    [Theory]
    [InlineData("job.spellbinder", 4, 5, SkillReasons.BaseLevelTooLow)]
    [InlineData("job.spellbinder", 5, 4, SkillReasons.JobLevelTooLow)]
    [InlineData("job.warden", 10, 8, SkillReasons.QuestNotCompleted)]
    [InlineData("job.wanderer", 10, 8, SkillReasons.SameJob)]
    [InlineData("job.nobody", 10, 8, SkillReasons.UnknownJob)]
    public void Job_change_refusals_each_have_a_reason(string job, int baseLevel, int jobLevel, string reason)
    {
        var (rig, player, _, state) = Duo();
        state.BaseLevel = baseLevel;
        state.JobLevel = jobLevel;
        rig.Send(new ChangeJob(player, job));
        rig.Ticks(2);
        rig.Of<JobChangeRefused>().Single().Reason.ShouldBe(reason);
        state.JobId.ShouldBe("job.wanderer");
    }

    [Fact]
    public void A_job_that_needs_an_unlock_is_refused_until_it_is_unlocked_and_a_job_cannot_skip_its_parent()
    {
        var (rig, player, _, state) = Duo();
        state.BaseLevel = 10;
        state.JobLevel = 8;
        state.CompletedQuests.Add("quest.warden_trial");
        rig.Send(new ChangeJob(player, "job.warden"));
        rig.Ticks(2);
        rig.Of<JobChangeRefused>().Single().Reason.ShouldBe(SkillReasons.NotUnlocked);
        state.UnlockedJobs.Add("job.warden");
        rig.Send(new ChangeJob(player, "job.warden"));
        rig.Ticks(2);
        state.JobId.ShouldBe("job.warden");
        rig.Send(new ChangeJob(player, "job.spellbinder"));   // its parent is wanderer, not warden
        rig.Ticks(2);
        rig.Of<JobChangeRefused>().Last().Reason.ShouldBe(SkillReasons.WrongParentJob);
    }

    // ---- casting ----

    [Fact]
    public void An_instant_skill_spends_SP_and_starts_the_cooldown_when_it_is_used()
    {
        var (rig, player, monster, _) = Duo();
        var vitals = rig.Get(player).Vitals!;
        Use(rig, player, "skill.p_strike", monster, level: 2);
        rig.Ticks(3);
        vitals.Sp.ShouldBe(100 - 7);   // 5 + level
        rig.Of<SkillUsed>().Single().Level.ShouldBe(2);
        rig.Hp(monster).ShouldBeLessThan(1000);
        rig.Of<SkillCooldownStarted>().Single().ShouldBe(new SkillCooldownStarted(player, "skill.p_strike", 2000));
        rig.Combat.CooldownLeftMs(player, "skill.p_strike").ShouldBeInRange(1800, 2000);

        Use(rig, player, "skill.p_strike", monster);
        rig.Ticks(2);
        Refusal(rig).ShouldBe(SkillReasons.OnCooldown);
        vitals.Sp.ShouldBe(93);

        rig.Ticks(20);   // 2 s
        Use(rig, player, "skill.p_strike", monster);
        rig.Ticks(3);
        rig.Of<SkillUsed>().Count.ShouldBe(2);
    }

    [Fact]
    public void A_cast_stands_still_for_the_cast_time_and_spends_the_SP_only_at_its_end()
    {
        var (rig, player, monster, _) = Duo(monsterCell: new GridCell(5, 2));
        var vitals = rig.Get(player).Vitals!;
        Use(rig, player, "skill.p_fire", monster);
        rig.Ticks(2);
        var started = rig.TimedOf<SkillCastStarted>().Single();
        started.Event.CastMs.ShouldBeInRange(900, 1000);   // cast_time_ms 1000 x cast_time_mod
        rig.Combat.IsCasting(player).ShouldBeTrue();
        vitals.Sp.ShouldBe(100);
        rig.TickUntil(() => rig.Of<SkillCastEnded>().Count > 0, 30);
        var ended = rig.TimedOf<SkillCastEnded>().Single();
        ended.Event.Result.ShouldBe(CastResults.Done);
        ((ended.Tick - started.Tick) * 100).ShouldBeInRange(started.Event.CastMs, started.Event.CastMs + 100);
        vitals.Sp.ShouldBe(90);
        rig.Ticks(2);
        rig.Of<SkillUsed>().Single().SkillId.ShouldBe("skill.p_fire");
        rig.Combat.IsCasting(player).ShouldBeFalse();
        rig.Combat.CooldownLeftMs(player, "skill.p_fire").ShouldBeGreaterThan(2000);
    }

    [Fact]
    public void Moving_during_a_cast_cancels_it_with_no_SP_spent_and_no_cooldown()
    {
        var (rig, player, monster, _) = Duo(monsterCell: new GridCell(5, 2));
        Use(rig, player, "skill.p_fire", monster);
        rig.Ticks(3);
        rig.Combat.IsCasting(player).ShouldBeTrue();
        rig.Send(new MoveTo(player, new GridCell(2, 5)));
        rig.Ticks(5);
        rig.Of<SkillCastEnded>().Single().Result.ShouldBe(CastResults.Cancelled);
        rig.Combat.IsCasting(player).ShouldBeFalse();
        rig.Get(player).Vitals!.Sp.ShouldBe(100);
        rig.Combat.CooldownLeftMs(player, "skill.p_fire").ShouldBe(0);
        rig.Of<SkillRequested>().ShouldBeEmpty();
    }

    [Fact]
    public void A_stop_or_a_new_order_cancels_a_cast_and_a_stun_interrupts_it()
    {
        var (rig, player, monster, _) = Duo(monsterCell: new GridCell(5, 2));
        Use(rig, player, "skill.p_fire", monster);
        rig.Ticks(3);
        rig.Send(new StopAttack(player));
        rig.Ticks(2);
        rig.Of<SkillCastEnded>().Single().Result.ShouldBe(CastResults.Cancelled);

        Use(rig, player, "skill.p_fire", monster);
        rig.Ticks(3);
        rig.Send(new AttackTarget(player, monster));   // a new order
        rig.Ticks(2);
        rig.Of<SkillCastEnded>().Last().Result.ShouldBe(CastResults.Cancelled);
        rig.Send(new StopAttack(player));
        rig.Ticks(2);

        Use(rig, player, "skill.p_fire", monster);
        rig.Ticks(3);
        rig.Combat.Runner!.Run(CompiledEffect.CompileList([new ApplyStatusEffect { StatusId = "status.stun", DurationMs = "3000", Chance = "1" }]),
            Actor(rig, monster), player.Value, null, 1);
        rig.Ticks(3);
        rig.Of<SkillCastEnded>().Last().Result.ShouldBe(CastResults.Interrupted);
        Refusal(rig).ShouldBe(SkillReasons.CannotUseSkills);
        rig.Get(player).Vitals!.Sp.ShouldBe(100);
    }

    private static EffectActor Actor(CombatRig rig, EntityId id)
    {
        var e = rig.Get(id);
        return new EffectActor(id.Value, e.Stats!, e.Cell, e.Facing, false, false);
    }

    [Fact]
    public void A_player_walks_into_range_then_casts()
    {
        var (rig, player, monster, _) = Duo(monsterCell: new GridCell(11, 2));
        Use(rig, player, "skill.p_instant", monster);
        rig.TickUntil(() => rig.Of<SkillUsed>().Count > 0, 80, "the skill is used");
        GridDistance.Chebyshev(rig.Get(player).Cell, rig.Get(monster).Cell).ShouldBeLessThanOrEqualTo(5);
        rig.Hp(monster).ShouldBeLessThan(1000);
    }

    [Fact]
    public void A_ground_skill_casts_at_a_cell_and_a_self_skill_needs_no_target()
    {
        var (rig, player, monster, _) = Duo(monsterCell: new GridCell(4, 2));
        Use(rig, player, "skill.p_ground", null, new GridCell(4, 2));
        rig.TickUntil(() => rig.Of<SkillUsed>().Count > 0, 40);
        rig.Hp(monster).ShouldBeLessThan(1000);
        rig.Get(player).Vitals!.Hp = 100;
        Use(rig, player, "skill.p_mend");
        rig.Ticks(3);
        rig.Hp(player).ShouldBe(110);
        Use(rig, player, "skill.p_ally");   // a friendly entity skill without a target is cast on the caster
        rig.Ticks(3);
        rig.Hp(player).ShouldBe(120);
        var before = rig.Hp(monster);
        Use(rig, player, "skill.p_slam");
        rig.Ticks(3);
        rig.Hp(monster).ShouldBeLessThan(before);   // a hostile self-centred area
    }

    // ---- refusals ----

    [Fact]
    public void Every_use_refusal_has_its_reason()
    {
        var (rig, player, monster, state) = Duo(sp: 6);
        void Expect(string reason, Action? act = null)
        {
            act?.Invoke();
            var before = rig.Of<SkillRefused>().Count;
            rig.Ticks(2);
            rig.Of<SkillRefused>().Count.ShouldBe(before + 1, reason);
            Refusal(rig).ShouldBe(reason);
        }

        Expect(SkillReasons.UnknownSkill, () => Use(rig, player, "skill.nobody", monster));
        state.Skills.Remove("skill.p_instant");
        Expect(SkillReasons.NotLearned, () => Use(rig, player, "skill.p_instant", monster));
        state.Skills["skill.p_instant"] = 3;
        Expect(SkillReasons.PassiveSkill, () => Use(rig, player, "skill.p_passive"));
        Expect(SkillReasons.LevelTooHigh, () => Use(rig, player, "skill.p_instant", monster, level: 4));
        Expect(SkillReasons.NotEnoughSp, () => Use(rig, player, "skill.p_fire", monster));   // costs 10, has 6
        Expect(SkillReasons.WrongWeapon, () => Use(rig, player, "skill.p_staff", monster));   // unarmed
        Expect(SkillReasons.InvalidTarget, () => Use(rig, player, "skill.p_instant", player));   // a hostile skill on a friend
        Expect(SkillReasons.InvalidTarget, () => Use(rig, player, "skill.p_ally", monster));      // a friendly skill on an enemy
        Expect(SkillReasons.InvalidTarget, () => Use(rig, player, "skill.p_instant"));            // no target at all
        Expect(SkillReasons.InvalidTarget, () => Use(rig, player, "skill.p_ground", null, new GridCell(99, 99)));
        Expect(SkillReasons.TargetGone, () => Use(rig, player, "skill.p_instant", new EntityId(9999)));
        Use(rig, player, "skill.p_strike", monster, level: 1);   // costs 6
        rig.Ticks(3);
        Expect(SkillReasons.OnCooldown, () => Use(rig, player, "skill.p_strike", monster, level: 1));
    }

    [Fact]
    public void A_staff_skill_works_with_a_staff_and_a_dead_target_is_refused()
    {
        var (rig, player, monster, _) = Duo();
        rig.Get(player).Weapon = new EquippedWeapon("item.oak_staff", WeaponTypes.Staff, 5, 0, 1, null);
        Use(rig, player, "skill.p_staff", monster);
        rig.Ticks(3);
        rig.Of<SkillUsed>().Count.ShouldBe(1);
        rig.Get(monster).Vitals!.Hp = 0;
        rig.Get(monster).Dead = true;
        Use(rig, player, "skill.p_staff", monster);
        rig.Ticks(2);
        Refusal(rig).ShouldBe(SkillReasons.TargetDead);
    }

    [Fact]
    public void A_hostile_skill_is_not_cast_from_a_safe_zone_but_a_friendly_one_is()
    {
        var grid = TestCombat.Open(14, 8, safe: [new GridRect(0, 0, 5, 5)]);
        var (rig, player, monster, _) = Duo(grid: grid);
        Use(rig, player, "skill.p_instant", monster);
        rig.Ticks(2);
        Refusal(rig).ShouldBe(SkillReasons.SafeZone);
        Use(rig, player, "skill.p_mend");
        rig.Ticks(3);
        rig.Of<SkillUsed>().Count.ShouldBe(1);
    }

    [Fact]
    public void A_wall_between_caster_and_target_blocks_a_ranged_skill_until_the_walk_finds_sight()
    {
        var grid = TestCombat.Grid(["..............", "..............", "....#.........", "....#.........", "..............", "..............", "..............", ".............."]);
        var rig = Rig(grid);
        var player = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0, spRegen: 0, maxSp: 100));
        var monster = rig.AddMonster(Dummy, new GridCell(6, 2));
        rig.Ticks();
        rig.Progressions.TryGet(player, out var state);
        state!.Skills["skill.p_instant"] = 1;
        Use(rig, player, "skill.p_instant", monster);
        rig.TickUntil(() => rig.Of<SkillUsed>().Count > 0, 80);
        LineOfSight.HasLineOfSight(grid, rig.Get(player).Cell, rig.Get(monster).Cell).ShouldBeTrue();
    }

    // ---- passives ----

    [Fact]
    public void A_learned_passive_adds_its_modifiers_with_the_skill_source_and_updates_with_its_level()
    {
        var rig = Rig();
        var player = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet());   // no max_hp override: the passive's percent shows
        rig.Ticks();
        rig.Progressions.TryGet(player, out var state);
        state!.JobId = "job.warden";
        state.SkillPoints = 3;
        var sheet = rig.Get(player).Stats!;
        var hp = sheet.Get(StatIndex.IndexOf(DerivedStats.MaxHp));
        rig.Send(new LearnSkill(player, "skill.tough_body"));
        rig.Ticks(2);
        sheet.Modifiers.HasSource("skill:skill.tough_body").ShouldBeTrue();
        var level1 = sheet.Get(StatIndex.IndexOf(DerivedStats.MaxHp));
        level1.ShouldBeGreaterThan(hp);
        rig.Send(new LearnSkill(player, "skill.tough_body"));
        rig.Ticks(2);
        sheet.Get(StatIndex.IndexOf(DerivedStats.MaxHp)).ShouldBeGreaterThan(level1);
        sheet.Modifiers.Count.ShouldBeGreaterThan(0);

        Use(rig, player, "skill.tough_body");   // a passive cannot be cast
        rig.Ticks(2);
        Refusal(rig).ShouldBe(SkillReasons.PassiveSkill);
    }

    [Fact]
    public void Passive_modifiers_come_off_when_the_level_goes_to_zero()
    {
        var sheet = TestCombat.PlayerSheet();
        var skill = Skills.Single(s => s.Id == "skill.p_passive");
        var atk = StatIndex.IndexOf(DerivedStats.Atk);
        var before = sheet.Get(atk);
        var scratch = new Game.Shared.Formulas.FormulaContext(Game.Shared.Formulas.FormulaSymbols.Default);
        SkillRules.ApplyPassive(sheet, skill, 2, scratch);
        sheet.Get(atk).ShouldBe(before + 6);
        SkillRules.ApplyPassive(sheet, skill, 3, scratch);
        sheet.Get(atk).ShouldBe(before + 9);
        SkillRules.ApplyPassive(sheet, skill, 0, scratch);
        sheet.Get(atk).ShouldBe(before);
        sheet.Modifiers.HasSource("skill:skill.p_passive").ShouldBeFalse();
    }

    // ---- allied summons ----

    [Fact]
    public void A_players_summon_fights_for_its_owner_and_never_turns_on_it_or_its_side()
    {
        var rig = Rig(ai: true, monsters: [Weak, TestEffects.Minion]);
        var player = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0, spRegen: 0, maxSp: 100));
        var wild = rig.AddMonster(Weak, new GridCell(6, 2));
        rig.Ticks();
        rig.Instance.Events.Publish(new EntitySpawned(wild, EntityKind.Monster, Weak.Id, new GridCell(6, 2), -1));   // a spawner would have said so: the AI knows it
        rig.Instance.Events.Publish(new SummonRequested(player, "monster.t_minion", 2, new GridCell(3, 2), 30000));
        rig.Ticks(4);
        var summons = rig.Of<MonsterSummoned>().Select(s => s.Monster).ToList();
        summons.Count.ShouldBe(2);
        rig.Get(summons[0]).SummonedByPlayer.ShouldBeTrue();
        rig.Get(summons[0]).OnPlayerSide.ShouldBeTrue();

        rig.TickUntil(() => rig.Of<DamageDealt>().Any(d => summons.Contains(d.Source) && d.Target == wild), 100, "a summon hits the wild monster");
        rig.Ticks(150);
        rig.Of<DamageDealt>().Where(d => summons.Contains(d.Source)).ShouldAllBe(d => d.Target == wild);
        rig.Of<DamageDealt>().Where(d => d.Source == player || d.Target == player).ShouldBeEmpty();
        rig.Get(player).Vitals!.Hp.ShouldBe(500);
        rig.Of<EntityDied>().Where(d => d.Entity == wild).ShouldNotBeEmpty();   // the summons can kill

        rig.Send(new AttackTarget(player, summons[0]));   // a player does not auto-attack an ally
        rig.Ticks(2);
        rig.Of<AttackRefused>().Last().Reason.ShouldBe(AttackReasons.InvalidTarget);
    }

    [Fact]
    public void A_summon_with_nothing_to_fight_follows_its_owner()
    {
        var rig = Rig(ai: true);
        var player = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0, spRegen: 0, maxSp: 100));
        rig.Ticks();
        rig.Instance.Events.Publish(new SummonRequested(player, "monster.t_minion", 1, new GridCell(3, 2), 60000));
        rig.Ticks(4);
        var summon = rig.Of<MonsterSummoned>().Single().Monster;
        rig.Send(new MoveTo(player, new GridCell(12, 6)));
        rig.TickUntil(() => !rig.Get(player).CannotMove && rig.Get(player).Cell.Equals(new GridCell(12, 6)), 200, "owner arrives");
        rig.Ticks(40);
        GridDistance.Chebyshev(rig.Get(summon).Cell, rig.Get(player).Cell).ShouldBeLessThanOrEqualTo(TestGrids.SampleContent.Balance.Ai.WanderRadius + 1);
    }

    // ---- hosting rules ----

    [Fact]
    public void Skills_can_be_hostile_to_a_wild_monster_only_and_areas_spare_the_owners_summons()
    {
        var rig = Rig(ai: true);
        var player = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0, spRegen: 0, maxSp: 100));
        rig.Ticks();
        rig.Progressions.TryGet(player, out var state);
        state!.Skills["skill.p_slam"] = 1;
        state.Skills["skill.p_instant"] = 1;
        rig.Instance.Events.Publish(new SummonRequested(player, "monster.t_minion", 1, new GridCell(3, 2), 60000));
        rig.Ticks(4);
        var summon = rig.Of<MonsterSummoned>().Single().Monster;
        var hp = rig.Hp(summon);
        Use(rig, player, "skill.p_slam");
        rig.Ticks(3);
        rig.Of<SkillUsed>().Count.ShouldBe(1);
        rig.Hp(summon).ShouldBe(hp);
        Use(rig, player, "skill.p_instant", summon);   // not a hostile target either
        rig.Ticks(2);
        Refusal(rig).ShouldBe(SkillReasons.InvalidTarget);
    }

    // ---- what observers see ----

    [Fact]
    public void Everyone_who_sees_a_caster_gets_its_cast_start_and_end_and_nobody_else_does()
    {
        var rig = new CombatRig(TestCombat.Open(60, 8), [Dummy], effects: Library.Value, aoi: true, viewRadius: 1);
        var caster = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0, spRegen: 0, maxSp: 100));
        var near = rig.AddPlayer(new GridCell(4, 2));
        var far = rig.AddPlayer(new GridCell(50, 2));
        var monster = rig.AddMonster(Dummy, new GridCell(3, 2));
        var (nearSink, farSink) = (new Game.Tests.Server.RecordingSink(), new Game.Tests.Server.RecordingSink());
        rig.Ticks();
        rig.Send(new Game.Server.World.Aoi.AttachView(near, nearSink));
        rig.Send(new Game.Server.World.Aoi.AttachView(far, farSink));
        rig.Progressions.TryGet(caster, out var state);
        state!.Skills["skill.p_fire"] = 1;
        rig.Ticks(2);
        Use(rig, caster, "skill.p_fire", monster);
        rig.TickUntil(() => rig.Of<SkillUsed>().Count > 0, 40);
        rig.Ticks(2);

        var starts = nearSink.Deltas.SelectMany(d => d.Casts!).ToList();
        starts.Single().Caster.ShouldBe(caster);
        starts.Single().Skill.ShouldBe("skill.p_fire");
        starts.Single().Ms.ShouldBeInRange(900, 1000);
        var ends = nearSink.Deltas.SelectMany(d => d.CastEnds!).ToList();
        ends.Single().ShouldBe(new Game.Server.World.Aoi.CastView(caster, "skill.p_fire", 0, CastResults.Done));
        farSink.Deltas.SelectMany(d => d.Casts!).ShouldBeEmpty();
        farSink.Deltas.SelectMany(d => d.CastEnds!).ShouldBeEmpty();
    }

    [Fact]
    public void Cooldowns_keep_running_across_a_map_change_and_are_never_reset_by_it()
    {
        var cooldowns = new SkillCooldowns();
        cooldowns.Advance(10_000);
        cooldowns.Start("skill.x", 3000);
        cooldowns.Advance(11_000);
        cooldowns.LeftMs("skill.x").ShouldBe(2000);
        cooldowns.Suspend();   // leaving a map
        cooldowns.LeftMs("skill.x").ShouldBe(2000);
        cooldowns.Resume(500);   // the other map's clock reads something else entirely
        cooldowns.LeftMs("skill.x").ShouldBe(2000);
        cooldowns.Advance(2500);
        cooldowns.IsReady("skill.x").ShouldBeTrue();
        cooldowns.Running().ShouldBeEmpty();
    }

    // ---- golden values (the real Twin Strike and Ember Bolt of content/, seed 1) ----

    private static List<int> Golden(string skill, string weaponType, int atk, int level)
    {
        var rig = new CombatRig(TestCombat.Open(14, 8), [Dummy], effects: Library.Value, seed: 1);
        var sheet = TestCombat.PlayerSheet(maxHp: 500, hpRegen: 0, spRegen: 0, maxSp: 100);
        sheet.Modifiers.Add("test", PrimaryStats.Int, ModifierOp.Flat, 30);
        sheet.Modifiers.Add("test", PrimaryStats.Str, ModifierOp.Flat, 20);
        var player = rig.AddPlayer(new GridCell(2, 2), sheet);
        var monster = rig.AddMonster(Dummy, new GridCell(3, 2));
        rig.Ticks();
        rig.Get(player).Weapon = new EquippedWeapon("item.t_weapon", weaponType, atk, 0, 1, null);
        rig.Progressions.TryGet(player, out var state);
        state!.Skills[skill] = level;
        rig.Send(new UseSkill(player, skill, 0, monster, null));
        rig.TickUntil(() => rig.Of<SkillUsed>().Count > 0, 40);
        return rig.Of<DamageDealt>().Where(d => d.Source == player).Select(d => d.Amount).ToList();
    }

    [Fact]
    public void Twin_strike_golden_hits()
    {
        Golden("skill.twin_strike", WeaponTypes.Dagger, 20, 3).ShouldBe([49]);   // level 3: 1.2x, two hits in one result
    }

    [Fact]
    public void Ember_bolt_golden_hit()
    {
        Golden("skill.ember_bolt", WeaponTypes.Staff, 10, 4).ShouldBe([69]);   // level 4: 1.5x fire, INT +30
    }
}
