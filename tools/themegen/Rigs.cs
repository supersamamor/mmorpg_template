using System.Numerics;

namespace Game.ThemeGen;

/// <summary>One shared rig: its bones, how to build its body at a level of detail, and its clips. Every model on the same rig shares the bones and clips.</summary>
internal sealed class Rig
{
    public required string Name { get; init; }
    public required Bone[] Bones { get; init; }
    public required Action<MeshBuilder, BodyLook> Body { get; init; }
    public required Func<Rig, Clip[]> MakeClips { get; init; }

    public int Index(string bone) => Array.FindIndex(Bones, b => b.Name == bone);

    private static readonly Dictionary<string, Rig> All = new();

    public static Rig Get(string name)
    {
        if (All.TryGetValue(name, out var rig)) return rig;
        rig = name switch
        {
            "humanoid" => Rigs.Humanoid(),
            "blob" => Rigs.Blob(),
            "sprout" => Rigs.Sprout(),
            "insect" => Rigs.Insect(),
            "quadruped" => Rigs.Quadruped(),
            _ => throw new ArgumentException("unknown rig " + name),
        };
        All[name] = rig;
        return rig;
    }
}

/// <summary>
/// How one model on a rig looks (T-222, ADR-003): the level of detail, the key's hash for small variations, and its colours, all linear: the two identity colours, a
/// trim, skin, hair, the eye ink, cheeks and the glow of unlit parts; <see cref="Look"/> is one of <see cref="Looks"/> for the humanoid.
/// </summary>
internal sealed record BodyLook(int Lod, uint Hash, Vector4 Primary, Vector4 Secondary, Vector4 Trim, Vector4 Skin, Vector4 Hair, Vector4 Ink, Vector4 Cheek, Vector4 Glow,
    Vector4 Cream, string Look)
{
    /// <summary>Segments (or rings) at this level of detail.</summary>
    public int Seg(int lod0, int lod1) => Lod == 0 ? lod0 : lod1;

    /// <summary>A darker <see cref="Primary"/>.</summary>
    public Vector4 Deep => Palette.Shade(Primary, 0.78f);
}

/// <summary>Builds clips from small functions of normalized time.</summary>
internal sealed class ClipBuilder
{
    private const int Keys = 12;
    private readonly Rig _rig;
    private readonly float _duration;
    private readonly List<Channel> _channels = new();

    public ClipBuilder(Rig rig, float duration)
    {
        _rig = rig;
        _duration = duration;
    }

    public ClipBuilder Rot(string bone, Func<float, Quaternion> f) => Add(bone, "rotation", 4, t => { var q = f(t); return new[] { q.X, q.Y, q.Z, q.W }; });

    /// <summary>Translation as an offset from the bone's rest position.</summary>
    public ClipBuilder Move(string bone, Func<float, Vector3> f)
    {
        var rest = _rig.Bones[_rig.Index(bone)].Local;
        return Add(bone, "translation", 3, t => { var v = rest + f(t); return new[] { v.X, v.Y, v.Z }; });
    }

    public ClipBuilder Scale(string bone, Func<float, Vector3> f) => Add(bone, "scale", 3, t => { var v = f(t); return new[] { v.X, v.Y, v.Z }; });

    private ClipBuilder Add(string bone, string path, int width, Func<float, float[]> f)
    {
        var index = _rig.Index(bone);
        if (index < 0) throw new ArgumentException($"rig {_rig.Name} has no bone {bone}");
        var times = new float[Keys + 1];
        var values = new float[(Keys + 1) * width];
        for (var i = 0; i <= Keys; i++)
        {
            var t = (float)i / Keys;
            times[i] = t * _duration;
            f(t).CopyTo(values, i * width);
        }
        _channels.Add(new Channel(index, path, times, values));
        return this;
    }

    public Clip Done(string name) => new(name, _channels);
}

/// <summary>Motion helpers. Angles are degrees; rotation about +X by a positive angle swings a hanging limb forward (toward -Z) and leans the body back.</summary>
internal static class Motion
{
    public static Quaternion Rx(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees * MathF.PI / 180f);

