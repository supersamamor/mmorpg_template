using Game.Content.Loading;
using Game.Content.Validation;
using Game.Shared.Formulas;
using Game.Shared.Vocabulary;

namespace Game.Tests.Validation;

/// <summary>T-109: <c>balance.yaml</c> loads into <c>BalanceDef</c>, bad ranges and formulas are reported against that file.</summary>
public class BalanceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "balance_" + Guid.NewGuid().ToString("N"));

    public BalanceTests() => CopyDir(Path.Combine(RepoRoot(), "content"), _temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private void Edit(string from, string to)
    {
        var path = Path.Combine(_temp, "balance.yaml");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        text.ShouldContain(from, customMessage: "test setup: '" + from + "' not found");
        File.WriteAllText(path, text.Replace(from, to));
    }

    private ValidationReport Validate()
    {
        var load = ContentLoader.Load(_temp);
        load.Errors.ShouldBeEmpty(string.Join("\n", load.Errors));
        var keys = AssetManifest.LoadKeys(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default"));
        return ContentValidator.Validate(load.Database!, _temp, keys);
    }

    [Fact]
    public void Sample_balance_loads_with_expected_values()
    {
        var load = ContentLoader.Load(Path.Combine(RepoRoot(), "content"));
        load.Errors.ShouldBeEmpty();
        var balance = load.Database!.Balance;

        balance.Progression.BaseExpTableId.ShouldBe("exp.base");
        balance.Progression.StartingStatPoints.ShouldBe(10);
        balance.Progression.StartingStatValue.ShouldBe(1);
        balance.Progression.MaxStatValue.ShouldBe(99);
        balance.Combat.MinDamage.ShouldBe(1);
        balance.Combat.MaxDamage.ShouldBe(999999);
        balance.Combat.MinHitChance.ShouldBe(0.05);
        balance.Combat.MaxHitChance.ShouldBe(0.95);
        balance.Combat.MinCritChance.ShouldBe(0);
        balance.Combat.MaxCritChance.ShouldBe(0.5);
        Formula.Compile(balance.Combat.CritMultiplier).TryGetConstant(out var critMultiplier).ShouldBeTrue();
        critMultiplier.ShouldBe(1.5);
        balance.Movement.MaxPathLength.ShouldBe(64);
        balance.Movement.MaxNodesExpanded.ShouldBe(2048);
        balance.Aoi.BucketSize.ShouldBe(6);
        balance.Aoi.ViewRadius.ShouldBe(2);

        var points = Formula.Compile(balance.Progression.StatPointsPerBaseLevel, FormulaSymbols.BaseLevelOnly);
        var ctx = new FormulaContext(FormulaSymbols.BaseLevelOnly);
        ctx[0] = 2;
        points.Evaluate(ctx).ShouldBe(3);
        ctx[0] = 99;
        points.Evaluate(ctx).ShouldBe(12);

        var cost = Formula.Compile(balance.Progression.StatPointCost, FormulaSymbols.StatValueOnly);
        var costCtx = new FormulaContext(FormulaSymbols.StatValueOnly);
        costCtx[0] = 1;
        cost.Evaluate(costCtx).ShouldBe(2);
        costCtx[0] = 11;
        cost.Evaluate(costCtx).ShouldBe(3);
        costCtx[0] = 98;
        cost.Evaluate(costCtx).ShouldBe(11);
    }

    [Fact]
    public void Sample_balance_has_no_validation_errors()
    {
        Validate().Issues.Where(i => i.Severity == ValidationSeverity.Error).ShouldBeEmpty();
    }

    [Fact]
    public void Chances_match_the_clamps_in_formulas_yaml()
    {
        // The hit/crit formulas return 0-1 and clamp themselves; balance must agree with them.
        var load = ContentLoader.Load(Path.Combine(RepoRoot(), "content"));
        var c = load.Database!.Balance.Combat;
        load.Database.Formulas.HitChance.ShouldContain($"{c.MinHitChance}, {c.MaxHitChance}");
        load.Database.Formulas.CritChance.ShouldContain($"{c.MinCritChance}, {c.MaxCritChance}");
    }

    [Fact]
    public void Min_above_max_damage_is_an_error_in_balance_yaml()
    {
        Edit("min_damage: 1\n", "min_damage: 5000000\n");
        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.BalanceRange);
        issue.File.ShouldBe("balance.yaml");
        issue.Path.ShouldBe("combat.max_damage");
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Max_stat_below_starting_value_is_an_error()
    {
        Edit("max_stat_value: 99", "max_stat_value: 0");
        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.BalanceRange);
        issue.File.ShouldBe("balance.yaml");
        issue.Path.ShouldBe("progression.max_stat_value");
    }

    [Fact]
    public void Chance_outside_zero_to_one_is_an_error()
    {
        Edit("max_hit_chance: 0.95", "max_hit_chance: 95");
        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.BalanceRange);
        issue.File.ShouldBe("balance.yaml");
        issue.Path.ShouldBe("combat.max_hit_chance");
    }

    [Theory]
    [InlineData("max_path_length: 64", "max_path_length: 0", "movement.max_path_length")]
    [InlineData("max_nodes_expanded: 2048", "max_nodes_expanded: -5", "movement.max_nodes_expanded")]
    public void Movement_limits_must_be_above_zero(string from, string to, string path)
    {
        Edit(from, to);
        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.BalanceRange);
        issue.File.ShouldBe("balance.yaml");
        issue.Path.ShouldBe(path);
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("bucket_size: 6", "bucket_size: 0", "aoi.bucket_size")]
    [InlineData("view_radius: 2", "view_radius: -1", "aoi.view_radius")]
    public void Aoi_numbers_must_be_above_zero(string from, string to, string path)
    {
        Edit(from, to);
        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.BalanceRange);
        issue.File.ShouldBe("balance.yaml");
        issue.Path.ShouldBe(path);
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Sample_new_character_starts_on_a_walkable_cell_of_the_start_map()
    {
        var load = ContentLoader.Load(Path.Combine(RepoRoot(), "content"));
        var start = load.Database!.Balance.NewCharacter;
        start.StartMapId.ShouldBe("map.starter_meadow");
        start.StartJobId.ShouldBe("job.novice");
        start.StartFacing.ShouldBe(Directions.N);
        load.Database.GetGrid(start.StartMapId).IsWalkable(start.StartX, start.StartY).ShouldBeTrue();
    }

    [Theory]
    [InlineData("start_map_id: map.starter_meadow", "start_map_id: map.nowhere", ValidationCodes.RefMissing, "new_character.start_map_id")]
    [InlineData("start_job_id: job.novice", "start_job_id: job.nope", ValidationCodes.RefMissing, "new_character.start_job_id")]
    [InlineData("start_facing: n", "start_facing: up", ValidationCodes.VocabUnknown, "new_character.start_facing")]
    [InlineData("start_x: 12", "start_x: 0", ValidationCodes.GridCellBlocked, "new_character.start_x")]   // the border wall
    [InlineData("start_x: 12", "start_x: 480", ValidationCodes.GridCellBlocked, "new_character.start_x")] // off the grid
    public void New_character_start_is_checked_at_load(string from, string to, string code, string path)
    {
        Edit(from, to);
        var issue = Validate().Issues.Single(i => i.File == "balance.yaml" && i.Severity == ValidationSeverity.Error);
        issue.Code.ShouldBe(code);
        issue.Path.ShouldBe(path);
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Crit_multiplier_below_one_is_an_error()
    {
        Edit("crit_multiplier: \"1.5\"", "crit_multiplier: \"0.8\"");
        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.BalanceRange);
        issue.File.ShouldBe("balance.yaml");
        issue.Path.ShouldBe("combat.crit_multiplier");
    }

    [Fact]
    public void Unknown_variable_in_crit_multiplier_is_an_error()
    {
        Edit("crit_multiplier: \"1.5\"", "crit_multiplier: \"1.5 + nope\"");
        var issue = Validate().Issues.Single(i => i.Code == FormulaErrorCodes.UnknownVariable);
        issue.File.ShouldBe("balance.yaml");
        issue.Path.ShouldBe("combat.crit_multiplier");
    }

    [Fact]
    public void Negative_starting_points_is_an_error()
    {
        Edit("starting_stat_points: 10", "starting_stat_points: -1");
        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.BalanceRange);
        issue.Path.ShouldBe("progression.starting_stat_points");
    }

    [Fact]
    public void Unknown_exp_table_is_an_error()
    {
        Edit("exp.base", "exp.nope");
        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.RefMissing && i.File == "balance.yaml");
        issue.Path.ShouldBe("progression.base_exp_table_id");
    }

    [Fact]
    public void Unknown_variable_in_a_balance_formula_is_an_error()
    {
        Edit("floor((stat_value - 1) / 10) + 2", "floor((vit - 1) / 10) + 2");
        var issue = Validate().Issues.Single(i => i.File == "balance.yaml");
        issue.Path.ShouldBe("progression.stat_point_cost");
        issue.Severity.ShouldBe(ValidationSeverity.Error);
    }

    [Fact]
    public void Stat_value_is_not_allowed_outside_the_cost_formula()
    {
        Edit("floor(base_level / 10) + 3", "stat_value");
        var issue = Validate().Issues.Single(i => i.File == "balance.yaml");
        issue.Path.ShouldBe("progression.stat_points_per_base_level");
        FormulaVariables.All.Contains(FormulaVariables.StatValue).ShouldBeFalse();
    }

    [Fact]
    public void Missing_balance_file_is_a_load_error()
    {
        File.Delete(Path.Combine(_temp, "balance.yaml"));
        var load = ContentLoader.Load(_temp);
        load.Errors.ShouldContain(e => e.File == "balance.yaml" && e.Code == ContentErrorCodes.FileMissing);
    }
}
