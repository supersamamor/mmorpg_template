using Game.Client.Core;
using Game.Client.Core.World;
using Godot;

namespace Game.Client.Scene;

/// <summary>Fixed-pitch 3/4 camera that follows a point (§7.2). Its numbers are client config; pinch or wheel zooms within a clamp.</summary>
public sealed class FollowCamera
{
    private readonly ClientConfig _config;
    private readonly float _sin;
    private readonly float _cos;
    private float _x;
    private float _z;
    private float _distance;

    /// <summary>Creates the camera under <paramref name="parent"/>, looking north.</summary>
    public FollowCamera(Node3D parent, ClientConfig config)
    {
        _config = config;
        var pitch = Mathf.DegToRad((float)config.CameraPitchDegrees);
        _sin = Mathf.Sin(pitch);
        _cos = Mathf.Cos(pitch);
        _distance = (float)config.CameraDistance;
        Camera = new Camera3D
        {
            Fov = (float)config.CameraFovDegrees,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Far = 100f,
            Current = true,
            RotationDegrees = new Vector3(-(float)config.CameraPitchDegrees, 0, 0),
        };
        parent.AddChild(Camera);
        Place();
    }

    /// <summary>The Godot camera.</summary>
    public Camera3D Camera { get; }

    /// <summary>The distance to the followed point.</summary>
    public float Distance => _distance;

    /// <summary>Follows a ground point. Does nothing when it has not moved.</summary>
    public void Follow(float x, float z)
    {
        if (x == _x && z == _z) return;
        _x = x;
        _z = z;
        Place();
    }

    /// <summary>Multiplies the distance by <paramref name="factor"/>, within the clamp.</summary>
    public void Zoom(double factor)
    {
        var next = Mathf.Clamp(_distance * (float)factor, (float)_config.CameraDistanceMin, (float)_config.CameraDistanceMax);
        if (next == _distance) return;
        _distance = next;
        Place();
    }

    /// <summary>The ground point under a canvas position, on the plane <c>y = 0</c>; false if the ray misses it.</summary>
    public bool TryGround(Vector2 canvas, out Vector3 ground)
    {
        var origin = Camera.ProjectRayOrigin(canvas);
        var direction = Camera.ProjectRayNormal(canvas);
        ground = default;
        if (direction.Y >= -1e-4f) return false;
        ground = origin + direction * (-origin.Y / direction.Y);
        return true;
    }

    /// <summary>The ground point under a canvas position on the terrain (<see cref="GroundHeights"/>); falls back to <see cref="TryGround"/> when the heights are flat. Its <c>Y</c> is the terrain height there.</summary>
    public bool TryTerrain(Vector2 canvas, GroundHeights ground, out Vector3 point)
    {
        point = default;
        var o = Camera.ProjectRayOrigin(canvas);
        var d = Camera.ProjectRayNormal(canvas);
        if (!TerrainRay.TryHit(ground, o.X, o.Y, o.Z, d.X, d.Y, d.Z, out var x, out var z)) return false;
        point = new Vector3((float)x, ground.At(x, z), (float)z);
        return true;
    }

    private void Place() => Camera.Position = new Vector3(_x, _distance * _sin, _z + _distance * _cos);
}
