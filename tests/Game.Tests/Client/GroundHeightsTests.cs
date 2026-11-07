using Game.Client.Core.Theme;
using Game.Client.Core.World;
using Game.Shared.World;

namespace Game.Tests.Client;

/// <summary>Standing on the terrain (T-208, §4): pure height blending, and the scene key's place in the fallback chain.</summary>
public class GroundHeightsTests
{
    private static readonly byte Open = CellFlagBits.Walkable;

    private static GroundHeights Ground(float[] heights, byte[] flags, int width = 2, int height = 2)
    {
        var ground = new GroundHeights();
        ground.Set(width, height, heights, flags);
        return ground;
    }

    [Fact]
    public void Flat_until_heights_arrive()
    {
        new GroundHeights().At(3.2, 4.7).ShouldBe(0f);
    }

    [Fact]
    public void A_cell_centre_has_its_own_height()
    {
        var ground = Ground(new[] { 1f, 2f, 3f, 4f }, new[] { Open, Open, Open, Open });

        ground.At(0.5, 0.5).ShouldBe(1f);
        ground.At(1.5, 1.5).ShouldBe(4f);
    }

    [Fact]
    public void Between_centres_it_blends()
    {
        var ground = Ground(new[] { 0f, 2f, 0f, 2f }, new[] { Open, Open, Open, Open });

        ground.At(1.0, 0.5).ShouldBe(1f, 1e-5f);
        ground.At(1.0, 1.0).ShouldBe(1f, 1e-5f);
    }

    [Fact]
    public void Blocked_cells_do_not_lift_a_character_beside_them()
    {
        var ground = Ground(new[] { 0.25f, 5f, 0.25f, 5f }, new byte[] { Open, 0, Open, 0 });

        ground.At(1.2, 1.0).ShouldBe(0.25f, 1e-5f);
    }

    [Fact]
    public void Off_the_map_is_zero_and_the_edge_uses_what_exists()
    {
        var ground = Ground(new[] { 1f, 1f, 1f, 1f }, new[] { Open, Open, Open, Open });

        ground.At(50, 50).ShouldBe(0f);
        ground.At(0.1, 0.1).ShouldBe(1f, 1e-5f);
    }

    [Fact]
    public void Every_change_moves_the_version()
    {
        var ground = new GroundHeights();
        var start = ground.Version;

        ground.Set(1, 1, new[] { 1f }, new[] { Open });
        ground.Version.ShouldBe(start + 1);

        ground.Clear();
        ground.Version.ShouldBe(start + 2);
        ground.At(0.5, 0.5).ShouldBe(0f);
    }

    private static ThemeCatalog Catalog(List<string> warnings, string json, Func<string, bool> exists)
    {
        ThemeManifest.TryParse("default", json, out var manifest, out var error).ShouldBeTrue(error);
        return new ThemeCatalog(new[] { manifest! }, exists, warnings.Add);
    }

    [Fact]
    public void A_scene_key_resolves_through_the_theme()
    {
        var warnings = new List<string>();
        var catalog = Catalog(warnings, """{ "scene.map.a": { "file": "maps/a.tscn" } }""", _ => true);

        catalog.ResolveScene("scene.map.a", out var path).ShouldBeTrue();

        path.ShouldBe("themes/default/maps/a.tscn");
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_scene_warns_once_and_says_so()
    {
        var warnings = new List<string>();
        var catalog = Catalog(warnings, """{ "scene.map.a": { "file": "maps/a.tscn" } }""", _ => false);

        catalog.ResolveScene("scene.map.a", out _).ShouldBeFalse();
        catalog.ResolveScene("scene.map.a", out _).ShouldBeFalse();

        warnings.ShouldHaveSingleItem().ShouldContain("scene.map.a");
    }
}