    public static Quaternion Rz(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    /// <summary>One full sine cycle over the clip.</summary>
    public static float S(float t, int cycles = 1) => MathF.Sin(2 * MathF.PI * t * cycles);

    /// <summary>0, up to 1 and back to 0.</summary>
    public static float P(float t) => MathF.Sin(MathF.PI * t);

    /// <summary>Smooth 0 to 1, then it holds.</summary>
    public static float E(float t) => t * t * (3 - 2 * t);

    /// <summary>Absolute value of a sine, for bobbing: <paramref name="cycles"/> bounces per clip.</summary>
    public static float Bob(float t, int cycles) => MathF.Abs(MathF.Sin(MathF.PI * t * cycles));
}

internal static class Rigs
{
    private static Bone B(string name, int parent, float x, float y, float z) => new(name, parent, new Vector3(x, y, z));

    /// <summary>Two eyes (and, at LOD0, cheeks) on an ellipsoid's front, at <paramref name="elevation"/> above its equator and <paramref name="spread"/> to each side.</summary>
    private static void Face(MeshBuilder m, int bone, Vector3 center, Vector3 radii, BodyLook k, float elevation, float spread, Vector3 eye)
    {
        foreach (var x in new[] { -1f, 1f })
        {
            m.Ellipsoid(bone, MeshBuilder.OnSurface(center, radii, elevation, spread * x, -eye.Z * 0.4f), eye, k.Seg(8, 6), k.Seg(5, 4), k.Ink);
            if (k.Lod == 0) m.Ellipsoid(bone, MeshBuilder.OnSurface(center, radii, elevation - 0.25f, (spread + 0.28f) * x, -0.01f), new Vector3(eye.X * 1.3f, eye.Y * 0.5f, eye.Z), 8, 4, k.Cheek);
        }
    }

    /// <summary>The humanoid's head (T-222): its centre in model space and its radii. Faces, hair and headgear are placed from these.</summary>
    public static readonly Vector3 HeadCenter = new(0, 1.74f, 0);

    /// <summary>The humanoid head's radii: about a third of the body's height (ADR-003 point 3).</summary>
    public static readonly Vector3 HeadRadii = new(0.33f, 0.31f, 0.32f);

    /// <summary>How far above the head's equator the eyes sit (radians): the face tips up toward the camera, which looks down at 50°.</summary>
    public const float EyeElevation = 0.26f;

    /// <summary>The humanoid's head bone's rest height (the origin of head-attached pieces).</summary>
    public const float HeadBoneY = 1.55f;

    // ---------------- humanoid ----------------

