using Game.Content.Loading;
using Game.Content.Registry;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Vocabulary;

namespace Game.Tests.Progression;

/// <summary>
/// Progression rules against the real /content. Hand arithmetic uses these sample numbers (if content changes, redo it):
/// exp.base[0..] = 65, 194, 400, 687, 1062, 1527, 2087, 2745, 3502 ... last (98th) = 604591, so max base level 99;
/// stat points on reaching level L = floor(L/10)+3; stat raise cost from value v = floor((v-1)/10)+2; start 10 points, stats at 1, cap 99;
/// exp.job_novice = 30, 80, 150, 240, 350, 480, 630, 800, 990 (max job level 10); 1 skill point per job level.
/// </summary>
public class ProgressionRulesTests
{
    private const string Novice = "job.novice";
    private const string Warden = "job.warden";
    private const string Spellbinder = "job.spellbinder";

    private static readonly Lazy<ContentDatabase> Db = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
        var result = ContentLoader.Load(Path.Combine(dir.FullName, "content"));
        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        return result.Database!;
    });

    private sealed class DbContent : IProgressionContent
    {
        public bool TryGetJob(string id, out JobDef job) => Db.Value.Jobs.TryGet(id, out job!);
        public bool TryGetSkill(string id, out SkillDef skill) => Db.Value.Skills.TryGet(id, out skill!);
        public bool TryGetExpTable(string id, out ExpTableDef table) => Db.Value.ExpTables.TryGet(id, out table!);
    }

    private static ProgressionRules Rules() => new(Db.Value.Balance, new DbContent());

    [Fact]
    public void Limits_come_from_the_tables()
    {
        var rules = Rules();
        rules.MaxBaseLevel.ShouldBe(99); // 98 entries + 1
        rules.MaxJobLevel(Novice).ShouldBe(10); // 9 entries + 1
        rules.MaxJobLevel("job.nope").ShouldBe(0);
    }

    [Fact]
    public void New_character_starts_from_balance()
    {
        var state = Rules().NewCharacter(Novice);
        state.BaseLevel.ShouldBe(1);
        state.JobLevel.ShouldBe(1);
        state.StatPoints.ShouldBe(Db.Value.Balance.Progression.StartingStatPoints);
        foreach (var stat in PrimaryStats.All.Ordered)
            state.StatValue(stat).ShouldBe(Db.Value.Balance.Progression.StartingStatValue);
    }

    [Fact]
    public void Single_grant_can_gain_several_levels()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);
        var result = rules.GainBaseExp(state, 1000);
        // 1000 - 65 (1→2) = 935; - 194 (2→3) = 741; - 400 (3→4) = 341; 687 (4→5) is more than 341: stays at level 4 with 341.
        state.BaseLevel.ShouldBe(4);
        state.BaseExp.ShouldBe(341);
        result.LevelsGained.ShouldBe(3);
        // 3 points each for levels 2, 3, 4 (floor(L/10)+3 = 3), on top of the 10 starting points.
        result.PointsAwarded.ShouldBe(9);
        state.StatPoints.ShouldBe(19);
        result.Events.ShouldBe(new IProgressionEvent[] { new LevelUp(2, 3), new LevelUp(3, 3), new LevelUp(4, 3) });
        result.Events[0].Name.ShouldBe(EventNames.LevelUp);
        result.ExpDiscarded.ShouldBe(0);
    }

    [Fact]
    public void Points_per_level_follow_the_formula_across_a_tier()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);
        // Levels 1→10 cost 65+194+400+687+1062+1527+2087+2745+3502 = 12269; +100 left over.
        var result = rules.GainBaseExp(state, 12269 + 100);
        state.BaseLevel.ShouldBe(10);
        state.BaseExp.ShouldBe(100);
        // Levels 2..9 give 3 each (24); level 10 gives floor(10/10)+3 = 4.
        result.PointsAwarded.ShouldBe(28);
        result.Events[^1].ShouldBe(new LevelUp(10, 4));
    }

    [Fact]
    public void Partial_exp_carries_between_grants()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);
        rules.GainBaseExp(state, 60).LevelsGained.ShouldBe(0);
        state.BaseExp.ShouldBe(60);
        rules.GainBaseExp(state, 10).LevelsGained.ShouldBe(1); // 60 + 10 = 70 ≥ 65 → level 2 with 5 left
        state.BaseLevel.ShouldBe(2);
        state.BaseExp.ShouldBe(5);
    }

    [Fact]
    public void Non_positive_exp_changes_nothing()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);
        rules.GainBaseExp(state, 0).LevelsGained.ShouldBe(0);
        rules.GainBaseExp(state, -500).LevelsGained.ShouldBe(0);
        state.BaseLevel.ShouldBe(1);
        state.BaseExp.ShouldBe(0);
    }

    [Fact]
    public void Base_exp_past_the_cap_is_discarded()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);
        state.BaseLevel = 98;
        state.BaseExp = 4;
        // Level 98→99 needs 604591 (last table entry); 4 already held, so 604591 - 4 + 500 reaches 99 with 500 spare.
        var result = rules.GainBaseExp(state, 604591 - 4 + 500);
        state.BaseLevel.ShouldBe(99);
        state.BaseExp.ShouldBe(0);
        result.ExpDiscarded.ShouldBe(500);
        result.PointsAwarded.ShouldBe(12); // floor(99/10)+3

        var again = rules.GainBaseExp(state, 1000);
        again.LevelsGained.ShouldBe(0);
        again.ExpDiscarded.ShouldBe(1000);
        state.BaseLevel.ShouldBe(99);
        state.BaseExp.ShouldBe(0);
    }

    [Fact]
    public void Huge_grant_does_not_overflow()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);
        var result = rules.GainBaseExp(state, long.MaxValue);
        state.BaseLevel.ShouldBe(99);
        state.BaseExp.ShouldBe(0);
        result.LevelsGained.ShouldBe(98);
        result.ExpDiscarded.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Job_exp_awards_skill_points_and_respects_the_job_cap()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);
        var result = rules.GainJobExp(state, 30 + 80 + 10); // 1→2 costs 30, 2→3 costs 80, 10 left
        state.JobLevel.ShouldBe(3);
        state.JobExp.ShouldBe(10);
        state.SkillPoints.ShouldBe(2); // 1 per level reached
        result.Events.ShouldBe(new IProgressionEvent[] { new JobLevelUp(Novice, 2, 1), new JobLevelUp(Novice, 3, 1) });
        state.BaseLevel.ShouldBe(1); // job EXP never touches base level

        // Remaining levels 3..10 cost 150+240+350+480+630+800+990 = 3640; 10 held, so grant 3630 + 25 spare.
        var cap = rules.GainJobExp(state, 3630 + 25);
        state.JobLevel.ShouldBe(10);
        state.JobExp.ShouldBe(0);
        cap.ExpDiscarded.ShouldBe(25);
        rules.GainJobExp(state, 7).ExpDiscarded.ShouldBe(7);
    }

    [Fact]
    public void Stat_raise_spends_points_by_current_value()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice); // 10 points, str 1
        var result = rules.RaiseStat(state, PrimaryStats.Str, 5); // each step from 1..5 costs floor((v-1)/10)+2 = 2 → 10
        result.Success.ShouldBeTrue();
        result.PointsSpent.ShouldBe(10);
        result.NewValue.ShouldBe(6);
        state.StatPoints.ShouldBe(0);
        state.StatValue(PrimaryStats.Str).ShouldBe(6);
    }

    [Fact]
    public void Stat_cost_grows_with_value()
    {
        var rules = Rules();
        rules.StatRaiseCost(10).ShouldBe(2); // floor(9/10)+2
        rules.StatRaiseCost(11).ShouldBe(3); // floor(10/10)+2
        rules.StatRaiseCost(98).ShouldBe(11); // floor(97/10)+2
        var state = rules.NewCharacter(Novice);
        state.Stats[PrimaryStats.Vit] = 10;
        state.StatPoints = 5;
        var result = rules.RaiseStat(state, PrimaryStats.Vit, 2); // 10→11 costs 2, 11→12 costs 3
        result.PointsSpent.ShouldBe(5);
        state.StatPoints.ShouldBe(0);
    }

    [Fact]
    public void Invalid_allocations_are_rejected_without_changes()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);

        rules.RaiseStat(state, PrimaryStats.Str, 0).Error.ShouldBe(SpendError.InvalidAmount);
        rules.RaiseStat(state, PrimaryStats.Str, -3).Error.ShouldBe(SpendError.InvalidAmount);
        rules.RaiseStat(state, "atk").Error.ShouldBe(SpendError.UnknownStat);
        rules.RaiseStat(state, null!).Error.ShouldBe(SpendError.UnknownStat);
        rules.RaiseStat(state, PrimaryStats.Str, 6).Error.ShouldBe(SpendError.NotEnoughPoints); // needs 12, has 10

        state.StatPoints.ShouldBe(10);
        state.StatValue(PrimaryStats.Str).ShouldBe(1);

        state.Stats[PrimaryStats.Agi] = 99;
        state.StatPoints = 1000;
        var atMax = rules.RaiseStat(state, PrimaryStats.Agi);
        atMax.Error.ShouldBe(SpendError.AtMaxStatValue);
        atMax.Success.ShouldBeFalse();
        atMax.PointsSpent.ShouldBe(0);
        state.StatPoints.ShouldBe(1000);
        rules.RaiseStat(state, PrimaryStats.Luk, 99).Error.ShouldBe(SpendError.AtMaxStatValue); // 1 + 99 = 100 > 99
    }

    private static ProgressionState WardenWith(int skillPoints)
    {
        var state = Rules().NewCharacter(Warden);
        state.SkillPoints = skillPoints;
        return state;
    }

    [Fact]
    public void Skill_prerequisite_is_enforced()
    {
        var rules = Rules();
        var state = WardenWith(10);

        rules.LearnSkill(state, "skill.shockwave").Error.ShouldBe(SpendError.PrerequisiteNotMet); // needs twin_strike 3
        rules.LearnSkill(state, "skill.twin_strike").Success.ShouldBeTrue();
        rules.LearnSkill(state, "skill.twin_strike");
        rules.LearnSkill(state, "skill.shockwave").Error.ShouldBe(SpendError.PrerequisiteNotMet); // only level 2
        var third = rules.LearnSkill(state, "skill.twin_strike");
        third.NewValue.ShouldBe(3);
        third.Events.ShouldBe(new IProgressionEvent[] { new SkillLearned("skill.twin_strike", 3) });

        var shock = rules.LearnSkill(state, "skill.shockwave");
        shock.Success.ShouldBeTrue();
        state.SkillLevel("skill.shockwave").ShouldBe(1);
        state.SkillPoints.ShouldBe(10 - 4);
    }

    [Fact]
    public void Skill_respects_max_level_tree_and_points()
    {
        var rules = Rules();
        var state = WardenWith(20);
        for (var i = 0; i < 5; i++) rules.LearnSkill(state, "skill.twin_strike").Success.ShouldBeTrue();
        var over = rules.LearnSkill(state, "skill.twin_strike");
        over.Error.ShouldBe(SpendError.AtMaxSkillLevel);
        state.SkillLevel("skill.twin_strike").ShouldBe(5);
        state.SkillPoints.ShouldBe(15);

        rules.LearnSkill(state, "skill.ember_bolt").Error.ShouldBe(SpendError.NotInSkillTree); // spellbinder skill
        rules.LearnSkill(state, "skill.nope").Error.ShouldBe(SpendError.UnknownSkill);
        rules.LearnSkill(state, "skill.second_wind").Success.ShouldBeTrue(); // from the parent job (novice)

        var broke = WardenWith(0);
        rules.LearnSkill(broke, "skill.iron_guard").Error.ShouldBe(SpendError.NotEnoughPoints);
        broke.Skills.ShouldBeEmpty();
    }

    [Fact]
    public void Job_change_lists_every_failing_reason()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice); // base 1, job 1, no quests, nothing unlocked
        var check = rules.CanChangeJob(state, Warden); // needs base 10, job 8, quest.warden_trial, unlock
        check.Allowed.ShouldBeFalse();
        check.Failures.Select(f => f.Kind).ShouldBe(new[]
        {
            JobChangeFailureKind.BaseLevelTooLow,
            JobChangeFailureKind.JobLevelTooLow,
            JobChangeFailureKind.QuestNotCompleted,
            JobChangeFailureKind.NotUnlocked,
        });
        check.Failures[0].Detail.ShouldBe("1/10");
        check.Failures[2].Detail.ShouldBe("quest.warden_trial");

        state.BaseLevel = 10;
        state.JobLevel = 8;
        state.CompletedQuests.Add("quest.warden_trial");
        rules.CanChangeJob(state, Warden).Failures.Select(f => f.Kind).ShouldBe(new[] { JobChangeFailureKind.NotUnlocked });
        state.UnlockedJobs.Add(Warden);
        rules.CanChangeJob(state, Warden).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Job_change_rejects_unknown_same_and_wrong_parent()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);
        rules.CanChangeJob(state, "job.nope").Failures.Select(f => f.Kind).ShouldBe(new[] { JobChangeFailureKind.UnknownJob });
        rules.CanChangeJob(state, Novice).Failures.Select(f => f.Kind).ShouldContain(JobChangeFailureKind.SameJob);

        var spell = rules.NewCharacter(Spellbinder);
        spell.BaseLevel = 50;
        spell.JobLevel = 40;
        spell.CompletedQuests.Add("quest.warden_trial");
        spell.UnlockedJobs.Add(Warden);
        rules.CanChangeJob(spell, Warden).Failures.Select(f => f.Kind).ShouldBe(new[] { JobChangeFailureKind.WrongParentJob });
    }

    [Fact]
    public void Change_job_applies_only_when_allowed()
    {
        var rules = Rules();
        var state = rules.NewCharacter(Novice);
        state.BaseLevel = 5;
        state.JobLevel = 5;
        state.JobExp = 12;
        state.SkillPoints = 3;
        state.Skills["skill.second_wind"] = 2;

        rules.ChangeJob(state, Spellbinder, out var check).ShouldBe(new IProgressionEvent[] { new JobChanged(Novice, Spellbinder) });
        check.Allowed.ShouldBeTrue();
        state.JobId.ShouldBe(Spellbinder);
        state.JobLevel.ShouldBe(1);
        state.JobExp.ShouldBe(0);
        state.SkillPoints.ShouldBe(3);
        state.SkillLevel("skill.second_wind").ShouldBe(2);

        var refused = rules.ChangeJob(state, Warden, out var bad);
        refused.ShouldBeEmpty();
        bad.Allowed.ShouldBeFalse();
        state.JobId.ShouldBe(Spellbinder);
    }
}
