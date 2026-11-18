using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Game.ThemeGen;

/// <summary>One bone: a name, its parent (-1 for the root) and its rest position relative to the parent.</summary>
internal sealed record Bone(string Name, int Parent, Vector3 Local);

/// <summary>A triangle mesh bound rigidly to bones (or to nothing).</summary>
internal sealed class MeshData
{
    public List<Vector3> Positions { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector4> Colors { get; } = new();
    public List<byte> Joints { get; } = new();
    public List<ushort> Indices { get; } = new();

    public int Triangles => Indices.Count / 3;

    /// <summary>
    /// Each vertex's normal averaged with every vertex at the same place (T-222): the direction an inverted-hull outline pushes the vertex out, so the hull stays closed
    /// at hard edges (a box's corner has three normals but one outline direction). Written as the glTF <c>TANGENT</c> (w = 1); the outline shader reads it there.
    /// </summary>
    public List<Vector3> OutlineNormals()
    {
        var sums = new Dictionary<(int, int, int), Vector3>();
        static (int, int, int) Cell(Vector3 p) => ((int)MathF.Round(p.X * 10000f), (int)MathF.Round(p.Y * 10000f), (int)MathF.Round(p.Z * 10000f));
        for (var i = 0; i < Positions.Count; i++)
        {
            var cell = Cell(Positions[i]);
            sums[cell] = sums.GetValueOrDefault(cell) + Normals[i];
        }
        var result = new List<Vector3>(Positions.Count);
        for (var i = 0; i < Positions.Count; i++)
        {
            var sum = sums[Cell(Positions[i])];
            result.Add(sum.LengthSquared() > 1e-8f ? Vector3.Normalize(sum) : Normals[i]);
        }
        return result;
    }

    public void Add(Vector3 p, Vector3 n, Vector4 c, int bone)
    {
        Positions.Add(p);
        Normals.Add(n);
        Colors.Add(c);
        Joints.Add((byte)Math.Max(0, bone));
    }
}

/// <summary>One animation channel: a bone's translation, rotation or scale sampled over time.</summary>
internal sealed record Channel(int Bone, string Path, float[] Times, float[] Values);

/// <summary>A clip: a name and its channels.</summary>
internal sealed record Clip(string Name, List<Channel> Channels);

/// <summary>
/// Writes a binary glTF 2.0 file by hand: one rig (optional), up to two LOD meshes named <c>Body_LOD0</c> and <c>Body_LOD1</c> that share one material, and
/// optional animations. Output is byte-for-byte deterministic for the same input (no timestamps, fixed ordering).
/// </summary>
internal sealed class GlbWriter
{
    private readonly List<byte> _bin = new();
    private readonly JsonArray _views = new();
    private readonly JsonArray _accessors = new();

    public static byte[] Write(string key, string generator, Vector4 baseColor, IReadOnlyList<Bone> bones, IReadOnlyList<(string Name, MeshData Mesh)> meshes,
        IReadOnlyList<Clip> clips)
    {
        var w = new GlbWriter();
        return w.Build(key, generator, baseColor, bones, meshes, clips);
    }

    private byte[] Build(string key, string generator, Vector4 baseColor, IReadOnlyList<Bone> bones, IReadOnlyList<(string Name, MeshData Mesh)> meshes,
        IReadOnlyList<Clip> clips)
    {
        var nodes = new JsonArray();
        var sceneNodes = new JsonArray();
        var skinned = bones.Count > 0;

        // Node layout: [0] Armature, [1..n] bones, then the mesh nodes.
        var boneNode = new int[bones.Count];
        var world = new Vector3[bones.Count];
        if (skinned)
        {
            nodes.Add(new JsonObject { ["name"] = "Armature" });
            sceneNodes.Add(0);
            for (var i = 0; i < bones.Count; i++)
            {
                boneNode[i] = nodes.Count;
                world[i] = bones[i].Local + (bones[i].Parent >= 0 ? world[bones[i].Parent] : Vector3.Zero);
                nodes.Add(new JsonObject { ["name"] = bones[i].Name, ["translation"] = Vec(bones[i].Local) });
            }
            for (var i = 0; i < bones.Count; i++)
            {
                var parent = bones[i].Parent;
                var holder = parent < 0 ? (JsonObject)nodes[0]! : (JsonObject)nodes[boneNode[parent]]!;
                if (holder["children"] is not JsonArray list) holder["children"] = list = new JsonArray();
                list.Add(boneNode[i]);
            }
        }

        JsonObject? skin = null;
        if (skinned)
        {
            var ibm = new List<float>();
            for (var i = 0; i < bones.Count; i++)
            {
                var m = Matrix4x4.CreateTranslation(-world[i]);
                ibm.AddRange(new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 });
            }
            var joints = new JsonArray();
            foreach (var n in boneNode) joints.Add(n);
            skin = new JsonObject { ["name"] = "Skin", ["inverseBindMatrices"] = FloatAccessor(ibm, "MAT4", null), ["joints"] = joints, ["skeleton"] = boneNode[0] };
        }

