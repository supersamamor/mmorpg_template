using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Game.Content.Themes;

/// <summary>
/// A glTF 2.0 asset held as its JSON tree and one binary buffer (T-224): what <c>ThemeCheck</c> measures and <c>tools/themeimport</c> edits. Loads a binary
/// <c>.glb</c>, or a <c>.gltf</c> whose buffers and images (files next to it or <c>data:</c> URIs) are folded into the one buffer, and writes a <c>.glb</c>. No Godot,
/// no I/O beyond reading the files it is given.
/// </summary>
public sealed class GltfFile
{
    private const uint Magic = 0x46546C67; // "glTF"
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;

    private GltfFile(JsonObject json, byte[] bin)
    {
        Json = json;
        Bin = bin;
    }

    /// <summary>The glTF JSON.</summary>
    public JsonObject Json { get; }

    /// <summary>The single binary buffer (buffer 0).</summary>
    public byte[] Bin { get; private set; }

    /// <summary>The <c>nodes</c> array (empty when there is none).</summary>
    public JsonArray Nodes => Array("nodes");

    /// <summary>A top-level array, made if missing.</summary>
    public JsonArray Array(string name)
    {
        if (Json[name] is JsonArray a) return a;
        var made = new JsonArray();
        Json[name] = made;
        return made;
    }

