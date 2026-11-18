using System.Numerics;
using System.Text;

namespace Game.ThemeGen;

/// <summary>Per-theme look: the same shapes in different colours, so a swap is visible.</summary>
internal sealed record ThemeStyle(string Salt, float HueShift);

/// <summary>Colours chosen from a model's key, so every model is tellable apart and the output is reproducible.</summary>
internal static class KeyColor
{
    /// <summary>FNV-1a, 32 bit. Not <see cref="string.GetHashCode()"/>, which changes per process.</summary>
    public static uint Hash(string text)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            h ^= b;
            h *= 16777619u;
        }
        return h;
    }

    public static Vector4 Of(string key, ThemeStyle style)
    {
        var h = Hash(style.Salt + key);
        var hue = ((h % 360u) / 360f + style.HueShift) % 1f;
        var sat = 0.55f + ((h >> 9) % 20u) / 100f;
        var val = 0.78f + ((h >> 15) % 17u) / 100f;
        return Hsv(hue, sat, val);
    }

    private static Vector4 Hsv(float h, float s, float v)
    {
        var i = (int)MathF.Floor(h * 6f);
        var f = h * 6f - i;
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        return (i % 6) switch
        {
            0 => new Vector4(v, t, p, 1),
            1 => new Vector4(q, v, p, 1),
            2 => new Vector4(p, v, t, 1),
            3 => new Vector4(p, q, v, 1),
            4 => new Vector4(t, p, v, 1),
            _ => new Vector4(v, p, q, 1),
        };
    }
}

/// <summary>Builds triangles for boxes and ellipsoids, each bound to one bone.</summary>
internal sealed class MeshBuilder
{
    public MeshData Mesh { get; } = new();

    public static readonly Vector4 White = new(1, 1, 1, 1);
    public static readonly Vector4 Dark = new(0.12f, 0.12f, 0.14f, 1);
    public static readonly Vector4 Skin = new(1f, 0.86f, 0.74f, 1);

