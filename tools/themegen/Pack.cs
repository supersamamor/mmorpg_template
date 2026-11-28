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
    public static readonly string[] OwnedFolders = { "models", "anims", "icons", "portraits", "audio", "vfx" };

    /// <summary>The theme the client falls back to for anything another theme leaves out.</summary>
    public const string DefaultTheme = "default";

    /// <summary>The second, partial theme: a few models in other colours.</summary>
    public const string TestTheme = "test";

    private static readonly ThemeStyle DefaultStyle = new("", 0f);
    private static readonly ThemeStyle TestStyle = new("test:", 0.5f);

    /// <summary>Every file of every theme, keyed by path relative to the themes directory (forward slashes), with the manifests rewritten.</summary>
    public static SortedDictionary<string, byte[]> Generate(string themesDir, string? contentDir = null)
    {
        var weaponTypes = WeaponShapes.TypesByVisualKey(contentDir ?? Path.Combine(themesDir, "..", "content"));
        var palette = Palette.Load(Path.Combine(themesDir, DefaultTheme, Palette.FileName));
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(themesDir, DefaultTheme, "asset-manifest.json")))!.AsObject();

        var testManifest = new JsonObject();
        foreach (var (key, node) in manifest.ToList())
        {
            if (node is not JsonObject entry || key is "audio" or "fallbacks") continue;
            if (key.StartsWith("scene.", StringComparison.Ordinal)) continue; // map scenes are authored in the editor (T-208): the manifest's path stands, and validate checks them
            if (key.StartsWith("minimap.", StringComparison.Ordinal)) continue; // minimap pictures are made by the map export (T-812): export-maps.ps1 -Check checks them
            if (entry["render"] is not null) continue; // the theme's render resources (T-222: toon material, outline, environment) are authored files too
            if (entry["variant_of"] is not null) continue; // a variant (T-224) draws its base's file: nothing to generate
            var (path, bytes) = Make(key, entry, DefaultStyle, manifest, weaponTypes, palette);
            SetFile(entry, path);
            files[DefaultTheme + "/" + path] = bytes;

            if (TestOverrides.Contains(key))
            {
                var copy = JsonNode.Parse(entry.ToJsonString())!.AsObject();
                var (testPath, testBytes) = Make(key, copy, TestStyle, manifest, weaponTypes, palette);
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
    private static (string Path, byte[] Bytes) Make(string key, JsonObject entry, ThemeStyle style, JsonObject manifest, IReadOnlyDictionary<string, string> weaponTypes, Palette palette)
    {
        var rest = key[(key.IndexOf('.') + 1)..].Replace('.', '/');
        var color = KeyColor.Of(key, style);
        var hash = KeyColor.Hash(style.Salt + key);

        if (key.StartsWith("model.", StringComparison.Ordinal)) return ($"models/{rest}.glb", Model(key, entry, color, hash, weaponTypes.GetValueOrDefault(key), palette, style));
        if (key.StartsWith("anim.", StringComparison.Ordinal)) return ($"anims/{rest}.glb", AnimLibrary(key, entry));
        if (key.StartsWith("icon.", StringComparison.Ordinal)) return ($"icons/{rest}.png", Media.Png(color, hash));
        if (key.StartsWith("portrait.", StringComparison.Ordinal)) return ($"portraits/{rest}.png", Media.Png(color, hash));
        if (key.StartsWith("sfx.", StringComparison.Ordinal) || key.StartsWith("music.", StringComparison.Ordinal) || key.StartsWith("ambience.", StringComparison.Ordinal))
            return ("audio/silence.ogg", Media.SilentOgg());
        if (key.StartsWith("vfx.", StringComparison.Ordinal))
        {
            // The combat effects (T-303) each get a small scene of their own; the other effect keys share the empty placeholder until their tasks arrive.
            var combat = Media.CombatVfxScene(key);
            return combat is null ? ("vfx/placeholder.tscn", Encoding.UTF8.GetBytes(Media.VfxScene)) : ($"vfx/{rest}.tscn", Encoding.UTF8.GetBytes(combat));
        }
        throw new InvalidOperationException($"No generator for asset key '{key}'; add one to tools/themegen/Pack.cs.");
    }

    // ---------------- models ----------------

    /// <summary>
    /// The colours of one model (T-222, ADR-003 point 4), linear: a model the palette names takes its identity colours and look; anything else alive picks two creature
    /// colours by its key, and gear two gear colours. The material's base colour is white and the colours are vertex colours, so a dressed character's pieces bake
    /// into one surface (§7.5). Without a palette file the key colour stands in, as before T-222. The test theme turns every hue, so a swap shows.
    /// </summary>
    internal static BodyLook LookOf(string key, uint hash, int lod, Vector4 keyColor, Palette p, ThemeStyle style, bool gear = false)
    {
        Vector4 primary, secondary, trim;
        var look = Looks.Plain;
        if (!p.Any)
        {
            (primary, secondary, trim) = (keyColor, Accent(keyColor), MeshBuilder.Dark);
        }
        else if (p.Model(key) is { } identity)
        {
            (primary, secondary, trim, look) = (identity.Primary, identity.Secondary, identity.Trim, identity.Look);
        }
        else
        {
            (primary, secondary) = gear ? p.Gear(hash) : p.Creature(hash);
            trim = Palette.Shade(secondary, 0.75f);
        }
        Vector4 S(Vector4 c) => Palette.Shift(c, style.HueShift);
        return new BodyLook(lod, hash, S(primary), S(secondary), S(trim), p.NamedOr("skin", MeshBuilder.Skin), S(p.Hair(hash >> 3)), p.NamedOr("ink", MeshBuilder.Dark),
            p.NamedOr("cheek", MeshBuilder.Skin), MeshBuilder.Glowing(p.NamedOr("glow", MeshBuilder.White)), p.NamedOr("cream", MeshBuilder.White), look);
    }

    private static byte[] Model(string key, JsonObject entry, Vector4 color, uint hash, string? weaponType, Palette palette, ThemeStyle style)
    {
        var rig = entry["rig"]?.GetValue<string>();
        var attach = entry["attach"]?.GetValue<string>();
        var skinned = entry["skinned"]?.GetValue<string>();
        var hasAnims = entry["anims"] is not null;
        // With a palette the colours are all in the vertices and the material is white; without one the key colour tints the material, as before T-222.
        var baseColor = palette.Any ? MeshBuilder.White : color;

        if (rig is not null && hasAnims)
        {
            var r = Rig.Get(rig);
            var lod0 = new MeshBuilder();
            var lod1 = new MeshBuilder();
            r.Body(lod0, LookOf(key, hash, 0, color, palette, style));
            r.Body(lod1, LookOf(key, hash, 1, color, palette, style));
            return GlbWriter.Write(key, Generator, baseColor, r.Bones, new[] { ("Body_LOD0", lod0.Mesh), ("Body_LOD1", lod1.Mesh) }, Array.Empty<Clip>());
        }

        var look = LookOf(key, hash, 0, color, palette, style, gear: true);
        if (rig is not null || skinned is not null)
        {
            // A part that follows the shared rig: a face on the head bone or a garment on the torso. Skinned to the full rig so it moves with the body.
            var r = Rig.Get(rig ?? skinned!);
            var lod0 = new MeshBuilder();
            var bone = r.Index(skinned is not null ? "spine" : "head");
            if (skinned is not null && OutfitShapes.Skinned(lod0, r, look))
            {
                // an outfit piece (T-708)
            }
            else if (skinned is not null)
            {
                // A garment just outside the T-222 torso and belt.
                lod0.Cylinder(bone, new Vector3(0, 1.21f, 0), 0.215f, 0.255f, 0.4f, 16, look.Primary);
                lod0.Cylinder(r.Index("hips"), new Vector3(0, 1.03f, 0), 0.262f, 0.262f, 0.08f, 16, look.Secondary);
            }
            else
            {
                // A face: brows and a mouth on the T-222 head (the body draws the head and the eyes), varied by the key.
                var head = Rigs.HeadCenter;
                var radii = Rigs.HeadRadii;
                var tilt = ((int)(hash % 3) - 1) * 0.12f; // signed: as a uint, "0 - 1" put the brows of every third face 51 million units away (found by T-224's size rule)
                foreach (var x in new[] { -1f, 1f })
                    lod0.Box(bone, MeshBuilder.OnSurface(head, radii, Rigs.EyeElevation + 0.2f, 0.36f * x, 0.005f) + new Vector3(0, tilt * x * 0.1f, 0), new Vector3(0.09f, 0.022f, 0.03f), look.Ink);
                lod0.Ellipsoid(bone, MeshBuilder.OnSurface(head, radii, -0.12f - 0.04f * (hash % 2), 0, -0.004f), new Vector3(0.05f + 0.015f * (hash % 3), 0.018f, 0.02f), 8, 4,
                    Palette.Shade(look.Cheek, 0.7f));
            }
            return GlbWriter.Write(key, Generator, baseColor, r.Bones, new[] { ("Body_LOD0", lod0.Mesh) }, Array.Empty<Clip>());
        }

        // Static meshes, positioned relative to their attach bone (or the ground for props). The head bone is at HeadBoneY, so the T-222 head's centre is above it.
        var m = new MeshBuilder();
        var headLocal = Rigs.HeadCenter - new Vector3(0, Rigs.HeadBoneY, 0);
        switch (attach)
        {
            case "head" when key.Contains(".hair", StringComparison.Ordinal):
                m.Dome(-1, headLocal + new Vector3(0, 0.025f, 0.015f), Rigs.HeadRadii + new Vector3(0.025f), MathF.PI * 0.52f, 0.45f, 16, 6, look.Hair);
                if (hash % 2 == 0) m.Box(-1, headLocal + new Vector3(0, -0.14f, 0.3f), new Vector3(0.22f, 0.32f, 0.08f), look.Hair); // long at the back
                else m.Ellipsoid(-1, headLocal + new Vector3(0, 0.36f, 0.05f), new Vector3(0.09f, 0.08f, 0.09f), 10, 6, look.Hair); // a topknot
                break;
            case "head" when OutfitShapes.Head(m, headLocal, look): // an outfit piece (T-708)
                break;
            case "head": // headgear: a cap with a brim at the front
                m.Dome(-1, headLocal + new Vector3(0, 0.03f, 0), Rigs.HeadRadii + new Vector3(0.035f), MathF.PI * 0.4f, 0.2f, 16, 5, look.Primary);
                m.Box(-1, headLocal + new Vector3(0, 0.15f, -0.36f), new Vector3(0.34f, 0.03f, 0.14f), look.Secondary);
                break;
            case "hand_r": // a weapon: its shape follows the weapon type of the item that wears it
                WeaponShapes.Build(m, weaponType, palette.NamedOr("steel", MeshBuilder.White), palette.NamedOr("leather_deep", MeshBuilder.Dark),
                    palette.Any ? look.Glow : null);
                break;
            case "hand_l": // a round shield with a metal boss
                m.Ellipsoid(-1, new Vector3(0, 0, -0.12f), new Vector3(0.22f, 0.22f, 0.04f), 16, 7, palette.NamedOr("bark", MeshBuilder.White));
                m.Ellipsoid(-1, new Vector3(0, 0, -0.16f), new Vector3(0.07f, 0.07f, 0.035f), 10, 5, palette.NamedOr("steel", MeshBuilder.Dark));
                break;
            default: // a prop on the ground: a sack tied at the neck
                m.Ellipsoid(-1, new Vector3(0, 0.15f, 0), new Vector3(0.22f, 0.17f, 0.22f), 12, 7, palette.NamedOr("leather", MeshBuilder.White));
                m.Cylinder(-1, new Vector3(0, 0.33f, 0), 0.09f, 0.06f, 0.08f, 10, palette.NamedOr("leather_deep", MeshBuilder.Dark));
                m.Ellipsoid(-1, new Vector3(0, 0.39f, 0), new Vector3(0.06f, 0.05f, 0.06f), 8, 5, look.Primary);
                break;
        }
        if (key == "model.missing")
        {
            // Unmistakable: a big magenta cube with dark corners. Its colour is the material's, so it stays magenta whatever the palette says.
            m = new MeshBuilder();
            m.Box(-1, new Vector3(0, 0.4f, 0), new Vector3(0.8f, 0.8f, 0.8f), MeshBuilder.White);
            m.Box(-1, new Vector3(0, 0.4f, -0.41f), new Vector3(0.3f, 0.3f, 0.04f), MeshBuilder.Dark);
            baseColor = new Vector4(1f, 0f, 1f, 1f);
        }
        return GlbWriter.Write(key, Generator, baseColor, Array.Empty<Bone>(), new[] { ("Body_LOD0", m.Mesh) }, Array.Empty<Clip>());
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
