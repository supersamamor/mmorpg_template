using System.Text.Json;
using Game.Content.ClientExport;
using Game.Content.Loading;
using Game.Content.Validation;
using Game.Shared.Vocabulary;

namespace Game.Tests.Validation;

/// <summary>T-704: <c>appearance.yaml</c> loads into <c>AppearanceDef</c>, bad options are reported against that file, the bundle carries it.</summary>
public class AppearanceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "appearance_" + Guid.NewGuid().ToString("N"));

    public AppearanceTests() => CopyDir(Path.Combine(RepoRoot(), "content"), _temp);

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

    /// <summary>Replaces the first <paramref name="from"/> in appearance.yaml; fails if absent.</summary>
    private void Edit(string from, string to)
    {
        var path = Path.Combine(_temp, "appearance.yaml");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        text.ShouldContain(from, customMessage: "test setup: '" + from + "' not found");
        var index = text.IndexOf(from, StringComparison.Ordinal);
        File.WriteAllText(path, text[..index] + to + text[(index + from.Length)..]);
    }

    private static IReadOnlySet<string> Keys() =>
        AssetManifest.LoadKeys(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default"));

    private ValidationReport Validate(IReadOnlySet<string>? keys = null)
    {
        var load = ContentLoader.Load(_temp);
        load.Errors.ShouldBeEmpty(string.Join("\n", load.Errors));
        return ContentValidator.Validate(load.Database!, _temp, keys ?? Keys());
    }

    private ValidationIssue Single(string code, string path)
    {
        var report = Validate();
        var hits = report.Issues.Where(i => i.Code == code && i.Path == path).ToList();
        hits.Count.ShouldBe(1, string.Join("\n", report.Issues));
        hits[0].File.ShouldBe("appearance.yaml");
        hits[0].Severity.ShouldBe(ValidationSeverity.Error);
        hits[0].Line.ShouldBeGreaterThan(0);
        return hits[0];
    }

    [Fact]
    public void Sample_appearance_loads_and_validates()
    {
        var load = ContentLoader.Load(_temp);
        var a = load.Database!.Appearance;

        a.Genders.Keys.OrderBy(k => k).ShouldBe(new[] { Genders.Female, Genders.Male });
        foreach (var g in a.Genders.Values)
        {
            g.Faces.Count.ShouldBe(3);
            g.Hairstyles.Count.ShouldBe(4);
        }
        a.SkinTones.Count.ShouldBe(6);
        a.HairColors.Count.ShouldBe(6);
        Validate().Issues.Where(i => i.File == "appearance.yaml").ShouldBeEmpty();
    }

    [Fact]
    public void Missing_file_is_a_load_error()
    {
        File.Delete(Path.Combine(_temp, "appearance.yaml"));

        var load = ContentLoader.Load(_temp);

        load.Database.ShouldBeNull();
        load.Errors.ShouldContain(e => e.File == "appearance.yaml" && e.Code == ContentErrorCodes.FileMissing);
    }

    [Fact]
    public void Missing_gender_is_reported()
    {
        var path = Path.Combine(_temp, "appearance.yaml");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var start = text.IndexOf("  female:", StringComparison.Ordinal);
        var end = text.IndexOf("skin_tones:", StringComparison.Ordinal);
        File.WriteAllText(path, text[..start] + text[end..]);

        Single(ValidationCodes.AppearanceGender, "genders").Message.ShouldContain("female");
    }

    [Fact]
    public void Unknown_gender_is_reported()
    {
        Edit("  female:", "  other:");

        Validate().Issues.ShouldContain(i => i.Code == ValidationCodes.AppearanceGender && i.Path == "genders.other");
        Validate().Issues.ShouldContain(i => i.Code == ValidationCodes.AppearanceGender && i.Path == "genders" && i.Message.Contains("'female'"));
    }

    [Fact]
    public void Duplicate_id_is_reported_across_genders()
    {
        Edit("id: face.female_bright", "id: face.male_calm");

        Single(ValidationCodes.AppearanceId, "genders.female.faces[1].id").Message.ShouldContain("duplicate");
    }

    [Fact]
    public void Wrong_prefix_is_reported()
    {
        Edit("id: hair.male_swept", "id: face.male_swept");

        Single(ValidationCodes.AppearanceId, "genders.male.hairstyles[1].id").Message.ShouldContain("hair.");
    }

    [Fact]
    public void Unknown_asset_key_is_reported()
    {
        Edit("model.char.hair_male_long", "model.char.hair_male_missing");

        Single(ValidationCodes.AssetMissing, "genders.male.hairstyles[2].model_asset_key");
    }

    [Fact]
    public void Face_on_another_rig_is_reported()
    {
        Edit("model_asset_key: model.char.face_male_keen", "model_asset_key: model.monster.gel_slime");

        Single(ValidationCodes.AppearanceRig, "genders.male.faces[1].model_asset_key").Message.ShouldContain("blob");
    }

    [Fact]
    public void Hairstyle_without_a_rig_must_attach_at_head()
    {
        // An equipment model has no rig and attaches at hand_r, so it cannot be hair.
        Edit("model.char.hair_female_bob", "model.equip.bone_dirk");

        Single(ValidationCodes.AppearanceRig, "genders.female.hairstyles[0].model_asset_key").Message.ShouldContain("head");
    }

    [Fact]
    public void Body_without_a_rig_is_reported()
    {
        Edit("body_model_asset_key: model.char.body_male", "body_model_asset_key: model.prop.loot_bag");

        Single(ValidationCodes.AppearanceRig, "genders.male.body_model_asset_key").Message.ShouldContain("no rig");
    }

    [Fact]
    public void Bad_colour_is_reported()
    {
        Edit("\"#F6D9C4\"", "\"F6D9C4\"");

        Single(ValidationCodes.AppearanceColor, "skin_tones[0].color");
    }

    [Fact]
    public void Empty_list_is_reported()
    {
        var path = Path.Combine(_temp, "appearance.yaml");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var start = text.IndexOf("hair_colors:", StringComparison.Ordinal);
        var end = text.IndexOf("default_hair_color_id", StringComparison.Ordinal);
        File.WriteAllText(path, text[..start] + "hair_colors: []\n" + text[end..]);

        Single(ValidationCodes.AppearanceEmpty, "hair_colors");
    }

    [Fact]
    public void Default_must_be_an_option_of_the_same_gender()
    {
        Edit("hairstyle_id: hair.female_bob", "hairstyle_id: hair.male_crop");

        Single(ValidationCodes.AppearanceDefault, "genders.female.default.hairstyle_id");
    }

    [Fact]
    public void Default_palette_entry_must_exist()
    {
        Edit("default_skin_tone_id: skin.light", "default_skin_tone_id: skin.green");

        Single(ValidationCodes.AppearanceDefault, "default_skin_tone_id");
    }

    [Fact]
    public void Rig_checks_are_skipped_when_the_key_set_has_no_metadata()
    {
        Edit("model_asset_key: model.char.face_male_keen", "model_asset_key: model.monster.gel_slime");
        var plain = new HashSet<string>(Keys(), StringComparer.Ordinal);

        Validate(plain).Issues.Where(i => i.Code == ValidationCodes.AppearanceRig).ShouldBeEmpty();
    }

    [Fact]
    public void Bundle_carries_the_options()
    {
        var load = ContentLoader.Load(_temp);
        using var doc = JsonDocument.Parse(ClientBundleExporter.Export(load.Database!).FileBytes);
        var appearance = doc.RootElement.GetProperty("appearance");

        appearance.GetProperty("genders").EnumerateArray().Select(g => g.GetProperty("gender").GetString())
            .ShouldBe(new[] { "male", "female" });
        var male = appearance.GetProperty("genders")[0];
        male.GetProperty("body").GetString().ShouldBe("model.char.body_male");
        male.GetProperty("faces").GetArrayLength().ShouldBe(3);
        male.GetProperty("hairstyles").GetArrayLength().ShouldBe(4);
        male.GetProperty("default_face").GetString().ShouldBe("face.male_calm");
        appearance.GetProperty("skin_tones").GetArrayLength().ShouldBe(6);
        appearance.GetProperty("skin_tones")[0].GetProperty("color").GetString().ShouldBe("#F6D9C4");
        appearance.GetProperty("hair_colors").GetArrayLength().ShouldBe(6);
        appearance.GetProperty("default_hair_color").GetString().ShouldBe("haircolor.chestnut");
    }

    [Fact]
    public void Schema_is_generated_and_mapped_in_vscode_settings()
    {
        var schemas = Path.Combine(RepoRoot(), "schemas", "appearance.schema.json");
        File.Exists(schemas).ShouldBeTrue();
        File.ReadAllText(schemas).ShouldContain("body_model_asset_key");

        File.ReadAllText(Path.Combine(RepoRoot(), ".vscode", "settings.json"))
            .ShouldContain("\"schemas/appearance.schema.json\": \"content/appearance.yaml\"");
    }
}
