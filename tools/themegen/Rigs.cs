using System.Numerics;

namespace Game.ThemeGen;

/// <summary>One shared rig: its bones, how to build its body at a level of detail, and its clips. Every model on the same rig shares the bones and clips.</summary>
internal sealed class Rig
{
    public required string Name { get; init; }
    public required Bone[] Bones { get; init; }
    public required Func<MeshBuilder, int, uint, Vector4, int> Body { get; init; }  // (builder, lod, hash, accent) -> unused
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

    private static Vector4 Mul(Vector4 a, float k) => new(a.X * k, a.Y * k, a.Z * k, 1);

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
        Body = (m, lod, hash, accent) =>
        {
            // bone indices: 0 hips, 1 spine, 2 head, 3 arm_l, 4 hand_l, 5 arm_r, 6 hand_r, 7 leg_l, 8 leg_r
            m.Box(0, new Vector3(0, 0.92f, 0), new Vector3(0.36f, 0.2f, 0.2f), MeshBuilder.White);
            m.Box(1, new Vector3(0, 1.3f, 0), new Vector3(0.44f, 0.5f, 0.26f), MeshBuilder.White);
            if (lod == 0) m.Ellipsoid(2, new Vector3(0, 1.72f, 0), new Vector3(0.15f, 0.16f, 0.15f), 14, 9, MeshBuilder.Skin);
            else m.Ellipsoid(2, new Vector3(0, 1.72f, 0), new Vector3(0.15f, 0.16f, 0.15f), 8, 5, MeshBuilder.Skin);
            m.Box(2, new Vector3(0, 1.7f, -0.16f), new Vector3(0.06f, 0.06f, 0.1f), accent); // the nose shows which way it faces
            m.Box(3, new Vector3(-0.34f, 1.25f, 0), new Vector3(0.1f, 0.5f, 0.1f), MeshBuilder.White);
            m.Box(4, new Vector3(-0.3f, 0.9f, 0), new Vector3(0.11f, 0.11f, 0.11f), MeshBuilder.Skin);
            m.Box(5, new Vector3(0.34f, 1.25f, 0), new Vector3(0.1f, 0.5f, 0.1f), MeshBuilder.White);
            m.Box(6, new Vector3(0.3f, 0.9f, 0), new Vector3(0.11f, 0.11f, 0.11f), MeshBuilder.Skin);
            m.Box(7, new Vector3(-0.12f, 0.45f, 0), new Vector3(0.15f, 0.9f, 0.15f), Mul(MeshBuilder.White, 0.8f));
            m.Box(8, new Vector3(0.12f, 0.45f, 0), new Vector3(0.15f, 0.9f, 0.15f), Mul(MeshBuilder.White, 0.8f));
            if (lod == 0)
            {
                m.Box(0, new Vector3(0, 1.07f, 0), new Vector3(0.46f, 0.07f, 0.28f), MeshBuilder.Dark); // belt
                m.Box(7, new Vector3(-0.12f, 0.05f, -0.03f), new Vector3(0.17f, 0.1f, 0.24f), MeshBuilder.Dark); // boots
                m.Box(8, new Vector3(0.12f, 0.05f, -0.03f), new Vector3(0.17f, 0.1f, 0.24f), MeshBuilder.Dark);
                m.Ellipsoid(3, new Vector3(-0.3f, 1.5f, 0), new Vector3(0.09f, 0.09f, 0.09f), 8, 5, MeshBuilder.White); // shoulders
                m.Ellipsoid(5, new Vector3(0.3f, 1.5f, 0), new Vector3(0.09f, 0.09f, 0.09f), 8, 5, MeshBuilder.White);
            }
            switch (hash % 3)
            {
                case 0: // hat
                    m.Box(2, new Vector3(0, 1.93f, 0), new Vector3(0.28f, 0.12f, 0.28f), accent);
                    m.Box(2, new Vector3(0, 1.87f, 0), new Vector3(0.4f, 0.03f, 0.4f), accent);
                    break;
                case 1: // pauldrons
                    m.Box(1, new Vector3(-0.3f, 1.52f, 0), new Vector3(0.2f, 0.1f, 0.26f), accent);
                    m.Box(1, new Vector3(0.3f, 1.52f, 0), new Vector3(0.2f, 0.1f, 0.26f), accent);
                    break;
                default: // pack
                    m.Box(1, new Vector3(0, 1.3f, 0.2f), new Vector3(0.3f, 0.35f, 0.12f), accent);
                    break;
            }
            return 0;
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
        },
    };

    // ---------------- blob ----------------

    public static Rig Blob() => new()
    {
        Name = "blob",
        Bones = new[] { B("root", -1, 0, 0, 0), B("body", 0, 0, 0.35f, 0) },
        Body = (m, lod, hash, accent) =>
        {
            if (lod == 0) m.Ellipsoid(1, new Vector3(0, 0.35f, 0), new Vector3(0.4f, 0.35f, 0.4f), 16, 10, MeshBuilder.White);
            else m.Ellipsoid(1, new Vector3(0, 0.35f, 0), new Vector3(0.4f, 0.35f, 0.4f), 8, 5, MeshBuilder.White);
            m.Box(1, new Vector3(-0.13f, 0.45f, -0.36f), new Vector3(0.08f, 0.1f, 0.06f), MeshBuilder.Dark);
            m.Box(1, new Vector3(0.13f, 0.45f, -0.36f), new Vector3(0.08f, 0.1f, 0.06f), MeshBuilder.Dark);
            if (lod == 0) m.Ellipsoid(1, new Vector3(0, 0.62f, 0.05f), new Vector3(0.12f, 0.08f, 0.12f), 8, 5, accent);
            return 0;
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
        Body = (m, lod, hash, accent) =>
        {
            m.Box(0, new Vector3(0, 0.05f, 0), new Vector3(0.35f, 0.1f, 0.35f), MeshBuilder.Dark);
            m.Box(1, new Vector3(0, 0.35f, 0), new Vector3(0.12f, 0.5f, 0.12f), MeshBuilder.White);
            if (lod == 0) m.Ellipsoid(1, new Vector3(0, 0.65f, 0), new Vector3(0.2f, 0.2f, 0.2f), 14, 9, MeshBuilder.White);
            else m.Ellipsoid(1, new Vector3(0, 0.65f, 0), new Vector3(0.2f, 0.2f, 0.2f), 8, 5, MeshBuilder.White);
            m.Box(1, new Vector3(0, 0.66f, -0.2f), new Vector3(0.07f, 0.07f, 0.08f), accent);
            m.Box(2, new Vector3(-0.2f, 0.55f, 0), new Vector3(0.3f, 0.04f, 0.14f), accent);
            m.Box(3, new Vector3(0.2f, 0.55f, 0), new Vector3(0.3f, 0.04f, 0.14f), accent);
            return 0;
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
        Body = (m, lod, hash, accent) =>
        {
            var wasp = hash % 2 == 1;
            var radii = wasp ? new Vector3(0.2f, 0.17f, 0.4f) : new Vector3(0.36f, 0.26f, 0.48f);
            if (lod == 0) m.Ellipsoid(1, new Vector3(0, 0.35f, 0), radii, 14, 9, MeshBuilder.White);
            else m.Ellipsoid(1, new Vector3(0, 0.35f, 0), radii, 8, 5, MeshBuilder.White);
            m.Ellipsoid(1, new Vector3(0, 0.36f, -radii.Z - 0.08f), new Vector3(0.14f, 0.13f, 0.13f), lod == 0 ? 10 : 6, lod == 0 ? 7 : 4, MeshBuilder.Dark);
            m.Box(1, new Vector3(0, 0.3f, -radii.Z - 0.2f), new Vector3(0.06f, 0.06f, 0.08f), accent);
            if (wasp) m.Box(1, new Vector3(0, 0.3f, radii.Z + 0.12f), new Vector3(0.05f, 0.05f, 0.25f), accent); // stinger
            m.Box(2, new Vector3(-0.2f, 0.62f, 0.05f), new Vector3(0.28f, 0.02f, 0.4f), accent);
            m.Box(3, new Vector3(0.2f, 0.62f, 0.05f), new Vector3(0.28f, 0.02f, 0.4f), accent);
            if (lod == 0)
            {
                for (var i = 0; i < 3; i++)
                {
                    var z = -0.25f + i * 0.25f;
                    m.Box(0, new Vector3(-0.3f, 0.15f, z), new Vector3(0.25f, 0.05f, 0.05f), MeshBuilder.Dark);
                    m.Box(0, new Vector3(0.3f, 0.15f, z), new Vector3(0.25f, 0.05f, 0.05f), MeshBuilder.Dark);
                }
            }
            return 0;
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
        Body = (m, lod, hash, accent) =>
        {
            m.Box(1, new Vector3(0, 0.6f, 0), new Vector3(0.5f, 0.4f, 1.0f), MeshBuilder.White);
            if (lod == 0) m.Ellipsoid(2, new Vector3(0, 0.65f, -0.62f), new Vector3(0.17f, 0.17f, 0.2f), 12, 8, MeshBuilder.White);
            else m.Box(2, new Vector3(0, 0.65f, -0.6f), new Vector3(0.3f, 0.3f, 0.35f), MeshBuilder.White);
            m.Box(2, new Vector3(0, 0.62f, -0.82f), new Vector3(0.1f, 0.1f, 0.08f), accent);
            foreach (var (bone, x, z) in new[] { (3, -0.2f, -0.35f), (4, 0.2f, -0.35f), (5, -0.2f, 0.35f), (6, 0.2f, 0.35f) })
                m.Box(bone, new Vector3(x, 0.25f, z), new Vector3(0.12f, 0.5f, 0.12f), Mul(MeshBuilder.White, 0.8f));
            if (lod == 0)
            {
                m.Box(2, new Vector3(-0.1f, 0.58f, -0.78f), new Vector3(0.03f, 0.03f, 0.14f), MeshBuilder.Skin); // tusks
                m.Box(2, new Vector3(0.1f, 0.58f, -0.78f), new Vector3(0.03f, 0.03f, 0.14f), MeshBuilder.Skin);
                for (var i = 0; i < 5; i++) m.Box(1, new Vector3(0, 0.85f, -0.4f + i * 0.2f), new Vector3(0.06f, 0.12f, 0.1f), accent); // back spikes
                m.Box(1, new Vector3(0, 0.7f, 0.55f), new Vector3(0.04f, 0.04f, 0.2f), MeshBuilder.Dark); // tail
            }
            return 0;
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
