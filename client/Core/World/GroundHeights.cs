using System;
using Game.Shared.World;

namespace Game.Client.Core.World;

/// <summary>
/// How high the terrain is under a point (§4): one sampled height per walkable cell, blended between cell centres. Blocked cells do not count, so a character beside a wall does not climb its top.
/// Purely visual: nothing here reaches the server. Reading allocates nothing.
/// </summary>
public sealed class GroundHeights
{
    private int _width;
    private int _height;
    private float[] _heights = Array.Empty<float>();
    private byte[] _flags = Array.Empty<byte>();

    /// <summary>Goes up when the heights change, so whoever placed things on the ground places them again.</summary>
    public int Version { get; private set; }

    /// <summary>Back to flat ground: every point has height 0.</summary>
    public void Clear()
    {
        _width = _height = 0;
        Version++;
    }

    /// <summary>Takes a map's heights. <paramref name="heights"/> and <paramref name="flags"/> hold one value per cell, row-major.</summary>
    public void Set(int width, int height, float[] heights, byte[] flags)
    {
        _width = width;
        _height = height;
        _heights = heights;
        _flags = flags;
        Version++;
    }

    /// <summary>The ground's height at a world point; 0 where there is no walkable cell near it.</summary>
    public float At(double x, double z)
    {
        if (_width == 0) return 0;
        var fx = x - 0.5;
        var fz = z - 0.5;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fz);
        var tx = fx - x0;
        var tz = fz - y0;
        double sum = 0, weight = 0;
        Add(x0, y0, (1 - tx) * (1 - tz), ref sum, ref weight);
        Add(x0 + 1, y0, tx * (1 - tz), ref sum, ref weight);
        Add(x0, y0 + 1, (1 - tx) * tz, ref sum, ref weight);
        Add(x0 + 1, y0 + 1, tx * tz, ref sum, ref weight);
        return weight > 0 ? (float)(sum / weight) : 0;
    }

    private void Add(int x, int y, double w, ref double sum, ref double weight)
    {
        if (w <= 0 || x < 0 || y < 0 || x >= _width || y >= _height) return;
        var i = y * _width + x;
        if ((_flags[i] & CellFlagBits.Walkable) == 0) return;
        sum += _heights[i] * w;
        weight += w;
    }
}
