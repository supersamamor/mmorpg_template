using Game.Content.Loading;
using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Server.Net;
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
/// T-503: spending stat points, the two reset effects and the rules for using a reset item, against the real sample content. Hand arithmetic uses these numbers
/// (redo it if content changes): a new character has 10 stat points and every stat at 1; raising a stat from value v costs floor((v - 1) / 10) + 2 (2 up to
/// 10, then 3); the cap is 99; <c>progression.reset</c> is free below base level 10, refused in combat and when dead.
/// </summary>
public class StatAllocationTests
{
    private static (CombatRig Rig, EntityId Player, ProgressionState State, StatSheet Sheet) Solo()
    {
        var rig = new CombatRig(TestCombat.Open(14, 8), []);
        var player = rig.AddPlayer(new GridCell(2, 2));
        rig.Ticks();
        rig.Progressions.TryGet(player, out var state).ShouldBeTrue();
        return (rig, player, state, rig.Get(player).Stats!);
    }

    [Fact]
    public void Raising_a_stat_spends_the_priced_steps_and_the_derived_stats_follow_in_the_same_tick()
    {
        var (rig, player, state, sheet) = Solo();
        state.StatPoints.ShouldBe(10);
        var atk = sheet.Get(DerivedStats.Atk);
        var maxHp = sheet.Get(DerivedStats.MaxHp);

        rig.Send(new RaiseStat(player, PrimaryStats.Str, 3));
        rig.Ticks();

        state.Stats[PrimaryStats.Str].ShouldBe(4);
        state.StatPoints.ShouldBe(4);   // three steps from 1: 2 + 2 + 2
        sheet.Base[PrimaryStats.Str].ShouldBe(4);
        sheet.Get(DerivedStats.Atk).ShouldBeGreaterThan(atk);
        rig.Of<StatRaised>().Single().ShouldBe(new StatRaised(player, PrimaryStats.Str, 4, 6));

        rig.Send(new RaiseStat(player, PrimaryStats.Vit, 2));
        rig.Ticks();
        sheet.Get(DerivedStats.MaxHp).ShouldBeGreaterThan(maxHp);
        rig.Get(player).Vitals!.MaxHp.ShouldBe(sheet.GetInt(DerivedStats.MaxHp));   // the pool follows the sheet
    }

    [Fact]
    public void The_cost_of_each_step_is_priced_from_the_value_before_it()
    {
        var (rig, player, state, _) = Solo();
        state.Stats[PrimaryStats.Agi] = 10;
        state.StatPoints = 50;
        rig.Send(new RaiseStat(player, PrimaryStats.Agi, 2));
        rig.Ticks();
        state.Stats[PrimaryStats.Agi].ShouldBe(12);
        state.StatPoints.ShouldBe(45);   // 10 -> 11 costs 2, 11 -> 12 costs 3
        rig.Of<StatRaised>().Single().Cost.ShouldBe(5);
    }

    [Theory]
    [InlineData("stat.luck", 1, 10, 1, StatReasons.UnknownStat)]
    [InlineData("str", 0, 10, 1, StatReasons.InvalidAmount)]
    [InlineData("str", -3, 10, 1, StatReasons.InvalidAmount)]
    [InlineData("str", 100, 100000, 1, StatReasons.InvalidAmount)]
    [InlineData("str", 5, 9, 1, StatReasons.NotEnoughPoints)]
    [InlineData("str", 1, 50, 99, StatReasons.AtMaxValue)]
    [InlineData("str", 2, 50, 98, StatReasons.AtMaxValue)]
    public void Every_refusal_has_a_reason_and_changes_nothing(string stat, int amount, int points, int value, string reason)
    {
        var (rig, player, state, sheet) = Solo();
        var id = stat == "str" ? PrimaryStats.Str : stat;
        state.Stats[PrimaryStats.Str] = value;
        sheet.Base[PrimaryStats.Str] = value;
        state.StatPoints = points;
        var atk = sheet.Get(DerivedStats.Atk);

        rig.Send(new RaiseStat(player, id, amount));
        rig.Ticks();

        rig.Of<StatRaiseRefused>().Single().ShouldBe(new StatRaiseRefused(player, id, reason));
        rig.Of<StatRaised>().ShouldBeEmpty();
        state.StatPoints.ShouldBe(points);
        state.Stats[PrimaryStats.Str].ShouldBe(value);
        sheet.Get(DerivedStats.Atk).ShouldBe(atk);
    }

