using System;

namespace Game.Client.Core.Input;

/// <summary>What a touch produced.</summary>
public enum GestureResult
{
    /// <summary>Nothing yet.</summary>
    None,

    /// <summary>A tap at <see cref="TapGestures.TapX"/>, <see cref="TapGestures.TapY"/>.</summary>
    Tap,

    /// <summary>A pinch changed: multiply the camera distance by <see cref="TapGestures.ZoomFactor"/>.</summary>
    Zoom,

    /// <summary>A long-press at <see cref="TapGestures.LongPressX"/>, <see cref="TapGestures.LongPressY"/>. It fires once, and the release that ends it is no tap.</summary>
    LongPress,
}

/// <summary>
/// Turns presses, drags, releases and the passing of time into taps, long-presses and pinches: the one input path for mouse and touch (a mouse arrives as touch
/// index 0). One finger down and still is a tap on release or a long-press after <see cref="ClientConfig.LongPressSeconds"/>; a finger that travels more than
/// <see cref="ClientConfig.TapMaxTravel"/> is a drag and neither; a second finger makes it a pinch, which never taps. Distances are in dp times
/// <see cref="UnitsPerDp"/>. Tracks up to two fingers in plain fields, so it allocates nothing.
/// </summary>
public sealed class TapGestures
{
    private readonly ClientConfig _config;
    private int _activeA = -1;
    private int _activeB = -1;
    private double _ax, _ay, _bx, _by;
    private double _startX, _startY, _startTime;
    private double _pinchDistance;
    private double _pinchStart;
    private bool _pinching;
    private bool _tapPossible;
    private bool _longPressFired;

    /// <summary>Creates a tracker.</summary>
    public TapGestures(ClientConfig config) => _config = config;

    /// <summary>Canvas units in one dp: the thresholds in dp are multiplied by it. 1 until the screen is known.</summary>
    public double UnitsPerDp { get; set; } = 1;

    /// <summary>The last tap's X.</summary>
    public double TapX { get; private set; }

    /// <summary>The last tap's Y.</summary>
    public double TapY { get; private set; }

    /// <summary>The last long-press's X.</summary>
    public double LongPressX { get; private set; }

    /// <summary>The last long-press's Y.</summary>
    public double LongPressY { get; private set; }

    /// <summary>The last pinch's distance factor: above 1 moves the camera away.</summary>
    public double ZoomFactor { get; private set; } = 1;

    /// <summary>True while one finger is down and could still become a tap or a long-press; the per-frame <see cref="Tick"/> is only needed then.</summary>
    public bool Waiting => _activeA >= 0 && _activeB < 0 && _tapPossible && !_longPressFired;

    /// <summary>A finger went down.</summary>
    public GestureResult Press(int index, double x, double y, double now)
    {
        if (_activeA < 0)
        {
            _activeA = index;
            _ax = x;
            _ay = y;
            _startX = x;
            _startY = y;
            _startTime = now;
            _tapPossible = true;
            _longPressFired = false;
            _pinching = false;
        }
        else if (_activeB < 0 && index != _activeA)
        {
            _activeB = index;
            _bx = x;
            _by = y;
            _tapPossible = false;
            _pinchDistance = Distance();
            _pinchStart = _pinchDistance;
            _pinching = false;
        }
        return GestureResult.None;
    }

    /// <summary>A finger moved.</summary>
    public GestureResult Drag(int index, double x, double y)
    {
        if (index == _activeA)
        {
            _ax = x;
            _ay = y;
        }
        else if (index == _activeB)
        {
            _bx = x;
            _by = y;
        }
        else
        {
            return GestureResult.None;
        }

        if (_activeB < 0)
        {
            if (TravelledTooFar(x, y)) _tapPossible = false;
            return GestureResult.None;
        }

        var distance = Distance();
        if (_pinchDistance <= 1 || distance <= 1) return GestureResult.None;
        if (!_pinching)
        {
            if (Math.Abs(distance - _pinchStart) <= _config.PinchDeadZone * UnitsPerDp) return GestureResult.None;
            _pinching = true;
        }
        ZoomFactor = _pinchDistance / distance; // fingers apart: closer
        _pinchDistance = distance;
        return GestureResult.Zoom;
    }

    /// <summary>
    /// Time passed with the fingers where they are. Returns <see cref="GestureResult.LongPress"/> once when a lone finger has stayed put for the long-press time.
    /// Call it while <see cref="Waiting"/>; at other times it does nothing.
    /// </summary>
    public GestureResult Tick(double now)
    {
        if (!Waiting || now - _startTime < _config.LongPressSeconds) return GestureResult.None;
        return FireLongPress();
    }

    /// <summary>A finger went up.</summary>
    public GestureResult Release(int index, double x, double y, double now)
    {
        if (index == _activeB)
        {
            _activeB = -1;
            return GestureResult.None;
        }
        if (index != _activeA) return GestureResult.None;
        if (_activeB >= 0)
        {
            // The first finger left a pinch: the second one takes over and can no longer tap.
            _activeA = _activeB;
            _ax = _bx;
            _ay = _by;
            _activeB = -1;
            _tapPossible = false;
            return GestureResult.None;
        }

        _activeA = -1;
        if (!_tapPossible || _longPressFired || TravelledTooFar(x, y)) return GestureResult.None;
        var held = now - _startTime;
        // Held long enough, but no Tick came in between (a stalled frame): it was a long-press all the same, and never a tap.
        if (held >= _config.LongPressSeconds) return FireLongPress();
        TapX = x;
        TapY = y;
        return GestureResult.Tap;
    }

    private GestureResult FireLongPress()
    {
        _longPressFired = true;
        _tapPossible = false;
        LongPressX = _startX;
        LongPressY = _startY;
        return GestureResult.LongPress;
    }

    private bool TravelledTooFar(double x, double y)
    {
        var dx = x - _startX;
        var dy = y - _startY;
        var limit = _config.TapMaxTravel * UnitsPerDp;
        return dx * dx + dy * dy > limit * limit;
    }

    private double Distance()
    {
        var dx = _ax - _bx;
        var dy = _ay - _by;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Picking rules that need no engine.</summary>
public static class TouchTargets
{
    /// <summary>Density the Android dp is defined at.</summary>
    private const double BaseDpi = 160;

    /// <summary>
    /// Half of a <paramref name="targetDp"/> touch target in canvas units. A phone's dpi and the stretch from window pixels to canvas units both
    /// change it, so a 48 dp target stays 48 dp wide on any screen. A device reporting no dpi counts as the base density.
    /// </summary>
    public static double RadiusInCanvasUnits(double targetDp, double dpi, double windowHeightPixels, double canvasHeightUnits)
    {
        var pixelsPerDp = (dpi > 0 ? dpi : BaseDpi) / BaseDpi;
        var unitsPerPixel = windowHeightPixels > 0 ? canvasHeightUnits / windowHeightPixels : 1;
        return targetDp * pixelsPerDp * unitsPerPixel / 2;
    }

    /// <summary>The cell under a ground point: cell <c>(x, y)</c> covers world <c>[x, x + 1) × [y, y + 1)</c> (ARCHITECTURE §4).</summary>
    public static int CellOf(double world) => (int)Math.Floor(world);
}
