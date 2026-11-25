using Game.Client.Core.Bundle;
using Game.Client.Core.Theme;
using Game.Client.Core.Ui;
using Game.Content.ClientExport;
using Game.Content.Themes;
using Game.Shared.ClientContent;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.World;

namespace Game.Tests.Client;

/// <summary>
/// T-706, drawing a character's looks: ids to models and colours through the bundle and the theme, the fallbacks and their one warning, what is merged into the body
/// (hair hiding, job pieces, gear), the cache keys, and the colour replacement. Pure: no engine.
/// </summary>
public class AppearanceDrawingTests
{
    private const string Manifest = """
        {
          "model.char.hero": { "file": "models/hero.glb", "rig": "humanoid", "anims": "anim.humanoid" },
          "model.body.m": { "file": "models/body_m.glb", "rig": "humanoid", "anims": "anim.humanoid" },
          "model.body.f": { "file": "models/body_f.glb", "rig": "humanoid", "anims": "anim.humanoid", "recolor": { "skin": "#ff8040", "cloth": [] } },
          "model.face.m1": { "file": "models/face_m1.glb", "rig": "humanoid" },
          "model.face.m2": { "file": "models/face_m2.glb", "attach": "head" },
          "model.face.wrong": { "file": "models/face_wrong.glb", "rig": "quadruped" },
          "model.face.f1": { "file": "models/face_f1.glb", "rig": "humanoid" },
          "model.hair.m1": { "file": "models/hair_m1.glb", "attach": "head" },
          "model.hair.m2": { "file": "models/hair_m2.glb", "attach": "head", "recolor": { "hair": ["#336699"] } },
          "model.hair.f1": { "file": "models/hair_f1.glb", "attach": "head" },
          "model.equip.cap": { "file": "models/cap.glb", "attach": "head", "hides_hair": true },
          "model.equip.crown": { "file": "models/crown.glb", "attach": "head" },
          "model.equip.vest": { "file": "models/vest.glb", "skinned": "humanoid" },
          "model.job.robe": { "file": "models/robe.glb", "skinned": "humanoid" },
          "model.job.helm": { "file": "models/helm.glb", "attach": "head", "hides_hair": true },
          "model.outfit.hood": { "file": "models/hood.glb", "attach": "head", "hides_hair": true },
          "model.outfit.wreath": { "file": "models/wreath.glb", "attach": "head" },
          "model.outfit.coat_m": { "file": "models/coat_m.glb", "skinned": "humanoid", "recolor": { "cloth": ["#112233"] } },
          "model.outfit.coat_f": { "file": "models/coat_f.glb", "skinned": "humanoid" },
          "model.missing": { "file": "models/missing.glb" },
          "fallbacks": { "missing": "model.missing", "player": "model.char.hero" }
        }
        """;

    private static readonly ClientAppearance Options = new(
        [
            new ClientGender("male", "Male", "model.body.m",
                [new("face.m1", "One", "model.face.m1"), new("face.m2", "Two", "model.face.m2"), new("face.lost", "Lost", "model.face.lost"), new("face.wrong", "Wrong", "model.face.wrong")],
                [new("hair.m1", "One", "model.hair.m1"), new("hair.m2", "Two", "model.hair.m2")], "face.m1", "hair.m1"),
            new ClientGender("female", "Female", "model.body.f", [new("face.f1", "One", "model.face.f1")], [new("hair.lost", "Lost", "model.hair.lost")], "face.f1", "hair.lost"),
        ],
        [new("skin.a", "A", "#804020"), new("skin.b", "B", "#FFE0C0")], "skin.a", [new("haircolor.a", "A", "#202020"), new("haircolor.b", "B", "#D0A040")], "haircolor.a", "job.plain");

    /// <summary>Two outfits (T-708): a hood and a coat cut per gender that takes the cloth colour, and a wreath for everyone.</summary>
    private static readonly ClientOutfit[] Outfits =
    [
        new("outfit.cloak", "Cloak", null, "icon.c", [AppearanceColorRoles.Cloth],
            [new("head", "model.outfit.hood", null), new("body", "model.outfit.coat_m", "male"), new("body", "model.outfit.coat_f", "female")]),
        new("outfit.wreath", "Wreath", null, "icon.w", [], [new("head", "model.outfit.wreath", null)]),
    ];

    private static readonly AppearanceRecord Male = new("male", "skin.a", "face.m1", "hair.m1", "haircolor.a");

