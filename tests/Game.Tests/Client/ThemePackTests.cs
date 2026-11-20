using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Game.Client.Core.Theme;
using Game.Content.ClientExport;
using Game.Shared.Protocol;
using Game.Tests.World;
using Game.ThemeGen;

namespace Game.Tests.Client;

/// <summary>
/// The generated placeholder pack (T-207, ADR-002 row 31) as files on disk: every asset key content uses has a real file, the models meet the §9 budgets,
/// the rig and clips line up, the pack is exactly what the generator writes, and a second theme changes visuals with no code change.
/// </summary>
public class ThemePackTests
{
    private static readonly string Themes = Path.Combine(TestGrids.RepoRoot(), "themes");

    private static readonly Regex AssetKey = new(@"^(model|anim|scene|tex|mat|vfx|sprite|icon|ui|portrait|sfx|music|font|ambience)\.", RegexOptions.Compiled);

    private static JsonElement Manifest(string theme)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Themes, theme, "asset-manifest.json")));
        return document.RootElement.Clone();
    }

    // A variant (T-224) has no file of its own: it draws its base's, which is checked here as an ordinary entry (ThemeContractTests covers variants).
    private static IEnumerable<(string Key, JsonElement Entry)> Entries(JsonElement manifest) =>
        manifest.EnumerateObject().Where(p => p.Name is not ("audio" or "fallbacks") && !p.Value.TryGetProperty("variant_of", out _)).Select(p => (p.Name, p.Value));

    private static IEnumerable<string> FilesOf(JsonElement entry)
    {
        if (entry.TryGetProperty("file", out var file)) yield return file.GetString()!;
        if (entry.TryGetProperty("files", out var files))
            foreach (var f in files.EnumerateArray()) yield return f.GetString()!;
    }

    /// <summary>Every string in the client bundle that is an asset key.</summary>
    private static SortedSet<string> KeysInBundle()
    {
        using var document = JsonDocument.Parse(ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String when AssetKey.IsMatch(e.GetString()!): keys.Add(e.GetString()!); break;
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value); break;
                case JsonValueKind.Array: foreach (var item in e.EnumerateArray()) Walk(item); break;
            }
        }
        Walk(document.RootElement);
        return keys;
    }

    // ---- every key has a file ----

    [Fact]
    public void Every_asset_key_the_content_bundle_uses_is_in_the_default_manifest_with_a_real_file_on_disk()
    {
        var keys = KeysInBundle();
        keys.Count.ShouldBeGreaterThan(60, "test setup: the bundle should name dozens of keys");
        var manifest = Manifest("default");
        var problems = new List<string>();
        foreach (var key in keys)
        {
            if (!manifest.TryGetProperty(key, out var entry)) { problems.Add($"{key}: not in the manifest"); continue; }
            var files = FilesOf(entry).ToList();
            if (files.Count == 0) problems.Add($"{key}: names no file");
            foreach (var file in files)
            {
                var path = Path.Combine(Themes, "default", file);
                if (!File.Exists(path)) problems.Add($"{key}: {file} does not exist");
                else if (new FileInfo(path).Length == 0) problems.Add($"{key}: {file} is empty");
            }
        }
        problems.ShouldBeEmpty(string.Join("\n", problems));
    }

    [Fact]
    public void Every_manifest_entry_has_a_file_inside_its_theme_folder_and_the_manifest_holds_no_absolute_or_escaping_path()
    {
        foreach (var theme in new[] { "default", "test" })
            foreach (var (key, entry) in Entries(Manifest(theme)))
                foreach (var file in FilesOf(entry))
                {
                    Path.IsPathRooted(file).ShouldBeFalse($"{theme}/{key}: {file}");
                    file.ShouldNotContain("..", customMessage: $"{theme}/{key}: {file}");
                    File.Exists(Path.Combine(Themes, theme, file)).ShouldBeTrue($"{theme}/{key}: {file}");
                }
    }

    [Fact]
    public void The_fallbacks_table_names_a_stand_in_for_every_entity_kind_and_for_missing_and_all_of_them_exist()
    {
        var manifest = Manifest("default");
        var fallbacks = manifest.GetProperty("fallbacks");
        foreach (var name in new[] { EntityKinds.Player, EntityKinds.Monster, EntityKinds.Npc, EntityKinds.Item, "missing" })
        {
            var key = fallbacks.GetProperty(name).GetString()!;
            manifest.TryGetProperty(key, out var entry).ShouldBeTrue($"fallbacks.{name} -> {key}");
            File.Exists(Path.Combine(Themes, "default", FilesOf(entry).Single())).ShouldBeTrue(key);
        }
    }

    // ---- budgets (ARCHITECTURE §9) ----

    private sealed record Glb(JsonElement Json, int Lod0Triangles, int Lod1Triangles);

    private static JsonDocument ReadGlb(string path)
    {
        var bytes = PackFiles.ReadAllBytes(path);
        BitConverter.ToUInt32(bytes, 0).ShouldBe(0x46546C67u, path + " is not a glb");
        var length = (int)BitConverter.ToUInt32(bytes, 12);
        return JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, length));
    }

    private static int Triangles(JsonElement root, string meshName)
    {
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        {
            if (mesh.GetProperty("name").GetString() != meshName) continue;
            var indices = mesh.GetProperty("primitives")[0].GetProperty("indices").GetInt32();
            return root.GetProperty("accessors")[indices].GetProperty("count").GetInt32() / 3;
        }
        return -1;
    }

    [Fact]
    public void Every_model_has_one_material_no_textures_and_meets_the_triangle_budgets_at_both_levels()
    {
        var checkedModels = 0;
        foreach (var theme in new[] { "default", "test" })
            foreach (var (key, entry) in Entries(Manifest(theme)).Where(e => e.Key.StartsWith("model.", StringComparison.Ordinal)))
            {
                using var glb = ReadGlb(Path.Combine(Themes, theme, FilesOf(entry).Single()));
                var root = glb.RootElement;
                root.GetProperty("materials").GetArrayLength().ShouldBe(1, $"{theme}/{key}: one material");
                root.TryGetProperty("images", out _).ShouldBeFalse($"{theme}/{key}: no textures, so none can exceed 1024x1024");
                var lod0 = Triangles(root, "Body_LOD0");
                var lod1 = Triangles(root, "Body_LOD1");
                lod0.ShouldBeInRange(1, 5000, $"{theme}/{key} LOD0");
                if (lod1 >= 0)
                {
                    lod1.ShouldBeInRange(1, 2000, $"{theme}/{key} LOD1");
                    lod1.ShouldBeLessThan(lod0, $"{theme}/{key}: LOD1 is the cheaper one");
                }
                checkedModels++;
            }
        checkedModels.ShouldBeGreaterThan(30);
    }

    [Fact]
    public void Worst_case_visible_characters_stay_inside_the_150k_triangle_budget()
    {
        // 8 nearest at LOD0 and 32 more at LOD1, each the most expensive character model in the pack.
        int Worst(string level) => Entries(Manifest("default")).Where(e => e.Key.StartsWith("model.", StringComparison.Ordinal) && e.Entry.TryGetProperty("anims", out _))
            .Max(e => { using var g = ReadGlb(Path.Combine(Themes, "default", FilesOf(e.Entry).Single())); return Triangles(g.RootElement, level); });
        (8 * Worst("Body_LOD0") + 32 * Worst("Body_LOD1")).ShouldBeLessThan(104_000, "ARCHITECTURE §9: 8 x 5k + 32 x 2k");
    }

    [Fact]
    public void Textures_are_no_larger_than_1024_and_the_whole_pack_is_small()
    {
        foreach (var file in Directory.EnumerateFiles(Themes, "*.png", SearchOption.AllDirectories))
        {
            var bytes = PackFiles.ReadAllBytes(file);
            var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            width.ShouldBeInRange(1, 1024, file);
            height.ShouldBeInRange(1, 1024, file);
        }
        var total = Directory.EnumerateFiles(Themes, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        total.ShouldBeLessThan(3L * 1024 * 1024, "the generated pack is a few hundred KB of the 200 MB install budget");
    }

    // ---- rigs and clips ----

    private static List<string> Joints(JsonElement root)
    {
        var skin = root.GetProperty("skins")[0];
        var nodes = root.GetProperty("nodes");
        return skin.GetProperty("joints").EnumerateArray().Select(j => nodes[j.GetInt32()].GetProperty("name").GetString()!).ToList();
    }

    [Fact]
    public void Every_animated_model_shares_its_rig_with_a_library_that_has_idle_walk_attack_hit_and_die()
    {
        var manifest = Manifest("default");
        var animated = Entries(manifest).Where(e => e.Entry.TryGetProperty("anims", out _)).ToList();
        animated.Count.ShouldBeGreaterThan(8);
        foreach (var (key, entry) in animated)
        {
            var libraryKey = entry.GetProperty("anims").GetString()!;
            manifest.TryGetProperty(libraryKey, out var library).ShouldBeTrue($"{key} -> {libraryKey}");
            library.GetProperty("rig").GetString().ShouldBe(entry.GetProperty("rig").GetString(), $"{key} and {libraryKey} must be on the same rig");

            var clips = library.GetProperty("clips");
            foreach (var id in new[] { "idle", "walk", "attack", "hit", "die" }) clips.TryGetProperty(id, out _).ShouldBeTrue($"{libraryKey} lacks '{id}'");

            using var model = ReadGlb(Path.Combine(Themes, "default", FilesOf(entry).Single()));
            using var animations = ReadGlb(Path.Combine(Themes, "default", FilesOf(library).Single()));
            Joints(animations.RootElement).ShouldBe(Joints(model.RootElement), $"{key}: the library's skeleton must match the model's");
            var names = animations.RootElement.GetProperty("animations").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToHashSet();
            foreach (var clip in clips.EnumerateObject()) names.ShouldContain(clip.Value.GetString(), $"{libraryKey}: clip '{clip.Name}'");
        }
    }

    [Fact]
    public void Animated_models_carry_their_key_as_the_material_name_and_each_has_its_own_colour()
    {
        var colours = new Dictionary<string, string>();
        foreach (var (key, entry) in Entries(Manifest("default")).Where(e => e.Entry.TryGetProperty("anims", out _)))
        {
            using var glb = ReadGlb(Path.Combine(Themes, "default", FilesOf(entry).Single()));
            var material = glb.RootElement.GetProperty("materials")[0];
            material.GetProperty("name").GetString().ShouldBe(key);
            glb.RootElement.GetProperty("asset").GetProperty("extras").GetProperty("key").GetString().ShouldBe(key);
            // T-222: the colours are vertex colours from the theme's palette (the material is white), so a model's look is its set of vertex colours.
            var colour = Colours(Path.Combine(Themes, "default", FilesOf(entry).Single()));
            colours.Values.ShouldNotContain(colour, $"{key} looks like another model");
            colours[key] = colour;
        }
    }

    /// <summary>The distinct vertex colours (RGBA bytes) of a model's first mesh, sorted, as one string, with its material's base colour.</summary>
    private static string Colours(string path)
    {
        var bytes = PackFiles.ReadAllBytes(path);
        var jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
        using var glb = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength));
        var root = glb.RootElement;
        var bin = 20 + jsonLength + 8;
        var accessor = root.GetProperty("accessors")[root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("COLOR_0").GetInt32()];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var start = bin + view.GetProperty("byteOffset").GetInt32();
        var set = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < accessor.GetProperty("count").GetInt32(); i++) set.Add(Convert.ToHexString(bytes, start + i * 4, 4));
        return root.GetProperty("materials")[0].GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor") + ":" + string.Join(",", set);
    }

    // ---- reproducible ----

    [Fact]
    public void The_pack_on_disk_is_exactly_what_the_generator_writes_and_has_no_stale_files()
    {
        var generated = Pack.Generate(Themes);
        foreach (var (relative, bytes) in generated)
        {
            var path = Path.Combine(Themes, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).ShouldBeTrue(relative + " is missing; run: dotnet run --project tools/themegen");
            PackFiles.ReadAllBytes(path).AsSpan().SequenceEqual(bytes).ShouldBeTrue(relative + " differs from the generator's output; run: dotnet run --project tools/themegen");
        }
        foreach (var theme in new[] { Pack.DefaultTheme, Pack.TestTheme })
            foreach (var folder in Pack.OwnedFolders.Select(f => Path.Combine(Themes, theme, f)).Where(Directory.Exists))
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".import", StringComparison.Ordinal)))
                    generated.ContainsKey(Path.GetRelativePath(Themes, file).Replace('\\', '/')).ShouldBeTrue(file + " is stale; run: dotnet run --project tools/themegen");
    }

    [Fact]
    public void The_generator_is_deterministic()
    {
        var a = Pack.Generate(Themes);
        var b = Pack.Generate(Themes);
        a.Keys.ShouldBe(b.Keys);
        foreach (var key in a.Keys) a[key].AsSpan().SequenceEqual(b[key]).ShouldBeTrue(key);
    }

    // ---- swapping a theme ----

    private static string Disk(string project) => Path.Combine(Themes, project["themes/".Length..].Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Switching_the_active_theme_to_test_changes_which_files_draw_a_model_with_no_code_change()
    {
        var warnings = new List<string>();
        ThemeCatalog Make(params string[] themes) => new(
            themes.Select(t => { ThemeManifest.TryParse(t, File.ReadAllText(Path.Combine(Themes, t, "asset-manifest.json")), out var m, out _); return m!; }).ToList(),
            path => File.Exists(Disk(path)), warnings.Add);

        var plain = Make("default");
        var swapped = Make("test", "default");

        var before = plain.ResolveModel("model.monster.gel_slime", EntityKinds.Monster);
        var after = swapped.ResolveModel("model.monster.gel_slime", EntityKinds.Monster);
        before.Path.ShouldBe("themes/default/models/monster/gel_slime.glb");
        after.Path.ShouldBe("themes/test/models/monster/gel_slime.glb");
        PackFiles.ReadAllBytes(Disk(after.Path!)).AsSpan().SequenceEqual(PackFiles.ReadAllBytes(Disk(before.Path!))).ShouldBeFalse("the test theme draws it differently");

        Colours(Disk(before.Path!)).ShouldNotBe(Colours(Disk(after.Path!)), "the test theme turns every palette hue");

        // What the test theme leaves out still comes from the default theme, and its animation library is the default one.
        swapped.ResolveModel("model.monster.mossling", EntityKinds.Monster).Path.ShouldBe("themes/default/models/monster/mossling.glb");
        swapped.ResolveAnimations(after.Asset!, out var library).ShouldNotBeNull();
        library.ShouldBe("themes/default/anims/blob.glb");
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Removing_a_real_model_file_falls_back_with_one_warning_however_often_it_is_asked_for()
    {
        var warnings = new List<string>();
        const string Gone = "themes/default/models/monster/mossling.glb";
        var manifest = ThemeManifest.TryParse("default", File.ReadAllText(Path.Combine(Themes, "default", "asset-manifest.json")), out var m, out _) ? m! : null!;
        var catalog = new ThemeCatalog([manifest], path => path != Gone && File.Exists(Disk(path)), warnings.Add);

        ModelResolution? last = null;
        for (var i = 0; i < 100; i++) last = catalog.ResolveModel("model.monster.mossling", EntityKinds.Monster);

        last!.Level.ShouldBe(ResolutionLevel.KindFallback);
        File.Exists(Disk(last.Path!)).ShouldBeTrue("the stand-in is a real file");
        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("model.monster.mossling");
    }

    [Fact]
    public void Every_bundle_key_resolves_exactly_with_no_warning_against_the_default_theme()
    {
        var warnings = new List<string>();
        var manifest = ThemeManifest.TryParse("default", File.ReadAllText(Path.Combine(Themes, "default", "asset-manifest.json")), out var m, out _) ? m! : null!;
        var catalog = new ThemeCatalog([manifest], path => File.Exists(Disk(path)), warnings.Add);

        foreach (var key in KeysInBundle().Where(k => k.StartsWith("model.", StringComparison.Ordinal)))
            catalog.ResolveModel(key, EntityKinds.Monster).Level.ShouldBe(ResolutionLevel.Exact, key);
        warnings.ShouldBeEmpty();
    }
}