    [Fact]
    public void The_stats_message_has_the_points_each_stats_next_cost_and_the_panels_derived_stats()
    {
        var (rig, player, state, sheet) = Solo();
        var own = new OwnProgress(TestGrids.SampleContent, rig.Progressions);

        var message = own.StatsFor(rig.Get(player)).ShouldNotBeNull();
        message.Points.ShouldBe(10);
        message.Stats.Select(s => s.Stat).ShouldBe(PrimaryStats.All.Ordered);
        message.Stats.ShouldAllBe(s => s.Value == 1 && s.NextCost == 2);
        message.Derived.Select(d => d.Stat).ShouldBe(DerivedStats.Panel.Ordered);
        message.Derived.ShouldAllBe(d => d.Value == sheet.GetInt(d.Stat));

        state.Stats[PrimaryStats.Luk] = TestGrids.SampleContent.Balance.Progression.MaxStatValue;
        state.Stats[PrimaryStats.Dex] = 10;
        var raised = own.StatsFor(rig.Get(player))!.Stats.ToDictionary(s => s.Stat);
        raised[PrimaryStats.Luk].NextCost.ShouldBe(0);   // at the cap: nothing to buy
        raised[PrimaryStats.Dex].NextCost.ShouldBe(2);
        state.Stats[PrimaryStats.Dex] = 11;
        own.StatsFor(rig.Get(player))!.Stats.Single(s => s.Stat == PrimaryStats.Dex).NextCost.ShouldBe(3);
    }

    // ---- the rules, without a world ----

    private static readonly Lazy<ContentDatabase> Db = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        var result = ContentLoader.Load(Path.Combine(dir!.FullName, "content"));
        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        return result.Database!;
    });

    private static ProgressionRules Rules() => new(Db.Value.Balance, new ContentProgression(Db.Value));

    [Fact]
    public void A_stat_reset_refunds_exactly_what_mixed_spending_cost()
    {
        var rules = Rules();
        var state = rules.NewCharacter("job.wanderer");
        state.StatPoints = 400;
        rules.RaiseStat(state, PrimaryStats.Str, 25).Success.ShouldBeTrue();   // 1 -> 26: 10 x 2 + 10 x 3 + 5 x 4
        rules.RaiseStat(state, PrimaryStats.Agi, 4).Success.ShouldBeTrue();    // 4 x 2
        rules.RaiseStat(state, PrimaryStats.Luk, 11).Success.ShouldBeTrue();   // 10 x 2 + 1 x 3
        rules.RaiseStat(state, PrimaryStats.Str, 1).Success.ShouldBeTrue();    // 26 -> 27: 4
        var spent = 400 - state.StatPoints;
        spent.ShouldBe(20 + 30 + 20 + 8 + 23 + 4);
        rules.StatPointsSpent(state).ShouldBe(spent);

        var result = rules.ResetStats(state);

        result.Refunded.ShouldBe(spent);
        state.StatPoints.ShouldBe(400);
        state.Stats.Values.ShouldAllBe(v => v == 1);
        rules.ResetStats(state).Refunded.ShouldBe(0);   // nothing left to take back
        state.StatPoints.ShouldBe(400);
    }

    [Fact]
    public void A_skill_reset_refunds_one_point_per_level_learned()
    {
        var rules = Rules();
        var state = rules.NewCharacter("job.wanderer");
        state.SkillPoints = 9;
        for (var i = 0; i < 3; i++) rules.LearnSkill(state, "skill.second_wind").Success.ShouldBeTrue();
        rules.LearnSkill(state, "skill.return_home").Success.ShouldBeTrue();
        state.SkillPoints.ShouldBe(5);

        rules.ResetSkills(state).Refunded.ShouldBe(4);

        state.SkillPoints.ShouldBe(9);
        state.Skills.ShouldBeEmpty();
        state.JobId.ShouldBe("job.wanderer");   // levels, EXP and the job stay
        rules.ResetSkills(state).Refunded.ShouldBe(0);
        state.SkillPoints.ShouldBe(9);
    }
}
