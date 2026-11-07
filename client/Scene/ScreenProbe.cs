using Game.Client.Core;
using Game.Client.Core.Ui;
using Godot;

namespace Game.Client.Scene;

/// <summary>Reads the screen from the engine into the plain numbers the UI rules work with (<see cref="ScreenMetrics"/>).</summary>
public static class ScreenProbe
{
    /// <summary>The platform this process runs on.</summary>
    public static UiPlatform Platform { get; } = TouchTargetRules.PlatformOf(OS.GetName()); // once: asking the engine for its name allocates a string

    /// <summary>True on a phone, where the OS reports a safe area worth trusting.</summary>
    public static bool IsPhone => Platform != UiPlatform.Desktop;

    /// <summary>The OS's safe area in pixels, or an empty rectangle on a desktop (where it is the whole monitor, not the window).</summary>
    public static Rect2I OsSafeArea() => IsPhone ? DisplayServer.GetDisplaySafeArea() : default;

    /// <summary>
    /// The screen as it is now. The simulated notch counts only in a debug build, so a <c>client_settings.json</c> edited after install cannot take space
    /// from a release build's screen.
    /// </summary>
    public static ScreenMetrics Read(Viewport viewport, Window window, ClientConfig config)
    {
        var platform = Platform;
        var canvas = viewport.GetVisibleRect().Size;
        var windowSize = window.Size;
        // A headless run has no real window (the engine gives it 64 pixels), so there is no stretch to undo.
        var windowRows = DisplayServer.GetName() == "headless" ? canvas.Y : windowSize.Y;
        var unitsPerDp = TouchTargetRules.UnitsPerDp(platform, DisplayServer.ScreenGetDpi(), windowRows, canvas.Y);

        var obscured = default(Insets);
        if (IsPhone)
        {
            var screen = DisplayServer.ScreenGetSize();
            var safe = DisplayServer.GetDisplaySafeArea();
            var pixels = SafeAreaMath.FromRects(screen.X, screen.Y, safe.Position.X, safe.Position.Y, safe.Size.X, safe.Size.Y);
            obscured = pixels.Scaled(windowSize.Y > 0 ? canvas.Y / windowSize.Y : 1);
        }
        if (OS.IsDebugBuild()) obscured = obscured.Max(SafeAreaMath.DebugNotch(config, unitsPerDp));
        return new ScreenMetrics(canvas.X, canvas.Y, unitsPerDp, platform, obscured);
    }
}
