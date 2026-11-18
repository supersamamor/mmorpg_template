using System.Security.Cryptography;
using System.Text;
using Game.Content.Loading;
using Game.Content.Maps;
using Game.Content.Validation;
using Game.Client.MapAuthoring;
using Game.Tools.Validation;

namespace Game.Tests.MapAuthoring;

/// <summary>The scene hash and <c>validate</c>'s staleness check (T-208, ARCHITECTURE §5.1, §5.3), with no Godot anywhere.</summary>
public class MapSceneHashTests : IDisposable
{
    private const string Visual = """
        [gd_scene format=3 uid="uid://visual"]

        [sub_resource type="StandardMaterial3D" id="mat_1"]
        albedo_color = Color(1, 0, 0, 1)

        [sub_resource type="BoxMesh" id="mesh_1"]
        material = SubResource("mat_1")

        [sub_resource type="BoxShape3D" id="shape_1"]
        size = Vector3(4, 1, 4)

        [node name="Map" type="Node3D"]

        [node name="Lamp" type="OmniLight3D" parent="."]
        light_energy = 2.0

        [node name="Ground" type="StaticBody3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 2, -0.5, 2)

        [node name="Shape" type="CollisionShape3D" parent="Ground"]
        shape = SubResource("shape_1")

        [node name="Mesh" type="MeshInstance3D" parent="Ground"]
        mesh = SubResource("mesh_1")

        """;

    private const string Authoring = """
        [gd_scene format=3]

        [ext_resource type="PackedScene" path="res://themes/default/maps/m.tscn" id="1_visual"]

        [node name="Root" type="Node3D"]
        metadata/map_id = "map.m"
        metadata/map_width = 4
        metadata/map_height = 4
        metadata/map_slope_limit_degrees = 40.0
        metadata/map_overrides = PackedStringArray("1,1:block")

        [node name="Visual" parent="." instance=ExtResource("1_visual")]

        [node name="Markers" type="Node3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0)

        [node name="Note" type="Node3D" parent="Markers"]

        [node name="Warp" type="Marker3D" parent="Markers"]
        metadata/map_marker = "warp"
        metadata/map_target_x = 1

        """;

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mapscene_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static string Hash(string authoring = Authoring, string visual = Visual)
    {
        var result = MapSceneHash.Compute(authoring, path => path == "res://themes/default/maps/m.tscn" ? Encoding.UTF8.GetBytes(visual) : null);
        result.Problems.ShouldBeEmpty();
        return result.Hash;
    }

    private static string Replaced(string text, string from, string to)
    {
        text.ShouldContain(from);
        return text.Replace(from, to);
    }

    // ---- what counts ------------------------------------------------------------------------------------------

    [Fact]
    public void Same_scene_same_hash()
    {
        Hash().ShouldBe(Hash());
        Hash().Length.ShouldBe(64);
    }

    [Fact]
    public void Line_endings_do_not_count()
    {
        Hash(Authoring.Replace("\n", "\r\n"), Visual.Replace("\n", "\r\n")).ShouldBe(Hash());
    }

    [Fact]
    public void What_a_resave_changes_does_not_count()
    {
        var authoring = Replaced(Authoring, "[gd_scene format=3]", "[gd_scene load_steps=3 format=3 uid=\"uid://abc\"]");
        authoring = Replaced(authoring, "[node name=\"Markers\" type=\"Node3D\" parent=\".\"]", "[node name=\"Markers\" type=\"Node3D\" parent=\".\" unique_id=123456]");
        authoring = Replaced(authoring, "id=\"1_visual\"", "uid=\"uid://zzz\" id=\"7_other\"");
        authoring = Replaced(authoring, "ExtResource(\"1_visual\")", "ExtResource(\"7_other\")");
        var visual = Replaced(Visual, "id=\"shape_1\"", "id=\"BoxShape3D_x9y8z\"");
        visual = Replaced(visual, "SubResource(\"shape_1\")", "SubResource(\"BoxShape3D_x9y8z\")");
        visual = Replaced(visual, "Vector3(4, 1, 4)", "Vector3(4,1,4)");

        Hash(authoring, visual).ShouldBe(Hash());
    }

    [Fact]
    public void Order_of_sections_does_not_count()
    {
        const string a = "[node name=\"R\" type=\"Node3D\"]\n\n[node name=\"A\" type=\"StaticBody3D\" parent=\".\"]\n\n[node name=\"B\" type=\"StaticBody3D\" parent=\".\"]\n";
        const string b = "[node name=\"R\" type=\"Node3D\"]\n\n[node name=\"B\" type=\"StaticBody3D\" parent=\".\"]\n\n[node name=\"A\" type=\"StaticBody3D\" parent=\".\"]\n";

        Hash(a).ShouldBe(Hash(b));
    }