    private static readonly AppearanceRecord Female = new("female", "skin.b", "face.f1", "hair.lost", "haircolor.b");

    private sealed class Rig
    {
        public readonly List<string> Warnings = new();
        public readonly EquipmentCatalog Equipment;
        public readonly AppearanceCatalog Looks;

        public Rig(ClientAppearance? options = null, string[]? without = null, int limit = 256)
        {
            ThemeManifest.TryParse("default", Manifest, out var manifest, out var error).ShouldBeTrue(error);
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in manifest!.Keys)
                if (manifest.TryGet(key, out var asset) && asset.File is not null && without?.Contains(key) != true) files.Add(ThemeCatalog.PathOf("default", asset.File));
            var catalog = new ThemeCatalog(new[] { manifest }, files.Contains, Warnings.Add);
            Equipment = new EquipmentCatalog(catalog, id => id.Replace("item.", "model.equip."), id => id is "item.cap" or "item.crown" ? ["head_top"] : ["body"]);
            Looks = new AppearanceCatalog(catalog, Equipment, Bundle(options ?? Options), limit);
        }

        public WornItem Worn(string item) => new(Equipment.Resolve(item, "humanoid")!, Equipment.SlotsOf(item));

        public List<DressPart> Dress(CharacterLook? look, params string[] items)
        {
            var parts = new List<DressPart>();
            DressPlan.Fill(look, items.Select(Worn).ToList(), parts);
            return parts;
        }

