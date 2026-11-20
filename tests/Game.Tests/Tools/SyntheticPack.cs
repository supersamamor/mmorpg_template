using System.Numerics;
using System.Text.Json.Nodes;
using Game.Content.Themes;

namespace Game.Tests.Tools;

/// <summary>
/// A tiny art pack written in code for the import tool's tests (T-224: no download): the kind of mistakes real packs have. Measured in centimetres, the characters
/// face +Z, bones and meshes have the exporter's names, the clips are named their own way, and the slime's skeleton has no <c>Armature</c>.
/// </summary>
internal static class SyntheticPack
{
    /// <summary>The pack's own humanoid bone names, with their parent and position at rest (centimetres; the character faces +Z, so its right hand is on -X).</summary>
    public static readonly (string Name, int Parent, Vector3 At)[] HeroBones =
    {
        ("mixamorig:Hips", -1, new Vector3(0, 95, 0)), ("mixamorig:Spine", 0, new Vector3(0, 120, 0)), ("mixamorig:Head", 1, new Vector3(0, 160, 0)),
        ("mixamorig:LeftArm", 1, new Vector3(25, 140, 0)), ("mixamorig:LeftHand", 3, new Vector3(60, 140, 0)),
        ("mixamorig:RightArm", 1, new Vector3(-25, 140, 0)), ("mixamorig:RightHand", 5, new Vector3(-60, 140, 0)),
        ("mixamorig:LeftUpLeg", 0, new Vector3(12, 90, 0)), ("mixamorig:RightUpLeg", 0, new Vector3(-12, 90, 0)),
    };

    /// <summary>The bone renames a mapping needs for <see cref="HeroBones"/>.</summary>
    public static readonly Dictionary<string, string> HeroBoneMap = new()
    {
        ["mixamorig:Hips"] = "hips", ["mixamorig:Spine"] = "spine", ["mixamorig:Head"] = "head", ["mixamorig:LeftArm"] = "arm_l", ["mixamorig:LeftHand"] = "hand_l",
        ["mixamorig:RightArm"] = "arm_r", ["mixamorig:RightHand"] = "hand_r", ["mixamorig:LeftUpLeg"] = "leg_l", ["mixamorig:RightUpLeg"] = "leg_r",
    };

    /// <summary>The clip names in the pack.</summary>
    public static readonly string[] HeroClips = { "Idle_Loop", "Run_Fwd", "Sword_Slash", "Hit_React", "Death_Back" };

    /// <summary>Writes the pack into <paramref name="folder"/>: <c>hero.glb</c>, <c>slime.glb</c>, <c>sword.glb</c> and <c>icons/sword.png</c>.</summary>
    public static void Write(string folder, string pngFrom)
    {
        Directory.CreateDirectory(Path.Combine(folder, "icons"));
        File.WriteAllBytes(Path.Combine(folder, "hero.glb"), Rigged("HeroMesh", "Hero_Rig", HeroBones, new Vector3(30, 180, 15), HeroClips));
        File.WriteAllBytes(Path.Combine(folder, "slime.glb"),
            Rigged("Goo", null, new[] { ("Root", -1, Vector3.Zero), ("Body", 0, new Vector3(0, 30, 0)) }, new Vector3(35, 60, 35), new[] { "idle", "move", "bite", "hurt", "dead" }));
        File.WriteAllBytes(Path.Combine(folder, "sword.glb"), Static("Sword", new Vector3(3, 3, 90)));
        File.Copy(pngFrom, Path.Combine(folder, "icons", "sword.png"), true);
    }

