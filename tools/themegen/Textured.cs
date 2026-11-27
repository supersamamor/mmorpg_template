using System.Numerics;
using System.Text.Json.Nodes;
using Game.Content.Themes;

namespace Game.ThemeGen;

/// <summary>
/// T-711: a small textured theme for tests and measuring, never committed. It takes the default theme's character parts (bodies, faces, hairstyles, outfit pieces and
/// worn equipment), gives every vertex a texture coordinate and every material a texture embedded in its own file: one atlas image for all of them, the same pixels in
/// every file, and another image for the parts named in <c>other</c>. Vertex colours stay, so the character's colours still show on it. Whatever it leaves out
/// (job models, monsters, maps) comes from <c>default</c>.
/// </summary>
public static class TexturedTheme
{
    /// <summary>The key prefixes of a character's parts: what one character merges into one mesh.</summary>
    public static readonly string[] PartPrefixes = { "model.char.body_", "model.char.face_", "model.char.hair_", "model.outfit.", "model.equip." };

    /// <summary>The atlas every part uses: a 64x64 PNG, light so the vertex colours read through it.</summary>
    public static byte[] Atlas { get; } = Media.Png(new Vector4(0.96f, 0.94f, 0.9f, 1), 1);

    /// <summary>The image of the parts that split the merge.</summary>
    public static byte[] OtherImage { get; } = Media.Png(new Vector4(0.9f, 0.96f, 0.94f, 1), 6);

    /// <summary>
    /// The files of the textured theme <paramref name="name"/>, relative to the themes folder: its manifest and one textured copy of each character part of
    /// <c>default</c>. Parts whose key is in <paramref name="other"/> get <see cref="OtherImage"/>; the rest share <see cref="Atlas"/>.
    /// </summary>
    public static SortedDictionary<string, byte[]> Generate(string themesDir, string name, IReadOnlyCollection<string> other)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(themesDir, Pack.DefaultTheme, "asset-manifest.json")))!.AsObject();
        var output = new JsonObject();
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, node) in manifest)
        {
            if (!PartPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)) || node?["file"]?.GetValue<string>() is not { } file || !file.EndsWith(".glb", StringComparison.Ordinal)) continue;
            var source = Path.Combine(themesDir, Pack.DefaultTheme, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) continue;
            files[name + "/" + file] = Texture(File.ReadAllBytes(source), other.Contains(key) ? OtherImage : Atlas);
            output[key] = node.DeepClone();
        }
        files[name + "/asset-manifest.json"] = System.Text.Encoding.UTF8.GetBytes(output.ToJsonString(GltfFile.Indented) + "\n");
        files[name + "/.gitignore"] = System.Text.Encoding.UTF8.GetBytes("# generated for a test or a measurement (T-711); never committed\n*\n");
        return files;
    }

    /// <summary>
    /// Writes <see cref="Generate"/>'s files under <paramref name="themesDir"/>; a file that is already the same is left alone, so the engine does not import it again.
    /// Returns how many were written. The folder's own <c>.gitignore</c> keeps it out of Git.
    /// </summary>
    public static int Write(string themesDir, string name, IReadOnlyCollection<string> other)
    {
        var written = 0;
        foreach (var (relative, bytes) in Generate(themesDir, name, other))
        {
            var path = Path.Combine(themesDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            written++;
        }
        return written;
    }

    /// <summary>
    /// A copy of a binary glTF with <paramref name="png"/> embedded and set as every material's base colour texture, and a <c>TEXCOORD_0</c> on every primitive
    /// (its positions projected onto the front, so the image shows). Everything else is kept as it was.
    /// </summary>
    public static byte[] Texture(byte[] glb, byte[] png)
    {
        var file = GltfFile.FromGlb(glb);
        if (file.Json["meshes"] is JsonArray meshes)
            foreach (var mesh in meshes)
                foreach (var primitive in mesh?["primitives"]?.AsArray() ?? new JsonArray())
                {
                    if (primitive?["attributes"] is not JsonObject attributes || attributes["POSITION"]?.GetValue<int>() is not { } at) continue;
                    var positions = file.ReadFloats(at);
                    var uvs = new float[positions.Length / 3 * 2];
                    for (var v = 0; v < uvs.Length / 2; v++)
                    {
                        uvs[v * 2] = positions[v * 3] * 0.7f + 0.5f; // the sampler repeats
                        uvs[v * 2 + 1] = 1f - positions[v * 3 + 1] * 0.5f;
                    }
                    attributes["TEXCOORD_0"] = file.AddFloatAccessor(uvs, "VEC2");
                }
        var image = file.AddView(png);
        file.Json["images"] = new JsonArray { new JsonObject { ["bufferView"] = image, ["mimeType"] = "image/png" } };
        file.Json["samplers"] = new JsonArray { new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987 } };
        file.Json["textures"] = new JsonArray { new JsonObject { ["source"] = 0, ["sampler"] = 0 } };
        foreach (var material in file.Array("materials"))
        {
            if (material is not JsonObject m) continue;
            if (m["pbrMetallicRoughness"] is not JsonObject pbr) m["pbrMetallicRoughness"] = pbr = new JsonObject();
            pbr["baseColorTexture"] = new JsonObject { ["index"] = 0 };
        }
        return file.ToGlb();
    }
}
