using System.Text.Json.Nodes;
using Game.Content.Loading;
using Game.Content.Themes;
using Game.Content.Validation;

namespace Game.Tests.Validation;

/// <summary>
/// T-706: what <c>validate</c> checks for character looks, on copies of the shipped pack and content: <c>recolor</c>, <c>hides_hair</c>, the paint on the bodies, the
/// budget of a body with its face and hairstyle, and a job's look.
/// </summary>
public class ThemeAppearanceTests : IClassFixture<ThemeAppearanceTests.Pack>
{
    private readonly string _themes;
    private readonly string _content;

    /// <summary>One copy of the pack and content for the class (they are large); each test starts with the shipped manifest and jobs.</summary>
    public sealed class Pack : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "themeappearance-" + Guid.NewGuid().ToString("N"));

        public Pack()
        {
            foreach (var folder in new[] { "models", "anims" }) Copy(Path.Combine(RepoRoot(), "themes", "default", folder), Path.Combine(Themes, "default", folder));
            Copy(Path.Combine(RepoRoot(), "content"), Content);
        }

        public string Themes => Path.Combine(_root, "themes");

        public string Content => Path.Combine(_root, "content");

        public void Reset()
        {
            File.Copy(Path.Combine(RepoRoot(), "themes", "default", "asset-manifest.json"), Path.Combine(Themes, "default", "asset-manifest.json"), true);
            Copy(Path.Combine(RepoRoot(), "content", "jobs"), Path.Combine(Content, "jobs"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    public ThemeAppearanceTests(Pack pack)
    {
        pack.Reset();
        _themes = pack.Themes;
        _content = pack.Content;
    }

    [Fact]
    public void The_shipped_pack_has_no_findings() => Check().ShouldBeEmpty();

    // ---- manifest fields ----

    [Theory]
    [InlineData("\"red\"", "an object")]
    [InlineData("{\"eyes\":\"#102030\"}", "not an appearance colour role")]
    [InlineData("{\"skin\":\"tan\"}", "#rrggbb")]
    [InlineData("{\"skin\":[\"#102030\",7]}", "#rrggbb")]
    [InlineData("{\"hair\":true}", "#rrggbb")]
    public void A_bad_recolor_is_an_error(string value, string says)
    {
        Edit(m => m["model.char.hair_male_crop"]!["recolor"] = JsonNode.Parse(value));
        var issue = Check().ShouldHaveSingleItem();
        issue.Code.ShouldBe(ValidationCodes.ThemeAppearance);
        issue.Severity.ShouldBe(ValidationSeverity.Error);
        issue.Message.ShouldContain(says);
        issue.Message.ShouldContain("model.char.hair_male_crop");
    }

    [Fact]
    public void A_good_recolor_is_fine()
    {
        Edit(m => m["model.char.hair_male_crop"]!["recolor"] = JsonNode.Parse("{\"hair\":[\"#3b3240\"],\"skin\":[]}"));
        Check().ShouldBeEmpty();
    }

    [Fact]
    public void Recolor_on_something_that_is_no_model_is_an_error()
    {
        Edit(m => m["icon.item.iron_blade"]!["recolor"] = JsonNode.Parse("{\"hair\":\"#3b3240\"}"));
        Check().ShouldHaveSingleItem().Message.ShouldContain("only a model");
    }

    [Theory]
    [InlineData("model.equip.iron_blade", "\"yes\"", "true or false")]
    [InlineData("model.monster.gel_slime", "true", "only a worn piece")]
    public void A_bad_hides_hair_is_an_error(string key, string value, string says)
    {
        Edit(m => m[key]!["hides_hair"] = JsonNode.Parse(value));
        var issue = Check().ShouldHaveSingleItem();
        issue.Code.ShouldBe(ValidationCodes.ThemeAppearance);
        issue.Message.ShouldContain(says);
    }

    // ---- paint and budgets ----

    [Fact]
    public void A_body_without_its_skin_paint_is_a_warning_that_says_what_to_paint()
    {
        Edit(m => m["model.char.body_male"]!["recolor"] = JsonNode.Parse("{\"skin\":\"#00ff00\"}"));
        var issue = Check().ShouldHaveSingleItem();
        (issue.Code, issue.Severity).ShouldBe((ValidationCodes.WarnThemeLook, ValidationSeverity.Warning));
        issue.Message.ShouldContain("#00ff00");
        issue.Message.ShouldContain("skin tones do not show");
    }

    [Fact]
    public void A_body_without_cloth_paint_is_a_warning_while_a_job_has_a_cloth_colour()
    {
        Edit(m => m["model.char.body_female"]!["recolor"] = JsonNode.Parse("{\"cloth\":\"#00ff00\"}"));
        Check().ShouldHaveSingleItem().Message.ShouldContain("job");
        foreach (var file in Directory.GetFiles(Path.Combine(_content, "jobs"))) File.WriteAllText(file, File.ReadAllText(file).Replace("\r\n", "\n").Split("\nlook:")[0] + "\n");
        Check().ShouldBeEmpty("no job colours cloth");
    }

    [Fact]
    public void An_empty_list_says_on_purpose_that_nothing_takes_the_colour()
    {
        Edit(m => m["model.char.body_male"]!["recolor"] = JsonNode.Parse("{\"skin\":[],\"cloth\":[]}"));
        Check().ShouldBeEmpty();
    }

    [Fact]
    public void A_body_that_is_over_the_budget_only_with_its_largest_face_and_hairstyle_is_an_error()
    {
        // The placeholder body is 620 triangles far away; a "hairstyle" of 1,812 is inside no piece budget, but the sum is what this rule names.
        Edit(m => m["model.char.hair_male_long"]!["file"] = "models/char/body_male.glb");
        var sum = Check().Where(i => i.Code == ValidationCodes.ThemeBudget && i.Message.StartsWith("model.char.body_male:", StringComparison.Ordinal)).ToList();
        sum.ShouldBeEmpty("1,812 + 72 + 1,812 near and 620 + 72 + 620 far are inside 5,000 and 2,000: " + string.Join("\n", sum));

        Edit(m =>
        {
            m["model.char.hair_male_long"]!["file"] = "models/char/warden.glb"; // 608 far, but the far body below is 1,716
            m["model.char.body_male"]!["file"] = "models/char/body_near_only.glb";
        });
        NearOnly(Path.Combine(_themes, "default", "models", "char", "body_male.glb"), Path.Combine(_themes, "default", "models", "char", "body_near_only.glb"));
        var issue = Check().Single(i => i.Code == ValidationCodes.ThemeBudget && i.Message.StartsWith("model.char.body_male:", StringComparison.Ordinal));
        issue.Severity.ShouldBe(ValidationSeverity.Error);
        issue.Message.ShouldContain("with its largest face and hairstyle");
        issue.Message.ShouldContain("2000");
    }

    /// <summary>The same model with its far mesh renamed away, so far characters draw the near one.</summary>
    private static void NearOnly(string from, string to)
    {
        var file = GltfFile.Load(from);
        foreach (var node in file.Nodes.OfType<JsonObject>())
            if (node["name"]?.GetValue<string>() == ModelFacts.Lod1) node["name"] = "Elsewhere";
        File.WriteAllBytes(to, file.ToGlb());
    }

    // ---- jobs ----

    [Fact]
    public void A_job_piece_must_be_a_worn_piece()
    {
        Job("  pieces:\n    - { model_asset_key: model.char.face_male_calm, gender: male, slot: head_top }\n");
        var issue = Check().ShouldHaveSingleItem();
        issue.Code.ShouldBe(ValidationCodes.EquipManifest);
        issue.Message.ShouldContain("exactly one of 'attach' or 'skinned'");
    }

    [Fact]
    public void A_job_look_with_pieces_loads_and_validates()
    {
        Job("  pieces:\n    - { model_asset_key: model.equip.padded_vest, slot: body }\n    - { model_asset_key: model.equip.leather_cap, gender: female }\n");
        Check().ShouldBeEmpty();
        Validate().ShouldBeEmpty();
        var job = ContentLoader.Load(_content).Database!.Jobs.All.Single(j => j.Id == "job.warden");
        job.Look.Pieces.Select(p => (p.ModelAssetKey, p.Gender, p.Slot)).ShouldBe([("model.equip.padded_vest", null, "body"), ("model.equip.leather_cap", "female", null)]);
    }

    [Theory]
    [InlineData("  pieces:\n    - { model_asset_key: model.equip.nowhere }\n", "look.pieces[0].model_asset_key", "model.equip.nowhere")]
    [InlineData("  pieces:\n    - { model_asset_key: model.equip.padded_vest, gender: robot }\n", "look.pieces[0].gender", "robot")]
    [InlineData("  pieces:\n    - { model_asset_key: model.equip.padded_vest, slot: tail }\n", "look.pieces[0].slot", "tail")]
    [InlineData("  cloth_color: \"blue\"\n", "look.cloth_color", "blue")]
    public void A_bad_job_look_is_an_error_at_its_place(string yaml, string path, string says)
    {
        Job(yaml);
        var issue = Validate().ShouldHaveSingleItem();
        issue.Path.ShouldBe(path);
        issue.Message.ShouldContain(says);
        issue.File.ShouldContain("warden.yaml");
    }

    private void Job(string look)
    {
        var path = Path.Combine(_content, "jobs", "warden.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\r\n", "\n").Split("\nlook:")[0] + "\nlook:\n" + look);
    }

    private IReadOnlyList<ValidationIssue> Validate()
    {
        var load = ContentLoader.Load(_content);
        load.Errors.ShouldBeEmpty(string.Join("\n", load.Errors));
        return ContentValidator.Validate(load.Database!, _content, AssetManifest.LoadKeys(AssetManifest.PathFor(_themes, "default"))).Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
    }

    private IReadOnlyList<ValidationIssue> Check()
    {
        var load = ContentLoader.Load(_content);
        load.Errors.ShouldBeEmpty(string.Join("\n", load.Errors));
        return ThemeCheck.Check(load.Database!, _themes, "default");
    }

    private void Edit(Action<JsonObject> change)
    {
        var path = Path.Combine(_themes, "default", "asset-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(manifest);
        File.WriteAllText(path, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found");
    }
}