    public static TheoryData<string, string> VisualOnly => new()
    {
        { "Color(1, 0, 0, 1)", "Color(0, 1, 0, 1)" },                // a material
        { "light_energy = 2.0", "light_energy = 3.0" },              // a light
        { "mesh = SubResource(\"mesh_1\")", "visible = false" },     // a mesh
    };

    [Theory]
    [MemberData(nameof(VisualOnly))]
    public void A_purely_visual_edit_does_not_count(string from, string to)
    {
        Hash(visual: Replaced(Visual, from, to)).ShouldBe(Hash());
    }

    [Fact]
    public void A_new_visual_node_or_empty_container_does_not_count()
    {
        Hash(visual: Visual + "\n[node name=\"Tree\" type=\"MeshInstance3D\" parent=\".\"]\ntransform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 9, 9, 9)\n").ShouldBe(Hash());
        Hash(Authoring + "\n[node name=\"Empty\" type=\"Node3D\" parent=\".\"]\n").ShouldBe(Hash());
    }

    public static TheoryData<string, string> CollisionEdits => new()
    {
        { "Vector3(4, 1, 4)", "Vector3(4, 1, 3)" },                                             // a shape's size
        { "0, 1, 2, -0.5, 2)", "0, 1, 2, -0.5, 1)" },                                           // a body's transform
        { "shape = SubResource(\"shape_1\")", "disabled = true\nshape = SubResource(\"shape_1\")" }, // a disabled shape
    };

    [Theory]
    [MemberData(nameof(CollisionEdits))]
    public void A_collision_edit_in_the_instanced_scene_counts(string from, string to)
    {
        Hash(visual: Replaced(Visual, from, to)).ShouldNotBe(Hash());
    }

    public static TheoryData<string, string> AuthoringEdits => new()
    {
        { "PackedStringArray(\"1,1:block\")", "PackedStringArray(\"1,1:walk\")" },     // paint
        { "map_slope_limit_degrees = 40.0", "map_slope_limit_degrees = 30.0" },        // slope limit
        { "map_width = 4", "map_width = 5" },                                          // size
        { "map_target_x = 1", "map_target_x = 2" },                                    // a marker's data
        { "0, 1, 1, 0, 0)", "0, 1, 2, 0, 0)" },                                        // an ancestor's transform moves a marker
    };

    [Theory]
    [MemberData(nameof(AuthoringEdits))]
    public void An_authoring_edit_counts(string from, string to)
    {
        Hash(Replaced(Authoring, from, to)).ShouldNotBe(Hash());
    }

    [Fact]
    public void A_new_collision_body_counts()
    {
        Hash(visual: Visual + "\n[node name=\"Rock\" type=\"StaticBody3D\" parent=\".\"]\n").ShouldNotBe(Hash());
    }

    [Fact]
    public void A_mesh_with_collision_under_it_counts_through_its_transform_only()
    {
        const string start = "[node name=\"R\" type=\"Node3D\"]\n\n[node name=\"M\" type=\"MeshInstance3D\" parent=\".\"]\n";
        const string end = "\n[node name=\"B\" type=\"StaticBody3D\" parent=\"M\"]\n";

        Hash(start + "cast_shadow = 0\n" + end).ShouldBe(Hash(start + end));
        Hash(start + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 0, 0)\n" + end).ShouldNotBe(Hash(start + end));
    }

    // ---- other files ------------------------------------------------------------------------------------------

    private static string HashWithBinary(byte[] file)
    {
        const string scene = "[gd_scene format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://themes/default/models/rock.glb\" id=\"1\"]\n\n[node name=\"R\" type=\"Node3D\"]\n\n[node name=\"Rock\" parent=\".\" instance=ExtResource(\"1\")]\n";
        return MapSceneHash.Compute(scene, _ => file).Hash;
    }

    [Fact]
    public void A_binary_resource_is_hashed_by_content_and_its_lfs_pointer_gives_the_same_value()
    {
        var real = new byte[] { 0x67, 0x6c, 0x54, 0x46, 1, 2, 3, 4 };
        var oid = Convert.ToHexString(SHA256.HashData(real)).ToLowerInvariant();
        var pointer = Encoding.ASCII.GetBytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize {real.Length}\n");

        HashWithBinary(pointer).ShouldBe(HashWithBinary(real));
        HashWithBinary(new byte[] { 9, 9 }).ShouldNotBe(HashWithBinary(real));
    }

