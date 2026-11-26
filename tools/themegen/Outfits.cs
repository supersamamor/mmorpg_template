using System.Numerics;

namespace Game.ThemeGen;

/// <summary>
/// Placeholder outfit pieces (T-708): original, plain shapes just outside the T-222 body. Which shape a model gets is its <c>look</c> in the palette (theme data), never its name.
/// The parts painted with the primary colour and its shade follow the theme contract where the palette says <c>cream</c> (THEMES.md 1.7), so they can take the job's cloth colour.
/// </summary>
internal static class OutfitShapes
{
    /// <summary>A ring of flowers on the hair.</summary>
    public const string Wreath = "wreath";

    /// <summary>A hood pulled up over the head, open at the face.</summary>
    public const string HoodUp = "hood_up";

    /// <summary>A vest with a broad sash across it.</summary>
    public const string Sash = "sash";

    /// <summary>A straight coat to the knee.</summary>
    public const string Coat = "coat";

    /// <summary>A flared coat, belted.</summary>
    public const string CoatFlared = "coat_flared";

    /// <summary>A short cape on the back.</summary>
    public const string Cape = "cape";

    /// <summary>Draws a head-attached piece; false when <paramref name="k"/> names no shape of this kind.</summary>
    public static bool Head(MeshBuilder m, Vector3 head, BodyLook k)
    {
        switch (k.Look)
        {
            case Wreath:
                m.Cylinder(-1, head + new Vector3(0, 0.15f, 0.01f), 0.325f, 0.335f, 0.05f, 14, k.Primary, 0.12f);
                for (var i = 0; i < 8; i++)
                {
                    var (sin, cos) = MathF.SinCos(2 * MathF.PI * (i + 0.5f) / 8);
                    m.Ellipsoid(-1, head + new Vector3(sin * 0.335f, 0.16f + cos * 0.04f, 0.01f - cos * 0.335f), new Vector3(0.055f, 0.05f, 0.055f), 6, 4, i % 2 == 0 ? k.Secondary : k.Trim);
                }
                return true;
            case HoodUp:
                m.Dome(-1, head + new Vector3(0, 0.03f, 0.03f), Rigs.HeadRadii + new Vector3(0.045f), MathF.PI * 0.62f, 0.75f, 14, 6, k.Primary);
                m.Cylinder(-1, head + new Vector3(0, -0.28f, 0.04f), 0.25f, 0.27f, 0.1f, 14, k.Deep);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Draws a piece skinned to the humanoid; false when <paramref name="k"/> names no shape of this kind.</summary>
    public static bool Skinned(MeshBuilder m, Rig rig, BodyLook k)
    {
        var hips = rig.Index("hips");
        var spine = rig.Index("spine");
        switch (k.Look)
        {
            case Sash:
                m.Cylinder(spine, new Vector3(0, 1.21f, 0), 0.215f, 0.255f, 0.42f, 14, k.Primary);
                m.Cylinder(spine, new Vector3(0, 1.2f, 0), 0.245f, 0.265f, 0.1f, 14, k.Secondary, 0.5f);
                m.Cylinder(hips, new Vector3(0, 1.0f, 0), 0.258f, 0.28f, 0.14f, 14, k.Trim);
                return true;
            case Coat:
                m.Cylinder(spine, new Vector3(0, 1.21f, 0), 0.22f, 0.26f, 0.42f, 14, k.Primary);
                m.Cylinder(hips, new Vector3(0, 0.83f, 0), 0.262f, 0.3f, 0.36f, 14, k.Primary);
                m.Cylinder(hips, new Vector3(0, 0.64f, 0), 0.302f, 0.308f, 0.04f, 14, k.Deep);
                m.Box(spine, new Vector3(0, 1.2f, -0.25f), new Vector3(0.05f, 0.36f, 0.02f), k.Secondary);
                return true;
            case CoatFlared:
                m.Cylinder(spine, new Vector3(0, 1.21f, 0), 0.215f, 0.245f, 0.42f, 14, k.Primary);
                m.Cylinder(hips, new Vector3(0, 1.02f, 0), 0.266f, 0.266f, 0.08f, 14, k.Secondary);
                m.Cylinder(hips, new Vector3(0, 0.79f, 0), 0.262f, 0.38f, 0.4f, 14, k.Primary);
                m.Cylinder(hips, new Vector3(0, 0.58f, 0), 0.382f, 0.388f, 0.04f, 14, k.Deep);
                return true;
            case Cape:
                m.Cylinder(spine, new Vector3(0, 1.42f, 0.02f), 0.2f, 0.3f, 0.12f, 14, k.Deep);
                m.Box(spine, new Vector3(0, 1.1f, 0.3f), new Vector3(0.5f, 0.6f, 0.04f), k.Primary);
                m.Box(spine, new Vector3(0, 0.79f, 0.3f), new Vector3(0.51f, 0.05f, 0.05f), k.Deep);
                return true;
            default:
                return false;
        }
    }
}
