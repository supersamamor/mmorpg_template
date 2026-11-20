using System.Numerics;
using System.Text.Json.Nodes;

namespace Game.Content.Themes;

/// <summary>One mesh node of a model, measured: what the theme contract's budgets and look rules need (docs/THEMES.md).</summary>
public sealed record MeshFacts(string Name, int Triangles, IReadOnlySet<int> Materials, bool HasOutlineDirections, bool Skinned, Vector3 Min, Vector3 Max)
{
    /// <summary>Height of the mesh at rest, in world units.</summary>
    public float Height => Max.Y - Min.Y;
}

/// <summary>
/// The facts <c>ThemeCheck</c> reads out of one theme model or animation library (T-224): mesh nodes and their triangles, materials and bounds; the skin's bones, where
/// they stand and what holds them; the animations and the nodes they move; the textures' sizes. Measured at rest, without Godot.
/// </summary>
public sealed class ModelFacts
{
    /// <summary>The level-of-detail mesh names the client looks for (ARCHITECTURE §7.7).</summary>
    public const string Lod0 = "Body_LOD0";

    /// <inheritdoc cref="Lod0"/>
    public const string Lod1 = "Body_LOD1";

    /// <summary>The node the skeleton must sit under (ARCHITECTURE §7.7: <c>Armature/Skeleton3D</c>).</summary>
    public const string ArmatureName = "Armature";

    private ModelFacts() { }

    /// <summary>Every mesh node.</summary>
    public IReadOnlyList<MeshFacts> Meshes { get; private init; } = System.Array.Empty<MeshFacts>();

    /// <summary>The <c>Body_LOD0</c> mesh node, or null.</summary>
    public MeshFacts? Body0 => Meshes.FirstOrDefault(m => m.Name == Lod0);

    /// <summary>The <c>Body_LOD1</c> mesh node, or null.</summary>
    public MeshFacts? Body1 => Meshes.FirstOrDefault(m => m.Name == Lod1);

    /// <summary>The joint names of the first skin, or empty.</summary>
    public IReadOnlyList<string> Bones { get; private init; } = System.Array.Empty<string>();

    /// <summary>Each bone's position at rest, in world units.</summary>
    public IReadOnlyDictionary<string, Vector3> BoneAt { get; private init; } = new Dictionary<string, Vector3>();

    /// <summary>The name of the node that holds the skeleton's root bone, or null when the root bone is a scene root.</summary>
    public string? SkeletonHolder { get; private init; }

    /// <summary>True when the file has a skin.</summary>
    public bool HasSkin => Bones.Count > 0;

    /// <summary>The animation names.</summary>
    public IReadOnlyList<string> Animations { get; private init; } = System.Array.Empty<string>();

    /// <summary>The names of the nodes animations move.</summary>
    public IReadOnlySet<string> AnimatedNodes { get; private init; } = new HashSet<string>();

    /// <summary>Every texture's size, by image index.</summary>
    public IReadOnlyList<(int Image, int Width, int Height)> Textures { get; private init; } = System.Array.Empty<(int, int, int)>();

    /// <summary>Indexes of materials that are blended (<c>alphaMode: BLEND</c>) or cut out (<c>MASK</c>): the toon look leaves those alone.</summary>
    public IReadOnlySet<int> SeeThroughMaterials { get; private init; } = new HashSet<int>();