    public static Rig Humanoid() => new()
    {
        Name = "humanoid",
        Bones = new[]
        {
            B("hips", -1, 0, 0.9f, 0), B("spine", 0, 0, 0.25f, 0), B("head", 1, 0, 0.4f, 0),
            B("arm_l", 1, -0.3f, 0.35f, 0), B("hand_l", 3, 0, -0.55f, 0), B("arm_r", 1, 0.3f, 0.35f, 0), B("hand_r", 5, 0, -0.55f, 0),
            B("leg_l", 0, -0.12f, 0, 0), B("leg_r", 0, 0.12f, 0, 0),
        },
        Body = (m, k) =>
        {
            // bone indices: 0 hips, 1 spine, 2 head, 3 arm_l, 4 hand_l, 5 arm_r, 6 hand_r, 7 leg_l, 8 leg_r. The bones are unchanged since T-207; only the shapes
            // bound to them follow ADR-003: a big head (about a third of the height), short limbs, the face tipped up toward the 50° camera.
            var s = k.Seg(16, 8);
            var r = k.Seg(10, 5);
            var small = k.Seg(10, 6);
            var sr = k.Seg(6, 4);
            var legs = Palette.Shade(k.Secondary, 0.72f);
            var boots = Palette.Shade(k.Trim, 0.85f);
            var robe = k.Look == Looks.Hat;

            m.Cylinder(7, new Vector3(-0.11f, 0.5f, 0), 0.085f, 0.075f, 0.8f, small, legs);
            m.Cylinder(8, new Vector3(0.11f, 0.5f, 0), 0.085f, 0.075f, 0.8f, small, legs);
            m.Ellipsoid(7, new Vector3(-0.11f, 0.08f, -0.03f), new Vector3(0.1f, 0.08f, 0.14f), small, sr, boots);
            m.Ellipsoid(8, new Vector3(0.11f, 0.08f, -0.03f), new Vector3(0.1f, 0.08f, 0.14f), small, sr, boots);

            m.Cylinder(0, new Vector3(0, 0.95f, 0), 0.21f, 0.22f, 0.2f, s, k.Deep);
            m.Cylinder(1, new Vector3(0, 1.2f, 0), 0.2f, 0.24f, 0.42f, s, k.Primary);
            m.Cylinder(0, new Vector3(0, 1.03f, 0), 0.25f, 0.25f, 0.07f, s, k.Trim);
            if (robe)
            {
                m.Cylinder(0, new Vector3(0, 0.55f, 0), 0.24f, 0.37f, 0.8f, s, k.Primary);
                if (k.Lod == 0) m.Cylinder(0, new Vector3(0, 0.17f, 0), 0.375f, 0.38f, 0.04f, s, k.Trim); // the hem
            }

            foreach (var (arm, hand, x) in new[] { (3, 4, -1f), (5, 6, 1f) })
            {
                m.Cylinder(arm, new Vector3(0.31f * x, 1.25f, 0), 0.068f, 0.058f, 0.36f, small, k.Secondary);
                if (k.Look == Looks.Helm) m.Ellipsoid(arm, new Vector3(0.31f * x, 1.47f, 0), new Vector3(0.15f, 0.1f, 0.15f), small, sr, k.Primary); // pauldrons
                else m.Ellipsoid(arm, new Vector3(0.29f * x, 1.43f, 0), new Vector3(0.1f, 0.085f, 0.1f), small, sr, k.Primary);
                m.Ellipsoid(hand, new Vector3(0.31f * x, 1.02f, 0), new Vector3(0.07f, 0.07f, 0.07f), small, sr, k.Skin);
            }

            var head = HeadCenter;
            var hr = HeadRadii;
            m.Ellipsoid(2, head, hr, s, r, k.Skin);
            foreach (var x in new[] { -1f, 1f })
            {
                m.Ellipsoid(2, MeshBuilder.OnSurface(head, hr, EyeElevation, 0.36f * x, -0.012f), new Vector3(0.034f, 0.05f, 0.03f), small, sr, k.Ink);
                if (k.Lod == 0) m.Ellipsoid(2, MeshBuilder.OnSurface(head, hr, 0.02f, 0.62f * x, -0.012f), new Vector3(0.048f, 0.028f, 0.03f), 8, 4, k.Cheek);
            }

            var hairColour = k.Look is Looks.Hood or Looks.Hat ? k.Secondary : k.Hair;
            if (k.Look != Looks.Helm)
                m.Dome(2, head + new Vector3(0, 0.025f, 0.015f), hr + new Vector3(0.02f), MathF.PI * 0.5f, 0.45f, s, k.Seg(6, 3), hairColour);
            switch (k.Look)
            {
                case Looks.Helm:
                    m.Dome(2, head + new Vector3(0, 0.03f, 0), hr + new Vector3(0.025f), MathF.PI * 0.42f, 0.2f, s, k.Seg(5, 3), k.Primary);
                    m.Box(2, new Vector3(0, 2.09f, 0.03f), new Vector3(0.07f, 0.14f, 0.42f), k.Secondary); // crest
                    m.Box(1, new Vector3(0, 1.12f, -0.245f), new Vector3(0.3f, 0.5f, 0.03f), k.Secondary); // tabard
                    if (k.Lod == 0) m.Box(1, new Vector3(0, 1.22f, -0.262f), new Vector3(0.09f, 0.09f, 0.01f), k.Trim);
                    break;
                case Looks.Hat:
                    m.Box(2, new Vector3(0, 1.56f, 0.2f), new Vector3(0.44f, 0.42f, 0.1f), hairColour); // long hair
                    m.Cylinder(2, new Vector3(0, 1.99f, 0.02f), 0.44f, 0.44f, 0.03f, s, k.Deep, 0.15f); // brim
                    m.Cylinder(2, new Vector3(0, 2.04f, 0.02f), 0.26f, 0.265f, 0.07f, s, k.Trim, 0.15f); // band
                    m.Cylinder(2, new Vector3(0, 2.3f, 0.07f), 0f, 0.26f, 0.5f, s, k.Primary, 0.3f); // a pointed crown, leaning back
                    m.Ellipsoid(2, new Vector3(0, 2.05f, -0.275f), new Vector3(0.045f, 0.06f, 0.03f), small, sr, MeshBuilder.Glowing(k.Glow)); // the gem glows (unlit)
                    if (k.Lod == 0) m.Cylinder(1, new Vector3(0, 1.41f, 0.02f), 0.25f, 0.21f, 0.1f, s, k.Deep); // collar
                    break;
                case Looks.Hood:
                    m.Ellipsoid(1, new Vector3(0, 1.47f, 0.2f), new Vector3(0.27f, 0.13f, 0.14f), s, k.Seg(6, 3), k.Deep); // the hood, fallen back
                    m.Box(1, new Vector3(0, 1.17f, 0.25f), new Vector3(0.44f, 0.5f, 0.04f), k.Deep); // cape
                    m.Box(0, new Vector3(0.27f, 0.98f, -0.06f), new Vector3(0.12f, 0.16f, 0.17f), k.Secondary); // satchel
                    break;
                default:
                    if (k.Hash % 2 == 0) m.Ellipsoid(2, head + new Vector3(0, 0.08f, 0.33f), new Vector3(0.09f, 0.09f, 0.09f), small, sr, hairColour); // a bun
                    break;
            }
        },
        MakeClips = rig => new[]
        {
            new ClipBuilder(rig, 1.6f).Rot("spine", t => Motion.Rx(1.5f * Motion.S(t))).Rot("arm_l", t => Motion.Rz(2f * Motion.S(t))).Rot("arm_r", t => Motion.Rz(-2f * Motion.S(t))).Done("Idle"),
            new ClipBuilder(rig, 0.8f)
                .Rot("leg_l", t => Motion.Rx(35 * Motion.S(t))).Rot("leg_r", t => Motion.Rx(-35 * Motion.S(t)))
                .Rot("arm_l", t => Motion.Rx(-30 * Motion.S(t))).Rot("arm_r", t => Motion.Rx(30 * Motion.S(t)))
                .Move("hips", t => new Vector3(0, 0.04f * Motion.Bob(t, 2), 0)).Done("Walk"),
            new ClipBuilder(rig, 0.5f).Rot("arm_r", t => Motion.Rx(120 * Motion.P(t))).Rot("spine", t => Motion.Rx(-15 * Motion.P(t))).Done("Attack"),
            new ClipBuilder(rig, 0.35f).Rot("spine", t => Motion.Rx(25 * Motion.P(t))).Move("hips", t => new Vector3(0, 0, 0.06f * Motion.P(t))).Done("Hit"),
            new ClipBuilder(rig, 0.8f).Rot("hips", t => Motion.Rx(90 * Motion.E(t))).Move("hips", t => new Vector3(0, -0.7f * Motion.E(t), 0)).Done("Die"),
            new ClipBuilder(rig, 0.8f).Rot("arm_l", t => Motion.Rx(150 * Motion.P(t))).Rot("arm_r", t => Motion.Rx(150 * Motion.P(t))).Rot("spine", t => Motion.Rx(8 * Motion.P(t))).Done("Cast"),
            new ClipBuilder(rig, 1.0f).Move("hips", t => new Vector3(0, -0.45f * Motion.E(MathF.Min(1, t / 0.3f)), 0))
                .Rot("leg_l", t => Motion.Rx(90 * Motion.E(MathF.Min(1, t / 0.3f)))).Rot("leg_r", t => Motion.Rx(90 * Motion.E(MathF.Min(1, t / 0.3f)))).Done("Sit"),
            new ClipBuilder(rig, 0.8f).Rot("spine", t => Motion.Rx(-55 * Motion.P(t))).Move("hips", t => new Vector3(0, -0.15f * Motion.P(t), 0))
                .Rot("arm_l", t => Motion.Rx(40 * Motion.P(t))).Rot("arm_r", t => Motion.Rx(40 * Motion.P(t))).Done("PickUp"),
            // Weapon-type variants (T-403, §7.6): the manifest maps them as attack.<weapon type> / idle.<weapon type>.
            new ClipBuilder(rig, 0.4f).Rot("arm_r", t => Motion.Rx(80 * Motion.P(t))).Move("hips", t => new Vector3(0, 0, -0.08f * Motion.P(t))).Done("AttackStab"),
            new ClipBuilder(rig, 1.6f).Rot("spine", t => Motion.Rx(1.5f * Motion.S(t))).Rot("arm_l", t => Motion.Rx(35 + 2f * Motion.S(t)))
                .Rot("arm_r", t => Motion.Rx(35 - 2f * Motion.S(t))).Done("IdleTwoHand"),
            new ClipBuilder(rig, 0.6f).Rot("arm_l", t => Motion.Rx(35 + 110 * Motion.P(t))).Rot("arm_r", t => Motion.Rx(35 + 110 * Motion.P(t)))
                .Rot("spine", t => Motion.Rx(-20 * Motion.P(t))).Done("AttackTwoHand"),
        },
    };

