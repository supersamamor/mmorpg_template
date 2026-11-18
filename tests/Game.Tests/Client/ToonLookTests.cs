using System.Numerics;
using Game.Client.Core.Theme;
using Game.Client.Core.Ui;
using Game.Shared.Protocol;
using Game.Tests.World;
using Game.ThemeGen;

namespace Game.Tests.Client;

/// <summary>
/// T-222, the storybook toon look (ADR-003): which entities get an outline, the fallback when a theme has no toon resources, the default theme's render keys, the
/// placeholders' proportions and colours, the bounded retry of a failed piece load, and the skill bar's text.
/// </summary>
public class ToonLookTests
{
    private static readonly string Themes = Path.Combine(TestGrids.RepoRoot(), "themes");

    // ---- outlines: by distance and kind, nearest first up to the count ----

    [Fact]
    public void Characters_monsters_npcs_and_ground_items_within_the_distance_get_an_outline()
    {
        var planner = new OutlinePlanner(10, 20);
        planner.Begin();
        var player = planner.Add(1, EntityKinds.Player, false, 0);
        var monster = planner.Add(2, EntityKinds.Monster, false, 9.9 * 9.9);
        var npc = planner.Add(3, EntityKinds.Npc, false, 25);
        var item = planner.Add(4, EntityKinds.Item, false, 4);
        var far = planner.Add(5, EntityKinds.Monster, false, 10.1 * 10.1);
        var dead = planner.Add(6, EntityKinds.Monster, true, 1);
        var other = planner.Add(7, "prop", false, 1);
        planner.Plan();
        planner.Outlined(player).ShouldBeTrue();
        planner.Outlined(monster).ShouldBeTrue();
        planner.Outlined(npc).ShouldBeTrue();
        planner.Outlined(item).ShouldBeTrue();
        planner.Outlined(far).ShouldBeFalse("beyond the distance");
        planner.Outlined(dead).ShouldBeFalse("a body is not outlined");
        planner.Outlined(other).ShouldBeFalse("only characters, monsters, NPCs and ground items");
        planner.OutlinedCount.ShouldBe(4);
    }

    [Fact]
    public void A_crowd_gets_outlines_on_its_nearest_few_only_and_ties_go_to_the_lower_id()
    {
        var planner = new OutlinePlanner(10, 3);
        planner.Begin();
        var slots = new List<int>();
        for (var i = 0; i < 10; i++) slots.Add(planner.Add(100 - i, EntityKinds.Player, false, i < 4 ? 4 : 9));
        planner.Plan();
        planner.OutlinedCount.ShouldBe(3);
        // Four at the same distance: the lower ids (97, 98, 99) win over 100.
        planner.Outlined(slots[0]).ShouldBeFalse();
        planner.Outlined(slots[1]).ShouldBeTrue();
        planner.Outlined(slots[2]).ShouldBeTrue();
        planner.Outlined(slots[3]).ShouldBeTrue();
        for (var i = 4; i < 10; i++) planner.Outlined(slots[i]).ShouldBeFalse();
    }

    [Fact]
    public void A_distance_or_count_of_zero_turns_outlines_off_and_planning_again_starts_clean()
    {
        foreach (var planner in new[] { new OutlinePlanner(0, 10), new OutlinePlanner(10, 0) })
        {
            planner.Begin();
            var slot = planner.Add(1, EntityKinds.Player, false, 0);
            planner.Plan();
            planner.Outlined(slot).ShouldBeFalse();
        }
        var again = new OutlinePlanner(5, 1);
        again.Begin();
        again.Add(1, EntityKinds.Player, false, 1);
        again.Plan();
        again.Begin();
        var moved = again.Add(1, EntityKinds.Player, false, 100);
        again.Plan();
        again.Outlined(moved).ShouldBeFalse("it walked out of range");
    }

    [Fact]
    public void Planning_a_large_crowd_grows_its_arrays()
    {
        var planner = new OutlinePlanner(100, 200);
        planner.Begin();
        for (var i = 0; i < 150; i++) planner.Add(i, EntityKinds.Monster, false, i);
        planner.Plan();
        planner.OutlinedCount.ShouldBe(150);
    }

    // ---- the render resources: from the theme, or the old look with one warning ----

    private static ThemeCatalog Catalog(List<string> warnings, Func<string, bool> exists, params (string Theme, string Json)[] layers) =>
        new(layers.Select(l => { ThemeManifest.TryParse(l.Theme, l.Json, out var m, out var error).ShouldBeTrue(error); return m!; }).ToList(), exists, warnings.Add);

