using System;

namespace Game.Client.Core.Ui;

/// <summary>The UI-scale setting: a value inside a configured range, and the steps the player cycles through (T-211).</summary>
public static class UiScale
{
    /// <summary>Clamps <paramref name="value"/> into the range. A value that is not a number gives <paramref name="fallback"/>; a range given the wrong way round is swapped.</summary>
    public static double Clamp(double value, double min, double max, double fallback)
    {
        if (min > max) (min, max) = (max, min);
        if (double.IsNaN(value) || double.IsInfinity(value)) value = fallback;
        return Math.Min(max, Math.Max(min, value));
    }

    /// <summary>How many steps the range has: <c>min, min + step, ...</c> up to <c>max</c>; at least one (the minimum alone).</summary>
    public static int StepCount(double min, double max, double step) => step > 0 && max > min ? (int)Math.Floor((max - min) / step + 1e-9) + 1 : 1;

    /// <summary>The scale after <paramref name="current"/> in the cycle; the last one goes back to the minimum. A value between steps goes to the next step above it.</summary>
    public static double Next(double current, double min, double max, double step)
    {
        var count = StepCount(min, max, step);
        for (var i = 0; i < count; i++)
        {
            var candidate = min + i * step;
            if (candidate > current + 1e-9) return candidate;
        }
        return min;
    }
}