    // ---------------- blob ----------------

    public static Rig Blob() => new()
    {
        Name = "blob",
        Bones = new[] { B("root", -1, 0, 0, 0), B("body", 0, 0, 0.35f, 0) },
        Body = (m, k) =>
        {
            // All head: a round body with the face high on its front, tipped up toward the camera.
            var body = new Vector3(0, 0.36f, 0);
            var radii = new Vector3(0.42f, 0.36f, 0.42f);
            m.Ellipsoid(1, body, radii, k.Seg(16, 8), k.Seg(10, 5), k.Primary);
            Face(m, 1, body, radii, k, 0.3f, 0.3f, new Vector3(0.045f, 0.065f, 0.035f));
            m.Ellipsoid(1, new Vector3(0, 0.7f, 0.06f), new Vector3(0.12f, 0.08f, 0.12f), k.Seg(8, 6), k.Seg(5, 3), k.Secondary); // a tuft
        },
        MakeClips = rig => new[]
        {
            new ClipBuilder(rig, 1.4f).Scale("body", t => new Vector3(1 + 0.05f * Motion.S(t), 1 - 0.07f * Motion.S(t), 1 + 0.05f * Motion.S(t))).Done("Idle"),
            new ClipBuilder(rig, 0.6f).Move("body", t => new Vector3(0, 0.18f * Motion.P(t), 0)).Scale("body", t => new Vector3(1, 1 + 0.1f * Motion.P(t), 1)).Done("Walk"),
            new ClipBuilder(rig, 0.5f).Move("body", t => new Vector3(0, 0, -0.35f * Motion.P(t))).Scale("body", t => new Vector3(1, 1, 1 + 0.3f * Motion.P(t))).Done("Attack"),
            new ClipBuilder(rig, 0.35f).Scale("body", t => new Vector3(1 + 0.2f * Motion.P(t), 1 - 0.3f * Motion.P(t), 1 + 0.2f * Motion.P(t))).Done("Hit"),
            new ClipBuilder(rig, 0.7f).Scale("body", t => new Vector3(1 + 0.5f * Motion.E(t), 1 - 0.8f * Motion.E(t), 1 + 0.5f * Motion.E(t)))
                .Move("body", t => new Vector3(0, -0.25f * Motion.E(t), 0)).Done("Die"),
        },
    };