    [Fact]
    public void A_theme_without_toon_resources_falls_back_to_the_old_look_with_one_warning()
    {
        var warnings = new List<string>();
        var catalog = Catalog(warnings, _ => true, ("bare", """{ "model.x": { "file": "x.glb" } }"""));
        var plan = RenderPlan.Resolve(catalog);
        plan.ToonShading.ShouldBeFalse();
        plan.Toon.ShouldBeNull();
        plan.Outline.ShouldBeNull();
        plan.Environment.ShouldBeNull();
        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("toon, outline, environment");
        RenderPlan.Resolve(catalog);
        warnings.Count.ShouldBe(1, "one warning however often it is asked");
    }

    [Fact]
    public void Render_roles_resolve_through_the_chain_and_a_missing_file_is_named()
    {
        var warnings = new List<string>();
        var catalog = Catalog(warnings, path => !path.EndsWith("gone.tres", StringComparison.Ordinal),
            ("mine", """{ "mat.mine": { "file": "gone.tres", "render": "toon" } }"""),
            ("default", """{ "mat.toon": { "file": "toon.tres", "render": "toon" }, "mat.outline": { "file": "o.tres", "render": "outline" }, "scene.environment": { "file": "e.tscn", "render": "environment" } }"""));
        var plan = RenderPlan.Resolve(catalog);
        plan.Toon.ShouldBe("themes/default/toon.tres", "the active theme's file is missing, so the default theme's");
        plan.Outline.ShouldBe("themes/default/o.tres");
        plan.Environment.ShouldBe("themes/default/e.tscn");
        plan.Warning.ShouldBeNull();
        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("mat.mine");
    }

    [Fact]
    public void The_look_switched_off_uses_nothing_from_the_theme()
    {
        RenderPlan.Off.ToonShading.ShouldBeFalse();
        RenderPlan.Off.Outline.ShouldBeNull();
        RenderPlan.Off.Warning.ShouldBeNull();
    }

    [Fact]
    public void The_default_theme_names_every_render_role_with_a_real_file_and_the_test_theme_gets_them_from_it()
    {
        var warnings = new List<string>();
        string Read(string theme) => File.ReadAllText(Path.Combine(Themes, theme, "asset-manifest.json"));
        bool Exists(string path) => File.Exists(Path.Combine(Themes, path["themes/".Length..].Replace('/', Path.DirectorySeparatorChar)));
        foreach (var layers in new[] { new[] { ("default", Read("default")) }, new[] { ("test", Read("test")), ("default", Read("default")) } })
        {
            var plan = RenderPlan.Resolve(Catalog(warnings, Exists, layers));
            plan.Toon.ShouldBe("themes/default/render/toon.tres");
            plan.Outline.ShouldBe("themes/default/render/outline.tres");
            plan.Environment.ShouldBe("themes/default/render/environment.tscn");
        }
        warnings.ShouldBeEmpty();
        RenderRoles.All.ShouldBe(new[] { "toon", "outline", "environment" });
    }

    [Fact]
    public void The_theme_render_files_reference_each_other_by_relative_path_and_the_outline_is_a_warm_near_black()
    {
        var render = Path.Combine(Themes, "default", "render");
        File.ReadAllText(Path.Combine(render, "toon.tres")).ShouldContain("path=\"toon.gdshader\"");
        File.ReadAllText(Path.Combine(render, "toon.gdshader")).ShouldContain("#include \"toon_light.gdshaderinc\"");
        File.ReadAllText(Path.Combine(render, "outline.gdshader")).ShouldContain("cull_front");
        var outline = File.ReadAllText(Path.Combine(render, "outline.tres"));
        outline.ShouldContain("outline_color = Color(0.165, 0.133, 0.125, 1)", customMessage: "warm (red above blue), not pure black (ADR-003 point 2)");
        foreach (var map in Directory.GetFiles(Path.Combine(Themes, "default", "maps"), "*.tscn").Where(f => !f.Contains(".authoring.", StringComparison.Ordinal)))
            File.ReadAllText(map).ShouldContain("render/toon_light.gdshaderinc", customMessage: map + ": terrain uses the same ramp");
    }

    // ---- placeholders: proportions and colours (themegen) ----

    private static readonly ThemeStyle Plain = new(string.Empty, 0f);
    private static Palette ThemePalette() => Palette.Load(Path.Combine(Themes, "default", Palette.FileName));

    private static MeshBuilder Body(string rig, string key, int lod = 0)
    {
        var m = new MeshBuilder();
        Rig.Get(rig).Body(m, Pack.LookOf(key, KeyColor.Hash(key), lod, Vector4.One, ThemePalette(), Plain));
        return m;
    }

