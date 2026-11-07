using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Game.ThemeGen;

/// <summary>
/// The generated placeholder pack (ADR-002 row 31): one file per asset key of <c>themes/default/asset-manifest.json</c>, plus a small second theme that
/// overrides a few models so the tests can prove a swap. Everything is derived from the manifest's keys and metadata, so nothing here names game content.
/// </summary>
public static class Pack
{
    private const string Generator = "themegen (T-207)";

    /// <summary>Folders the generator owns inside a theme; files in them that it does not produce are stale.</summary>
    public static readonly string[] OwnedFolders = { "models", "anims", "icons", "portraits", "audio", "maps", "vfx" };

    /// <summary>The theme the client falls back to for anything another theme leaves out.</summary>
    public const string DefaultTheme = "default";

    /// <summary>The second, partial theme: a few models in other colours.</summary>
    public const string TestTheme = "test";

    private static readonly ThemeStyle DefaultStyle = new("", 0f);
    private static readonly ThemeStyle TestStyle = new("test:", 0.5f);

    /// <summary>Every file of every theme, keyed by path relative to the themes directory (forward slashes), with the manifests rewritten.</summary>
    public static SortedDictionary<string, byte[]> Generate(string themesDir)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(themesDir, DefaultTheme, "asset-manifest.json")))!.AsObject();

        var testManifest = new JsonObject();
        foreach (var (key, node) in manifest.ToList())
        {
            if (node is not JsonObject entry || key is "audio" or "fallbacks") continue;
            var (path, bytes) = Make(key, entry, DefaultStyle, manifest);
            SetFile(entry, path);
            files[DefaultTheme + "/" + path] = bytes;

            if (TestOverrides.Contains(key))
            {
                var copy = JsonNode.Parse(entry.ToJsonString())!.AsObject();
                var (testPath, testBytes) = Make(key, copy, TestStyle, manifest);
                SetFile(copy, testPath);
                files[TestTheme + "/" + testPath] = testBytes;
                testManifest[key] = copy;
            }
        }
        files[DefaultTheme + "/asset-manifest.json"] = Json(manifest);
        files[TestTheme + "/asset-manifest.json"] = Json(testManifest);
        return files;
    }

    // The test theme redraws these (a character, a monster and the ground item); everything else comes from the default theme.
    private static readonly HashSet<string> TestOverrides = new(StringComparer.Ordinal)
    {
        "model.char.wanderer", "model.monster.gel_slime", "model.prop.loot_bag",
    };

    private static void SetFile(JsonObject entry, string path)
    {
        if (entry["files"] is JsonArray list)
        {
            for (var i = 0; i < list.Count; i++) list[i] = path;
        }
        else
        {
            entry["file"] = path;
        }
    }

    private static byte[] Json(JsonObject manifest)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" };
        var text = manifest.ToJsonString(options);
        // Arrays of plain values stay on one line, as hand-written manifests have them: "pitch": [0.95, 1.05].
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[\s*([^\[\]{}]*?)\s*\]", m => "[" + System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @",\s*\n\s*", ", ") + "]");
        return Encoding.UTF8.GetBytes(text + "\n");
    }

    /// <summary>The file for one key: its path inside the theme and its bytes.</summary>
    private static (string Path, byte[] Bytes) Make(string key, JsonObject entry, ThemeStyle style, JsonObject manifest)
    {
        var rest = key[(key.IndexOf('.') + 1)..].Replace('.', '/');
        var color = KeyColor.Of(key, style);
        var hash = KeyColor.Hash(style.Salt + key);

        if (key.StartsWith("model.", StringComparison.Ordinal)) return ($"models/{rest}.glb", Model(key, entry, color, hash));
        if (key.StartsWith("anim.", StringComparison.Ordinal)) return ($"anims/{rest}.glb", AnimLibrary(key, entry));
        if (key.StartsWith("icon.", StringComparison.Ordinal)) return ($"icons/{rest}.png", Media.Png(color, hash));
        if (key.StartsWith("portrait.", StringComparison.Ordinal)) return ($"portraits/{rest}.png", Media.Png(color, hash));
        if (key.StartsWith("sfx.", StringComparison.Ordinal) || key.StartsWith("music.", StringComparison.Ordinal) || key.StartsWith("ambience.", StringComparison.Ordinal))
            return ("audio/silence.ogg", Media.SilentOgg());
        if (key.StartsWith("scene.", StringComparison.Ordinal)) return ("maps/placeholder.tscn", Encoding.UTF8.GetBytes(Media.MapScene));
        if (key.StartsWith("vfx.", StringComparison.Ordinal)) return ("vfx/placeholder.tscn", Encoding.UTF8.GetBytes(Media.VfxScene));
        throw new InvalidOperationException($"No generator for asset key '{key}'; add one to tools/themegen/Pack.cs.");
    }

    // ---------------- models ----------------

    private static byte[] Model(string key, JsonObject entry, Vector4 color, uint hash)
    {
        var rig = entry["rig"]?.GetValue<string>();
        var attach = entry["attach"]?.GetValue<string>();
        var skinned = entry["skinned"]?.GetValue<string>();
        var hasAnims = entry["anims"] is not null;

        if (rig is not null && hasAnims)
        {
            var r = Rig.Get(rig);
            var lod0 = new MeshBuilder();
            var lod1 = new MeshBuilder();
            r.Body(lod0, 0, hash, Accent(color));
            r.Body(lod1, 1, hash, Accent(color));
            return GlbWriter.Write(key, Generator, color, r.Bones, new[] { ("Body_LOD0", lod0.Mesh), ("Body_LOD1", lod1.Mesh) }, Array.Empty<Clip>());
        }

        if (rig is not null || skinned is not null)
        {
            // A part that follows the shared rig: a face on the head bone or a garment on the torso. Skinned to the full rig so it moves with the body.
            var r = Rig.Get(rig ?? skinned!);
            var lod0 = new MeshBuilder();
            var bone = r.Index(skinned is not null ? "spine" : "head");
            if (skinned is not null)
            {
                lod0.Box(bone, new Vector3(0, 1.3f, 0), new Vector3(0.48f, 0.54f, 0.3f), MeshBuilder.White);
                lod0.Box(r.Index("hips"), new Vector3(0, 0.98f, 0), new Vector3(0.4f, 0.14f, 0.24f), MeshBuilder.Dark);
            }
            else
            {
                lod0.Ellipsoid(bone, new Vector3(0, 1.72f, 0), new Vector3(0.155f, 0.165f, 0.155f), 12, 8, MeshBuilder.White);
                lod0.Box(bone, new Vector3(-0.06f, 1.74f, -0.15f), new Vector3(0.04f, 0.04f, 0.03f), MeshBuilder.Dark);
                lod0.Box(bone, new Vector3(0.06f, 1.74f, -0.15f), new Vector3(0.04f, 0.04f, 0.03f), MeshBuilder.Dark);
                lod0.Box(bone, new Vector3(0, 1.64f, -0.15f), new Vector3(0.08f, 0.02f, 0.03f), Accent(color));
            }
            return GlbWriter.Write(key, Generator, color, r.Bones, new[] { ("Body_LOD0", lod0.Mesh) }, Array.Empty<Clip>());
        }

        // Static meshes, positioned relative to their attach bone (or the ground for props).
        var m = new MeshBuilder();
        switch (attach)
        {
            case "head" when key.Contains(".hair", StringComparison.Ordinal):
                m.Ellipsoid(-1, new Vector3(0, 0.2f, 0.01f), new Vector3(0.17f, 0.13f, 0.17f), 12, 7, MeshBuilder.White);
                if (hash % 2 == 0) m.Box(-1, new Vector3(0, 0.0f, 0.14f), new Vector3(0.2f, 0.3f, 0.06f), MeshBuilder.White);
                else m.Box(-1, new Vector3(0, 0.33f, 0), new Vector3(0.06f, 0.1f, 0.06f), MeshBuilder.White);
                break;
            case "head":
                m.Ellipsoid(-1, new Vector3(0, 0.2f, 0), new Vector3(0.19f, 0.12f, 0.19f), 12, 7, MeshBuilder.White);
                m.Box(-1, new Vector3(0, 0.12f, -0.16f), new Vector3(0.2f, 0.03f, 0.1f), Accent(color));
                break;
            case "hand_r":
                if (hash % 2 == 0)
                {
                    m.Box(-1, new Vector3(0, 0, -0.1f), new Vector3(0.06f, 0.06f, 0.2f), MeshBuilder.Dark); // grip, blade
                    m.Box(-1, new Vector3(0, 0, -0.5f), new Vector3(0.05f, 0.1f, 0.6f), MeshBuilder.White);
                }
                else
                {
                    m.Box(-1, new Vector3(0, 0.25f, 0), new Vector3(0.05f, 1.3f, 0.05f), MeshBuilder.Dark); // staff
                    m.Ellipsoid(-1, new Vector3(0, 0.95f, 0), new Vector3(0.09f, 0.09f, 0.09f), 8, 5, MeshBuilder.White);
                }
                break;
            case "hand_l":
                m.Ellipsoid(-1, new Vector3(0, 0, -0.12f), new Vector3(0.2f, 0.2f, 0.04f), 12, 7, MeshBuilder.White);
                break;
            default: // a prop on the ground
                m.Ellipsoid(-1, new Vector3(0, 0.12f, 0), new Vector3(0.2f, 0.16f, 0.2f), 10, 6, MeshBuilder.White);
                m.Box(-1, new Vector3(0, 0.3f, 0), new Vector3(0.08f, 0.08f, 0.08f), Accent(color));
                break;
        }
        if (key == "model.missing")
        {
            // Unmistakable: a big magenta cube with dark corners.
            m = new MeshBuilder();
            m.Box(-1, new Vector3(0, 0.4f, 0), new Vector3(0.8f, 0.8f, 0.8f), MeshBuilder.White);
            m.Box(-1, new Vector3(0, 0.4f, -0.41f), new Vector3(0.3f, 0.3f, 0.04f), MeshBuilder.Dark);
            color = new Vector4(1f, 0f, 1f, 1f);
        }
        return GlbWriter.Write(key, Generator, color, Array.Empty<Bone>(), new[] { ("Body_LOD0", m.Mesh) }, Array.Empty<Clip>());
    }

    private static Vector4 Accent(Vector4 color) => new(1 - color.X * 0.6f, 1 - color.Y * 0.6f, 1 - color.Z * 0.6f, 1);

    // ---------------- animation libraries ----------------

    private static byte[] AnimLibrary(string key, JsonObject entry)
    {
        var rig = Rig.Get(entry["rig"]!.GetValue<string>());
        var clips = rig.MakeClips(rig);
        var wanted = (entry["clips"] as JsonObject)?.Select(p => p.Value!.GetValue<string>()).ToHashSet() ?? new HashSet<string>();
        var have = clips.Select(c => c.Name).ToHashSet();
        foreach (var name in wanted)
            if (!have.Contains(name)) throw new InvalidOperationException($"{key}: rig {rig.Name} has no clip '{name}'");

        // The library is a rig with one tiny triangle, so an importer builds the same skeleton it builds for a model and the tracks line up.
        var dummy = new MeshBuilder();
        dummy.Mesh.Add(new Vector3(0, 0, 0), Vector3.UnitY, MeshBuilder.White, 0);
        dummy.Mesh.Add(new Vector3(0.001f, 0, 0), Vector3.UnitY, MeshBuilder.White, 0);
        dummy.Mesh.Add(new Vector3(0, 0, 0.001f), Vector3.UnitY, MeshBuilder.White, 0);
        dummy.Mesh.Indices.AddRange(new ushort[] { 0, 2, 1 });
        return GlbWriter.Write(key, Generator, new Vector4(1, 1, 1, 1), rig.Bones, new[] { ("LibraryAnchor", dummy.Mesh) }, clips.Where(c => wanted.Count == 0 || wanted.Contains(c.Name)).ToArray());
    }
}
