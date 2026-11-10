using Game.Client.Core.Input;
using Game.Client.Core.World;
using Game.Shared.World;

namespace Game.Tests.Client;

/// <summary>Tap picking on the terrain (T-219, §7.3): the ray meets the ground where the finger is, not where the flat plane y = 0 would put it.</summary>
public class TerrainRayTests
{
    // The sample hill of starter_meadow (T-208): a plateau 0.5 high over x 23..26, ramps 2 cells wide on each side, all along z 13..18.
    private const double PitchDegrees = 50; // the default camera (ClientConfig)
    private const double Distance = 15;

    private static GroundHeights SampleHill()
    {
        const int width = 48, height = 32;
        var heights = new float[width * height];
        var flags = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                flags[y * width + x] = CellFlagBits.Walkable;
                var cx = x + 0.5;
                heights[y * width + x] = y is >= 13 and < 18
                    ? (float)Math.Clamp(Math.Min(cx - 21, 27 - cx) * 0.25, 0, 0.5)
                    : 0f;
            }
        var ground = new GroundHeights();
        ground.Set(width, height, heights, flags);
        return ground;
    }

    // A ray from the follow camera (looking at ground point (lookX, lookZ)) through a world point.
    private static (double ox, double oy, double oz, double dx, double dy, double dz) RayThrough(double lookX, double lookZ, double x, double y, double z)
    {
        var pitch = PitchDegrees * Math.PI / 180;
        var ox = lookX;
        var oy = Distance * Math.Sin(pitch);
        var oz = lookZ + Distance * Math.Cos(pitch);
        return (ox, oy, oz, x - ox, y - oy, z - oz);
    }

    private static (double x, double z) FlatPlane((double ox, double oy, double oz, double dx, double dy, double dz) r)
    {
        var t = -r.oy / r.dy;
        return (r.ox + r.dx * t, r.oz + r.dz * t);
    }

    [Theory]
    [InlineData(24.5, 15.5)] // plateau centre
    [InlineData(23.1, 13.2)] // plateau, north-west corner
    [InlineData(25.9, 17.8)] // plateau, south-east corner
    [InlineData(22.5, 15.5)] // west ramp
    [InlineData(26.4, 14.0)] // east ramp
    public void A_tap_on_the_hill_lands_on_the_point_under_the_finger(double x, double z)
    {
        var ground = SampleHill();
        var y = ground.At(x, z);
        var ray = RayThrough(24.5, 15.5, x, y, z);

        TerrainRay.TryHit(ground, ray.ox, ray.oy, ray.oz, ray.dx, ray.dy, ray.dz, out var hx, out var hz).ShouldBeTrue();

        Math.Abs(hx - x).ShouldBeLessThan(0.01); // after
        Math.Abs(hz - z).ShouldBeLessThan(0.01);
    }

    [Fact]
    public void The_flat_plane_misses_the_plateau_by_almost_half_a_cell()
    {
        var ground = SampleHill();
        var ray = RayThrough(24.5, 15.5, 24.5, ground.At(24.5, 15.5), 15.5);

        var (px, pz) = FlatPlane(ray);

        px.ShouldBe(24.5, 1e-9);
        (15.5 - pz).ShouldBeGreaterThan(0.4); // before: 0.5 high, seen at 50 degrees: the flat plane puts the tap 0.44 cells beyond the monster or cell, away from the camera
        (15.5 - pz).ShouldBeLessThan(0.5);
    }

    [Fact]
    public void A_tap_near_a_cell_edge_on_the_plateau_picks_the_right_cell_only_on_the_terrain()
    {
        var ground = SampleHill();
        var ray = RayThrough(24.5, 15.5, 24.5, 0.5, 15.2); // inside cell z = 15, 0.2 from its north edge

        TerrainRay.TryHit(ground, ray.ox, ray.oy, ray.oz, ray.dx, ray.dy, ray.dz, out _, out var hz).ShouldBeTrue();

        TouchTargets.CellOf(hz).ShouldBe(15);
        TouchTargets.CellOf(FlatPlane(ray).z).ShouldBe(14); // the old plane picked the cell beyond
    }

    [Fact]
    public void Flat_ground_gives_the_plane_hit()
    {
        var ground = new GroundHeights();
        var ray = RayThrough(10, 10, 12.3, 0, 9.1);

        TerrainRay.TryHit(ground, ray.ox, ray.oy, ray.oz, ray.dx, ray.dy, ray.dz, out var hx, out var hz).ShouldBeTrue();

        var (px, pz) = FlatPlane(ray);
        hx.ShouldBe(px, 1e-3);
        hz.ShouldBe(pz, 1e-3);
    }

    [Fact]
    public void A_ray_that_does_not_point_down_misses()
    {
        TerrainRay.TryHit(SampleHill(), 0, 5, 0, 0, 0.1, -1, out _, out _).ShouldBeFalse();
        TerrainRay.TryHit(SampleHill(), 0, 5, 0, 0, 0, -1, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Max_and_min_height_follow_the_walkable_cells()
    {
        var ground = SampleHill();
        ground.MaxHeight.ShouldBe(0.5f);
        ground.MinHeight.ShouldBe(0f);
        ground.Clear();
        ground.MaxHeight.ShouldBe(0f);
    }
}