    public void Box(int bone, Vector3 center, Vector3 size, Vector4 tint)
    {
        var h = size / 2;
        // Each face: normal, then two tangent axes; four corners per face.
        (Vector3 n, Vector3 u, Vector3 v)[] faces =
        {
            (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
            (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
            (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
            (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
        };
        foreach (var (n, u, v) in faces)
        {
            var baseIndex = (ushort)Mesh.Positions.Count;
            var c = center + n * Abs(n, h);
            var du = u * Abs(u, h);
            var dv = v * Abs(v, h);
            Mesh.Add(c - du - dv, n, tint, bone);
            Mesh.Add(c + du - dv, n, tint, bone);
            Mesh.Add(c + du + dv, n, tint, bone);
            Mesh.Add(c - du + dv, n, tint, bone);
            // Wind counter-clockwise seen from outside: pick the order from the handedness of (u, v, n).
            var flip = Vector3.Dot(Vector3.Cross(u, v), n) < 0;
            ushort[] order = flip ? new ushort[] { 0, 2, 1, 0, 3, 2 } : new ushort[] { 0, 1, 2, 0, 2, 3 };
            foreach (var o in order) Mesh.Indices.Add((ushort)(baseIndex + o));
        }
    }

    private static float Abs(Vector3 axis, Vector3 half) => MathF.Abs(axis.X) * half.X + MathF.Abs(axis.Y) * half.Y + MathF.Abs(axis.Z) * half.Z;

    /// <summary>A glowing tint (ADR-003 point 1): vertex alpha 0 tells the toon material to draw it unlit. Opaque either way.</summary>
    public static Vector4 Glowing(Vector4 tint) => new(tint.X, tint.Y, tint.Z, 0);

    /// <summary>
    /// A cylinder (or a cone, with a zero radius) standing on the Y axis at <paramref name="center"/>, with smooth sides and flat caps; <paramref name="tiltX"/> leans
    /// it about X (radians, positive tips the top back toward +Z and raises the front).
    /// </summary>
    public void Cylinder(int bone, Vector3 center, float radiusTop, float radiusBottom, float height, int segments, Vector4 tint, float tiltX = 0)
    {
        var rotation = Matrix4x4.CreateRotationX(tiltX);
        Vector3 Place(Vector3 local) => center + Vector3.Transform(local, rotation);
        Vector3 Turn(Vector3 normal) => Vector3.Normalize(Vector3.TransformNormal(normal, rotation));
        var half = height / 2;
        var slope = (radiusBottom - radiusTop) / height;
        var side = Mesh.Positions.Count;
        for (var s = 0; s <= segments; s++)
        {
            var theta = 2 * MathF.PI * s / segments;
            var (sin, cos) = MathF.SinCos(theta);
            var normal = Turn(Vector3.Normalize(new Vector3(cos, slope, sin)));
            Mesh.Add(Place(new Vector3(cos * radiusTop, half, sin * radiusTop)), normal, tint, bone);
            Mesh.Add(Place(new Vector3(cos * radiusBottom, -half, sin * radiusBottom)), normal, tint, bone);
        }
        for (var s = 0; s < segments; s++)
        {
            var top = (ushort)(side + s * 2);
            var bottom = (ushort)(top + 1);
            var nextTop = (ushort)(top + 2);
            var nextBottom = (ushort)(top + 3);
            // Counter-clockwise seen from outside (the outward normal is (cos, 0, sin)).
            if (radiusTop > 0) { Mesh.Indices.Add(top); Mesh.Indices.Add(nextTop); Mesh.Indices.Add(bottom); }
            if (radiusBottom > 0) { Mesh.Indices.Add(nextTop); Mesh.Indices.Add(nextBottom); Mesh.Indices.Add(bottom); }
        }
        Cap(center, rotation, half, radiusTop, segments, tint, bone, up: true);
        Cap(center, rotation, -half, radiusBottom, segments, tint, bone, up: false);
    }

    private void Cap(Vector3 center, Matrix4x4 rotation, float y, float radius, int segments, Vector4 tint, int bone, bool up)
    {
        if (radius <= 0) return;
        var normal = Vector3.Normalize(Vector3.TransformNormal(up ? Vector3.UnitY : -Vector3.UnitY, rotation));
        var middle = (ushort)Mesh.Positions.Count;
        Mesh.Add(center + Vector3.Transform(new Vector3(0, y, 0), rotation), normal, tint, bone);
        for (var s = 0; s <= segments; s++)
        {
            var (sin, cos) = MathF.SinCos(2 * MathF.PI * s / segments);
            Mesh.Add(center + Vector3.Transform(new Vector3(cos * radius, y, sin * radius), rotation), normal, tint, bone);
        }
        for (var s = 0; s < segments; s++)
        {
            var a = (ushort)(middle + 1 + s);
            var b = (ushort)(a + 1);
            if (up) { Mesh.Indices.Add(middle); Mesh.Indices.Add(b); Mesh.Indices.Add(a); }
            else { Mesh.Indices.Add(middle); Mesh.Indices.Add(a); Mesh.Indices.Add(b); }
        }
    }

    /// <summary>
    /// The top of an ellipsoid down to <paramref name="phiMax"/> (radians from the top: π/2 is a half), open below, tipped about X by <paramref name="tiltX"/> (positive
    /// raises the front, -Z, and lowers the back). For hair, helmets and caps on the head.
    /// </summary>
    public void Dome(int bone, Vector3 center, Vector3 radii, float phiMax, float tiltX, int segments, int rings, Vector4 tint)
    {
        var rotation = Matrix4x4.CreateRotationX(tiltX);
        var baseIndex = Mesh.Positions.Count;
        for (var r = 0; r <= rings; r++)
        {
            var phi = phiMax * r / rings;
            for (var s = 0; s <= segments; s++)
            {
                var theta = 2 * MathF.PI * s / segments;
                var unit = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                var normal = Vector3.Normalize(new Vector3(unit.X / radii.X, unit.Y / radii.Y, unit.Z / radii.Z));
                Mesh.Add(center + Vector3.Transform(unit * radii, rotation), Vector3.Normalize(Vector3.TransformNormal(normal, rotation)), tint, bone);
            }
        }
        var stride = segments + 1;
        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                var a = (ushort)(baseIndex + r * stride + s);
                var b = (ushort)(a + 1);
                var c = (ushort)(a + stride);
                var d = (ushort)(c + 1);
                if (r != 0) { Mesh.Indices.Add(a); Mesh.Indices.Add(b); Mesh.Indices.Add(c); }
                Mesh.Indices.Add(b); Mesh.Indices.Add(d); Mesh.Indices.Add(c);
            }
        }
    }

    /// <summary>A point on an ellipsoid's surface at <paramref name="elevation"/> above its equator and <paramref name="azimuth"/> from its front (-Z), pushed out by <paramref name="lift"/>.</summary>
    public static Vector3 OnSurface(Vector3 center, Vector3 radii, float elevation, float azimuth, float lift = 0)
    {
        var dir = new Vector3(MathF.Sin(azimuth) * MathF.Cos(elevation), MathF.Sin(elevation), -MathF.Cos(azimuth) * MathF.Cos(elevation));
        return center + dir * (radii + new Vector3(lift));
    }

    public void Ellipsoid(int bone, Vector3 center, Vector3 radii, int segments, int rings, Vector4 tint)
    {
        var baseIndex = Mesh.Positions.Count;
        for (var r = 0; r <= rings; r++)
        {
            var phi = MathF.PI * r / rings;
            for (var s = 0; s <= segments; s++)
            {
                var theta = 2 * MathF.PI * s / segments;
                var unit = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                var normal = Vector3.Normalize(new Vector3(unit.X / radii.X, unit.Y / radii.Y, unit.Z / radii.Z));
                Mesh.Add(center + unit * radii, normal, tint, bone);
            }
        }
        var stride = segments + 1;
        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                var a = (ushort)(baseIndex + r * stride + s);
                var b = (ushort)(a + 1);
                var c = (ushort)(a + stride);
                var d = (ushort)(c + 1);
                if (r != 0)
                {
                    Mesh.Indices.Add(a); Mesh.Indices.Add(b); Mesh.Indices.Add(c);
                }
                if (r != rings - 1)
                {
                    Mesh.Indices.Add(b); Mesh.Indices.Add(d); Mesh.Indices.Add(c);
                }
            }
        }
    }
}