    [Fact]
    public void A_missing_resource_is_a_problem()
    {
        MapSceneHash.Compute(Authoring, _ => null).Problems.ShouldHaveSingleItem().ShouldContain("does not exist");
    }

    [Fact]
    public void Scenes_instancing_themselves_end_as_a_problem()
    {
        const string loop = "[gd_scene format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://themes/default/maps/m.tscn\" id=\"1\"]\n\n[node name=\"R\" parent=\".\" instance=ExtResource(\"1\")]\n";

        MapSceneHash.Compute(loop, _ => Encoding.UTF8.GetBytes(loop)).Problems.ShouldNotBeEmpty();
    }

    [Fact]
    public void Authoring_path_sits_next_to_the_scene()
    {
        MapSceneHash.AuthoringPathOf("default/maps/m.tscn").ShouldBe("default/maps/m.authoring.tscn");
    }

    // ---- the parser -------------------------------------------------------------------------------------------

    [Fact]
    public void The_parser_reads_values_that_span_lines_and_brackets_in_strings()
    {
        const string text = "; comment\n[node name=\"A [b]\" groups=[\"x\", \"y z\"]]\nmetadata/d = {\n\"k\": [1, 2],\n\"s\": \"line [\nnext \\\" ]\"\n}\nmetadata/a = [1, 2]\nb = 3\n\n[connection signal=\"x\"]\n";

        var sections = TscnFile.Parse(text);

        sections.Count.ShouldBe(2);
        sections[0].Tag.ShouldBe("node");
        sections[0].Text("name").ShouldBe("A [b]");
        sections[0].Attributes["groups"].ShouldBe("[\"x\", \"y z\"]");
        sections[0].Properties.Select(p => p.Key).ShouldBe(new[] { "metadata/d", "metadata/a", "b" });
        sections[0].Properties[1].Value.ShouldBe("[1, 2]");
        sections[0].Properties[2].Value.ShouldBe("3");
        sections[1].Tag.ShouldBe("connection");
    }

    [Theory]
    [InlineData("[node name=\"a\"")]
    [InlineData("[node]\nb = \"open")]
    [InlineData("b = 1\n")]
    public void Bad_scene_text_is_a_format_error(string text)
    {
        Should.Throw<FormatException>(() => TscnFile.Parse(text));
    }

    // ---- validate ---------------------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found");
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private (string Content, string Themes) CopyRepo()
    {
        var content = Path.Combine(_temp, "content");
        var themes = Path.Combine(_temp, "themes");
        Copy(Path.Combine(RepoRoot(), "content"), content);
        foreach (var file in new[] { "starter_meadow.tscn", "starter_meadow.authoring.tscn", "thistle_hollow.tscn", "thistle_hollow.authoring.tscn" })
        {
            Directory.CreateDirectory(Path.Combine(themes, "default", "maps"));
            File.Copy(Path.Combine(RepoRoot(), "themes", "default", "maps", file), Path.Combine(themes, "default", "maps", file));
        }
        File.Copy(Path.Combine(RepoRoot(), "themes", "default", "asset-manifest.json"), Path.Combine(themes, "default", "asset-manifest.json"));
        return (content, themes);
    }

    private static IReadOnlyList<ValidationIssue> Check(string content, string themes)
    {
        var load = ContentLoader.Load(content);
        load.Database.ShouldNotBeNull();
        return MapSceneCheck.Check(load.Database!, content, themes, "default");
    }

