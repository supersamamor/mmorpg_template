using System.Text.Json.Nodes;
using Game.Content.Loading;
using Game.Content.Validation;

namespace Game.Tests.Validation;

/// <summary>
/// <c>validate</c>'s theme checks for worn equipment and weapon clips (T-405): the shipped pack is clean, and each rule has a broken copy of the pack that it catches
/// with the file, the key and the reason.
/// </summary>
public class ThemeCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "themecheck-" + Guid.NewGuid().ToString("N"));
    private readonly string _themes;

    public ThemeCheckTests()
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

    [Fact]
    public void The_shipped_pack_has_no_findings() => Check().ShouldBeEmpty();

    [Fact]
    public void An_equip_entry_with_both_attach_and_skinned_is_an_error()
    {
        Edit(m => m["model.equip.padded_vest"]!["attach"] = "hand_r");
        var issue = Check().ShouldHaveSingleItem();
        issue.Severity.ShouldBe(ValidationSeverity.Error);
        issue.File.ShouldBe("themes/default/asset-manifest.json");
        issue.Line.ShouldBeGreaterThan(0);
        issue.Message.ShouldContain("model.equip.padded_vest");
        issue.Message.ShouldContain("both");
    }

    [Fact]
    public void An_equip_entry_with_neither_attach_nor_skinned_is_an_error()
    {
        Edit(m => ((JsonObject)m["model.equip.oak_staff"]!).Remove("attach"));
        var issue = Check().ShouldHaveSingleItem();
        issue.Message.ShouldContain("model.equip.oak_staff");
        issue.Message.ShouldContain("neither");
    }

    [Fact]
    public void An_attach_bone_the_rig_lacks_is_an_error_that_lists_the_bones()
    {
        Edit(m => m["model.equip.bone_dirk"]!["attach"] = "tail");
        var issue = Check().ShouldHaveSingleItem();
        issue.Severity.ShouldBe(ValidationSeverity.Error);
        issue.Message.ShouldContain("model.equip.bone_dirk");
        issue.Message.ShouldContain("'tail'");
        issue.Message.ShouldContain("hand_r");
    }

    [Fact]
    public void A_garment_skinned_to_an_unknown_rig_is_an_error()
    {
        Edit(m => m["model.equip.padded_vest"]!["skinned"] = "centaur");
        var issue = Check().ShouldHaveSingleItem();
        issue.Message.ShouldContain("centaur");
    }

    [Fact]
    public void A_clip_suffix_that_is_no_weapon_type_is_an_error()
    {
        Edit(m => m["anim.humanoid"]!["clips"]!["attack.wand"] = "AttackStab");
        var issue = Check().ShouldHaveSingleItem();
        issue.Message.ShouldContain("attack.wand");
        issue.Message.ShouldContain("not a weapon type");
    }

    [Fact]
    public void A_worn_visual_without_a_file_is_a_warning_that_names_the_fallback()
    {
        File.Delete(Path.Combine(_themes, "default", "models", "equip", "leather_cap.glb"));
        var issue = Check().ShouldHaveSingleItem();
        issue.Severity.ShouldBe(ValidationSeverity.Warning);
        issue.Message.ShouldContain("model.equip.leather_cap");
        issue.Message.ShouldContain("model.missing");
    }

    [Fact]
    public void A_model_that_is_a_git_lfs_pointer_says_to_run_git_lfs_pull()
    {
        File.WriteAllText(Path.Combine(_themes, "default", "models", "char", "body_male.glb"), "version https://git-lfs.github.com/spec/v1\noid sha256:00\nsize 1\n");
        var issue = Check().ShouldHaveSingleItem();
        issue.Severity.ShouldBe(ValidationSeverity.Error);
        issue.Message.ShouldContain("git lfs pull");
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