    // ---------------- sprout ----------------

    public static Rig Sprout() => new()
    {
        Name = "sprout",
        Bones = new[] { B("root", -1, 0, 0, 0), B("stem", 0, 0, 0.1f, 0), B("leaf_l", 1, -0.05f, 0.45f, 0), B("leaf_r", 1, 0.05f, 0.45f, 0) },
        Body = (m, k) =>
        {
            m.Cylinder(0, new Vector3(0, 0.06f, 0), 0.17f, 0.2f, 0.12f, k.Seg(12, 6), Palette.Shade(k.Trim, 0.8f)); // the root clump
            m.Cylinder(1, new Vector3(0, 0.3f, 0), 0.05f, 0.07f, 0.4f, k.Seg(8, 5), k.Deep);
            var head = new Vector3(0, 0.62f, 0);
            var radii = new Vector3(0.24f, 0.22f, 0.23f); // the head is about a third of the height
            m.Ellipsoid(1, head, radii, k.Seg(14, 8), k.Seg(9, 5), k.Primary);
            Face(m, 1, head, radii, k, 0.28f, 0.34f, new Vector3(0.03f, 0.045f, 0.025f));
            m.Ellipsoid(2, new Vector3(-0.24f, 0.86f, 0), new Vector3(0.16f, 0.03f, 0.08f), k.Seg(10, 6), k.Seg(5, 3), k.Secondary);
            m.Ellipsoid(3, new Vector3(0.24f, 0.86f, 0), new Vector3(0.16f, 0.03f, 0.08f), k.Seg(10, 6), k.Seg(5, 3), k.Secondary);
        },
        MakeClips = rig => new[]
        {
            new ClipBuilder(rig, 1.4f).Rot("stem", t => Motion.Rz(5 * Motion.S(t))).Rot("leaf_l", t => Motion.Rz(10 * Motion.S(t))).Rot("leaf_r", t => Motion.Rz(-10 * Motion.S(t))).Done("Idle"),
            new ClipBuilder(rig, 0.5f).Move("root", t => new Vector3(0, 0.1f * Motion.Bob(t, 2), 0)).Rot("stem", t => Motion.Rz(8 * Motion.S(t))).Done("Walk"),
            new ClipBuilder(rig, 0.5f).Rot("stem", t => Motion.Rx(-50 * Motion.P(t))).Done("Attack"),
            new ClipBuilder(rig, 0.35f).Rot("stem", t => Motion.Rx(30 * Motion.P(t))).Done("Hit"),
            new ClipBuilder(rig, 0.7f).Rot("root", t => Motion.Rz(90 * Motion.E(t))).Done("Die"),
        },
    };

