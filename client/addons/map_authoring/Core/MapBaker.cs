using System;
using Game.Shared.World;

namespace Game.Client.MapAuthoring;

/// <summary>What the downward ray of one cell found.</summary>
/// <param name="Hit">It hit a collision shape. A cell with nothing under it is not walkable.</param>
/// <param name="NormalY">The up component of the surface's unit normal.</param>
/// <param name="SurfaceWalkable">False when the body it hit has <c>map_walkable = false</c> (water, a wall top).</param>
/// <param name="SurfaceBlocksLos">True when the body it hit has <c>map_blocks_los = true</c>.</param>
public readonly record struct CellRayHit(bool Hit, double NormalY, bool SurfaceWalkable, bool SurfaceBlocksLos)
{
    /// <summary>Nothing under the cell.</summary>
    public static readonly CellRayHit Miss = new CellRayHit(false, 0, false, false);
}

/// <summary>
/// The bake (ARCHITECTURE §5.3): one downward ray per cell, in a fixed order, through the centre of the cell. The ray itself is the engine's and comes in as a delegate, so everything else is pure and
/// nothing about the editor (camera, selection) can reach it.
/// </summary>
public static class MapBaker
{
    // The engine's normals are 32-bit floats: a surface at exactly the limit must not depend on their last bit.
    private const double SlopeTolerance = 1e-6;

    /// <summary>The ray's start height. Anything authored above it is not seen.</summary>
    public const double RayTop = 512;

    /// <summary>The ray's end height.</summary>
    public const double RayBottom = -512;

    /// <summary>One flag byte per cell, row-major: <paramref name="ray"/> gets the world x and z of each cell's centre.</summary>
    public static byte[] Bake(int width, int height, double slopeLimitDegrees, Func<double, double, CellRayHit> ray)
    {
        var flags = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                flags[y * width + x] = Flags(ray(x + 0.5, y + 0.5), slopeLimitDegrees);
        return flags;
    }

    /// <summary>The flags of one ray's result: walkable when it hit a walkable surface no steeper than the limit.</summary>
    public static byte Flags(CellRayHit hit, double slopeLimitDegrees)
    {
        if (!hit.Hit) return 0;
        byte flags = 0;
        if (hit.SurfaceWalkable && hit.NormalY + SlopeTolerance >= Math.Cos(slopeLimitDegrees * Math.PI / 180)) flags |= CellFlagBits.Walkable;
        if (hit.SurfaceBlocksLos) flags |= CellFlagBits.BlocksLos;
        return flags;
    }
}
