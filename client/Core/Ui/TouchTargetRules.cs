using System;

namespace Game.Client.Core.Ui;

/// <summary>The device family, which decides the smallest touch target (ARCHITECTURE §7.4).</summary>
public enum UiPlatform
{
    /// <summary>A desktop build, for development only. It uses the Android size.</summary>
    Desktop,

    /// <summary>Android: density-independent pixels (dp), 1/160 inch.</summary>
    Android,

    /// <summary>iOS: points (pt), 1/163 inch.</summary>
    Ios,
}

/// <summary>Touch-target sizes and the dp to canvas conversion, as pure rules (T-211).</summary>
public static class TouchTargetRules
{
    /// <summary>Smallest touch target on Android, in dp.</summary>
    public const double AndroidMinDp = 48;

    /// <summary>Smallest touch target on iOS, in pt.</summary>
    public const double IosMinPt = 44;

    /// <summary>Density Android's dp is defined at.</summary>
    public const double AndroidBaseDpi = 160;

    /// <summary>Density iOS's point is defined at.</summary>
    public const double IosBaseDpi = 163;

    /// <summary>The platform for the engine's operating-system name. Anything that is not a phone is a desktop.</summary>
    public static UiPlatform PlatformOf(string osName) => osName switch
    {
        "Android" => UiPlatform.Android,
        "iOS" => UiPlatform.Ios,
        _ => UiPlatform.Desktop,
    };

    /// <summary>The smallest touch target on a platform, in its own unit (dp or pt).</summary>
    public static double MinTarget(UiPlatform platform) => platform == UiPlatform.Ios ? IosMinPt : AndroidMinDp;

    /// <summary>The density one dp or pt is defined at.</summary>
    public static double BaseDpi(UiPlatform platform) => platform == UiPlatform.Ios ? IosBaseDpi : AndroidBaseDpi;

    /// <summary>
    /// The density to size the UI by. A phone reports its own (0 counts as the base density). A desktop monitor reports a number that says little about
    /// how big a window should look, so it never goes below the base density: a development build stays usable.
    /// </summary>
    public static double EffectiveDpi(UiPlatform platform, double reportedDpi)
    {
        var baseDpi = BaseDpi(platform);
        if (platform == UiPlatform.Desktop) return Math.Max(reportedDpi, baseDpi);
        return reportedDpi > 0 ? reportedDpi : baseDpi;
    }

    /// <summary>
    /// Canvas units in one dp (or pt). The window is stretched to a canvas of <paramref name="canvasHeightUnits"/> rows, so a phone's density and the
    /// stretch both change it. Zero or negative sizes count as no stretch.
    /// </summary>
    public static double UnitsPerDp(UiPlatform platform, double reportedDpi, double windowHeightPixels, double canvasHeightUnits)
    {
        var pixelsPerDp = EffectiveDpi(platform, reportedDpi) / BaseDpi(platform);
        var unitsPerPixel = windowHeightPixels > 0 && canvasHeightUnits > 0 ? canvasHeightUnits / windowHeightPixels : 1;
        return pixelsPerDp * unitsPerPixel;
    }

    /// <summary>
    /// The size of a touch target that should be <paramref name="wantedDp"/> at the current UI scale: scaled, but never below the platform's minimum,
    /// because a small UI scale must not make buttons smaller than a thumb.
    /// </summary>
    public static double TargetDp(UiPlatform platform, double wantedDp, double uiScale) => Math.Max(MinTarget(platform), wantedDp * uiScale);

    /// <summary>True if a target of <paramref name="sizeDp"/> on each side meets the platform's minimum.</summary>
    public static bool Meets(UiPlatform platform, double sizeDp) => sizeDp >= MinTarget(platform) - 1e-9;

    /// <summary>
    /// The picking radius for monsters and other world things, in canvas units: half the larger of the platform minimum and the configured size
    /// (<paramref name="configuredDp"/>), so picking is never smaller than the UI's own targets.
    /// </summary>
    public static double PickRadius(UiPlatform platform, double configuredDp, double unitsPerDp) => Math.Max(MinTarget(platform), configuredDp) * unitsPerDp / 2;
}