    // ---------------- insect ----------------

    public static Rig Insect() => new()
    {
        Name = "insect",
        Bones = new[] { B("root", -1, 0, 0, 0), B("body", 0, 0, 0.35f, 0), B("wing_l", 1, -0.1f, 0.25f, 0), B("wing_r", 1, 0.1f, 0.25f, 0) },
        Body = (m, k) =>
        {
            var wasp = k.Hash % 2 == 1;
            var radii = wasp ? new Vector3(0.2f, 0.17f, 0.36f) : new Vector3(0.34f, 0.25f, 0.42f);
            m.Ellipsoid(1, new Vector3(0, 0.35f, 0.04f), radii, k.Seg(14, 8), k.Seg(9, 5), k.Primary);
            // A big round head in front, about a third of the length, with the face tipped up.
            var head = new Vector3(0, 0.4f, -radii.Z - 0.1f);
            var headRadii = new Vector3(0.2f, 0.19f, 0.18f);
            m.Ellipsoid(1, head, headRadii, k.Seg(12, 6), k.Seg(8, 4), k.Secondary);
            Face(m, 1, head, headRadii, k, 0.3f, 0.42f, new Vector3(0.035f, 0.05f, 0.03f));
            if (wasp) m.Cylinder(1, new Vector3(0, 0.32f, radii.Z + 0.16f), 0f, 0.05f, 0.22f, k.Seg(8, 5), k.Ink, MathF.PI / 2); // stinger
            m.Ellipsoid(2, new Vector3(-0.22f, 0.62f, 0.08f), new Vector3(0.16f, 0.02f, 0.24f), k.Seg(10, 6), k.Seg(4, 3), k.Cream);
            m.Ellipsoid(3, new Vector3(0.22f, 0.62f, 0.08f), new Vector3(0.16f, 0.02f, 0.24f), k.Seg(10, 6), k.Seg(4, 3), k.Cream);
            if (k.Lod == 0)
            {
                for (var i = 0; i < 3; i++)
                {
                    var z = -0.22f + i * 0.22f;
                    m.Box(0, new Vector3(-0.3f, 0.15f, z), new Vector3(0.25f, 0.05f, 0.05f), k.Ink);
                    m.Box(0, new Vector3(0.3f, 0.15f, z), new Vector3(0.25f, 0.05f, 0.05f), k.Ink);
                }
            }
        },
        MakeClips = rig => new[]
        {
            new ClipBuilder(rig, 1.0f).Rot("wing_l", t => Motion.Rz(20 * Motion.S(t, 3))).Rot("wing_r", t => Motion.Rz(-20 * Motion.S(t, 3))).Move("body", t => new Vector3(0, 0.03f * Motion.S(t), 0)).Done("Idle"),
            new ClipBuilder(rig, 0.5f).Move("body", t => new Vector3(0, 0.04f * Motion.Bob(t, 2), 0)).Rot("wing_l", t => Motion.Rz(20 * Motion.S(t, 4))).Rot("wing_r", t => Motion.Rz(-20 * Motion.S(t, 4))).Done("Walk"),
            new ClipBuilder(rig, 0.5f).Rot("body", t => Motion.Rx(-35 * Motion.P(t))).Move("body", t => new Vector3(0, 0, -0.2f * Motion.P(t))).Done("Attack"),
            new ClipBuilder(rig, 0.35f).Rot("body", t => Motion.Rx(30 * Motion.P(t))).Done("Hit"),
            new ClipBuilder(rig, 0.7f).Rot("root", t => Motion.Rz(180 * Motion.E(t))).Move("root", t => new Vector3(0, 0.55f * Motion.E(t), 0)).Done("Die"),
        },
    };