        /// <summary>T-708: dressed in gear and in outfits by slot.</summary>
        public List<DressPart> Dress(CharacterLook? look, ShownOutfit[] outfit, params string[] items)
        {
            var pieces = new List<OutfitPart>();
            Looks.ResolveOutfit(outfit, look?.Ids.Gender, "humanoid", pieces);
            var parts = new List<DressPart>();
            DressPlan.Fill(look, items.Select(Worn).ToList(), pieces, parts);
            return parts;
        }
    }

    private static DisplayBundle Bundle(ClientAppearance options)
    {
        var jobs = new[]
        {
            new ClientJob("job.plain", "Plain", null, null, [], "model.char.hero", null, []),
            new ClientJob("job.blue", "Blue", null, null, [], "model.char.hero", null, [], "#2040C0"),
            new ClientJob("job.robed", "Robed", null, null, [], "model.char.hero", null, [], null,
                [new ClientJobPiece("model.job.robe", null, "body"), new ClientJobPiece("model.job.helm", "male", "head_top")]),
        };
        var bundle = new ClientBundle(ClientBundle.CurrentFormatVersion, [], [], [], [], jobs, [], [], [], [], [], options, Outfits: Outfits);
        DisplayBundle.TryRead(ClientBundleWriter.Export(bundle).FileBytes, out var read, out _).ShouldBe(BundleStatus.Ok);
        return read!;
    }

    // ---- resolution ----

    [Fact]
    public void Ids_become_the_genders_body_the_face_the_hairstyle_and_the_colours()
    {
        var rig = new Rig();

        var look = rig.Looks.Resolve(Male, "job.blue")!;

        look.Ids.ShouldBe(Male);
        look.Body.Key.ShouldBe("model.body.m");
        (look.Face!.VisualKey, look.Face.Mode).ShouldBe(("model.face.m1", WornMode.Skinned));
        (look.Hair!.VisualKey, look.Hair.Mode, look.Hair.Bone).ShouldBe(("model.hair.m1", WornMode.Attached, "head"));
        (look.Skin!.Value.ToString(), look.HairColor!.Value.ToString(), look.Cloth!.Value.ToString()).ShouldBe(("#804020", "#202020", "#2040c0"));
        rig.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Characters_with_the_same_ids_and_job_share_one_look()
    {
        var rig = new Rig();
        var a = rig.Looks.Resolve(Male, "job.blue");
        rig.Looks.Resolve(Male with { }, "job.blue").ShouldBeSameAs(a);
        rig.Looks.Resolve(Male, "job.plain").ShouldNotBeSameAs(a);
        rig.Looks.Count.ShouldBe(2);
    }

    [Fact]
    public void Remembered_looks_are_bounded()
    {
        var rig = new Rig(limit: 2);
        foreach (var job in new[] { "job.a", "job.b", "job.c", "job.d", "job.e" }) rig.Looks.Resolve(Male, job).ShouldNotBeNull();
        rig.Looks.Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Without_ids_or_without_options_the_job_model_is_drawn()
    {
        new Rig().Looks.Resolve(null, "job.blue").ShouldBeNull();
        new Rig(Options with { Genders = [] }).Looks.Resolve(Male, "job.blue").ShouldBeNull();
    }

    // ---- fallbacks ----

    [Theory]
    [InlineData("male", "skin.gone", "face.m2", "hair.m2", "haircolor.b", "skin.gone")]
    [InlineData("male", "skin.b", "face.f1", "hair.m2", "haircolor.b", "face.f1")]
    [InlineData("male", "skin.b", "face.m2", "hair.gone", "haircolor.b", "hair.gone")]
    [InlineData("male", "skin.b", "face.m2", "hair.m2", "haircolor.gone", "haircolor.gone")]
    public void An_id_the_bundle_does_not_have_is_drawn_as_the_default_with_one_warning(string gender, string skin, string face, string hair, string color, string gone)
    {
        var rig = new Rig();

        var look = rig.Looks.Resolve(new AppearanceRecord(gender, skin, face, hair, color), "job.plain")!;
        rig.Looks.Resolve(new AppearanceRecord(gender, skin, face, hair, color), "job.blue");

        look.Ids.ShouldBe(new AppearanceRecord(gender, skin == gone ? "skin.a" : skin, face == gone ? "face.m1" : face, hair == gone ? "hair.m1" : hair, color == gone ? "haircolor.a" : color));
        rig.Warnings.Count.ShouldBe(1);
        rig.Warnings[0].ShouldContain("'" + gone + "'");
    }

    [Fact]
    public void An_unknown_gender_is_drawn_as_the_first_one_with_its_defaults()
    {
        var rig = new Rig();
        rig.Looks.Resolve(new AppearanceRecord("other", "skin.b", "face.x", "hair.x", "haircolor.b"), null)!.Ids.ShouldBe(new AppearanceRecord("male", "skin.b", "face.m1", "hair.m1", "haircolor.b"));
    }

    [Theory]
    [InlineData("face.lost", "no theme lists it")]
    [InlineData("face.wrong", "rig 'quadruped'")]
    public void A_face_no_theme_has_or_on_another_rig_is_drawn_as_the_default_face_with_one_warning(string face, string why)
    {
        var rig = new Rig();

        var look = rig.Looks.Resolve(Male with { Face = face }, "job.plain")!;
        rig.Looks.Resolve(Male with { Face = face }, "job.blue");

        look.Face!.VisualKey.ShouldBe("model.face.m1");
        rig.Warnings.Count.ShouldBe(1);
        rig.Warnings[0].ShouldContain(why);
        rig.Warnings[0].ShouldContain(face);
    }

    [Fact]
    public void A_part_whose_default_is_missing_too_is_the_missing_model_with_one_warning()
    {
        var rig = new Rig();

        var look = rig.Looks.Resolve(Female, "job.plain")!;
        rig.Looks.Resolve(Female, "job.blue");

        look.Hair!.Fallback.ShouldBeTrue();
        look.Hair.Model.Key.ShouldBe("model.missing");
        rig.Warnings.Count.ShouldBe(1);
        rig.Warnings[0].ShouldContain("model.hair.lost");
    }

    [Fact]
    public void A_body_no_theme_has_means_the_job_model_with_one_warning()
    {
        var rig = new Rig(without: ["model.body.m"]);
        rig.Looks.Resolve(Male, "job.plain").ShouldBeNull();
        rig.Looks.Resolve(Male, "job.blue").ShouldBeNull();
        rig.Looks.Resolve(Female, "job.plain").ShouldNotBeNull("the other body is there");
        rig.Warnings.Count(w => w.Contains("model.body.m")).ShouldBe(1);
    }

    // ---- jobs ----

    [Fact]
    public void A_job_adds_its_pieces_for_the_gender_and_its_cloth_colour_and_an_unknown_job_adds_nothing()
    {
        var rig = new Rig();
        rig.Looks.Resolve(Male, "job.robed")!.JobParts.Select(p => p.Piece.VisualKey).ShouldBe(["model.job.robe", "model.job.helm"]);
        rig.Looks.Resolve(Female, "job.robed")!.JobParts.Select(p => p.Piece.VisualKey).ShouldBe(["model.job.robe"]);
        var none = rig.Looks.Resolve(Male, "job.unknown")!;
        none.JobParts.ShouldBeEmpty();
        none.Cloth.ShouldBeNull();
    }

    [Fact]
    public void Equipment_in_a_job_pieces_slot_is_drawn_instead_of_it()
    {
        var rig = new Rig();
        var look = rig.Looks.Resolve(Male, "job.robed");
        rig.Dress(look).Select(p => p.Piece.VisualKey).ShouldBe(["model.face.m1", "model.job.robe", "model.job.helm"], "the helm hides the hair");
        rig.Dress(look, "item.vest").Select(p => p.Piece.VisualKey).ShouldBe(["model.face.m1", "model.job.helm", "model.equip.vest"]);
        rig.Dress(look, "item.crown").Select(p => p.Piece.VisualKey).ShouldBe(["model.face.m1", "model.hair.m1", "model.job.robe", "model.equip.crown"], "the crown stands for the helm, so the hair shows");
    }

    // ---- hair hiding ----

    [Fact]
    public void Headgear_that_hides_hair_takes_the_hairstyle_off_and_other_headgear_does_not()
    {
        var rig = new Rig();
        var look = rig.Looks.Resolve(Male, "job.plain");
        rig.Dress(look).Select(p => p.Role).ShouldBe([PartRole.Face, PartRole.Hair]);
        rig.Dress(look, "item.crown").Select(p => p.Role).ShouldBe([PartRole.Face, PartRole.Hair, PartRole.Gear]);
        rig.Dress(look, "item.cap").Select(p => p.Role).ShouldBe([PartRole.Face, PartRole.Gear]);
        rig.Dress(look, "item.vest", "item.cap").ShouldNotContain(p => p.Role == PartRole.Hair);
    }

    [Fact]
    public void A_piece_standing_in_for_a_missing_key_hides_nothing()
    {
        var rig = new Rig(without: ["model.equip.cap"]);
        rig.Dress(rig.Looks.Resolve(Male, "job.plain"), "item.cap").ShouldContain(p => p.Role == PartRole.Hair);
    }

    [Fact]
    public void Without_looks_only_the_gear_is_merged()
    {
        var rig = new Rig();
        rig.Dress(null, "item.vest", "item.cap").Select(p => p.Role).ShouldBe([PartRole.Gear, PartRole.Gear]);
    }

    // ---- cache keys ----

    [Fact]
    public void Equal_looks_share_a_merged_mesh_and_every_visible_difference_has_its_own()
    {
        var rig = new Rig();
        string Key(AppearanceRecord ids, string job, params string[] items)
        {
            var look = rig.Looks.Resolve(ids, job)!;
            return WornSet.Key(look.Body.PoolKey, look, rig.Dress(look, items));
        }

        var plain = Key(Male, "job.plain");
        Key(Male with { }, "job.plain").ShouldBe(plain);
        Key(Male, "job.unknown").ShouldBe(plain, "a job that adds nothing looks like another that adds nothing");
        Key(Male, "job.plain", "item.vest", "item.crown").ShouldBe(Key(Male, "job.plain", "item.crown", "item.vest"), "the order the server lists items in");
        var different = new[]
        {
            plain, Key(Male with { Skin = "skin.b" }, "job.plain"), Key(Male with { HairColor = "haircolor.b" }, "job.plain"), Key(Male with { Hair = "hair.m2" }, "job.plain"),
            Key(Male with { Face = "face.m2" }, "job.plain"), Key(Female, "job.plain"), Key(Male, "job.blue"), Key(Male, "job.robed"), Key(Male, "job.plain", "item.vest"), Key(Male, "job.plain", "item.cap"),
        };
        different.Distinct().Count().ShouldBe(different.Length);
        Key(Male, "job.plain", "item.cap").ShouldBe(Key(Male with { Hair = "hair.m2" }, "job.plain", "item.cap"), "hidden hair makes no difference");
    }

    [Fact]
    public void A_hairstyles_file_worn_as_gear_is_another_mesh()
    {
        var rig = new Rig();
        var look = rig.Looks.Resolve(Male, "job.plain")!;
        WornSet.Key("b", look, [new DressPart(look.Hair!, PartRole.Hair)]).ShouldNotBe(WornSet.Key("b", look, [new DressPart(look.Hair!, PartRole.Gear)]));
    }

    // ---- colours ----

    private static LinearColor Linear(string hex)
    {
        ThemeColor.TryParse(hex, out var color).ShouldBeTrue();
        return LinearColor.From(color);
    }

    private static void ShouldBeNear(LinearColor actual, LinearColor expected, string what)
    {
        actual.R.ShouldBe(expected.R, 0.004, what);
        actual.G.ShouldBe(expected.G, 0.004, what);
        actual.B.ShouldBe(expected.B, 0.004, what);
    }

    [Fact]
    public void Painted_skin_hair_and_cloth_take_the_characters_colours_and_everything_else_stays()
    {
        var look = new Rig().Looks.Resolve(Male, "job.blue")!;
        var body = look.BodyColors;
        ShouldBeNear(body.Apply(Linear(AppearancePaint.Skin[0]), default), Linear("#804020"), "skin");
        ShouldBeNear(body.Apply(Linear(AppearancePaint.Cloth[0]), default), Linear("#2040c0"), "cloth");
        foreach (var hair in AppearancePaint.Hair) ShouldBeNear(body.Apply(Linear(hair), default), Linear("#202020"), "hair painted " + hair);
        ShouldBeNear(body.Apply(Linear("#2a2226"), default), Linear("#2a2226"), "ink stays");
        ShouldBeNear(body.Apply(Linear("#8a5a33"), default), Linear("#8a5a33"), "leather stays");
        ShouldBeNear(look.FaceColors.Apply(Linear(AppearancePaint.Cloth[0]), default), Linear(AppearancePaint.Cloth[0]), "a face has no cloth");
        look.ColorsOf(new DressPart(look.Face!, PartRole.Gear)).IsNone.ShouldBeTrue("gear keeps its own colours");
        look.ColorsOf(new DressPart(look.Face!, PartRole.Job)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public void A_job_without_a_cloth_colour_leaves_the_cloth_as_painted()
    {
        var look = new Rig().Looks.Resolve(Male, "job.plain")!;
        ShouldBeNear(look.BodyColors.Apply(Linear(AppearancePaint.Cloth[0]), default), Linear(AppearancePaint.Cloth[0]), "cloth");
    }

    [Fact]
    public void A_shade_keeps_its_relation_to_its_base()
    {
        var look = new Rig().Looks.Resolve(Male with { Skin = "skin.a" }, "job.plain")!;
        var (skin, cheek, tone) = (Linear(AppearancePaint.Skin[0]), Linear(AppearancePaint.Skin[1]), Linear("#804020"));
        ShouldBeNear(look.BodyColors.Apply(cheek, default), new LinearColor(tone.R * cheek.R / skin.R, tone.G * cheek.G / skin.G, tone.B * cheek.B / skin.B), "cheek");
    }

    [Fact]
    public void A_vertex_colour_as_the_engine_keeps_it_still_matches_and_a_close_colour_does_not()
    {
        var look = new Rig().Looks.Resolve(Male, "job.plain")!;
        var paint = Linear(AppearancePaint.Skin[0]);
        LinearColor Quantised(LinearColor c, int step) => new((MathF.Round(c.R * 255) + step) / 255f, MathF.Round(c.G * 255) / 255f, MathF.Round(c.B * 255) / 255f);
        ShouldBeNear(look.BodyColors.Apply(Quantised(paint, 0), default), Linear("#804020"), "8 bits a channel");
        var close = Quantised(paint, 4);
        ShouldBeNear(look.BodyColors.Apply(close, default), close, "four steps away is another colour");
    }

    [Fact]
    public void A_hairstyle_takes_the_hair_colour_whatever_it_is_painted_with_and_keeps_its_strands()
    {
        var colors = new Rig().Looks.Resolve(Male with { HairColor = "haircolor.b" }, "job.plain")!.HairColors;
        colors.NeedsDominant.ShouldBeTrue();
        var (paint, strand, color) = (Linear("#906040"), Linear("#705030"), Linear("#d0a040"));
        var dominant = Recolor.Dominant([paint, paint, strand, paint]);
        ShouldBeNear(dominant, paint, "the most common colour");
        ShouldBeNear(colors.Apply(paint, dominant), color, "base");
        ShouldBeNear(colors.Apply(strand, dominant), new LinearColor(color.R * strand.R / dominant.R, color.G * strand.G / dominant.G, color.B * strand.B / dominant.B), "a darker strand stays darker");
        ShouldBeNear(colors.Apply(new LinearColor(1, 1, 1), Recolor.Dominant([])), color, "an unpainted mesh is white");
        var lighter = colors.Apply(new LinearColor(1, 1, 1), dominant);
        (lighter.R <= 1 && lighter.G <= 1 && lighter.B <= 1).ShouldBeTrue("never above full");
    }

    [Fact]
    public void The_manifest_names_a_models_own_painted_colours_and_an_empty_list_turns_a_role_off()
    {
        var rig = new Rig();
        var female = rig.Looks.Resolve(Female, "job.blue")!;
        ShouldBeNear(female.BodyColors.Apply(Linear("#ff8040"), default), Linear("#ffe0c0"), "its own skin paint");
        ShouldBeNear(female.BodyColors.Apply(Linear(AppearancePaint.Skin[0]), default), Linear(AppearancePaint.Skin[0]), "no longer the contract's");
        ShouldBeNear(female.BodyColors.Apply(Linear(AppearancePaint.Cloth[0]), default), Linear(AppearancePaint.Cloth[0]), "cloth is off");
        var hair = rig.Looks.Resolve(Male with { Hair = "hair.m2", HairColor = "haircolor.b" }, "job.plain")!.HairColors;
        hair.NeedsDominant.ShouldBeFalse("a hairstyle with its own paint is not coloured whole");
        ShouldBeNear(hair.Apply(Linear("#336699"), default), Linear("#d0a040"), "the strands");
        ShouldBeNear(hair.Apply(Linear("#ff0000"), default), Linear("#ff0000"), "a ribbon keeps its colour");
    }

    // ---- the turning preview ----

    [Fact]
    public void The_preview_turns_by_itself_and_by_drag_and_a_held_one_waits()
    {
        var table = new Turntable(30, 360);
        table.Advance(1);
        table.Degrees.ShouldBe(30, 1e-9);
        table.Drag(50, 100);
        table.Degrees.ShouldBe(30, 1e-9, "a move without a finger down does nothing");
        table.Press();
        table.Advance(5);
        table.Degrees.ShouldBe(30, 1e-9, "held");
        table.Drag(-25, 100);
        table.Degrees.ShouldBe(300, 1e-9, "a quarter of the width back");
        table.Drag(10, 0);
        table.Degrees.ShouldBe(300, 1e-9, "no width yet");
        table.Release();
        table.Advance(3);
        table.Degrees.ShouldBe(30, 1e-9, "round again");
        table.Reset();
        (table.Degrees, table.Held).ShouldBe((0, false));
    }

    // ---- the real pack ----

    private static (DisplayBundle Bundle, EquipmentCatalog Equipment, AppearanceCatalog Looks, List<string> Warnings) RealPack()
    {
        var root = TestGrids.RepoRoot();
        ThemeManifest.TryParse("default", File.ReadAllText(Path.Combine(root, "themes", "default", "asset-manifest.json")), out var manifest, out var error).ShouldBeTrue(error);
        var warnings = new List<string>();
        var catalog = new ThemeCatalog(new[] { manifest! }, path => File.Exists(Path.Combine(root, path)), warnings.Add);
        DisplayBundle.TryRead(ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        var equipment = new EquipmentCatalog(catalog, id => bundle!.Item(id)?.Visual, id => bundle!.Item(id)?.Slots);
        return (bundle!, equipment, new AppearanceCatalog(catalog, equipment, bundle!), warnings);
    }

    [Fact]
    public void The_bundle_carries_every_option_and_the_look_of_every_job_the_content_has()
    {
        var (bundle, _, _, _) = RealPack();
        var content = TestGrids.SampleContent;
        var a = bundle.Appearance.ShouldNotBeNull();
        a.Genders.Select(g => g.Gender).ShouldBe(Genders.All.Ordered);
        foreach (var gender in a.Genders)
        {
            var def = content.Appearance.Genders[gender.Gender];
            gender.Faces.Select(f => (f.Id, f.Name, f.ModelAssetKey)).ShouldBe(def.Faces.Select(f => (f.Id, f.Name, f.ModelAssetKey)));
            gender.Hairstyles.Select(h => (h.Id, h.Name, h.ModelAssetKey)).ShouldBe(def.Hairstyles.Select(h => (h.Id, h.Name, h.ModelAssetKey)));
            (gender.DefaultFaceId, gender.DefaultHairstyleId).ShouldBe((def.Default.FaceId, def.Default.HairstyleId));
        }
        a.SkinTones.Select(c => (c.Id, c.Color)).ShouldBe(content.Appearance.SkinTones.Select(c => (c.Id, c.Color)));
        a.HairColors.Select(c => (c.Id, c.Color)).ShouldBe(content.Appearance.HairColors.Select(c => (c.Id, c.Color)));
        (a.DefaultSkinToneId, a.DefaultHairColorId, a.StartJobId).ShouldBe((content.Appearance.DefaultSkinToneId, content.Appearance.DefaultHairColorId, content.Balance.NewCharacter.StartJobId));
        foreach (var job in content.Jobs.All) bundle.JobLookOf(job.Id).ClothColor.ShouldBe(job.Look.ClothColor, job.Id);
        bundle.JobLookOf("job.none").ShouldBe(JobLook.None);
    }

    [Fact]
    public void Every_option_of_the_content_is_drawn_by_the_default_theme_without_a_warning_and_every_job_looks_different()
    {
        var (bundle, _, looks, warnings) = RealPack();
        var a = bundle.Appearance!;
        foreach (var g in a.Genders)
            foreach (var face in g.Faces)
                foreach (var hair in g.Hairstyles)
                {
                    var look = looks.Resolve(new AppearanceRecord(g.Gender, a.SkinTones[0].Id, face.Id, hair.Id, a.HairColors[0].Id), a.StartJobId)!;
                    look.Face!.Fallback.ShouldBeFalse(face.Id);
                    look.Hair!.Fallback.ShouldBeFalse(hair.Id);
                }
        warnings.ShouldBeEmpty();
        var one = a.Genders[0];
        var keys = bundle.JobIds.Select(job => looks.Resolve(new AppearanceRecord(one.Gender, a.SkinTones[0].Id, one.Faces[0].Id, one.Hairstyles[0].Id, a.HairColors[0].Id), job)!.ColorKey).ToList();
        keys.Distinct().Count().ShouldBe(keys.Count, "a job is told apart by its cloth");
    }

    [Fact]
    public void The_placeholder_theme_has_one_piece_that_hides_hair_and_its_bodies_are_painted_by_the_contract()
    {
        var root = TestGrids.RepoRoot();
        var path = Path.Combine(root, "themes", "default", "asset-manifest.json");
        ThemeManifest.TryParse("default", File.ReadAllText(path), out var manifest, out _).ShouldBeTrue();
        manifest!.Keys.Count(k => k.StartsWith("model.equip.", StringComparison.Ordinal) && manifest.TryGet(k, out var asset) && asset.HidesHair).ShouldBe(1);
        manifest.Keys.Where(k => manifest.TryGet(k, out var asset) && asset.HidesHair && !k.StartsWith("model.equip.", StringComparison.Ordinal)).ShouldBe(["model.outfit.rain_hood"]);   // T-708
        foreach (var key in new[] { "model.char.body_male", "model.char.body_female" })
        {
            manifest.TryGet(key, out var asset).ShouldBeTrue();
            var file = Path.Combine(root, "themes", "default", asset.File!);
            PackFiles.ReadAllBytes(file);
            var facts = ModelFacts.Of(GltfFile.Load(file));
            foreach (var mesh in new[] { facts.Body0!, facts.Body1! })
            {
                mesh.IsPainted([AppearancePaint.Skin[0]]).ShouldBeTrue(key + " skin");
                mesh.IsPainted([AppearancePaint.Cloth[0]]).ShouldBeTrue(key + " cloth");
                mesh.IsPainted(AppearancePaint.Hair).ShouldBeTrue(key + " hair");
                mesh.IsPainted(["#00ff00"]).ShouldBeFalse();
            }
        }
    }

    // ---- outfits (T-708) ----

    private static ShownOutfit[] Wearing(params (string Slot, string Outfit)[] shown) => shown.Select(s => new ShownOutfit(s.Slot, s.Outfit)).ToArray();

    [Fact]
    public void An_outfit_takes_the_place_of_the_gear_and_the_job_pieces_of_the_slots_it_covers_in_the_drawing_only()
    {
        var rig = new Rig();
        var look = rig.Looks.Resolve(Male, "job.robed")!;
        static string[] Keys(IEnumerable<DressPart> parts) => parts.Select(p => p.Role + ":" + p.Piece.VisualKey).ToArray();

        Keys(rig.Dress(look, "item.vest", "item.crown")).ShouldBe(["Face:model.face.m1", "Hair:model.hair.m1", "Gear:model.equip.vest", "Gear:model.equip.crown"]);
        Keys(rig.Dress(look, Wearing(("body", "outfit.cloak")), "item.vest", "item.crown"))
            .ShouldBe(["Face:model.face.m1", "Hair:model.hair.m1", "Gear:model.equip.crown", "Outfit:model.outfit.coat_m"], "the coat covers the vest; the crown on the head stays");
        Keys(rig.Dress(look, Wearing(("head", "outfit.wreath")), "item.cap"))
            .ShouldBe(["Face:model.face.m1", "Hair:model.hair.m1", "Job:model.job.robe", "Outfit:model.outfit.wreath"], "a wreath over the cap: the cap and its hiding of the hair are gone, and the helm of the job too");
        Keys(rig.Dress(look, Wearing(("head", "outfit.cloak"))))
            .ShouldBe(["Face:model.face.m1", "Job:model.job.robe", "Outfit:model.outfit.hood"], "the hood hides the hair, and takes the job's helm's place");
        Keys(rig.Dress(rig.Looks.Resolve(Female, "job.plain"), Wearing(("body", "outfit.cloak"))))
            .ShouldBe(["Face:model.face.f1", "Hair:model.hair.lost", "Outfit:model.outfit.coat_f"], "each gender its own cut");
        Keys(rig.Dress(look, Wearing(("body", "outfit.unknown"), ("feet", "outfit.wreath")), "item.vest"))
            .ShouldBe(["Face:model.face.m1", "Job:model.job.helm", "Gear:model.equip.vest"], "what the bundle cannot draw is left out, and the gear and the job show");
        rig.Warnings.Count(w => w.Contains("drawing the gear", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public void An_outfits_piece_takes_the_colours_it_names_and_is_its_own_mesh()
    {
        var rig = new Rig();
        var look = rig.Looks.Resolve(Male, "job.blue")!;
        var coat = rig.Dress(look, Wearing(("body", "outfit.cloak"))).Single(p => p.Role == PartRole.Outfit);
        coat.TakesColors.ShouldBe([AppearanceColorRoles.Cloth]);
        ShouldBeNear(look.ColorsOf(coat).Apply(Linear("#112233"), default), Linear("#2040c0"), "the coat's own cloth paint takes the job's colour");
        ShouldBeNear(look.ColorsOf(coat).Apply(Linear(AppearancePaint.Cloth[0]), default), Linear(AppearancePaint.Cloth[0]), "its recolor names its paint, the contract's is left");
        var wreath = rig.Dress(look, Wearing(("head", "outfit.wreath"))).Single(p => p.Role == PartRole.Outfit);
        look.ColorsOf(wreath).IsNone.ShouldBeTrue("an outfit that takes no colours keeps its own");

        var plain = WornSet.Key(look.Body.PoolKey, look, rig.Dress(look));
        var dressed = WornSet.Key(look.Body.PoolKey, look, rig.Dress(look, Wearing(("body", "outfit.cloak"))));
        dressed.ShouldNotBe(plain);
        WornSet.Key(look.Body.PoolKey, look, rig.Dress(look, Wearing(("body", "outfit.cloak")))).ShouldBe(dressed, "the same outfit shares the merged mesh");
        WornSet.Key("b", look, [coat]).ShouldNotBe(WornSet.Key("b", look, [new DressPart(coat.Piece, PartRole.Gear)]), "the same file worn as gear keeps its colours: another mesh");
    }

    [Fact]
    public void The_wardrobe_book_holds_the_servers_word_and_its_rows_fit_the_gender()
    {
        var book = new Game.Client.Core.Outfits.WardrobeBook(Bundle(Options));
        var gained = new List<string>();
        book.Apply(new WardrobeMessage(["outfit.cloak"], [new ShownOutfit("body", "outfit.cloak")], false), gained);
        gained.ShouldBeEmpty("the first wardrobe of a login is no news");
        book.Apply(new WardrobeMessage(["outfit.cloak", "outfit.wreath"], [new ShownOutfit("body", "outfit.cloak")], true), gained);
        gained.ShouldBe(["outfit.wreath"]);
        (book.Hidden, book.Chosen("body"), book.Chosen("head")).ShouldBe((true, "outfit.cloak", (string?)null));
        book.Rows("male").Select(r => (r.Slot, string.Join(",", r.Outfits.Select(o => o.Id)), r.Chosen))
            .ShouldBe([("head", "outfit.cloak,outfit.wreath", (string?)null), ("body", "outfit.cloak", "outfit.cloak")]);
        book.Rows(null).Select(r => r.Slot).ShouldBe(["head"], "no gender, no coat");
        var version = book.Version;
        book.Reset();
        (book.Known, book.Owned.Count, book.Version > version).ShouldBe((false, 0, true));
    }
}
