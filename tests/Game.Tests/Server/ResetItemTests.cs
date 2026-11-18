using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Effects;
using Game.Server.World.Inventory;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>
/// T-503: the reset items end to end on the server (inventory gate, effect runner, combat host): the exact refund, passives off, a cast cancelled and cooldowns
/// kept, the rules of <c>progression.reset</c> (free below base level 10, refused in combat, when dead and when there is nothing to reset), and the load checks.
/// Sample numbers as in <see cref="StatAllocationTests"/>.
/// </summary>
public class ResetItemTests
{
    private const string StatReset = "item.realign_tonic";
    private const string SkillReset = "item.blank_page";

    private static (InventoryRig Rig, EntityId Player, ProgressionState State, StatSheet Sheet) Solo(int baseLevel = 10)
    {
        var rig = new InventoryRig();
        var player = rig.AddPlayer(new GridCell(2, 2), TestCombat.PlayerSheet(aspd: null, speed: null));   // no test overrides: the real derived stats
        rig.Ticks();
        rig.Progressions.TryGet(player, out var state).ShouldBeTrue();
        state.BaseLevel = baseLevel;
        rig.Get(player).Stats!.Base.BaseLevel = baseLevel;
        return (rig, player, state, rig.Get(player).Stats!);
    }

    private static void Spend(InventoryRig rig, EntityId player, ProgressionState state)
    {
        state.StatPoints = 12;
        rig.Send(new RaiseStat(player, PrimaryStats.Str, 3));
        rig.Send(new RaiseStat(player, PrimaryStats.Vit, 3));
        rig.Ticks();
        state.StatPoints.ShouldBe(0);
    }

    private static int Carried(InventoryRig rig, EntityId player, string item) => rig.State(player).Entries.Where(e => e.ItemId == item).Sum(e => e.Quantity);

    private static string? Refusal(InventoryRig rig) => rig.Of<ItemRefused>().LastOrDefault()?.Reason;

    // ---- reset_stats ----

    [Fact]
    public void The_stat_reset_gives_back_every_spent_point_and_takes_the_item()
    {
        var (rig, player, state, sheet) = Solo();
        var fresh = DerivedStats.Panel.Ordered.Select(sheet.GetInt).ToArray();
        Spend(rig, player, state);
        DerivedStats.Panel.Ordered.Select(sheet.GetInt).ToArray().ShouldNotBe(fresh);

        var entry = rig.Give(player, StatReset);
        rig.Send(new UseItem(player, entry));
        rig.Ticks(3);

        state.StatPoints.ShouldBe(12);
        state.Stats.Values.ShouldAllBe(v => v == 1);
        sheet.Base[PrimaryStats.Str].ShouldBe(1);
        DerivedStats.Panel.Ordered.Select(sheet.GetInt).ToArray().ShouldBe(fresh);
        rig.Of<StatsReset>().Single().ShouldBe(new StatsReset(player, 12));
        Carried(rig, player, StatReset).ShouldBe(0);   // level 10: not free
        rig.Of<ItemUsed>().Single().Consumed.ShouldBeTrue();
    }

    [Fact]
    public void Golden_a_level_10_characters_derived_stats_before_and_after_a_stat_reset()
    {
        // Level 10 wanderer (no job bonuses), job level 1, every stat 1 but str 4 and vit 4 (12 points). From the sample formulas.yaml, by hand:
        //   atk = str + floor(dex/5) + floor(luk/5) + floor(level/4) = 4 + 0 + 0 + 2 = 6          (after: 1 + 2 = 3)
        //   matk = floor(int * 1.5) + floor(level/4) = 1 + 2 = 3                                    (unchanged)
        //   def = floor(vit/2) + floor(level/5) = 2 + 2 = 4                                         (after: 0 + 2 = 2)
        //   hit = level + dex + 10 = 21, flee = level + agi + 10 = 21                               (unchanged)
        //   aspd = min(3, 1 + agi*0.01 + dex*0.002) = 1.012, whole part 1                           (unchanged)
        //   max_hp = floor((40 + level*8) * (100 + vit)/100) = floor(120 * 1.04) = 124              (after: floor(120 * 1.01) = 121)
        //   max_sp = floor((10 + level*3) * (100 + int)/100) = floor(40 * 1.01) = 40                (unchanged)
        var (rig, player, state, sheet) = Solo();
        Spend(rig, player, state);
        Panel(sheet).ShouldBe(GoldenSpent, "before the reset");

        rig.Send(new UseItem(player, rig.Give(player, StatReset)));
        rig.Ticks(3);

        Panel(sheet).ShouldBe(GoldenReset, "after the reset");
    }

