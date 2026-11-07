using Game.Client.Core.Theme;
using Game.Shared.Protocol;

namespace Game.Tests.Client;

/// <summary>The manifest loader and its fallback chain (T-207, ARCHITECTURE §8): pure logic, no engine and no files.</summary>
public class ThemeCatalogTests
{
    private const string DefaultManifest = """
        {
          "model.a": { "file": "models/a.glb", "rig": "humanoid", "anims": "anim.humanoid" },
          "model.b": { "file": "models/b.glb" },
          "model.missing": { "file": "models/missing.glb" },
          "model.kind.monster": { "file": "models/monster.glb" },
          "anim.humanoid": { "file": "anims/humanoid.glb", "rig": "humanoid", "clips": { "idle": "Idle", "walk": "Walk" } },
          "sfx.hit": { "files": ["audio/a.ogg", "audio/b.ogg"] },
          "audio": { "defaults": { "ui.tap": "sfx.hit" } },
          "fallbacks": { "missing": "model.missing", "monster": "model.kind.monster" }
        }
        """;

    private const string OtherManifest = """
        {
          "model.a": { "file": "models/a_alt.glb", "rig": "humanoid", "anims": "anim.humanoid" }
        }
        """;

    private static ThemeManifest Parse(string theme, string json)
    {
        ThemeManifest.TryParse(theme, json, out var manifest, out var error).ShouldBeTrue(error);
        return manifest!;
    }

    private sealed class Rig
    {
        public readonly HashSet<string> Files = new(StringComparer.Ordinal)
        {
            "themes/default/models/a.glb", "themes/default/models/b.glb", "themes/default/models/missing.glb", "themes/default/models/monster.glb",
            "themes/default/anims/humanoid.glb", "themes/other/models/a_alt.glb",
        };

        public readonly List<string> Warnings = new();

        public ThemeCatalog Catalog(params string[] themes) =>
            new(themes.Select(t => Parse(t, t == "default" ? DefaultManifest : OtherManifest)).ToList(), Files.Contains, Warnings.Add);
    }

    // ---- parsing ----

    [Fact]
    public void The_manifest_is_read_with_its_metadata_and_the_two_tables_are_not_assets()
    {
        var manifest = Parse("default", DefaultManifest);

        manifest.TryGet("model.a", out var a).ShouldBeTrue();
        a.File.ShouldBe("models/a.glb");
        a.Rig.ShouldBe("humanoid");
        a.Anims.ShouldBe("anim.humanoid");
        manifest.TryGet("anim.humanoid", out var library).ShouldBeTrue();
        library.Clips["walk"].ShouldBe("Walk");
        manifest.TryGet("sfx.hit", out var sound).ShouldBeTrue();
        sound.File.ShouldBeNull("an entry with 'files' names no single file");
        manifest.TryGet("audio", out _).ShouldBeFalse();
        manifest.TryGet("fallbacks", out _).ShouldBeFalse();
        manifest.TryGetFallback("monster", out var key).ShouldBeTrue();
        key.ShouldBe("model.kind.monster");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{ \"model.a\": 3 }")]
    [InlineData("{ \"fallbacks\": [] }")]
    [InlineData("{ not json")]
    public void A_malformed_manifest_is_refused_with_a_reason_and_never_throws(string json)
    {
        ThemeManifest.TryParse("x", json, out var manifest, out var error).ShouldBeFalse();
        manifest.ShouldBeNull();
        error.ShouldNotBeEmpty();
    }

    // ---- the chain ----

