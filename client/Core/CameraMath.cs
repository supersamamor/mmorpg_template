using System;

namespace Game.Client.Core;

/// <summary>The ground a fixed-pitch camera shows, around the point it looks at. All in cells, since 1 cell = 1 world unit.</summary>
public readonly struct VisibleGround
{
    /// <summary>Creates a measurement.</summary>
    public VisibleGround(double farHalfWidth, double middleHalfWidth, double nearHalfWidth, double far, double near)
    {
        FarHalfWidth = farHalfWidth;
        MiddleHalfWidth = middleHalfWidth;
        NearHalfWidth = nearHalfWidth;
        Far = far;
        Near = near;
    }

    /// <summary>Half the width along the top of the screen.</summary>
    public double FarHalfWidth { get; }

    /// <summary>Half the width through the player.</summary>
    public double MiddleHalfWidth { get; }

    /// <summary>Half the width along the bottom of the screen.</summary>
    public double NearHalfWidth { get; }

    /// <summary>From the player to the top of the screen.</summary>
    public double Far { get; }

    /// <summary>From the player to the bottom of the screen.</summary>
    public double Near { get; }

    /// <summary>The farthest any visible ground is from the player on one axis (Chebyshev), which is what the AOI's reach must cover.</summary>
    public double Reach => Math.Max(Math.Max(FarHalfWidth, NearHalfWidth), Math.Max(Far, Near));
}

/// <summary>The camera's geometry: where it sits and what it shows. Godot's vertical field of view is fixed; the horizontal one follows the aspect.</summary>
public static class CameraMath
{
    /// <summary>The ground visible for a camera looking at the plane from <paramref name="distance"/>, <paramref name="pitchDegrees"/> below the horizon.</summary>
    public static VisibleGround Visible(double pitchDegrees, double fovDegrees, double distance, double aspect)
    {
        var pitch = pitchDegrees * Math.PI / 180;
        var half = fovDegrees * Math.PI / 360;
        var height = distance * Math.Sin(pitch);
        var target = distance * Math.Cos(pitch);
        var tanHalf = Math.Tan(half);

        double Depth(double below) => height * Math.Cos(half) / Math.Sin(below);

        var far = height / Math.Tan(pitch - half) - target;
        var near = target - height / Math.Tan(pitch + half);
        var perDepth = aspect * tanHalf;
        return new VisibleGround(perDepth * Depth(pitch - half), perDepth * distance, perDepth * Depth(pitch + half), far, near);
    }
}