    /// <summary>Loads <paramref name="path"/> (<c>.glb</c> or <c>.gltf</c>). Throws <see cref="InvalidDataException"/> with a readable reason.</summary>
    public static GltfFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == Magic) return FromGlb(bytes, path);
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        if (!text.TrimStart().StartsWith("{", StringComparison.Ordinal)) throw new InvalidDataException($"{path} is neither a binary glTF (.glb) nor a glTF JSON (.gltf)");
        return FromGltf(text, Path.GetDirectoryName(Path.GetFullPath(path))!, path);
    }

    /// <summary>Reads a binary glTF.</summary>
    public static GltfFile FromGlb(byte[] bytes, string name = "glb")
    {
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != Magic) throw new InvalidDataException($"{name} is not a binary glTF");
        var length = (int)BitConverter.ToUInt32(bytes, 12);
        if (BitConverter.ToUInt32(bytes, 16) != JsonChunk || 20 + length > bytes.Length) throw new InvalidDataException($"{name}: the first chunk is not glTF JSON");
        var json = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 20, length))?.AsObject() ?? throw new InvalidDataException($"{name}: empty glTF JSON");
        var at = 20 + length;
        var bin = System.Array.Empty<byte>();
        if (at + 8 <= bytes.Length && BitConverter.ToUInt32(bytes, at + 4) == BinChunk)
        {
            var binLength = (int)BitConverter.ToUInt32(bytes, at);
            bin = bytes.AsSpan(at + 8, Math.Min(binLength, bytes.Length - at - 8)).ToArray();
        }
        if (json["buffers"] is JsonArray buffers && buffers.Count > 1) throw new InvalidDataException($"{name}: a .glb with more than one buffer is not supported; re-export it from Blender");
        return new GltfFile(json, bin);
    }

    private static GltfFile FromGltf(string text, string folder, string name)
    {
        var json = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException($"{name}: empty glTF JSON");
        var parts = new List<byte[]>();
        if (json["buffers"] is JsonArray buffers)
            foreach (var buffer in buffers)
                parts.Add(ReadUri(buffer?["uri"]?.GetValue<string>(), folder, name));
        // One buffer: each old buffer's views move by where that buffer now starts.
        var starts = new List<int>();
        var bin = new List<byte>();
        foreach (var part in parts)
        {
            while (bin.Count % 4 != 0) bin.Add(0);
            starts.Add(bin.Count);
            bin.AddRange(part);
        }
        if (json["bufferViews"] is JsonArray views)
            foreach (var view in views.OfType<JsonObject>())
            {
                var buffer = view["buffer"]?.GetValue<int>() ?? 0;
                view["buffer"] = 0;
                view["byteOffset"] = (view["byteOffset"]?.GetValue<int>() ?? 0) + starts[buffer];
            }
        var file = new GltfFile(json, bin.ToArray());
        if (json["images"] is JsonArray images)
            foreach (var image in images.OfType<JsonObject>())
            {
                if (image["uri"]?.GetValue<string>() is not { } uri) continue;
                var data = ReadUri(uri, folder, name);
                image.Remove("uri");
                image["mimeType"] ??= uri.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || uri.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("data:image/jpeg", StringComparison.Ordinal)
                    ? "image/jpeg" : "image/png";
                image["bufferView"] = file.AddView(data);
            }
        file.SyncBuffer();
        return file;
    }

    private static byte[] ReadUri(string? uri, string folder, string name)
    {
        if (uri is null) throw new InvalidDataException($"{name}: a buffer without a uri in a .gltf");
        if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = uri.IndexOf(',');
            return Convert.FromBase64String(uri[(comma + 1)..]);
        }
        var path = Path.Combine(folder, Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new InvalidDataException($"{name}: '{uri}' is not next to the .gltf");
        return File.ReadAllBytes(path);
    }

    /// <summary>The asset as a binary glTF: JSON padded with spaces, the buffer with zeros.</summary>
    public byte[] ToGlb()
    {
        SyncBuffer();
        var json = Encoding.UTF8.GetBytes(Json.ToJsonString());
        var jsonPadded = (json.Length + 3) & ~3;
        var binPadded = (Bin.Length + 3) & ~3;
        var total = 12 + 8 + jsonPadded + (Bin.Length > 0 ? 8 + binPadded : 0);
        var output = new byte[total];
        var span = output.AsSpan();
        BitConverter.TryWriteBytes(span[0..], Magic);
        BitConverter.TryWriteBytes(span[4..], 2u);
        BitConverter.TryWriteBytes(span[8..], (uint)total);
        BitConverter.TryWriteBytes(span[12..], (uint)jsonPadded);
        BitConverter.TryWriteBytes(span[16..], JsonChunk);
        json.CopyTo(span[20..]);
        for (var i = 20 + json.Length; i < 20 + jsonPadded; i++) output[i] = (byte)' ';
        if (Bin.Length > 0)
        {
            var at = 20 + jsonPadded;
            BitConverter.TryWriteBytes(span[at..], (uint)binPadded);
            BitConverter.TryWriteBytes(span[(at + 4)..], BinChunk);
            Bin.CopyTo(span[(at + 8)..]);
        }
        return output;
    }

    private void SyncBuffer()
    {
        if (Bin.Length == 0 && Json["bufferViews"] is null) { Json.Remove("buffers"); return; }
        Json["buffers"] = new JsonArray { new JsonObject { ["byteLength"] = Bin.Length } };
    }

    /// <summary>Appends <paramref name="data"/> to the buffer as a new buffer view (4-byte aligned) and returns its index.</summary>
    public int AddView(byte[] data, int? target = null)
    {
        var start = (Bin.Length + 3) & ~3;
        var grown = new byte[start + data.Length];
        Bin.CopyTo(grown, 0);
        data.CopyTo(grown, start);
        Bin = grown;
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = start, ["byteLength"] = data.Length };
        if (target is { } t) view["target"] = t;
        var views = Array("bufferViews");
        views.Add(view);
        return views.Count - 1;
    }

    /// <summary>Appends a float accessor (<c>VEC3</c>, <c>VEC4</c>, ...) and returns its index; <paramref name="minMax"/> adds the bounds glTF requires on positions.</summary>
    public int AddFloatAccessor(float[] values, string type, bool minMax = false)
    {
        var width = Width(type);
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        var accessor = new JsonObject { ["bufferView"] = AddView(bytes, 34962), ["componentType"] = 5126, ["count"] = values.Length / width, ["type"] = type };
        if (minMax)
        {
            var min = new JsonArray();
            var max = new JsonArray();
            for (var c = 0; c < width; c++)
            {
                var lo = float.MaxValue;
                var hi = float.MinValue;
                for (var i = c; i < values.Length; i += width) { lo = Math.Min(lo, values[i]); hi = Math.Max(hi, values[i]); }
                min.Add(lo);
                max.Add(hi);
            }
            accessor["min"] = min;
            accessor["max"] = max;
        }
        var accessors = Array("accessors");
        accessors.Add(accessor);
        return accessors.Count - 1;
    }

    /// <summary>Number of components of an accessor type.</summary>
    public static int Width(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16, _ => 1 };

    /// <summary>The element count of an accessor.</summary>
    public int Count(int accessor) => Accessor(accessor)["count"]?.GetValue<int>() ?? 0;

    private JsonObject Accessor(int index) => Json["accessors"]?[index]?.AsObject() ?? throw new InvalidDataException($"accessor {index} does not exist");

    /// <summary>
    /// An accessor's values as floats (normalized integers scaled to 0..1 or -1..1, plain integers as they are), components flat. A sparse accessor or one without
    /// a buffer view reads as zeros, which is enough for measuring.
    /// </summary>
    public float[] ReadFloats(int index)
    {
        var accessor = Accessor(index);
        var type = accessor["type"]!.GetValue<string>();
        var width = Width(type);
        var count = accessor["count"]!.GetValue<int>();
        var component = accessor["componentType"]!.GetValue<int>();
        var normalized = accessor["normalized"]?.GetValue<bool>() ?? false;
        var result = new float[count * width];
        if (accessor["bufferView"] is not { } viewIndex) return result;
        var view = Json["bufferViews"]![viewIndex.GetValue<int>()]!.AsObject();
        var size = component switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
        var offset = (view["byteOffset"]?.GetValue<int>() ?? 0) + (accessor["byteOffset"]?.GetValue<int>() ?? 0);
        var stride = view["byteStride"]?.GetValue<int>() ?? size * width;
        if (type.StartsWith("MAT", StringComparison.Ordinal) && view["byteStride"] is null) stride = size * width; // padded matrix columns only matter for byte/short matrices
        for (var i = 0; i < count; i++)
            for (var c = 0; c < width; c++)
            {
                var at = offset + i * stride + c * size;
                if (at + size > Bin.Length) return result;
                result[i * width + c] = component switch
                {
                    5126 => BitConverter.ToSingle(Bin, at),
                    5125 => BitConverter.ToUInt32(Bin, at),
                    5123 => normalized ? BitConverter.ToUInt16(Bin, at) / 65535f : BitConverter.ToUInt16(Bin, at),
                    5122 => normalized ? Math.Max(BitConverter.ToInt16(Bin, at) / 32767f, -1) : BitConverter.ToInt16(Bin, at),
                    5121 => normalized ? Bin[at] / 255f : Bin[at],
                    5120 => normalized ? Math.Max((sbyte)Bin[at] / 127f, -1) : (sbyte)Bin[at],
                    _ => 0,
                };
            }
        return result;
    }

    /// <summary>The bytes of an image (by image index), from its buffer view; null for an external or missing one.</summary>
    public byte[]? ImageBytes(int image)
    {
        var node = Json["images"]?[image];
        if (node?["bufferView"] is { } viewIndex)
        {
            var view = Json["bufferViews"]![viewIndex.GetValue<int>()]!.AsObject();
            var offset = view["byteOffset"]?.GetValue<int>() ?? 0;
            var length = view["byteLength"]!.GetValue<int>();
            return offset + length <= Bin.Length ? Bin.AsSpan(offset, length).ToArray() : null;
        }
        if (node?["uri"]?.GetValue<string>() is { } uri && uri.StartsWith("data:", StringComparison.Ordinal)) return Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
        return null;
    }

    /// <summary>A node's local matrix (row-vector convention, as <see cref="Matrix4x4"/> uses): its <c>matrix</c>, else scale, then rotation, then translation.</summary>
    public static Matrix4x4 LocalMatrix(JsonNode? node)
    {
        if (node?["matrix"] is JsonArray m && m.Count == 16)
        {
            var v = m.Select(x => x!.GetValue<float>()).ToArray();
            return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }
        var s = Vec(node?["scale"], Vector3.One);
        var r = node?["rotation"] is JsonArray q && q.Count == 4 ? new Quaternion(q[0]!.GetValue<float>(), q[1]!.GetValue<float>(), q[2]!.GetValue<float>(), q[3]!.GetValue<float>()) : Quaternion.Identity;
        var t = Vec(node?["translation"], Vector3.Zero);
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }

    private static Vector3 Vec(JsonNode? node, Vector3 fallback) =>
        node is JsonArray a && a.Count == 3 ? new Vector3(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()) : fallback;

    /// <summary>Each node's parent index (-1 for a root).</summary>
    public int[] Parents()
    {
        var nodes = Nodes;
        var parents = Enumerable.Repeat(-1, nodes.Count).ToArray();
        for (var i = 0; i < nodes.Count; i++)
            if (nodes[i]?["children"] is JsonArray children)
                foreach (var child in children)
                    if (child?.GetValue<int>() is { } c && c >= 0 && c < parents.Length) parents[c] = i;
        return parents;
    }

    /// <summary>Each node's world matrix at rest.</summary>
    public Matrix4x4[] WorldMatrices()
    {
        var nodes = Nodes;
        var parents = Parents();
        var world = new Matrix4x4[nodes.Count];
        var done = new bool[nodes.Count];
        Matrix4x4 Of(int i, int depth)
        {
            if (done[i]) return world[i];
            var local = LocalMatrix(nodes[i]);
            world[i] = parents[i] >= 0 && depth < 256 ? local * Of(parents[i], depth + 1) : local;
            done[i] = true;
            return world[i];
        }
        for (var i = 0; i < nodes.Count; i++) Of(i, 0);
        return world;
    }

    /// <summary>The root nodes of the default scene (or of every scene's first, or every parentless node).</summary>
    public List<int> SceneRoots()
    {
        var sceneIndex = Json["scene"]?.GetValue<int>() ?? 0;
        if (Json["scenes"]?[sceneIndex]?["nodes"] is JsonArray roots) return roots.Select(r => r!.GetValue<int>()).ToList();
        var parents = Parents();
        return Enumerable.Range(0, parents.Length).Where(i => parents[i] < 0).ToList();
    }

    /// <summary>The name of node <paramref name="index"/>, or <c>node&lt;index&gt;</c>.</summary>
    public string NodeName(int index) => Nodes[index]?["name"]?.GetValue<string>() ?? "node" + index;

    /// <summary>Reads a JSON number that may be stored as an integer or a float.</summary>
    internal static float Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<float>(out var f) ? f : 0;

    /// <summary>The width and height of a PNG or JPEG, or null.</summary>
    public static (int Width, int Height)? ImageSize(byte[] data)
    {
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
            return (BigEndian(data, 16), BigEndian(data, 20));
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var at = 2;
            while (at + 9 < data.Length)
            {
                if (data[at] != 0xFF) { at++; continue; }
                var marker = data[at + 1];
                var length = (data[at + 2] << 8) | data[at + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ((data[at + 7] << 8) | data[at + 8], (data[at + 5] << 8) | data[at + 6]);
                at += 2 + length;
            }
        }
        return null;
    }

    private static int BigEndian(byte[] d, int at) => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];

    /// <summary>Parses JSON text the way the loader does (for tests and tools).</summary>
    public static GltfFile FromJson(string json, byte[] bin) => new(JsonNode.Parse(json)!.AsObject(), bin);

    /// <summary>The JSON serializer options the tools write manifests with.</summary>
    public static JsonSerializerOptions Indented { get; } = new() { WriteIndented = true, NewLine = "\n" };
}
