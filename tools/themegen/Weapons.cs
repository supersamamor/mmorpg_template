using System.Numerics;
using System.Text.RegularExpressions;

namespace Game.ThemeGen;

/// <summary>
/// Placeholder weapon shapes by weapon type (T-405, ADR-002 row 181): a dagger is a short blade, a staff a staff, and so on, from boxes and ellipsoids, original and
/// simple. A shape sits on the right hand's attach point with its grip at the origin; blades and shafts point along -Z, a staff along +Y (as the T-403 placeholders did).
/// The type comes from the item content (<c>weapon_type</c> next to <c>visual_asset_key</c>), never from the model's name.
/// </summary>
public static class WeaponShapes
{
    private static readonly Regex VisualLine = new(@"^\s*visual_asset_key:\s*(\S+)\s*$", RegexOptions.Compiled);
    private static readonly Regex TypeLine = new(@"^\s*weapon_type:\s*(\S+)\s*$", RegexOptions.Compiled);

    /// <summary>The weapon type of every worn visual key in <paramref name="contentDir"/>'s items (<c>items/*.yaml</c>); empty when there is no such folder.</summary>
    public static IReadOnlyDictionary<string, string> TypesByVisualKey(string contentDir)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var items = Path.Combine(contentDir, "items");
        if (!Directory.Exists(items)) return result;
        foreach (var file in Directory.GetFiles(items, "*.yaml"))
        {
            string? visual = null;
            string? type = null;
            foreach (var line in File.ReadLines(file))
            {
                if (VisualLine.Match(line) is { Success: true } v) visual = v.Groups[1].Value;
                else if (TypeLine.Match(line) is { Success: true } t) type = t.Groups[1].Value;
            }
            if (visual is not null && type is not null) result[visual] = type;
        }
        return result;
    }

    /// <summary>Which of the shapes a weapon type gets: its own, else the one-handed sword's.</summary>
    public static string ShapeOf(string? weaponType) => weaponType switch
    {
        "dagger" or "sword_1h" or "sword_2h" or "spear" or "axe" or "mace" or "staff" or "bow" or "knuckle" => weaponType,
        _ => "sword_1h",
    };

    /// <summary>The size of the shape's bounding box, in metres (x across, y up, z along the weapon). For the tests that tell shapes apart.</summary>
    public static Vector3 Size(string? weaponType)
    {
        var mesh = new MeshBuilder();
        Build(mesh, weaponType);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in mesh.Mesh.Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return max - min;
    }

    internal static void Build(MeshBuilder m, string? weaponType)
    {
        var white = MeshBuilder.White;
        var dark = MeshBuilder.Dark;
        switch (ShapeOf(weaponType))
        {
            case "dagger": // a short blade and a small guard
                m.Box(-1, new Vector3(0, 0, -0.05f), new Vector3(0.05f, 0.05f, 0.1f), dark);
                m.Box(-1, new Vector3(0, 0, -0.11f), new Vector3(0.12f, 0.04f, 0.03f), dark);
                m.Box(-1, new Vector3(0, 0, -0.26f), new Vector3(0.04f, 0.07f, 0.28f), white);
                break;
            case "sword_2h": // a long blade, a wide guard and a long grip
                m.Box(-1, new Vector3(0, 0, 0.06f), new Vector3(0.06f, 0.06f, 0.32f), dark);
                m.Box(-1, new Vector3(0, 0, -0.12f), new Vector3(0.3f, 0.05f, 0.05f), dark);
                m.Box(-1, new Vector3(0, 0, -0.65f), new Vector3(0.08f, 0.12f, 1.05f), white);
                break;
            case "spear": // a long shaft and a leaf tip
                m.Box(-1, new Vector3(0, 0, -0.45f), new Vector3(0.04f, 0.04f, 1.5f), dark);
                m.Ellipsoid(-1, new Vector3(0, 0, -1.28f), new Vector3(0.04f, 0.07f, 0.16f), 8, 5, white);
                break;
            case "axe": // a handle and a wedge head to one side
                m.Box(-1, new Vector3(0, 0, -0.3f), new Vector3(0.05f, 0.05f, 0.7f), dark);
                m.Box(-1, new Vector3(0.1f, 0, -0.58f), new Vector3(0.22f, 0.05f, 0.2f), white);
                break;
            case "mace": // a handle and a ball head
                m.Box(-1, new Vector3(0, 0, -0.25f), new Vector3(0.05f, 0.05f, 0.55f), dark);
                m.Ellipsoid(-1, new Vector3(0, 0, -0.58f), new Vector3(0.11f, 0.11f, 0.11f), 10, 6, white);
                break;
            case "staff": // a tall shaft and an orb on top
                m.Box(-1, new Vector3(0, 0.25f, 0), new Vector3(0.05f, 1.3f, 0.05f), dark);
                m.Ellipsoid(-1, new Vector3(0, 0.95f, 0), new Vector3(0.09f, 0.09f, 0.09f), 8, 5, white);
                break;
            case "bow": // a bent limb from three boxes, and a string
                m.Box(-1, new Vector3(0, 0, -0.1f), new Vector3(0.04f, 0.5f, 0.04f), dark);
                m.Box(-1, new Vector3(0, 0.34f, -0.04f), new Vector3(0.04f, 0.2f, 0.04f), dark);
                m.Box(-1, new Vector3(0, -0.34f, -0.04f), new Vector3(0.04f, 0.2f, 0.04f), dark);
                m.Box(-1, new Vector3(0, 0, 0.02f), new Vector3(0.01f, 0.9f, 0.01f), white);
                break;
            case "knuckle": // a brass bar across the fist
                m.Box(-1, new Vector3(0, 0, -0.05f), new Vector3(0.1f, 0.14f, 0.05f), white);
                break;
            default: // sword_1h: a grip, a guard and a blade of middling length
                m.Box(-1, new Vector3(0, 0, -0.04f), new Vector3(0.05f, 0.05f, 0.14f), dark);
                m.Box(-1, new Vector3(0, 0, -0.13f), new Vector3(0.2f, 0.04f, 0.04f), dark);
                m.Box(-1, new Vector3(0, 0, -0.5f), new Vector3(0.06f, 0.1f, 0.7f), white);
                break;
        }
    }
}
