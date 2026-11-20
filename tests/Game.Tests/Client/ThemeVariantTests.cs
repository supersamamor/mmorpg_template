using Game.Client.Core.Theme;
using Game.Shared.Protocol;

namespace Game.Tests.Client;

/// <summary>
/// Manifest variants on the client (T-224, docs/THEMES.md "Variants"): a key that draws another key's model with a tint, a scale and an outline override, resolved
/// through the theme chain. Pure logic, no engine and no files.
/// </summary>
public class ThemeVariantTests
{
    private const string Default = """
        {
          "model.monster.slime": { "file": "models/slime.glb", "rig": "blob", "anims": "anim.blob", "impact_delay": 0.3 },
          "model.monster.slime_elite": { "variant_of": "model.monster.slime", "tint": "#ff8000", "scale": 1.5, "outline": "#200000" },
          "model.monster.slime_king": { "variant_of": "model.monster.slime_elite", "tint": "#808080", "scale": 2 },
          "model.monster.slime_ghost": { "variant_of": "model.monster.slime", "outline": false },
          "model.monster.alias": { "variant_of": "model.monster.slime" },
          "model.monster.loop_a": { "variant_of": "model.monster.loop_b" },
          "model.monster.loop_b": { "variant_of": "model.monster.loop_a" },
          "model.monster.orphan": { "variant_of": "model.monster.nothing" },
          "anim.blob": { "file": "anims/blob.glb", "rig": "blob", "clips": { "idle": "Idle" } },
          "model.missing": { "file": "models/missing.glb" },
          "fallbacks": { "missing": "model.missing" }
        }
        """;

    private const string Fork = """
        {
          "model.monster.slime": { "file": "models/goo.glb", "rig": "blob", "anims": "anim.blob" }
        }
        """;

    private readonly List<string> _warnings = new();

    private ThemeCatalog Catalog(bool fork = false)
    {
        var layers = new List<ThemeManifest>();
        if (fork) layers.Add(Parse("fork", Fork));
        layers.Add(Parse("default", Default));
        var files = new HashSet<string>(StringComparer.Ordinal) { "themes/default/models/slime.glb", "themes/default/anims/blob.glb", "themes/default/models/missing.glb", "themes/fork/models/goo.glb" };
        return new ThemeCatalog(layers, files.Contains, _warnings.Add);
    }

    private static ThemeManifest Parse(string theme, string json)
    {
        ThemeManifest.TryParse(theme, json, out var manifest, out var error).ShouldBeTrue(error);
        return manifest!;
    }

    [Fact]
    public void A_variant_draws_its_base_file_with_its_own_tint_scale_and_outline()
    {
        var model = Catalog().ResolveModel("model.monster.slime_elite", EntityKinds.Monster);
        model.Level.ShouldBe(ResolutionLevel.Exact);
        model.Key.ShouldBe("model.monster.slime_elite");
        model.Path.ShouldBe("themes/default/models/slime.glb");
        var asset = model.Asset!;
        asset.Rig.ShouldBe("blob");
        asset.Anims.ShouldBe("anim.blob");
        asset.VariantOf.ShouldBe("model.monster.slime");
        asset.Tint.ShouldBe(new ThemeColor(1, 128 / 255f, 0));
        asset.Scale.ShouldBe(1.5);
        asset.OutlineColor.ShouldBe(new ThemeColor(32 / 255f, 0, 0));
        asset.OutlineOff.ShouldBeFalse();
        _warnings.ShouldBeEmpty();
    }

    [Fact]
    public void A_variant_of_a_variant_multiplies_tints_and_scales_and_keeps_the_nearest_outline()
    {
        var asset = Catalog().ResolveModel("model.monster.slime_king", EntityKinds.Monster).Asset!;
        asset.Scale.ShouldBe(3);
        asset.Tint!.Value.R.ShouldBe(128 / 255f, 1e-6);
        asset.Tint!.Value.G.ShouldBe(128 / 255f * 128 / 255f, 1e-6);
        asset.Tint!.Value.B.ShouldBe(0);
        asset.OutlineColor.ShouldBe(new ThemeColor(32 / 255f, 0, 0));
        asset.VariantOf.ShouldBe("model.monster.slime");
    }

    [Fact]
    public void A_variant_can_turn_the_outline_off_and_a_plain_alias_is_drawn_exactly_like_its_base()
    {
        var catalog = Catalog();
        catalog.ResolveModel("model.monster.slime_ghost", EntityKinds.Monster).Asset!.OutlineOff.ShouldBeTrue();
        var alias = catalog.ResolveModel("model.monster.alias", EntityKinds.Monster);
        var plain = catalog.ResolveModel("model.monster.slime", EntityKinds.Monster);
        alias.Asset!.Restyled.ShouldBeFalse();
        alias.PoolKey.ShouldBe(plain.PoolKey); // idle instances are shared
        catalog.ResolveModel("model.monster.slime_elite", EntityKinds.Monster).PoolKey.ShouldNotBe(plain.PoolKey); // a pooled slime never comes back as an elite
    }

    [Fact]
    public void A_variant_takes_the_impact_delay_of_its_base_unless_it_has_its_own()
    {
        var catalog = Catalog();
        catalog.ImpactDelayOf(catalog.ResolveModel("model.monster.slime_elite", EntityKinds.Monster), 0.2).ShouldBe(0.3);
    }

    [Fact]
    public void The_base_is_found_through_the_chain_so_a_fork_that_redraws_the_base_redraws_its_variants()
    {
        var model = Catalog(fork: true).ResolveModel("model.monster.slime_elite", EntityKinds.Monster);
        model.Path.ShouldBe("themes/fork/models/goo.glb");
        model.Asset!.Tint.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("model.monster.loop_a")]
    [InlineData("model.monster.orphan")]
    public void A_variant_whose_base_cannot_be_found_falls_back_with_one_warning_that_says_why(string key)
    {
        var catalog = Catalog();
        catalog.ResolveModel(key, EntityKinds.Monster).Level.ShouldBe(ResolutionLevel.MissingFallback);
        catalog.ResolveModel(key, EntityKinds.Monster);
        _warnings.Count(w => w.Contains(key)).ShouldBe(1);
        _warnings.Single(w => w.Contains(key)).ShouldContain("variant of");
    }

    [Theory]
    [InlineData("#ff8000", true)]
    [InlineData("#FF8000", true)]
    [InlineData("ff8000", false)]
    [InlineData("#ff80", false)]
    [InlineData("#gg8000", false)]
    public void Colours_are_hash_and_six_hex_digits(string text, bool ok)
    {
        ThemeColor.TryParse(text, out var color).ShouldBe(ok);
        if (ok) color.ToString().ShouldBe(text.ToLowerInvariant());
    }
}