    // ---------------- quadruped ----------------

    public static Rig Quadruped() => new()
    {
        Name = "quadruped",
        Bones = new[]
        {
            B("root", -1, 0, 0, 0), B("body", 0, 0, 0.6f, 0), B("head", 1, 0, 0.05f, -0.5f),
            B("leg_fl", 1, -0.2f, -0.1f, -0.35f), B("leg_fr", 1, 0.2f, -0.1f, -0.35f), B("leg_bl", 1, -0.2f, -0.1f, 0.35f), B("leg_br", 1, 0.2f, -0.1f, 0.35f),
        },
        Body = (m, k) =>
        {
            m.Ellipsoid(1, new Vector3(0, 0.6f, 0.05f), new Vector3(0.3f, 0.25f, 0.5f), k.Seg(14, 8), k.Seg(9, 5), k.Primary);
            // A big head, tipped up so the face reads from above.
            var head = new Vector3(0, 0.72f, -0.6f);
            var headRadii = new Vector3(0.25f, 0.24f, 0.25f);
            m.Ellipsoid(2, head, headRadii, k.Seg(14, 8), k.Seg(9, 5), k.Primary);
            Face(m, 2, head, headRadii, k, 0.32f, 0.4f, new Vector3(0.04f, 0.055f, 0.035f));
            m.Ellipsoid(2, new Vector3(0, 0.63f, -0.84f), new Vector3(0.1f, 0.08f, 0.06f), k.Seg(10, 6), k.Seg(5, 3), Palette.Shade(k.Primary, 0.7f)); // snout
            foreach (var (bone, x, z) in new[] { (3, -0.2f, -0.35f), (4, 0.2f, -0.35f), (5, -0.2f, 0.35f), (6, 0.2f, 0.35f) })
                m.Cylinder(bone, new Vector3(x, 0.24f, z), 0.07f, 0.06f, 0.48f, k.Seg(8, 5), Palette.Shade(k.Primary, 0.65f));
            m.Cylinder(2, new Vector3(-0.13f, 0.62f, -0.86f), 0f, 0.035f, 0.18f, k.Seg(6, 4), k.Cream, -MathF.PI / 2.4f); // tusks
            m.Cylinder(2, new Vector3(0.13f, 0.62f, -0.86f), 0f, 0.035f, 0.18f, k.Seg(6, 4), k.Cream, -MathF.PI / 2.4f);
            var spikes = k.Lod == 0 ? 5 : 3;
            for (var i = 0; i < spikes; i++)
                m.Cylinder(1, new Vector3(0, 0.88f, -0.3f + i * (0.6f / (spikes - 1))), 0f, 0.06f, 0.16f, k.Seg(6, 4), k.Secondary, 0.3f); // back spikes
            if (k.Lod == 0) m.Cylinder(1, new Vector3(0, 0.66f, 0.6f), 0.02f, 0.035f, 0.2f, 6, k.Ink, MathF.PI / 2.5f); // tail
        },
        MakeClips = rig => new[]
        {
            new ClipBuilder(rig, 1.6f).Rot("head", t => Motion.Rx(5 * Motion.S(t))).Done("Idle"),
            new ClipBuilder(rig, 0.7f)
                .Rot("leg_fl", t => Motion.Rx(30 * Motion.S(t))).Rot("leg_br", t => Motion.Rx(30 * Motion.S(t)))
                .Rot("leg_fr", t => Motion.Rx(-30 * Motion.S(t))).Rot("leg_bl", t => Motion.Rx(-30 * Motion.S(t)))
                .Move("body", t => new Vector3(0, 0.03f * Motion.Bob(t, 2), 0)).Done("Walk"),
            new ClipBuilder(rig, 0.5f).Rot("body", t => Motion.Rx(-12 * Motion.P(t))).Move("body", t => new Vector3(0, 0, -0.4f * Motion.P(t))).Rot("head", t => Motion.Rx(-20 * Motion.P(t))).Done("Attack"),
            new ClipBuilder(rig, 0.35f).Rot("body", t => Motion.Rx(15 * Motion.P(t))).Move("body", t => new Vector3(0, 0, 0.1f * Motion.P(t))).Done("Hit"),
            new ClipBuilder(rig, 0.8f).Rot("root", t => Motion.Rz(90 * Motion.E(t))).Move("root", t => new Vector3(0, 0.25f * Motion.E(t), 0)).Done("Die"),
        },
    };
}
