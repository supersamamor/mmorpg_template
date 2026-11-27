using System.Text.Json.Nodes;
using Game.Content.Loading;
using Game.Content.Themes;
using Game.Content.Validation;
using Game.ThemeGen;

namespace Game.Tests.Validation;

/// <summary>
/// T-711: <c>validate --theme</c> on textured characters. A textured test theme is written for the class (<see cref="TexturedTheme"/>, from the shipped placeholders,
/// in a temporary folder): every character part embeds the same atlas in its own file, so they merge; a part with another image, no texture, another material
/// setting or a second texture is a warning naming it and the bodies it splits from.
/// </summary>
public class ThemeImageTests : IClassFixture<ThemeImageTests.Pack>
{
    private const string Theme = "textured";
    private readonly Pack _pack;

    /// <summary>The default pack copied once; each test writes the textured theme afresh beside it.</summary>
    public sealed class Pack : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "themeimage-" + Guid.NewGuid().ToString("N"));

        public Pack()
        {
            var source = Path.Combine(RepoRoot(), "themes", "default");
            foreach (var folder in new[] { "models", "anims" })
                foreach (var file in Directory.GetFiles(Path.Combine(source, folder), "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(Themes, "default", Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            File.Copy(Path.Combine(source, "asset-manifest.json"), Path.Combine(Themes, "default", "asset-manifest.json"));
            var load = ContentLoader.Load(Path.Combine(RepoRoot(), "content"));
            load.Errors.ShouldBeEmpty();
            Database = load.Database!;
        }

        public string Themes => Path.Combine(_root, "themes");

        public Game.Content.Registry.ContentDatabase Database { get; }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    public ThemeImageTests(Pack pack)
    {
        _pack = pack;
        var folder = Path.Combine(pack.Themes, Theme);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    private IReadOnlyList<ValidationIssue> Check(params string[] other)
    {
        if (!Directory.Exists(Path.Combine(_pack.Themes, Theme))) TexturedTheme.Write(_pack.Themes, Theme, other);
        return ThemeCheck.Check(_pack.Database, _pack.Themes, Theme);
    }

    private string FileOf(string relative) => Path.Combine(_pack.Themes, Theme, relative.Replace('/', Path.DirectorySeparatorChar));

    private void EditModel(string relative, Action<JsonObject> material)
    {
        var file = GltfFile.FromGlb(File.ReadAllBytes(FileOf(relative)));
        foreach (var m in file.Array("materials").OfType<JsonObject>()) material(m);
        File.WriteAllBytes(FileOf(relative), file.ToGlb());
    }

    [Fact]
    public void Parts_that_embed_the_same_atlas_each_in_its_own_file_merge_without_a_warning()
    {
        Check().ShouldBeEmpty();
        var body = ModelFacts.Of(GltfFile.Load(FileOf("models/char/body_female.glb")));
        var hair = ModelFacts.Of(GltfFile.Load(FileOf("models/char/hair_female_bob.glb")));
        body.MaterialGroups.Values.ShouldHaveSingleItem().ShouldStartWith("image:");
        hair.MaterialGroups.Values.ShouldBe(body.MaterialGroups.Values); // the same pixels in another file are the same image
    }

    [Fact]
    public void A_part_with_another_image_is_a_warning_with_its_key_and_the_body()
    {
        var issue = Check("model.char.hair_female_bob").ShouldHaveSingleItem();
        issue.Severity.ShouldBe(ValidationSeverity.Warning);
        issue.Code.ShouldBe(ValidationCodes.WarnThemeLook);
        issue.File.ShouldBe($"themes/{Theme}/asset-manifest.json");
        issue.Line.ShouldBeGreaterThan(0);
        issue.Message.ShouldStartWith("model.char.hair_female_bob: draws image ");
        issue.Message.ShouldContain("where model.char.body_female draws image ");
        issue.Message.ShouldContain("one more draw call");
        issue.Message.ShouldNotContain("model.char.body_male"); // a female hairstyle splits only from the female body
        ModelFacts.Of(GltfFile.Load(FileOf("models/char/hair_female_bob.glb"))).MaterialGroups.Values.ShouldNotBe(
            ModelFacts.Of(GltfFile.Load(FileOf("models/char/body_female.glb"))).MaterialGroups.Values);
    }

    [Fact]
    public void Worn_equipment_on_another_image_splits_from_every_body()
    {
        var issue = Check("model.equip.wooden_buckler").ShouldHaveSingleItem();
        issue.Message.ShouldContain("model.equip.wooden_buckler");
        issue.Message.ShouldContain("model.char.body_female and model.char.body_male draw image");
    }

    [Fact]
    public void An_untextured_part_on_a_textured_body_is_a_warning()
    {
        Check();
        var manifest = JsonNode.Parse(File.ReadAllText(FileOf("asset-manifest.json")))!.AsObject();
        manifest.Remove("model.char.face_male_keen"); // now default's, which has no texture
        File.WriteAllText(FileOf("asset-manifest.json"), manifest.ToJsonString());
        var issue = ThemeCheck.Check(_pack.Database, _pack.Themes, Theme).ShouldHaveSingleItem();
        issue.Message.ShouldStartWith("model.char.face_male_keen: draws no texture where model.char.body_male draws image ");
        issue.File.ShouldBe("themes/default/asset-manifest.json");
    }

    [Fact]
    public void The_same_image_with_other_material_settings_is_a_warning_that_says_which()
    {
        Check();
        EditModel("models/outfit/rain_cape.glb", m => m["pbrMetallicRoughness"]!["roughnessFactor"] = 0.4);
        var issue = ThemeCheck.Check(_pack.Database, _pack.Themes, Theme).ShouldHaveSingleItem();
        issue.Message.ShouldContain("model.outfit.rain_cape");
        issue.Message.ShouldContain("r0.4");
        issue.Message.ShouldContain("r0.9");
    }

    [Fact]
    public void A_part_with_a_second_texture_is_a_warning_since_it_keeps_its_own_material()
    {
        Check();
        EditModel("models/char/hair_male_swept.glb", m => m["normalTexture"] = new JsonObject { ["index"] = 0 });
        var issue = ThemeCheck.Check(_pack.Database, _pack.Themes, Theme).ShouldHaveSingleItem();
        issue.Message.ShouldStartWith("model.char.hair_male_swept: draws a material of its own");
    }

    [Fact]
    public void The_untextured_default_pack_has_no_image_warnings() =>
        ThemeCheck.Check(_pack.Database, _pack.Themes, "default").ShouldBeEmpty();

    [Fact]
    public void An_untextured_part_with_other_material_settings_on_an_untextured_body_is_not_the_atlas_rule()
    {
        var folder = Path.Combine(_pack.Themes, Theme);
        var model = Path.Combine(folder, "models", "equip", "wooden_buckler.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        var file = GltfFile.FromGlb(File.ReadAllBytes(Path.Combine(_pack.Themes, "default", "models", "equip", "wooden_buckler.glb")));
        foreach (var m in file.Array("materials").OfType<JsonObject>()) m["pbrMetallicRoughness"]!["metallicFactor"] = 1.0; // a metal shield
        File.WriteAllBytes(model, file.ToGlb());
        var defaults = JsonNode.Parse(File.ReadAllText(Path.Combine(_pack.Themes, "default", "asset-manifest.json")))!.AsObject();
        File.WriteAllText(Path.Combine(folder, "asset-manifest.json"), new JsonObject { ["model.equip.wooden_buckler"] = defaults["model.equip.wooden_buckler"]!.DeepClone() }.ToJsonString());
        ThemeCheck.Check(_pack.Database, _pack.Themes, Theme).Where(i => i.Code == ValidationCodes.WarnThemeLook).ShouldBeEmpty();
    }

    [Fact]
    public void The_textured_theme_keeps_the_vertex_colours_and_only_adds_texture_coordinates()
    {
        var source = GltfFile.Load(Path.Combine(_pack.Themes, "default", "models", "char", "body_male.glb"));
        var textured = GltfFile.FromGlb(TexturedTheme.Texture(File.ReadAllBytes(Path.Combine(_pack.Themes, "default", "models", "char", "body_male.glb")), TexturedTheme.Atlas));
        foreach (var (a, b) in source.Array("meshes").Zip(textured.Array("meshes")))
        {
            var before = a!["primitives"]![0]!["attributes"]!.AsObject();
            var after = b!["primitives"]![0]!["attributes"]!.AsObject();
            after.ContainsKey("TEXCOORD_0").ShouldBeTrue();
            textured.ReadFloats(after["COLOR_0"]!.GetValue<int>()).ShouldBe(source.ReadFloats(before["COLOR_0"]!.GetValue<int>()));
        }
        textured.ImageBytes(0).ShouldBe(TexturedTheme.Atlas);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repo root");
    }
}