    private static void Edit(string path, string from, string to)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        text.ShouldContain(from);
        File.WriteAllText(path, text.Replace(from, to));
    }

    [Fact]
    public void Committed_grids_match_their_scenes()
    {
        var root = RepoRoot();

        Check(Path.Combine(root, "content"), Path.Combine(root, "themes")).ShouldBeEmpty();
    }

    [Fact]
    public void Staleness_passes_with_crlf_scenes()
    {
        var (content, themes) = CopyRepo();
        foreach (var file in Directory.GetFiles(Path.Combine(themes, "default", "maps")))
            File.WriteAllText(file, File.ReadAllText(file).Replace("\r\n", "\n").Replace("\n", "\r\n"));

        Check(content, themes).ShouldBeEmpty();
    }

    [Fact]
    public void A_changed_marker_fails_with_file_and_path()
    {
        var (content, themes) = CopyRepo();
        Edit(Path.Combine(themes, "default", "maps", "thistle_hollow.authoring.tscn"), "map_count = 4", "map_count = 5");

        var issue = Check(content, themes).ShouldHaveSingleItem();

        issue.Severity.ShouldBe(ValidationSeverity.Error);
        issue.Code.ShouldBe(ValidationCodes.GridSceneStale);
        issue.File.ShouldBe("maps/thistle_hollow.grid.json");
        issue.Path.ShouldBe("source_scene_hash");
        issue.Line.ShouldBeGreaterThan(0);
        issue.Message.ShouldContain("themes/default/maps/thistle_hollow.authoring.tscn");
    }

    [Fact]
    public void Changed_collision_in_the_visual_scene_fails()
    {
        var (content, themes) = CopyRepo();
        Edit(Path.Combine(themes, "default", "maps", "thistle_hollow.tscn"), "id=\"ground_shape\"]\nsize = Vector3(20, 1, 14)", "id=\"ground_shape\"]\nsize = Vector3(20, 1, 13)");

        Check(content, themes).ShouldHaveSingleItem().Code.ShouldBe(ValidationCodes.GridSceneStale);
    }

    [Fact]
    public void A_new_material_colour_does_not_fail()
    {
        var (content, themes) = CopyRepo();
        Edit(Path.Combine(themes, "default", "maps", "thistle_hollow.tscn"), "albedo_color = Color(0.62, 0.6, 0.57, 1)", "albedo_color = Color(0.7, 0.6, 0.57, 1)");

        Check(content, themes).ShouldBeEmpty();
    }

    [Fact]
    public void A_grid_with_no_hash_fails()
    {
        var (content, themes) = CopyRepo();
        var grid = Path.Combine(content, "maps", "thistle_hollow.grid.json");
        var file = GridFileJson.Read(File.ReadAllText(grid));
        File.WriteAllText(grid, GridFileJson.Write(file with { SourceSceneHash = string.Empty }));

        Check(content, themes).ShouldHaveSingleItem().Message.ShouldContain("grid has none");
    }

    [Fact]
    public void A_missing_authoring_scene_fails()
    {
        var (content, themes) = CopyRepo();
        File.Delete(Path.Combine(themes, "default", "maps", "thistle_hollow.authoring.tscn"));

        var issue = Check(content, themes).ShouldHaveSingleItem();

        issue.Code.ShouldBe(ValidationCodes.GridSceneMissing);
        issue.Message.ShouldContain("does not exist");
    }

    [Fact]
    public void A_binary_map_scene_is_refused_with_the_reason()
    {
        var (content, themes) = CopyRepo();
        Edit(Path.Combine(themes, "default", "asset-manifest.json"), "maps/thistle_hollow.tscn", "maps/thistle_hollow.scn");

        Check(content, themes).ShouldHaveSingleItem().Message.ShouldContain("must be .tscn");
    }

    [Fact]
    public void Exporter_output_matches_the_grid_schema()
    {
        var (content, _) = CopyRepo();
        var file = GridFileJson.Read(File.ReadAllText(Path.Combine(content, "maps", "starter_meadow.grid.json")));
        File.WriteAllBytes(Path.Combine(content, "maps", "starter_meadow.grid.json"), GridJsonWriter.WriteBytes(file));

        SchemaCheck.Check(content, Path.Combine(RepoRoot(), "schemas")).ShouldBeEmpty();
    }

    [Fact]
    public void Export_presets_keep_authoring_scenes_and_the_plugin_out_of_every_build()
    {
        var presets = File.ReadAllLines(Path.Combine(RepoRoot(), "client", "export_presets.cfg"));

        var filters = presets.Where(l => l.StartsWith("exclude_filter=", StringComparison.Ordinal)).ToList();

        filters.Count.ShouldBe(presets.Count(l => l.StartsWith("export_filter=", StringComparison.Ordinal)));
        filters.ShouldAllBe(l => l.Contains("themes/*/maps/*.authoring.tscn") && l.Contains("addons/*"));
    }

    [Fact]
    public void Shipped_map_scenes_hold_no_spawn_data()
    {
        foreach (var scene in Directory.GetFiles(Path.Combine(RepoRoot(), "themes"), "*.tscn", SearchOption.AllDirectories))
        {
            if (scene.EndsWith(MapSceneHash.AuthoringSuffix, StringComparison.Ordinal)) continue;
            File.ReadAllText(scene).ShouldNotContain("map_monster_id", customMessage: scene);
            File.ReadAllText(scene).ShouldNotContain("map_marker", customMessage: scene);
        }
    }
}