    /// <summary>A skinned box on a skeleton (all weighted to the first bone) with one rotation channel on that bone per clip.</summary>
    private static byte[] Rigged(string meshName, string? holderName, (string Name, int Parent, Vector3 At)[] bones, Vector3 size, string[] clips)
    {
        var file = GltfFile.FromJson("""{"asset":{"version":"2.0","generator":"synthetic pack (T-224 tests)"}}""", Array.Empty<byte>());
        var nodes = file.Nodes;
        var first = holderName is null ? 0 : 1;
        if (holderName is not null) nodes.Add(new JsonObject { ["name"] = holderName, ["children"] = new JsonArray(first) });
        for (var i = 0; i < bones.Length; i++)
        {
            var (name, parent, at) = bones[i];
            var local = parent < 0 ? at : at - bones[parent].At;
            nodes.Add(new JsonObject { ["name"] = name, ["translation"] = new JsonArray(local.X, local.Y, local.Z) });
        }
        for (var i = 0; i < bones.Length; i++)
        {
            var children = Enumerable.Range(0, bones.Length).Where(c => bones[c].Parent == i).Select(c => (JsonNode)(c + first)).ToArray();
            if (children.Length > 0) nodes[i + first]!["children"] = new JsonArray(children);
        }
        var ibm = new List<float>();
        foreach (var (_, _, at) in bones) ibm.AddRange(new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -at.X, -at.Y, -at.Z, 1 });
        file.Array("skins").Add(new JsonObject
        {
            ["inverseBindMatrices"] = file.AddFloatAccessor(ibm.ToArray(), "MAT4"),
            ["joints"] = new JsonArray(Enumerable.Range(first, bones.Length).Select(j => (JsonNode)j).ToArray()),
        });
        var mesh = Box(file, size, skinned: true);
        nodes.Add(new JsonObject { ["name"] = meshName, ["mesh"] = mesh, ["skin"] = 0 });
        file.Json["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0, nodes.Count - 1) });
        file.Json["scene"] = 0;

        var times = file.AddFloatAccessor(new[] { 0f, 1f }, "SCALAR", minMax: true);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        var values = file.AddFloatAccessor(new[] { 0f, 0, 0, 1, turn.X, turn.Y, turn.Z, turn.W }, "VEC4");
        var animations = file.Array("animations");
        foreach (var clip in clips)
            animations.Add(new JsonObject
            {
                ["name"] = clip,
                ["samplers"] = new JsonArray(new JsonObject { ["input"] = times, ["output"] = values }),
                ["channels"] = new JsonArray(new JsonObject { ["sampler"] = 0, ["target"] = new JsonObject { ["node"] = first, ["path"] = "rotation" } }),
            });
        return file.ToGlb();
    }

    private static byte[] Static(string meshName, Vector3 size)
    {
        var file = GltfFile.FromJson("""{"asset":{"version":"2.0","generator":"synthetic pack (T-224 tests)"}}""", Array.Empty<byte>());
        file.Nodes.Add(new JsonObject { ["name"] = meshName, ["mesh"] = Box(file, size, skinned: false) });
        file.Json["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
        file.Json["scene"] = 0;
        return file.ToGlb();
    }

    /// <summary>A box standing on the ground (y from 0 to size.Y), one material, flat normals and no outline directions.</summary>
    private static int Box(GltfFile file, Vector3 size, bool skinned)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var indices = new List<ushort>();
        var h = new Vector3(size.X / 2, size.Y / 2, size.Z / 2);
        var center = new Vector3(0, size.Y / 2, 0);
        foreach (var normal in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
        {
            var u = MathF.Abs(normal.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            var v = Vector3.Cross(normal, u);
            var start = (ushort)(positions.Count / 3);
            foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var p = center + (normal + u * a + v * b) * h;
                positions.AddRange(new[] { p.X, p.Y, p.Z });
                normals.AddRange(new[] { normal.X, normal.Y, normal.Z });
            }
            indices.AddRange(new[] { start, (ushort)(start + 1), (ushort)(start + 2), start, (ushort)(start + 2), (ushort)(start + 3) });
        }
        var indexBytes = new byte[indices.Count * 2];
        Buffer.BlockCopy(indices.ToArray(), 0, indexBytes, 0, indexBytes.Length);
        var accessors = file.Array("accessors");
        accessors.Add(new JsonObject { ["bufferView"] = file.AddView(indexBytes, 34963), ["componentType"] = 5123, ["count"] = indices.Count, ["type"] = "SCALAR" });
        var indexAccessor = accessors.Count - 1;
        var attributes = new JsonObject
        {
            ["POSITION"] = file.AddFloatAccessor(positions.ToArray(), "VEC3", minMax: true),
            ["NORMAL"] = file.AddFloatAccessor(normals.ToArray(), "VEC3"),
        };
        if (skinned)
        {
            var count = positions.Count / 3;
            var joints = new byte[count * 4];
            accessors.Add(new JsonObject { ["bufferView"] = file.AddView(joints, 34962), ["componentType"] = 5121, ["count"] = count, ["type"] = "VEC4" });
            attributes["JOINTS_0"] = accessors.Count - 1;
            attributes["WEIGHTS_0"] = file.AddFloatAccessor(Enumerable.Range(0, count).SelectMany(_ => new[] { 1f, 0, 0, 0 }).ToArray(), "VEC4");
        }
        file.Array("materials").Add(new JsonObject { ["name"] = "Atlas", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.8f, 0.6f, 0.4f, 1f) } });
        var meshes = file.Array("meshes");
        meshes.Add(new JsonObject
        {
            ["name"] = "Mesh",
            ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = attributes, ["indices"] = indexAccessor, ["material"] = file.Array("materials").Count - 1 }),
        });
        return meshes.Count - 1;
    }
}
