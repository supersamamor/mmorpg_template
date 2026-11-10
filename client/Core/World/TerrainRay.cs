using Game.Shared.World;

namespace Game.Client.Core.World;

/// <summary>
/// Where a camera ray meets the terrain (§4, §7.3): the point of the ray whose height equals <see cref="GroundHeights.At"/> under it. A tap uses the cell under that point,
/// so a tap on a hill lands where the finger is, not where the flat plane <c>y = 0</c> would put it. Pure and allocation-free; the heights already answer it, so there is no physics query.
/// </summary>
public static class TerrainRay
{
    private const int Steps = 48; // marching steps across the height range, before bisecting the first crossing
    private const int Bisections = 14;

    /// <summary>
    /// The first point where the ray (from <paramref name="originY"/> heading down) is on or below the terrain. False if it never goes below the highest ground.
    /// Directions are in world units (x east, y up, z south); the direction need not be normalised.
    /// </summary>
    public static bool TryHit(GroundHeights ground, double originX, double originY, double originZ, double dirX, double dirY, double dirZ,
        out double hitX, out double hitZ)
    {
        hitX = hitZ = 0;
        if (dirY >= -1e-6) return false;
        var top = Math.Max(ground.MaxHeight, 0.0);
        var bottom = Math.Min(ground.MinHeight, 0.0);
        if (originY <= bottom) return false;
        // t along the ray where y == top (or the origin if it starts lower), and where y == bottom.
        var t0 = originY > top ? (top - originY) / dirY : 0.0;
        var t1 = (bottom - originY) / dirY;
        if (t1 < t0) return false;

        double Gap(double t) => originY + dirY * t - ground.At(originX + dirX * t, originZ + dirZ * t); // above the terrain > 0

        var low = t0;
        var high = t1;
        var previous = t0;
        for (var i = 1; i <= Steps; i++) // the last step is at the lowest ground, where the ray is always at or under the terrain, so a crossing is found
        {
            var t = t0 + (t1 - t0) * i / Steps;
            if (Gap(t) <= 0)
            {
                low = previous;
                high = t;
                break;
            }
            previous = t;
        }
        for (var i = 0; i < Bisections && high > low; i++)
        {
            var mid = (low + high) / 2;
            if (Gap(mid) <= 0) high = mid; else low = mid;
        }
        hitX = originX + dirX * high;
        hitZ = originZ + dirZ * high;
        return true;
    }
}
