using System.Numerics;
using System.Text.Json.Nodes;
using Game.Content.Themes;

namespace Game.ThemeImport;

/// <summary>
/// The fixes the import tool makes to a pack's glTF so it meets the theme contract (docs/THEMES.md): bone and mesh names, the <c>Armature</c> node, scale and facing,
/// outline directions. Each returns a short note for the report, or nothing when there was nothing to do. Deterministic: the same input gives the same bytes.
/// </summary>
internal static class GlbFixes
{
    /// <summary>Renames nodes by <paramref name="bones"/> (the joints, and the nodes animation channels move, which are the same nodes). Returns how many were renamed.</summary>
    public static int RenameBones(GltfFile file, IReadOnlyDictionary<string, string> bones)
    {
        if (bones.Count == 0) return 0;
        var renamed = 0;
        foreach (var node in file.Nodes.OfType<JsonObject>())
        {
            if (node["name"]?.GetValue<string>() is not { } name || !bones.TryGetValue(name, out var to) || to == name) continue;
            node["name"] = to;
            renamed++;
        }
        return renamed;
    }

    /// <summary>
    /// Names the levels of detail: the mapping's <paramref name="lod0"/> and <paramref name="lod1"/> nodes become <c>Body_LOD0</c> and <c>Body_LOD1</c>; with no
    /// mapping and no <c>Body_LOD0</c>, a file with exactly one mesh node has it renamed. Throws <see cref="MappingException"/> for a named node the file lacks.
    /// </summary>
    public static string? NameMeshes(GltfFile file, string? lod0, string? lod1, string where)
    {
        var meshNodes = file.Nodes.OfType<JsonObject>().Where(n => n["mesh"] is not null).ToList();
        string Names() => string.Join(", ", meshNodes.Select(n => n["name"]?.GetValue<string>() ?? "(unnamed)"));
        var notes = new List<string>();
        foreach (var (wanted, target) in new[] { (lod0, ModelFacts.Lod0), (lod1, ModelFacts.Lod1) })
        {
            if (wanted is null || wanted == target) continue;
            var node = meshNodes.FirstOrDefault(n => n["name"]?.GetValue<string>() == wanted)
                ?? throw new MappingException($"{where}: 'meshes' names mesh node '{wanted}', which the file does not have (mesh nodes: {Names()})");
            node["name"] = target;
            notes.Add($"mesh '{wanted}' -> {target}");
        }
        if (lod0 is null && meshNodes.All(n => n["name"]?.GetValue<string>() != ModelFacts.Lod0) && meshNodes.Count == 1)
        {
            notes.Add($"mesh '{meshNodes[0]["name"]?.GetValue<string>() ?? "(unnamed)"}' -> {ModelFacts.Lod0}");
            meshNodes[0]["name"] = ModelFacts.Lod0;
        }
        return notes.Count == 0 ? null : string.Join(", ", notes);
    }

    /// <summary>
    /// Puts the skeleton under a node named <c>Armature</c> (ARCHITECTURE §7.7): the node that holds the root bone is renamed, or, when the root bone is a scene root,
    /// a new <c>Armature</c> is put above it.
    /// </summary>
    public static string? Armature(GltfFile file)
    {
        if (file.Json["skins"] is not JsonArray skins || skins.Count == 0 || skins[0]?["joints"] is not JsonArray joints || joints.Count == 0) return null;
        var parents = file.Parents();
        var set = joints.Select(j => j!.GetValue<int>()).ToHashSet();
        var root = set.First(j => parents[j] < 0 || !set.Contains(parents[j]));
        var holder = parents[root];
        if (holder >= 0)
        {
            var name = file.NodeName(holder);
            if (name == ModelFacts.ArmatureName) return null;
            file.Nodes[holder]!["name"] = ModelFacts.ArmatureName;
            return $"'{name}' -> {ModelFacts.ArmatureName}";
        }
        var nodes = file.Nodes;
        nodes.Add(new JsonObject { ["name"] = ModelFacts.ArmatureName, ["children"] = new JsonArray(root) });
        var armature = nodes.Count - 1;
        var sceneIndex = file.Json["scene"]?.GetValue<int>() ?? 0;
        if (file.Json["scenes"]?[sceneIndex]?["nodes"] is JsonArray roots)
            for (var i = 0; i < roots.Count; i++)
                if (roots[i]!.GetValue<int>() == root) roots[i] = armature;
        return $"added {ModelFacts.ArmatureName} above '{file.NodeName(root)}'";
    }

    /// <summary>The turn about Y that makes a model facing <paramref name="facing"/> face -Z, in radians. Throws for an unknown facing.</summary>
    public static float TurnFor(string facing, string where) => facing.Trim().ToLowerInvariant() switch
    {
        "-z" => 0f,
        "+z" or "z" => MathF.PI,
        "+x" or "x" => MathF.PI / 2,
        "-x" => -MathF.PI / 2,
        _ => throw new MappingException($"{where}: facing '{facing}' is not one of -z, +z, +x, -x"),
    };