    [Fact]
    public void The_humanoid_head_is_about_a_third_of_the_height_and_its_eyes_are_tipped_up_toward_the_camera()
    {
        var m = Body("humanoid", "model.char.wanderer");
        var head = Rig.Get("humanoid").Index("head");
        var all = m.Mesh.Positions;
        var onHead = all.Where((_, i) => m.Mesh.Joints[i] == head).ToList();
        var height = all.Max(p => p.Y) - all.Min(p => p.Y);
        var headHeight = onHead.Max(p => p.Y) - onHead.Min(p => p.Y);
        (headHeight / height).ShouldBeInRange(0.28f, 0.4f, "ADR-003 point 3: a head about a third of the body's height");

        var eye = MeshBuilder.OnSurface(Rigs.HeadCenter, Rigs.HeadRadii, Rigs.EyeElevation, 0.36f);
        eye.Y.ShouldBeGreaterThan(Rigs.HeadCenter.Y + 0.05f, "the face sits above the head's equator, so it reads from a camera 50° above");
        eye.Z.ShouldBeLessThan(0f, "on the front (models face -Z)");
    }

    [Fact]
    public void The_rigs_bones_are_unchanged_so_animation_and_attach_points_keep_working()
    {
        string Bones(string rig) => string.Join(",", Rig.Get(rig).Bones.Select(b => $"{b.Name}:{b.Parent}:{b.Local.X:0.###}/{b.Local.Y:0.###}/{b.Local.Z:0.###}"));
        Bones("humanoid").ShouldBe("hips:-1:0/0.9/0,spine:0:0/0.25/0,head:1:0/0.4/0,arm_l:1:-0.3/0.35/0,hand_l:3:0/-0.55/0,arm_r:1:0.3/0.35/0,hand_r:5:0/-0.55/0,leg_l:0:-0.12/0/0,leg_r:0:0.12/0/0");
        Bones("blob").ShouldBe("root:-1:0/0/0,body:0:0/0.35/0");
        Rigs.HeadBoneY.ShouldBe(0.9f + 0.25f + 0.4f, 1e-5f);
    }

    [Fact]
    public void The_jobs_wear_their_ADR_003_colours_and_monsters_pick_from_the_shared_palette()
    {
        var palette = ThemePalette();
        palette.Any.ShouldBeTrue();
        void Job(string key, string primary, string secondary)
        {
            var look = Pack.LookOf(key, KeyColor.Hash(key), 0, Vector4.One, palette, Plain);
            look.Primary.ShouldBe(palette.Named(primary), key);
            look.Secondary.ShouldBe(palette.Named(secondary), key);
        }
        Job("model.char.wanderer", "moss", "leather");
        Job("model.char.warden", "steel", "lumen_blue");
        Job("model.char.spellbinder", "violet", "teal");
        Pack.LookOf("model.char.spellbinder", 0, 0, Vector4.One, palette, Plain).Trim.ShouldBe(palette.Named("gold"), "gold trim");

        var creatures = new[] { "leaf", "sun", "amber", "berry", "rose", "sky", "lilac", "stone", "mint", "bark" }.Select(palette.Named).ToList();
        foreach (var key in new[] { "model.monster.gel_slime", "model.monster.mossling", "model.monster.rock_beetle", "model.monster.sting_wasp", "model.monster.thornback_boar" })
        {
            var look = Pack.LookOf(key, KeyColor.Hash(key), 0, Vector4.One, palette, Plain);
            creatures.ShouldContain(look.Primary, key);
            creatures.ShouldContain(look.Secondary, key);
            look.Primary.ShouldNotBe(look.Secondary, key);
        }
    }

    [Fact]
    public void Placeholder_colours_avoid_pure_black_and_pure_white_and_glowing_parts_are_marked_unlit()
    {
        var m = Body("humanoid", "model.char.spellbinder");
        foreach (var c in m.Mesh.Colors)
        {
            (c.X + c.Y + c.Z).ShouldBeGreaterThan(0.01f, "no pure black (ADR-003 point 4)");
            (c.X > 0.999f && c.Y > 0.999f && c.Z > 0.999f).ShouldBeFalse("no pure white (ADR-003 point 4)");
        }
        m.Mesh.Colors.Count(c => c.W == 0).ShouldBeGreaterThan(0, "the hat's gem glows: vertex alpha 0 is drawn unlit by the toon material");
        Body("humanoid", "model.char.wanderer").Mesh.Colors.ShouldAllBe(c => c.W == 1);
    }

