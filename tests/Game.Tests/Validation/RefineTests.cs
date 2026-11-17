using Game.Content.Loading;
using Game.Content.Validation;
using Game.Shared.Vocabulary;

namespace Game.Tests.Validation;

/// <summary>
/// T-402: <c>refine.yaml</c> loads into <c>RefineDef</c>, and every bad number, level, failure or material is reported against that file with its
/// path and line. A material only refine.yaml uses still counts as used.
/// </summary>
public class RefineTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "refine_" + Guid.NewGuid().ToString("N"));

    public RefineTests() => CopyDir(Path.Combine(RepoRoot(), "content"), _temp);

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

    /// <summary>Replaces the first <paramref name="from"/> in a content file (refine.yaml by default); fails if absent.</summary>
    private void Edit(string from, string to, string file = ContentLayout.RefineFile)
    {
        var path = Path.Combine(_temp, file);
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        text.ShouldContain(from, customMessage: "test setup: '" + from + "' not found");
        var index = text.IndexOf(from, StringComparison.Ordinal);
        File.WriteAllText(path, text[..index] + to + text[(index + from.Length)..]);
    }

    private ValidationReport Validate()
    {
        var load = ContentLoader.Load(_temp);
        load.Errors.ShouldBeEmpty(string.Join("\n", load.Errors));
        return ContentValidator.Validate(load.Database!, _temp, AssetManifest.LoadKeys(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default")));
    }

    private void Single(string code, string path)
    {
        var report = Validate();
        var hits = report.Issues.Where(i => i.Code == code && i.Path == path).ToList();
        hits.Count.ShouldBe(1, string.Join("\n", report.Issues));
        hits[0].File.ShouldBe(ContentLayout.RefineFile);
        hits[0].Severity.ShouldBe(ValidationSeverity.Error);
        hits[0].Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Sample_refine_table_loads_and_validates()
    {
        var load = ContentLoader.Load(_temp);
        load.Errors.ShouldBeEmpty();
        var refine = load.Database!.Refine;
        refine.MaxLevel.ShouldBe(7);
        refine.Levels.Select(l => l.Level).ShouldBe([1, 2, 3, 4, 5, 6, 7]);
        refine.Levels.ShouldAllBe(l => l.MaterialItemId == "item.temper_shard" && l.SuccessPercent >= 1 && l.SuccessPercent <= 100);
        refine.Levels.Select(l => l.Failure).Distinct().ShouldBe([RefineFailures.Keep, RefineFailures.Down, RefineFailures.Break]);
        refine.StepTo(1)!.SuccessPercent.ShouldBe(100);   // the first refine always works (the scripted run relies on it)
        refine.StepTo(8).ShouldBeNull();
        Validate().Issues.Where(i => i.File == ContentLayout.RefineFile).ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_refine_file_is_a_load_error()
    {
        File.Delete(Path.Combine(_temp, ContentLayout.RefineFile));
        var load = ContentLoader.Load(_temp);
        load.Errors.ShouldContain(e => e.Code == ContentErrorCodes.FileMissing && e.File == ContentLayout.RefineFile);
    }

    [Fact]
    public void Levels_must_be_one_per_level_in_order()
    {
        Edit("max_level: 7", "max_level: 8");
        Single(ValidationCodes.RefineRange, "levels");
    }

    [Fact]
    public void A_level_out_of_order_is_reported()
    {
        Edit("level: 3,", "level: 4,");
        Single(ValidationCodes.RefineRange, "levels[2].level");
    }

    [Fact]
    public void A_chance_outside_1_to_100_is_reported()
    {
        Edit("success_percent: 90", "success_percent: 0");
        Single(ValidationCodes.RefineRange, "levels[2].success_percent");
    }

    [Fact]
    public void A_material_quantity_below_1_is_reported()
    {
        Edit("material_quantity: 3, failure: break", "material_quantity: 0, failure: break");
        Single(ValidationCodes.RefineRange, "levels[6].material_quantity");
    }

    [Fact]
    public void An_unknown_material_is_a_missing_reference()
    {
        Edit("{ level: 1, success_percent: 100, material_item_id: item.temper_shard", "{ level: 1, success_percent: 100, material_item_id: item.temper_shardd");
        Single(ValidationCodes.RefMissing, "levels[0].material_item_id");
    }

    [Fact]
    public void A_material_must_be_a_material_item()
    {
        Edit("{ level: 1, success_percent: 100, material_item_id: item.temper_shard", "{ level: 1, success_percent: 100, material_item_id: item.bone_dirk");
        Single(ValidationCodes.RefineRange, "levels[0].material_item_id");
    }

    [Fact]
    public void A_material_used_only_by_refining_is_not_an_unused_item()
    {
        foreach (var tier in new[] { "tier1_supplies", "tier2_supplies", "tier3_supplies" })
            Edit("  - { item_id: item.temper_shard", "  - { item_id: item.healing_draught", Path.Combine("drops", tier + ".yaml"));
        Validate().Issues.ShouldNotContain(i => i.Code == ValidationCodes.WarnUnusedItem && i.Message.Contains("item.temper_shard"));
    }
}