        var meshArray = new JsonArray();
        foreach (var (name, mesh) in meshes)
        {
            var attributes = new JsonObject
            {
                ["POSITION"] = FloatAccessor(mesh.Positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList(), "VEC3", Bounds(mesh.Positions)),
                ["NORMAL"] = FloatAccessor(mesh.Normals.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList(), "VEC3", null),
                ["TANGENT"] = FloatAccessor(mesh.OutlineNormals().SelectMany(p => new[] { p.X, p.Y, p.Z, 1f }).ToList(), "VEC4", null),
                ["COLOR_0"] = ByteAccessor(mesh.Colors.SelectMany(c => new[] { Q(c.X), Q(c.Y), Q(c.Z), Q(c.W) }).ToArray(), "VEC4", true),
            };
            if (skinned)
            {
                attributes["JOINTS_0"] = ByteAccessor(mesh.Joints.SelectMany(j => new byte[] { j, 0, 0, 0 }).ToArray(), "VEC4", false);
                attributes["WEIGHTS_0"] = FloatAccessor(mesh.Joints.SelectMany(_ => new[] { 1f, 0f, 0f, 0f }).ToList(), "VEC4", null);
            }
            var primitive = new JsonObject { ["attributes"] = attributes, ["indices"] = IndexAccessor(mesh.Indices), ["material"] = 0, ["mode"] = 4 };
            meshArray.Add(new JsonObject { ["name"] = name, ["primitives"] = new JsonArray { primitive } });
            var node = new JsonObject { ["name"] = name, ["mesh"] = meshArray.Count - 1 };
            if (skinned) node["skin"] = 0;
            sceneNodes.Add(nodes.Count);
            nodes.Add(node);
        }

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = generator, ["extras"] = new JsonObject { ["key"] = key } },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["name"] = key, ["nodes"] = sceneNodes } },
            ["nodes"] = nodes,
            ["meshes"] = meshArray,
            ["materials"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = key,
                    ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = Vec4(baseColor), ["metallicFactor"] = 0f, ["roughnessFactor"] = 0.9f },
                },
            },
        };
        if (skin is not null) root["skins"] = new JsonArray { skin };

        if (clips.Count > 0)
        {
            var animations = new JsonArray();
            foreach (var clip in clips)
            {
                var samplers = new JsonArray();
                var channels = new JsonArray();
                foreach (var ch in clip.Channels)
                {
                    var components = ch.Path == "rotation" ? 4 : 3;
                    var input = FloatAccessor(ch.Times.ToList(), "SCALAR", (new[] { ch.Times.Min() }, new[] { ch.Times.Max() }));
                    var output = FloatAccessor(ch.Values.ToList(), components == 4 ? "VEC4" : "VEC3", null);
                    samplers.Add(new JsonObject { ["input"] = input, ["output"] = output, ["interpolation"] = "LINEAR" });
                    channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = boneNode[ch.Bone], ["path"] = ch.Path } });
                }
                animations.Add(new JsonObject { ["name"] = clip.Name, ["samplers"] = samplers, ["channels"] = channels });
            }
            root["animations"] = animations;
        }

        root["bufferViews"] = _views;
        root["accessors"] = _accessors;
        Pad(_bin, 4, 0);
        root["buffers"] = new JsonArray { new JsonObject { ["byteLength"] = _bin.Count } };

        var json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var jsonPadded = new List<byte>(json);
        Pad(jsonPadded, 4, 0x20);
        var total = 12 + 8 + jsonPadded.Count + 8 + _bin.Count;
        using var stream = new MemoryStream(total);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u); // "glTF"
        writer.Write(2u);
        writer.Write((uint)total);
        writer.Write((uint)jsonPadded.Count);
        writer.Write(0x4E4F534Au); // "JSON"
        writer.Write(jsonPadded.ToArray());
        writer.Write((uint)_bin.Count);
        writer.Write(0x004E4942u); // "BIN\0"
        writer.Write(_bin.ToArray());
        writer.Flush();
        return stream.ToArray();
    }

    private static byte Q(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private static JsonArray Vec(Vector3 v) => new() { v.X, v.Y, v.Z };

    private static JsonArray Vec4(Vector4 v) => new() { v.X, v.Y, v.Z, v.W };

    private static (float[] Min, float[] Max) Bounds(List<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (new[] { min.X, min.Y, min.Z }, new[] { max.X, max.Y, max.Z });
    }

    private static void Pad(List<byte> bytes, int to, byte value)
    {
        while (bytes.Count % to != 0) bytes.Add(value);
    }

    private int View(byte[] data, int? target)
    {
        Pad(_bin, 4, 0);
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Count, ["byteLength"] = data.Length };
        if (target is { } t) view["target"] = t;
        _bin.AddRange(data);
        _views.Add(view);
        return _views.Count - 1;
    }

    private int FloatAccessor(List<float> values, string type, (float[] Min, float[] Max)? bounds)
    {
        var count = type switch { "SCALAR" => 1, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new ArgumentException(type) };
        var data = new byte[values.Count * 4];
        Buffer.BlockCopy(values.ToArray(), 0, data, 0, data.Length);
        var accessor = new JsonObject { ["bufferView"] = View(data, null), ["componentType"] = 5126, ["count"] = values.Count / count, ["type"] = type };
        if (bounds is { } b)
        {
            accessor["min"] = new JsonArray(b.Min.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
            accessor["max"] = new JsonArray(b.Max.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        }
        _accessors.Add(accessor);
        return _accessors.Count - 1;
    }

    private int ByteAccessor(byte[] values, string type, bool normalized)
    {
        var accessor = new JsonObject { ["bufferView"] = View(values, null), ["componentType"] = 5121, ["count"] = values.Length / 4, ["type"] = type };
        if (normalized) accessor["normalized"] = true;
        _accessors.Add(accessor);
        return _accessors.Count - 1;
    }

    private int IndexAccessor(List<ushort> indices)
    {
        var data = new byte[indices.Count * 2];
        Buffer.BlockCopy(indices.ToArray(), 0, data, 0, data.Length);
        _accessors.Add(new JsonObject { ["bufferView"] = View(data, 34963), ["componentType"] = 5123, ["count"] = indices.Count, ["type"] = "SCALAR" });
        return _accessors.Count - 1;
    }
}