    [Fact]
    public void Every_vertex_carries_an_outline_direction_that_closes_the_hull_at_hard_edges()
    {
        var m = new MeshBuilder();
        m.Box(0, Vector3.Zero, Vector3.One, MeshBuilder.White);
        var outline = m.Mesh.OutlineNormals();
        outline.Count.ShouldBe(m.Mesh.Positions.Count);
        for (var i = 0; i < outline.Count; i++)
        {
            // Every box corner's three vertices share one direction: straight out of the corner.
            var expected = Vector3.Normalize(m.Mesh.Positions[i]);
            Vector3.Dot(outline[i], expected).ShouldBeGreaterThan(0.999f);
        }
    }

    [Fact]
    public void The_pack_models_have_job_colours_in_their_vertices_and_a_white_material()
    {
        var bytes = PackFiles.ReadAllBytes(Path.Combine(Themes, "default", "models", "char", "warden.glb"));
        var length = (int)BitConverter.ToUInt32(bytes, 12);
        var json = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes, 20, length)).RootElement;
        json.GetProperty("materials")[0].GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor").ToString().ShouldBe("[1,1,1,1]");
        var attributes = json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
        attributes.TryGetProperty("TANGENT", out _).ShouldBeTrue("the outline direction");
        attributes.TryGetProperty("COLOR_0", out _).ShouldBeTrue();
    }

    // ---- a failed piece load is retried a bounded number of times (ADR-002 row 225) ----

    [Fact]
    public void A_failed_load_may_be_tried_again_until_its_tries_are_used()
    {
        var budget = new RetryBudget(3);
        budget.MayRetry("a.glb").ShouldBeFalse("nothing failed yet: a first load, not a retry");
        budget.GaveUp("a.glb").ShouldBeFalse();
        budget.Failed("a.glb");
        budget.MayRetry("a.glb").ShouldBeTrue();
        budget.Failed("a.glb");
        budget.MayRetry("a.glb").ShouldBeTrue();
        budget.Failed("a.glb");
        budget.MayRetry("a.glb").ShouldBeFalse();
        budget.GaveUp("a.glb").ShouldBeTrue("three tries in all");
        budget.Failures("b.glb").ShouldBe(0);
        new RetryBudget(0).Failed("c.glb");
    }

    // ---- the skill bar's text (ADR-002 rows 217 and 220) ----

    [Fact]
    public void A_slot_shows_its_number_the_whole_seconds_left_and_the_name()
    {
        HotbarText.Compose(0, null, 0).ShouldBe("1");
        HotbarText.Compose(2, "Ember Bolt", 0).ShouldBe("3\nEmber Bolt");
        HotbarText.Compose(2, "Ember Bolt", 4).ShouldBe("3  4s\nEmber Bolt");
        HotbarText.WholeSeconds(3.01).ShouldBe(4);
        HotbarText.WholeSeconds(3.0).ShouldBe(3);
        HotbarText.WholeSeconds(0).ShouldBe(0);
        HotbarText.WholeSeconds(-1).ShouldBe(0);
    }

    [Fact]
    public void The_font_shrinks_until_every_word_fits_so_a_name_never_breaks_mid_word()
    {
        double Width(string word, int size) => word.Length * size * 0.6;
        HotbarText.FitFontSize("Call Seedling", Width, 50, 13, 9).ShouldBe(10, "'Seedling' is 8 letters: 8 x 10 x 0.6 = 48 fits, at 11 it is 52.8");
        HotbarText.FitFontSize("Bash", Width, 50, 13, 9).ShouldBe(13);
        HotbarText.FitFontSize("Unbelievablylongword", Width, 50, 13, 9).ShouldBe(9, "the smallest step when nothing fits");
    }

    [Fact]
    public void The_text_is_written_again_only_when_the_skill_or_a_whole_second_changes()
    {
        var cache = new HotbarTextCache();
        cache.Changed(0, "Bash", 0).ShouldBeTrue();
        cache.Changed(0, "Bash", 0).ShouldBeFalse();
        var writes = 0;
        for (var t = 3.0; t > 0; t -= 1 / 30.0) if (cache.Changed(0, "Bash", HotbarText.WholeSeconds(t))) writes++;
        writes.ShouldBeInRange(3, 4, "about once a second over a 3 s cooldown at 30 fps, not 90 times");
        cache.Changed(1, null, 0).ShouldBeTrue("a new slot");
        cache.Changed(0, "Ember Bolt", 0).ShouldBeTrue("another skill");
        cache.Clear();
        cache.Changed(1, null, 0).ShouldBeTrue("cleared: every slot is written again");
    }
}
