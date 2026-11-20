using System.Text.Json.Nodes;
using Game.Content.Themes;
using Game.Content.Validation;
using Game.ThemeImport;

namespace Game.Tests.Tools;

/// <summary>
/// <c>tools/themeimport</c> (T-224) on a synthetic pack the test writes itself (<see cref="SyntheticPack"/>: centimetres, facing +Z, the exporter's bone, mesh and
/// clip names, no <c>Armature</c>). With the mapping's fixes the theme meets the contract; a second run changes nothing; without them each rule fails with its fix.
/// </summary>
public class ThemeImportTests : IDisposable
{
    private const string Theme = "eval-synthetic";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "themeimport-" + Guid.NewGuid().ToString("N"));
    private readonly string _themes;
    private readonly string _content;

    public ThemeImportTests()
    {
        var repo = RepoRoot();
        _themes = Path.Combine(_root, "themes");
        _content = Path.Combine(_root, "content");
        Copy(Path.Combine(repo, "content"), _content);
        var source = Path.Combine(repo, "themes", "default");
        foreach (var folder in new[] { "models", "anims" }) Copy(Path.Combine(source, folder), Path.Combine(_themes, "default", folder));
        File.Copy(Path.Combine(source, "asset-manifest.json"), Path.Combine(_themes, "default", "asset-manifest.json"));
        SyntheticPack.Write(Path.Combine(_root, "packs", "synth"), Directory.GetFiles(Path.Combine(source, "icons", "item"), "*.png")[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Fixed => $$"""
        theme: {{Theme}}
        pack: packs/synth
        rigs:
          humanoid:
            bones:
        {{string.Join("\n", SyntheticPack.HeroBoneMap.Select(b => $"      \"{b.Key}\": {b.Value}"))}}
          blob:
            bones: { Root: root, Body: body }
        assets:
          model.char.wanderer:
            source: hero.glb
            rig: humanoid
            anims: anim.humanoid
            scale: 0.01
            facing: +z
            meshes: { lod0: HeroMesh }
          anim.humanoid:
            source: hero.glb
            rig: humanoid
            scale: 0.01
            facing: +z
            clips: { idle: Idle_Loop, walk: Run_Fwd, attack: Sword_Slash, hit: Hit_React, die: Death_Back }
          model.monster.gel_slime:
            source: slime.glb
            rig: blob
            anims: anim.blob
            scale: 0.01
            facing: -x
          anim.blob:
            source: slime.glb
            rig: blob
            scale: 0.01
            facing: -x
            impact_delay: 0.3
            clips: { idle: idle, walk: move, attack: bite, hit: hurt, die: dead }
          model.equip.iron_blade:
            source: sword.glb
            attach: hand_r
            scale: 0.01
          icon.item.iron_blade:
            source: icons/sword.png
          model.monster.gel_slime_elite:
            variant: { of: model.monster.gel_slime, tint: "#ffb070", scale: 1.4, outline: "false" }
        """;

    private ImportResult Import(string mapping, bool check = false)
    {
        var path = Path.Combine(_root, "mapping.yaml");
        File.WriteAllText(path, mapping);
        return Importer.Run(new ImportOptions(path, _themes, _content, Check: check));
    }

    private ModelFacts Facts(string relative) => ModelFacts.Of(GltfFile.Load(Path.Combine(_themes, Theme, relative)));

    [Fact]
    public void The_fixed_mapping_imports_the_pack_into_a_theme_that_meets_the_contract()
    {
        var result = Import(Fixed);
        result.Failed.ShouldBeFalse(result.Report);
        result.Issues.Where(i => i.File.Contains(Theme)).ShouldBeEmpty(result.Report);
        result.Report.ShouldEndWith("OK");
        result.Variants.ShouldBe(new[] { "model.monster.gel_slime_elite" });
        result.Missing.ShouldContain("model.monster.mossling");
        result.Missing.ShouldNotContain("model.char.wanderer");
        result.Report.ShouldContain("model.monster: ");

        var hero = Facts("models/char/wanderer.glb");
        hero.Body0.ShouldNotBeNull();
        hero.Body0!.Height.ShouldBe(1.8f, 0.01f);
        hero.Body0.Min.Y.ShouldBe(0f, 0.01f);
        hero.SkeletonHolder.ShouldBe(ModelFacts.ArmatureName);
        ThemeContract.HumanoidBones.ShouldAllBe(b => hero.Bones.Contains(b));
        hero.BoneAt["hand_r"].X.ShouldBeGreaterThan(0.5f, "turned to face -Z, so the right hand is on +X");
        hero.Body0.HasOutlineDirections.ShouldBeTrue();

        var slime = Facts("models/monster/gel_slime.glb");
        slime.SkeletonHolder.ShouldBe(ModelFacts.ArmatureName, "an Armature was put above the root bone");
        slime.Body0!.Height.ShouldBe(0.6f, 0.01f);
        Facts("anims/blob.glb").Animations.ShouldContain("bite");
        Facts("models/equip/iron_blade.glb").Body0!.Max.Z.ShouldBe(0.45f, 0.01f);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_themes, Theme, "asset-manifest.json")))!;
        manifest["anim.humanoid"]!["clips"]!["attack"]!.GetValue<string>().ShouldBe("Sword_Slash");
        manifest["anim.blob"]!["impact_delay"]!.GetValue<double>().ShouldBe(0.3);
        manifest["model.equip.iron_blade"]!["attach"]!.GetValue<string>().ShouldBe("hand_r");
        manifest["model.monster.gel_slime_elite"]!["variant_of"]!.GetValue<string>().ShouldBe("model.monster.gel_slime");
        manifest["model.monster.gel_slime_elite"]!["outline"]!.GetValue<bool>().ShouldBeFalse();
        manifest["model.monster.gel_slime_elite"]!["file"].ShouldBeNull();
        File.Exists(Path.Combine(_themes, Theme, "icons", "item", "iron_blade.png")).ShouldBeTrue();
    }

    [Fact]
    public void Running_it_twice_changes_nothing()
    {
        Import(Fixed).Changed.ShouldBeTrue();
        var before = Directory.GetFiles(Path.Combine(_themes, Theme), "*", SearchOption.AllDirectories).ToDictionary(f => f, f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)));
        var again = Import(Fixed);
        again.Changed.ShouldBeFalse(again.Report);
        again.Report.ShouldNotContain("wrote");
        foreach (var (file, (bytes, time)) in before)
        {
            File.ReadAllBytes(file).ShouldBe(bytes);
            File.GetLastWriteTimeUtc(file).ShouldBe(time);
        }
        Import(Fixed, check: true).Changed.ShouldBeFalse();
    }

    [Fact]
    public void Check_mode_writes_nothing_and_says_what_would_change()
    {
        var result = Import(Fixed, check: true);
        result.Changed.ShouldBeTrue();
        result.Report.ShouldContain("would write");
        Directory.Exists(Path.Combine(_themes, Theme)).ShouldBeFalse();
    }

    [Fact]
    public void Without_the_fixes_each_rule_fails_with_its_fix()
    {
        var broken = Fixed.Replace("    scale: 0.01\n", "").Replace("    facing: +z\n", "").Replace("    clips: { idle: Idle_Loop, walk: Run_Fwd, attack: Sword_Slash, hit: Hit_React, die: Death_Back }\n", "");
        var result = Import(broken);
        result.Failed.ShouldBeTrue();
        result.Report.ShouldEndWith("FAILED");
        string Find(string key, string says) => result.Issues.Select(i => i.Message).FirstOrDefault(m => m.StartsWith(key + ":") && m.Contains(says))
            ?? throw new ShouldAssertException($"no finding for {key} saying '{says}' in:\n{result.Report}");
        Find("model.char.wanderer", "units tall").ShouldContain("scale: 0.01");
        Find("model.char.wanderer", "faces +Z").ShouldContain("facing: +z");
        Find("anim.humanoid", "maps no clip to 'idle'").ShouldContain("Idle_Loop");
        Find("model.equip.iron_blade", "larger than").ShouldContain("'scale'");
    }

    [Fact]
    public void Without_bone_renames_the_humanoid_rule_lists_the_missing_bones()
    {
        var broken = string.Join("\n", Fixed.Split('\n').Where(l => !l.Contains("mixamorig")));
        broken = broken.Replace("  humanoid:\n    bones:\n", "  humanoid:\n    bones: {}\n");
        var result = Import(broken);
        result.Issues.ShouldContain(i => i.Code == ValidationCodes.ThemeModel && i.Message.Contains("lacks bone(s) hips") && i.Message.Contains("rigs.humanoid.bones"), result.Report);
    }

    [Fact]
    public void Mistakes_in_the_mapping_stop_the_run_with_a_reason()
    {
        Should.Throw<MappingException>(() => Import(Fixed.Replace($"theme: {Theme}", "theme: default"))).Message.ShouldContain("themegen");
        Should.Throw<MappingException>(() => Import(Fixed.Replace("meshes: { lod0: HeroMesh }", "meshes: { lod0: Hero }"))).Message.ShouldContain("HeroMesh");
        Should.Throw<MappingException>(() => Import(Fixed.Replace("source: sword.glb", "source: axe.glb"))).Message.ShouldContain("axe.glb");
        Should.Throw<MappingException>(() => Import(Fixed.Replace("attach: hand_r", "atach: hand_r"))).Message.ShouldContain("atach");
        Should.Throw<MappingException>(() => Import(Fixed.Replace("facing: -x", "facing: up"))).Message.ShouldContain("facing 'up'");
    }

    [Fact]
    public void Files_in_the_theme_that_the_mapping_no_longer_produces_are_reported_not_deleted()
    {
        Import(Fixed);
        var stray = Path.Combine(_themes, Theme, "models", "old.glb");
        File.WriteAllBytes(stray, new byte[] { 1 });
        var result = Import(Fixed);
        result.Stale.ShouldBe(new[] { "models/old.glb" });
        File.Exists(stray).ShouldBeTrue();
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found");
    }
}
