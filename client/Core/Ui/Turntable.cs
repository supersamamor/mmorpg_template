using System;

namespace Game.Client.Core.Ui;

/// <summary>
/// How the creation preview turns (T-706): by itself, and by a drag, which holds it until the finger lifts. Pure: time and drag distances come in.
/// </summary>
public sealed class Turntable
{
    private readonly double _degreesPerSecond;
    private readonly double _dragDegrees;

    /// <summary>Creates a turntable that faces the viewer.</summary>
    /// <param name="degreesPerSecond">How fast it turns by itself.</param>
    /// <param name="dragDegrees">How far a drag across the whole width turns it.</param>
    public Turntable(double degreesPerSecond, double dragDegrees)
    {
        _degreesPerSecond = degreesPerSecond;
        _dragDegrees = dragDegrees;
    }

    /// <summary>The angle, 0 to 360 degrees: 0 faces the viewer.</summary>
    public double Degrees { get; private set; }

    /// <summary>True while a finger holds it.</summary>
    public bool Held { get; private set; }

    /// <summary>A finger went down on it.</summary>
    public void Press() => Held = true;

    /// <summary>The finger moved by <paramref name="distance"/> of the preview's <paramref name="width"/> (both in the same unit); nothing while no finger is down.</summary>
    public void Drag(double distance, double width)
    {
        if (!Held || width <= 0) return;
        Turn(distance / width * _dragDegrees);
    }

    /// <summary>The finger lifted: it turns by itself again from where it is.</summary>
    public void Release() => Held = false;

    /// <summary>Faces the viewer again.</summary>
    public void Reset()
    {
        Degrees = 0;
        Held = false;
    }

    /// <summary>Time passed.</summary>
    public void Advance(double seconds)
    {
        if (!Held) Turn(seconds * _degreesPerSecond);
    }

    private void Turn(double degrees)
    {
        var next = (Degrees + degrees) % 360;
        Degrees = next < 0 ? next + 360 : next;
    }
}