    private static readonly int[] GoldenSpent = [6, 3, 4, 21, 21, 1, 124, 40];
    private static readonly int[] GoldenReset = [3, 3, 2, 21, 21, 1, 121, 40];

    private static int[] Panel(StatSheet sheet) => DerivedStats.Panel.Ordered.Select(sheet.GetInt).ToArray();

    // ---- the rules of progression.reset ----

    [Fact]
    public void Below_the_free_level_the_reset_works_and_the_item_stays()
    {
        var (rig, player, state, _) = Solo(baseLevel: 9);
        Spend(rig, player, state);
        var entry = rig.Give(player, StatReset);

        rig.Send(new UseItem(player, entry));
        rig.Ticks(3);

        state.StatPoints.ShouldBe(12);
        Carried(rig, player, StatReset).ShouldBe(1);
        rig.Of<ItemUsed>().Single().Consumed.ShouldBeFalse();
    }

    [Fact]
    public void At_the_free_level_it_costs_the_item()
    {
        var (rig, player, state, _) = Solo(baseLevel: 10);
        TestGrids.SampleContent.Balance.Progression.Reset.FreeBelowBaseLevel.ShouldBe(10);
        Spend(rig, player, state);
        rig.Send(new UseItem(player, rig.Give(player, StatReset)));
        rig.Ticks(3);
        Carried(rig, player, StatReset).ShouldBe(0);
    }

    [Fact]
    public void A_reset_is_refused_in_combat_and_nothing_is_used_up_until_the_delay_has_passed()
    {
        var (rig, player, state, _) = Solo();
        Spend(rig, player, state);
        var entry = rig.Give(player, StatReset);
        rig.Get(player).InCombatUntilMs = rig.Now + 5000;   // what combat sets when a hit lands

        rig.Send(new UseItem(player, entry));
        rig.Ticks(2);

        Refusal(rig).ShouldBe(ItemReasons.ResetInCombat);
        Carried(rig, player, StatReset).ShouldBe(1);
        state.StatPoints.ShouldBe(0);
        state.Stats[PrimaryStats.Str].ShouldBe(4);
        rig.Of<StatsReset>().ShouldBeEmpty();
        rig.Of<ItemUsed>().ShouldBeEmpty();

        rig.Ticks(60);   // 6 s on: the delay is over, and the refused use started no cooldown
        rig.Clock.Advance(6000);
        rig.Send(new UseItem(player, entry));
        rig.Ticks(3);
        state.StatPoints.ShouldBe(12);
    }

    [Fact]
    public void A_reset_is_refused_when_dead()
    {
        var (rig, player, state, _) = Solo();
        Spend(rig, player, state);
        var entry = rig.Give(player, StatReset);
        rig.Get(player).Dead = true;

        rig.Send(new UseItem(player, entry));
        rig.Ticks(2);

        // Either the instance drops a dead entity's intents or the gate refuses it: the item is never used up.
        Carried(rig, player, StatReset).ShouldBe(1);
        state.StatPoints.ShouldBe(0);
        rig.Of<StatsReset>().ShouldBeEmpty();
    }

    [Fact]
    public void A_reset_that_would_change_nothing_is_refused_and_keeps_the_item()
    {
        var (rig, player, state, _) = Solo();
        var stats = rig.Give(player, StatReset);
        var skills = rig.Give(player, SkillReset);

        rig.Send(new UseItem(player, stats));
        rig.Ticks(2);
        Refusal(rig).ShouldBe(ItemReasons.NothingToReset);
        rig.Send(new UseItem(player, skills));
        rig.Ticks(2);

        rig.Of<ItemRefused>().Select(r => r.Reason).ShouldBe([ItemReasons.NothingToReset, ItemReasons.NothingToReset]);
        Carried(rig, player, StatReset).ShouldBe(1);
        Carried(rig, player, SkillReset).ShouldBe(1);
        state.StatPoints.ShouldBe(10);
    }

