using System.Text.Json.Nodes;
using Game.Content.Loading;
using Game.Content.Themes;
using Game.Content.Validation;

namespace Game.Tests.Validation;

/// <summary>
/// <c>validate</c>'s theme contract checks (T-224, docs/THEMES.md) on a copy of the shipped pack: variants, animation libraries and the measured facts of the
/// placeholder models. Each broken copy is caught with the key, the reason and the fix. The model-file rules are also exercised on a synthetic pack in
/// <c>Tools/ThemeImportTests</c>.
/// </summary>
public class ThemeContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "themecontract-" + Guid.NewGuid().ToString("N"));
    private readonly string _themes;

    public ThemeContractTests()
    {
        _themes = Path.Combine(_root, "themes");
        var source = Path.Combine(RepoRoot(), "themes", "default");
        foreach (var folder in new[] { "models", "anims" })
            foreach (var file in Directory.GetFiles(Path.Combine(source, folder), "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(_themes, "default", Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        File.Copy(Path.Combine(source, "asset-manifest.json"), Path.Combine(_themes, "default", "asset-manifest.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ---- the shipped pack ----

    [Fact]
    public void The_shipped_variant_example_draws_the_slime_bigger_and_warmer_and_takes_its_rig()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_themes, "default", "asset-manifest.json")))!;
        var variants = manifest.AsObject().Where(p => p.Value?["variant_of"] is not null).ToList();
        variants.Count.ShouldBe(1, "the placeholder theme has at most one variant example (T-224)");
        var keys = (AssetKeySet)AssetManifest.LoadKeys(Path.Combine(_themes, "default", "asset-manifest.json"));
        keys.InfoOf(variants[0].Key)!.Rig.ShouldBe(keys.InfoOf(variants[0].Value!["variant_of"]!.GetValue<string>())!.Rig);
    }

    [Theory]
    [InlineData("model.char.wanderer", 1.2, 2.4)]
    [InlineData("model.char.body_female", 1.2, 2.4)]
    [InlineData("model.monster.gel_slime", 0.2, 1.5)]
    [InlineData("model.monster.thornback_boar", 0.4, 2)]
    public void The_placeholders_are_measured_in_cells(string key, double low, double high)
    {
        var facts = Facts(key);
        facts.Body0.ShouldNotBeNull();
        facts.Body0!.Height.ShouldBeInRange((float)low, (float)high);
        Math.Abs(facts.Body0.Min.Y).ShouldBeLessThan(0.05f, "the origin is on the ground");
        facts.SkeletonHolder.ShouldBe(ModelFacts.ArmatureName);
    }

    [Fact]
    public void The_placeholder_humanoid_has_the_shared_bones_and_faces_minus_z()
    {
        var facts = Facts("model.char.warden");
        ThemeContract.HumanoidBones.ShouldAllBe(b => facts.Bones.Contains(b));
        facts.BoneAt["hand_r"].X.ShouldBeGreaterThan(facts.BoneAt["hand_l"].X);
        facts.Body0!.HasOutlineDirections.ShouldBeTrue();
        facts.Body0.Triangles.ShouldBeLessThanOrEqualTo(ThemeContract.CharacterLod0Triangles);
        facts.Body1!.Triangles.ShouldBeLessThanOrEqualTo(ThemeContract.CharacterLod1Triangles);
    }

    // ---- variants ----

    [Fact]
    public void A_variant_whose_base_is_in_no_theme_is_an_error()
    {
        Edit(m => m["model.monster.gel_slime_elite"]!["variant_of"] = "model.monster.nothing");
        var issue = Check().ShouldHaveSingleItem();
        issue.Code.ShouldBe(ValidationCodes.ThemeVariant);
        issue.Message.ShouldContain("model.monster.nothing");
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Variants_that_form_a_loop_are_an_error()
    {
        Edit(m =>
        {
            m["model.monster.loop_a"] = new JsonObject { ["variant_of"] = "model.monster.loop_b" };
            m["model.monster.loop_b"] = new JsonObject { ["variant_of"] = "model.monster.loop_a" };
        });
        var issues = Check();
        issues.Count.ShouldBe(2);
        issues.ShouldAllBe(i => i.Message.Contains("loop"));
    }

    [Fact]
    public void A_variant_with_its_own_file_or_rig_is_an_error_that_says_it_comes_from_the_base()
    {
        Edit(m =>
        {
            m["model.monster.gel_slime_elite"]!["file"] = "models/monster/gel_slime.glb";
            m["model.monster.gel_slime_elite"]!["rig"] = "blob";
        });
        var issues = Check();
        issues.Count.ShouldBe(2);
        issues.ShouldAllBe(i => i.Message.Contains("from its base"));
    }

    [Theory]
    [InlineData("tint", "orange", "colour")]
    [InlineData("scale", "big", "number")]
    [InlineData("outline", "#12", "false (no outline)")]
    public void A_bad_tint_scale_or_outline_is_an_error(string field, string value, string says)
    {
        Edit(m => m["model.monster.gel_slime_elite"]![field] = value);
        var issue = Check().ShouldHaveSingleItem();
        issue.Message.ShouldContain($"'{field}'");
        issue.Message.ShouldContain(says);
    }

    [Fact]
    public void A_scale_outside_the_range_is_an_error()
    {
        Edit(m => m["model.monster.gel_slime_elite"]!["scale"] = 40);
        Check().ShouldHaveSingleItem().Message.ShouldContain("from 0.1 to 4");
    }

    [Fact]
    public void A_worn_piece_cannot_be_a_variant_nor_the_base_of_one()
    {
        Edit(m =>
        {
            m["model.equip.iron_blade_gold"] = new JsonObject { ["variant_of"] = "model.equip.iron_blade", ["tint"] = "#ffd700" };
            m["model.monster.hairy"] = new JsonObject { ["variant_of"] = "model.char.hair_male_crop" };
        });
        var issues = Check();
        issues.ShouldContain(i => i.Message.StartsWith("model.equip.iron_blade_gold") && i.Message.Contains("whole models"));
        issues.ShouldContain(i => i.Message.StartsWith("model.monster.hairy") && i.Message.Contains("worn piece"));
    }

    // ---- animation libraries ----

    [Fact]
    public void A_library_missing_a_required_clip_is_an_error_that_lists_the_file_s_clips()
    {
        Edit(m => ((JsonObject)m["anim.blob"]!["clips"]!).Remove("die"));
        var issue = Check().ShouldHaveSingleItem();
        issue.Code.ShouldBe(ValidationCodes.ThemeAnim);
        issue.Message.ShouldContain("'die'");
        issue.Message.ShouldContain("the file has: ");
        issue.Message.ShouldContain("Die");
    }

    [Fact]
    public void A_clip_name_the_file_lacks_is_an_error()
    {
        Edit(m => m["anim.blob"]!["clips"]!["walk"] = "Wobble");
        var issue = Check().ShouldHaveSingleItem();
        issue.Message.ShouldContain("'Wobble'");
        issue.Message.ShouldContain("does not have");
    }

    [Fact]
    public void A_clip_id_that_is_no_animation_id_is_an_error()
    {
        Edit(m => m["anim.blob"]!["clips"]!["dance"] = "Idle");
        Check().ShouldHaveSingleItem().Message.ShouldContain("not an animation id");
    }

    [Fact]
    public void A_model_whose_library_is_on_another_rig_is_an_error()
    {
        Edit(m => m["model.monster.gel_slime"]!["anims"] = "anim.insect");
        var issues = Check();
        issues.ShouldContain(i => i.Message.StartsWith("model.monster.gel_slime:") && i.Message.Contains("rig 'insect'"));
    }

    private ModelFacts Facts(string key)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_themes, "default", "asset-manifest.json")))!;
        return ModelFacts.Of(GltfFile.Load(Path.Combine(_themes, "default", manifest[key]!["file"]!.GetValue<string>())));
    }

    private IReadOnlyList<ValidationIssue> Check()
    {
        var load = ContentLoader.Load(Path.Combine(RepoRoot(), "content"));
        load.Database.ShouldNotBeNull();
        return ThemeCheck.Check(load.Database!, _themes, "default");
    }

    private void Edit(Action<JsonObject> change)
    {
        var path = Path.Combine(_themes, "default", "asset-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(manifest);
        File.WriteAllText(path, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found");
    }
}