    [Fact]
    public void A_listed_key_with_its_file_resolves_to_a_path_inside_the_theme_with_no_warning()
    {
        var rig = new Rig();
        var resolution = rig.Catalog("default").ResolveModel("model.a", EntityKinds.Monster);

        resolution.Level.ShouldBe(ResolutionLevel.Exact);
        resolution.Key.ShouldBe("model.a");
        resolution.Path.ShouldBe("themes/default/models/a.glb");
        resolution.Asset!.Rig.ShouldBe("humanoid");
        rig.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void The_active_theme_wins_and_anything_it_leaves_out_comes_from_the_default_theme()
    {
        var rig = new Rig();
        var catalog = rig.Catalog("other", "default");

        catalog.ResolveModel("model.a", EntityKinds.Npc).Path.ShouldBe("themes/other/models/a_alt.glb");
        catalog.ResolveModel("model.b", EntityKinds.Npc).Path.ShouldBe("themes/default/models/b.glb");
        rig.Warnings.ShouldBeEmpty("an overlay theme is allowed to be partial");
    }

    [Fact]
    public void A_key_whose_file_is_gone_falls_back_to_the_kind_stand_in_with_exactly_one_warning()
    {
        var rig = new Rig();
        rig.Files.Remove("themes/default/models/b.glb");
        var catalog = rig.Catalog("default");

        ModelResolution? last = null;
        for (var i = 0; i < 50; i++) last = catalog.ResolveModel("model.b", EntityKinds.Monster);

        last!.Level.ShouldBe(ResolutionLevel.KindFallback);
        last.Key.ShouldBe("model.kind.monster");
        rig.Warnings.Count.ShouldBe(1);
        rig.Warnings[0].ShouldContain("model.b");
        rig.Warnings[0].ShouldContain("missing on disk");
    }

    [Fact]
    public void The_file_missing_in_the_active_theme_but_present_in_the_default_one_is_not_a_problem()
    {
        var rig = new Rig();
        rig.Files.Remove("themes/other/models/a_alt.glb");

        rig.Catalog("other", "default").ResolveModel("model.a", EntityKinds.Npc).Path.ShouldBe("themes/default/models/a.glb");
        rig.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void A_key_no_theme_lists_goes_to_the_missing_model_for_a_kind_without_a_stand_in()
    {
        var rig = new Rig();
        var resolution = rig.Catalog("default").ResolveModel("model.nope", EntityKinds.Player);

        resolution.Level.ShouldBe(ResolutionLevel.MissingFallback);
        resolution.Key.ShouldBe("model.missing");
        rig.Warnings.Count.ShouldBe(1);
        rig.Warnings[0].ShouldContain("no theme lists it");
    }

    [Fact]
    public void An_entity_content_gave_no_model_uses_the_kind_stand_in_without_a_warning()
    {
        var rig = new Rig();
        var catalog = rig.Catalog("default");

        catalog.ResolveModel(null, EntityKinds.Monster).Key.ShouldBe("model.kind.monster");
        catalog.ResolveModel(null, EntityKinds.Player).Key.ShouldBe("model.missing");
        rig.Warnings.ShouldBeEmpty("having no model is a content choice, not a theme fault");
    }

    [Fact]
    public void With_nothing_usable_the_chain_ends_in_the_built_in_marker_and_never_throws()
    {
        var rig = new Rig();
        rig.Files.Clear();
        var catalog = rig.Catalog("default");

        var resolution = catalog.ResolveModel("model.a", EntityKinds.Monster);

        resolution.Level.ShouldBe(ResolutionLevel.BuiltIn);
        resolution.Key.ShouldBeNull();
        resolution.Path.ShouldBeNull();
        catalog.ResolveModel("model.a", EntityKinds.Monster).ShouldBeSameAs(resolution, "cached");
        new ThemeCatalog(Array.Empty<ThemeManifest>(), _ => false, _ => { }).ResolveModel("model.a", EntityKinds.Item).Level.ShouldBe(ResolutionLevel.BuiltIn);
        rig.Warnings.Count.ShouldBe(4, string.Join("\n", rig.Warnings)); // each distinct key once: model.a, the kind stand-in, model.missing, and the chain's end
    }

    [Fact]
    public void A_missing_animation_library_warns_once_and_the_model_still_resolves()
    {
        var rig = new Rig();
        rig.Files.Remove("themes/default/anims/humanoid.glb");
        var catalog = rig.Catalog("default");
        var model = catalog.ResolveModel("model.a", EntityKinds.Npc);

        for (var i = 0; i < 10; i++) catalog.ResolveAnimations(model.Asset!, out _).ShouldBeNull();

        model.Level.ShouldBe(ResolutionLevel.Exact);
        rig.Warnings.Count.ShouldBe(1);
        rig.Warnings[0].ShouldContain("anim.humanoid");
    }

    [Fact]
    public void A_present_library_resolves_with_its_clip_names()
    {
        var rig = new Rig();
        var catalog = rig.Catalog("default");
        var library = catalog.ResolveAnimations(catalog.ResolveModel("model.a", EntityKinds.Npc).Asset!, out var path);

        path.ShouldBe("themes/default/anims/humanoid.glb");
        library!.Clips["idle"].ShouldBe("Idle");
    }
}