    // ---- reset_skills ----

    [Fact]
    public void The_skill_reset_refunds_the_points_takes_passives_off_cancels_the_cast_and_keeps_cooldowns()
    {
        var (rig, player, state, sheet) = Solo();
        state.JobId = "job.warden";
        state.SkillPoints = 6;
        var maxHp = sheet.Get(DerivedStats.MaxHp);
        rig.Send(new LearnSkill(player, "skill.tough_body"));
        rig.Send(new LearnSkill(player, "skill.twin_strike"));
        rig.Ticks(2);
        state.SkillPoints.ShouldBe(4);
        sheet.Modifiers.HasSource(Game.Shared.Skills.SkillRules.PassiveSource("skill.tough_body")).ShouldBeTrue();
        sheet.Get(DerivedStats.MaxHp).ShouldBeGreaterThan(maxHp);
        rig.Get(player).Cooldowns!.Start("skill.twin_strike", 20_000);

        state.Skills["skill.return_home"] = 1;   // a self cast with a 3 s cast time
        state.SkillPoints += 1;
        rig.Get(player).Vitals!.Sp = rig.Get(player).Vitals!.MaxSp;   // the cast costs 20 SP
        rig.Send(new UseSkill(player, "skill.return_home", 0, null, null));
        rig.Ticks(3);
        rig.Of<SkillRefused>().ShouldBeEmpty();
        rig.Of<SkillCastStarted>().Count.ShouldBe(1);

        rig.Send(new UseItem(player, rig.Give(player, SkillReset)));
        rig.Ticks(3);

        state.SkillPoints.ShouldBe(8);   // the 5 left + 1 for each of the three skills (two learned, one set)
        state.Skills.ShouldBeEmpty();
        sheet.Modifiers.HasSource(Game.Shared.Skills.SkillRules.PassiveSource("skill.tough_body")).ShouldBeFalse();
        sheet.Get(DerivedStats.MaxHp).ShouldBe(maxHp);
        rig.Of<SkillCastEnded>().Single().Result.ShouldBe(CastResults.Cancelled);
        rig.Get(player).Cooldowns!.LeftMs("skill.twin_strike").ShouldBeGreaterThan(0);
        rig.Of<SkillsReset>().Single().ShouldBe(new SkillsReset(player, 3));
        Carried(rig, player, SkillReset).ShouldBe(0);
        state.JobId.ShouldBe("job.warden");
    }

    // ---- load validation ----

    [Theory]
    [InlineData(EffectSite.Item, false, true)]
    [InlineData(EffectSite.SelfSkill, false, false)]
    [InlineData(EffectSite.EntitySkill, false, false)]
    [InlineData(EffectSite.GroundSkill, false, false)]
    [InlineData(EffectSite.StatusOnApply, false, false)]
    [InlineData(EffectSite.StatusOnTick, false, false)]
    [InlineData(EffectSite.Item, true, false)]
    public void The_reset_effects_are_allowed_only_directly_in_a_consumable(EffectSite site, bool inArea, bool allowed)
    {
        foreach (EffectDef effect in new EffectDef[] { new ResetStatsEffect(), new ResetSkillsEffect() })
        {
            var problem = EffectSupport.Check(effect, site, inArea);
            if (allowed) problem.ShouldBeNull();
            else problem.ShouldNotBeNull().ShouldContain(effect.Type);
        }
    }

    [Fact]
    public void The_reset_items_and_the_reset_rules_are_content_not_code()
    {
        var db = TestGrids.SampleContent;
        db.Items.Get(StatReset).Consumable!.Effects.Single().ShouldBeOfType<ResetStatsEffect>();
        db.Items.Get(SkillReset).Consumable!.Effects.Single().ShouldBeOfType<ResetSkillsEffect>();
        var reset = db.Balance.Progression.Reset;
        (reset.FreeBelowBaseLevel, reset.RefuseInCombat, reset.RefuseWhenDead).ShouldBe((10, true, true));
        db.Balance.NewCharacter.StartItems.Select(i => i.ItemId).ShouldContain(StatReset);
        db.Balance.NewCharacter.StartItems.Select(i => i.ItemId).ShouldContain(SkillReset);
    }
}