    /// <summary>
    /// Scales and turns the whole model about its origin: each scene root's own transform is composed with the fix (exact for a uniform scale and a turn about Y).
    /// A root that an animation moves gets a new parent instead, so the clip does not undo the fix.
    /// </summary>
    public static string? ScaleAndTurn(GltfFile file, double scale, float turn)
    {
        if (scale == 1 && turn == 0) return null;
        var s = (float)scale;
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn);
        var fix = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(rotation);
        var animated = AnimatedNodes(file);
        var nodes = file.Nodes;
        var sceneIndex = file.Json["scene"]?.GetValue<int>() ?? 0;
        var roots = file.Json["scenes"]?[sceneIndex]?["nodes"] as JsonArray;
        var rootList = roots?.Select(r => r!.GetValue<int>()).ToList() ?? file.SceneRoots();
        for (var i = 0; i < rootList.Count; i++)
        {
            var index = rootList[i];
            var node = nodes[index]!.AsObject();
            if (animated.Contains(index))
            {
                nodes.Add(new JsonObject { ["name"] = file.NodeName(index) + "_fix", ["children"] = new JsonArray(index) });
                SetTrs(nodes[nodes.Count - 1]!.AsObject(), new Vector3(s), rotation, Vector3.Zero);
                if (roots is not null) roots[i] = nodes.Count - 1;
                continue;
            }
            if (node["matrix"] is not null)
            {
                var m = GltfFile.LocalMatrix(node) * fix;
                node["matrix"] = new JsonArray(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
                continue;
            }
            var localScale = Read3(node["scale"], Vector3.One);
            var localRotation = node["rotation"] is JsonArray q && q.Count == 4 ? new Quaternion(F(q[0]), F(q[1]), F(q[2]), F(q[3])) : Quaternion.Identity;
            var localTranslation = Read3(node["translation"], Vector3.Zero);
            SetTrs(node, localScale * s, Quaternion.Normalize(Quaternion.Concatenate(localRotation, rotation)), Vector3.Transform(localTranslation, fix));
        }
        var parts = new List<string>();
        if (scale != 1) parts.Add($"scale {scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (turn != 0) parts.Add($"turned {Math.Round(turn * 180 / Math.PI)} deg to face -Z");
        return string.Join(", ", parts);
    }

    private static float F(JsonNode? n) => n!.GetValue<float>();

    private static Vector3 Read3(JsonNode? node, Vector3 fallback) => node is JsonArray a && a.Count == 3 ? new Vector3(F(a[0]), F(a[1]), F(a[2])) : fallback;

    private static void SetTrs(JsonObject node, Vector3 scale, Quaternion rotation, Vector3 translation)
    {
        static float Clean(float v) => MathF.Abs(v) < 1e-6f ? 0f : MathF.Abs(v - MathF.Round(v)) < 1e-6f ? MathF.Round(v) : v;
        node.Remove("scale");
        node.Remove("rotation");
        node.Remove("translation");
        if (translation != Vector3.Zero) node["translation"] = new JsonArray(Clean(translation.X), Clean(translation.Y), Clean(translation.Z));
        if (rotation != Quaternion.Identity) node["rotation"] = new JsonArray(Clean(rotation.X), Clean(rotation.Y), Clean(rotation.Z), Clean(rotation.W));
        if (scale != Vector3.One) node["scale"] = new JsonArray(Clean(scale.X), Clean(scale.Y), Clean(scale.Z));
    }

    private static HashSet<int> AnimatedNodes(GltfFile file)
    {
        var result = new HashSet<int>();
        if (file.Json["animations"] is JsonArray anims)
            foreach (var anim in anims)
                if (anim?["channels"] is JsonArray channels)
                    foreach (var channel in channels)
                        if (channel?["target"]?["node"] is { } n) result.Add(n.GetValue<int>());
        return result;
    }

    /// <summary>
    /// Writes outline directions (T-222): per vertex, the normal averaged over every vertex at the same place in the primitive, as glTF <c>TANGENT</c> with w = 1,
    /// so the outline hull stays closed at hard edges. Replaces any tangents the pack had (the toon material does not use normal maps). Returns the primitives done.
    /// </summary>
    public static int OutlineDirections(GltfFile file)
    {
        var done = 0;
        if (file.Json["meshes"] is not JsonArray meshes) return 0;
        var made = new Dictionary<(int, int), int>(); // (position, normal) accessors -> the tangent accessor, so shared attributes share it too
        foreach (var mesh in meshes)
        {
            if (mesh?["primitives"] is not JsonArray primitives) continue;
            foreach (var primitive in primitives.OfType<JsonObject>())
            {
                if (primitive["attributes"] is not JsonObject attributes || attributes["POSITION"] is null || attributes["NORMAL"] is null) continue;
                var p = attributes["POSITION"]!.GetValue<int>();
                var n = attributes["NORMAL"]!.GetValue<int>();
                if (!made.TryGetValue((p, n), out var tangent))
                {
                    var positions = file.ReadFloats(p);
                    var normals = file.ReadFloats(n);
                    var sums = new Dictionary<(long, long, long), Vector3>();
                    (long, long, long) Key(int i) => ((long)MathF.Round(positions[i * 3] * 1e4f), (long)MathF.Round(positions[i * 3 + 1] * 1e4f), (long)MathF.Round(positions[i * 3 + 2] * 1e4f));
                    var count = positions.Length / 3;
                    for (var i = 0; i < count; i++)
                    {
                        var key = Key(i);
                        sums[key] = (sums.TryGetValue(key, out var sum) ? sum : Vector3.Zero) + new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
                    }
                    var values = new float[count * 4];
                    for (var i = 0; i < count; i++)
                    {
                        var v = sums[Key(i)];
                        v = v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
                        values[i * 4] = v.X;
                        values[i * 4 + 1] = v.Y;
                        values[i * 4 + 2] = v.Z;
                        values[i * 4 + 3] = 1f;
                    }
                    tangent = file.AddFloatAccessor(values, "VEC4");
                    made[(p, n)] = tangent;
                }
                attributes["TANGENT"] = tangent;
                done++;
            }
        }
        return done;
    }
}