    /// <summary>Measures a loaded asset.</summary>
    public static ModelFacts Of(GltfFile file)
    {
        var nodes = file.Nodes;
        var world = file.WorldMatrices();
        var parents = file.Parents();

        var bones = new List<string>();
        var boneAt = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        string? holder = null;
        Matrix4x4? skinToWorld = null;
        if (file.Json["skins"] is JsonArray skins && skins.Count > 0 && skins[0]?["joints"] is JsonArray joints)
        {
            var jointIndexes = joints.Select(j => j!.GetValue<int>()).ToList();
            foreach (var j in jointIndexes)
            {
                bones.Add(file.NodeName(j));
                boneAt[file.NodeName(j)] = world[j].Translation;
            }
            var set = jointIndexes.ToHashSet();
            var root = jointIndexes.FirstOrDefault(j => parents[j] < 0 || !set.Contains(parents[j]));
            if (jointIndexes.Count > 0) holder = parents[root] >= 0 ? file.NodeName(parents[root]) : null;
            // Skinned vertices land at joint world x inverse bind x position; at rest that is the same for every joint, so the first one tells where the mesh is.
            if (skins[0]?["inverseBindMatrices"] is { } ibmIndex && jointIndexes.Count > 0)
            {
                var m = file.ReadFloats(ibmIndex.GetValue<int>());
                if (m.Length >= 16)
                {
                    var ibm = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                    skinToWorld = ibm * world[jointIndexes[0]];
                }
            }
            else if (jointIndexes.Count > 0) skinToWorld = world[jointIndexes[0]];
        }

        var meshes = new List<MeshFacts>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i]?["mesh"] is not { } meshIndex) continue;
            var mesh = file.Json["meshes"]?[meshIndex.GetValue<int>()];
            if (mesh?["primitives"] is not JsonArray primitives) continue;
            var skinned = nodes[i]?["skin"] is not null;
            var toWorld = skinned && skinToWorld is { } s ? s : world[i];
            var triangles = 0;
            var materials = new HashSet<int>();
            var outline = primitives.Count > 0;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var primitive in primitives.OfType<JsonObject>())
            {
                var mode = primitive["mode"]?.GetValue<int>() ?? 4;
                var attributes = primitive["attributes"] as JsonObject;
                var positions = attributes?["POSITION"]?.GetValue<int>();
                var count = primitive["indices"] is { } indices ? file.Count(indices.GetValue<int>()) : positions is { } p ? file.Count(p) : 0;
                triangles += mode switch { 4 => count / 3, 5 or 6 => Math.Max(0, count - 2), _ => 0 };
                if (primitive["material"] is { } material) materials.Add(material.GetValue<int>());
                if (attributes?["TANGENT"] is null) outline = false;
                if (positions is { } pa && file.Json["accessors"]?[pa] is JsonObject accessor && accessor["min"] is JsonArray lo && accessor["max"] is JsonArray hi && lo.Count == 3 && hi.Count == 3)
                {
                    for (var corner = 0; corner < 8; corner++)
                    {
                        var c = new Vector3(GltfFile.Number((corner & 1) == 0 ? lo[0] : hi[0]), GltfFile.Number((corner & 2) == 0 ? lo[1] : hi[1]), GltfFile.Number((corner & 4) == 0 ? lo[2] : hi[2]));
                        var w = Vector3.Transform(c, toWorld);
                        min = Vector3.Min(min, w);
                        max = Vector3.Max(max, w);
                    }
                }
            }
            if (min.X > max.X) (min, max) = (Vector3.Zero, Vector3.Zero);
            meshes.Add(new MeshFacts(file.NodeName(i), triangles, materials, outline, skinned, min, max));
        }

        var animations = new List<string>();
        var animated = new HashSet<string>(StringComparer.Ordinal);
        if (file.Json["animations"] is JsonArray anims)
            foreach (var anim in anims.OfType<JsonObject>())
            {
                animations.Add(anim["name"]?.GetValue<string>() ?? "animation" + animations.Count);
                if (anim["channels"] is JsonArray channels)
                    foreach (var channel in channels)
                        if (channel?["target"]?["node"] is { } target) animated.Add(file.NodeName(target.GetValue<int>()));
            }

        var textures = new List<(int, int, int)>();
        if (file.Json["images"] is JsonArray images)
            for (var i = 0; i < images.Count; i++)
                if (file.ImageBytes(i) is { } bytes && GltfFile.ImageSize(bytes) is { } size) textures.Add((i, size.Width, size.Height));

        var seeThrough = new HashSet<int>();
        if (file.Json["materials"] is JsonArray materialList)
            for (var i = 0; i < materialList.Count; i++)
                if (materialList[i]?["alphaMode"]?.GetValue<string>() is "BLEND" or "MASK") seeThrough.Add(i);

        return new ModelFacts
        {
            Meshes = meshes,
            Bones = bones,
            BoneAt = boneAt,
            SkeletonHolder = holder,
            Animations = animations,
            AnimatedNodes = animated,
            Textures = textures,
            SeeThroughMaterials = seeThrough,
        };
    }
}
