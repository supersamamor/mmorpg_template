using System;

namespace Game.Client.Core.Ui;

/// <summary>Distances from the four screen edges, in whatever unit the caller works in (pixels or canvas units).</summary>
public readonly struct Insets
{
    /// <summary>Creates insets; negative values count as zero.</summary>
    public Insets(double left, double top, double right, double bottom)
    {
        Left = Math.Max(0, left);
        Top = Math.Max(0, top);
        Right = Math.Max(0, right);
        Bottom = Math.Max(0, bottom);
    }

    /// <summary>Distance from the left edge.</summary>
    public double Left { get; }

    /// <summary>Distance from the top edge.</summary>
    public double Top { get; }

    /// <summary>Distance from the right edge.</summary>
    public double Right { get; }

    /// <summary>Distance from the bottom edge.</summary>
    public double Bottom { get; }

    /// <summary>Each side times <paramref name="factor"/>.</summary>
    public Insets Scaled(double factor) => new(Left * factor, Top * factor, Right * factor, Bottom * factor);

    /// <summary>The larger inset on each side: two obscured areas together.</summary>
    public Insets Max(Insets other) => new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    /// <summary>True when every side is equal to within a rounding error.</summary>
    public bool Same(Insets other) =>
        Math.Abs(Left - other.Left) < 1e-6 && Math.Abs(Top - other.Top) < 1e-6 && Math.Abs(Right - other.Right) < 1e-6 && Math.Abs(Bottom - other.Bottom) < 1e-6;
}

/// <summary>The safe-area arithmetic (notches, rounded corners, the home indicator), free of the engine (T-211).</summary>
public static class SafeAreaMath
{
    /// <summary>
    /// Insets from a screen of <paramref name="screenWidth"/> x <paramref name="screenHeight"/> and the rectangle the OS calls safe inside it. A safe rectangle
    /// that is empty means the OS reported nothing useful, and gives no inset.
    /// </summary>
    public static Insets FromRects(double screenWidth, double screenHeight, double safeX, double safeY, double safeWidth, double safeHeight)
    {
        if (safeWidth <= 0 || safeHeight <= 0 || screenWidth <= 0 || screenHeight <= 0) return default;
        return new Insets(safeX, safeY, screenWidth - (safeX + safeWidth), screenHeight - (safeY + safeHeight));
    }

    /// <summary>
    /// The simulated notch for desktop development (<see cref="ClientConfig.DebugNotch"/>), in canvas units. A landscape phone has its camera cut-out
    /// on one short side and the home indicator at the bottom.
    /// </summary>
    public static Insets DebugNotch(ClientConfig config, double unitsPerDp) =>
        config.DebugNotch ? new Insets(config.DebugNotchLeftDp, config.DebugNotchTopDp, config.DebugNotchRightDp, config.DebugNotchBottomDp).Scaled(unitsPerDp) : default;

    /// <summary>
    /// The usable rectangle on a canvas after the insets. Insets that would leave less than half of either side (a corrupt report) are ignored and the
    /// whole canvas is used.
    /// </summary>
    public static (double X, double Y, double Width, double Height) SafeRect(double canvasWidth, double canvasHeight, Insets insets)
    {
        var width = canvasWidth - insets.Left - insets.Right;
        var height = canvasHeight - insets.Top - insets.Bottom;
        if (width < canvasWidth / 2 || height < canvasHeight / 2) return (0, 0, canvasWidth, canvasHeight);
        return (insets.Left, insets.Top, width, height);
    }
}

/// <summary>What the UI needs to know about the screen: its canvas size, how big a dp is on it, and what is obscured. Plain numbers, so layout can be tested without a screen.</summary>
public readonly struct ScreenMetrics
{
    /// <summary>Creates metrics.</summary>
    public ScreenMetrics(double canvasWidth, double canvasHeight, double unitsPerDp, UiPlatform platform, Insets obscured)
    {
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        UnitsPerDp = unitsPerDp > 0 ? unitsPerDp : 1;
        Platform = platform;
        Obscured = obscured;
    }

    /// <summary>Width of the canvas, in canvas units.</summary>
    public double CanvasWidth { get; }

    /// <summary>Height of the canvas, in canvas units.</summary>
    public double CanvasHeight { get; }

    /// <summary>Canvas units in one dp (or pt).</summary>
    public double UnitsPerDp { get; }

    /// <summary>The device family (the smallest touch target depends on it).</summary>
    public UiPlatform Platform { get; }

    /// <summary>The obscured edges (notch, home indicator), in canvas units.</summary>
    public Insets Obscured { get; }

    /// <summary>The safe rectangle, in canvas units.</summary>
    public (double X, double Y, double Width, double Height) Safe => SafeAreaMath.SafeRect(CanvasWidth, CanvasHeight, Obscured);

    /// <summary>The safe width in dp: what layout has to fit in, before the UI scale.</summary>
    public double SafeWidthDp => Safe.Width / UnitsPerDp;

    /// <summary>The safe height in dp.</summary>
    public double SafeHeightDp => Safe.Height / UnitsPerDp;

    /// <summary>True when every number is equal.</summary>
    public bool Same(ScreenMetrics other) =>
        Math.Abs(CanvasWidth - other.CanvasWidth) < 1e-6 && Math.Abs(CanvasHeight - other.CanvasHeight) < 1e-6 && Math.Abs(UnitsPerDp - other.UnitsPerDp) < 1e-9 &&
        Platform == other.Platform && Obscured.Same(other.Obscured);
}
