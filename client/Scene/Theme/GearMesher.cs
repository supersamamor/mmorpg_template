using Godot;

namespace Game.Client.Scene;

/// <summary>A worn piece's meshes, taken once from its scene (T-403): the LOD0 mesh and, if the piece has one, a LOD1 mesh, each with its skin and its transform in the scene.</summary>
public sealed class PieceMeshes
{
    internal PieceMeshes(Mesh lod0, Skin? skin0, Transform3D transform0, Mesh? lod1, Skin? skin1, Transform3D transform1)
    {
        Lod0 = lod0;
        Skin0 = skin0;
        Transform0 = transform0;
        Lod1 = lod1;
        Skin1 = skin1;
        Transform1 = transform1;
    }

    /// <summary>The full-detail mesh.</summary>
    public Mesh Lod0 { get; }

    /// <summary>Its skin (a skinned piece), or null.</summary>
    public Skin? Skin0 { get; }

    /// <summary>Its transform relative to the piece scene's root (an attached piece's offset from the bone).</summary>
    public Transform3D Transform0 { get; }

    /// <summary>The low-detail mesh, or null (the full one is used at both levels).</summary>
    public Mesh? Lod1 { get; }

    /// <summary>Its skin, or null.</summary>
    public Skin? Skin1 { get; }

    /// <summary>Its transform relative to the scene's root.</summary>
    public Transform3D Transform1 { get; }
}

/// <summary>One part to merge into a character's mesh: a skinned mesh with its skin, or a rigid mesh placed on a skeleton bone.</summary>
/// <param name="Mesh">The mesh.</param>
/// <param name="Skin">The skin of a skinned part; null for a rigid one.</param>
/// <param name="Transform">A rigid part's transform in its bone's space (its offset, times the fallback scale).</param>
/// <param name="Bone">A rigid part's bone on the character's skeleton.</param>
public readonly record struct GearPart(Mesh Mesh, Skin? Skin, Transform3D Transform, int Bone);

/// <summary>
/// Merges a character's body and its worn pieces into one skinned mesh per level of detail (T-403, §9). Attached pieces become parts of the skin weighted fully to their
/// bone, so they follow it exactly as a bone attachment would; skinned pieces keep their weights, with their skin's binds mapped onto the character's. Surfaces whose
/// materials differ only in colour (untextured <see cref="StandardMaterial3D"/>, opaque, the same shading) are joined into one surface with the colour baked into the
/// vertex colours, so a character costs one draw call whatever it wears; a textured material keeps its own surface (real art needs an atlas to merge, §9's
/// "1 material"). Results are cached by <see cref="Game.Client.Core.Theme.WornSet"/> key, so a crowd wearing the same set shares one mesh and one skin.
/// </summary>
public sealed class GearMesher
{
    private readonly Dictionary<string, (ArrayMesh Mesh, Skin Skin)> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, StandardMaterial3D> _baked = new(StringComparer.Ordinal);
    private readonly int _limit;

    /// <summary>Creates a mesher that keeps at most <paramref name="limit"/> merged meshes (the oldest go first; a character still showing one keeps it).</summary>
    public GearMesher(int limit) => _limit = Math.Max(1, limit);

    /// <summary>Merged meshes cached now (for measuring).</summary>
    public int Cached => _cache.Count;

    /// <summary>Merges built so far (for measuring).</summary>
    public int Built { get; private set; }

    /// <summary>The merged mesh and skin for <paramref name="key"/>, from the cache or built now.</summary>
    public (ArrayMesh Mesh, Skin Skin) Get(string key, Mesh body, Skin bodySkin, Skeleton3D skeleton, IReadOnlyList<GearPart> parts)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var result = Merge(body, bodySkin, skeleton, parts);
        Built++;
        _cache[key] = result;
        _order.Enqueue(key);
        while (_cache.Count > _limit) _cache.Remove(_order.Dequeue());
        return result;
    }

    /// <summary>Lets go of every merged mesh.</summary>
    public void Clear()
    {
        _cache.Clear();
        _order.Clear();
        _baked.Clear();
    }

    private sealed class Group
    {
        public Material? Material;
        public readonly List<Vector3> Vertices = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Color> Colors = new();
        public readonly List<Vector2> Uvs = new();
        public readonly List<int> Bones = new();
        public readonly List<float> Weights = new();
        public readonly List<int> Indices = new();
        public bool HasUv;
    }

    private (ArrayMesh, Skin) Merge(Mesh body, Skin bodySkin, Skeleton3D skeleton, IReadOnlyList<GearPart> parts)
    {
        var skin = new Skin();
        var named = bodySkin.GetBindCount() > 0 && bodySkin.GetBindName(0).ToString().Length > 0;
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var order = new List<string>();

        var bodyMap = MapBinds(skin, bodySkin, named);
        AddMesh(groups, order, body, Transform3D.Identity, bodyMap, -1);
        foreach (var part in parts)
        {
            if (part.Skin is { } partSkin)
            {
                AddMesh(groups, order, part.Mesh, Transform3D.Identity, MapBinds(skin, partSkin, named), -1);
            }
            else
            {
                var bind = named ? BindIndex(skin, skeleton.GetBoneName(part.Bone), -1, Transform3D.Identity) : BindIndex(skin, string.Empty, part.Bone, Transform3D.Identity);
                AddMesh(groups, order, part.Mesh, part.Transform, null, bind);
            }
        }

        var mesh = new ArrayMesh();
        foreach (var key in order)
        {
            var g = groups[key];
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = g.Vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = g.Normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = g.Colors.ToArray();
            if (g.HasUv) arrays[(int)Mesh.ArrayType.TexUV] = g.Uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Bones] = g.Bones.ToArray();
            arrays[(int)Mesh.ArrayType.Weights] = g.Weights.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = g.Indices.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, g.Material);
        }
        return (mesh, skin);
    }

    /// <summary>Copies a source skin's binds into the merged skin (a bind the same as one already there is reused) and returns old index to new.</summary>
    private static int[] MapBinds(Skin into, Skin from, bool named)
    {
        var map = new int[from.GetBindCount()];
        for (var i = 0; i < map.Length; i++)
            map[i] = BindIndex(into, named ? from.GetBindName(i).ToString() : string.Empty, named ? -1 : from.GetBindBone(i), from.GetBindPose(i));
        return map;
    }

    private static int BindIndex(Skin skin, string name, int bone, Transform3D pose)
    {
        var count = skin.GetBindCount();
        for (var i = 0; i < count; i++)
        {
            var same = name.Length > 0 ? skin.GetBindName(i).ToString() == name : skin.GetBindBone(i) == bone;
            if (same && skin.GetBindPose(i).IsEqualApprox(pose)) return i;
        }
        if (name.Length > 0) skin.AddNamedBind(name, pose);
        else skin.AddBind(bone, pose);
        return count;
    }

    /// <summary>Adds every surface of <paramref name="source"/> to its group. <paramref name="bindMap"/> remaps a skinned source's bones; otherwise every vertex goes fully to <paramref name="rigidBind"/>.</summary>
    private void AddMesh(Dictionary<string, Group> groups, List<string> order, Mesh source, Transform3D transform, int[]? bindMap, int rigidBind)
    {
        for (var s = 0; s < source.GetSurfaceCount(); s++)
        {
            var arrays = source.SurfaceGetArrays(s);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (vertices.Length == 0) continue;
            var normals = Optional(arrays, Mesh.ArrayType.Normal)?.AsVector3Array();
            var colors = Optional(arrays, Mesh.ArrayType.Color)?.AsColorArray();
            var uvs = Optional(arrays, Mesh.ArrayType.TexUV)?.AsVector2Array();
            var bones = Optional(arrays, Mesh.ArrayType.Bones)?.AsInt32Array();
            var weights = Optional(arrays, Mesh.ArrayType.Weights)?.AsFloat32Array();
            var indices = Optional(arrays, Mesh.ArrayType.Index)?.AsInt32Array();
            var material = source.SurfaceGetMaterial(s);

            var (key, target, tint) = Classify(material);
            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = new Group { Material = target };
                order.Add(key);
            }
            var start = group.Vertices.Count;
            var perVertex = bones is not null && bones.Length >= vertices.Length * 4 ? bones.Length / vertices.Length : 0;
            var basis = transform.Basis;
            for (var v = 0; v < vertices.Length; v++)
            {
                group.Vertices.Add(transform * vertices[v]);
                group.Normals.Add(normals is not null && v < normals.Length ? (basis * normals[v]).Normalized() : Vector3.Up);
                var color = colors is not null && v < colors.Length ? colors[v] : Colors.White;
                group.Colors.Add(tint(color));
                if (uvs is not null && v < uvs.Length)
                {
                    group.Uvs.Add(uvs[v]);
                    group.HasUv = true;
                }
                else
                {
                    group.Uvs.Add(Vector2.Zero);
                }
                if (bindMap is not null && perVertex > 0 && weights is not null)
                {
                    // Four influences per vertex; a source with eight keeps its first four, renormalised.
                    var total = 0f;
                    for (var k = 0; k < 4; k++) total += weights[v * perVertex + k];
                    for (var k = 0; k < 4; k++)
                    {
                        var b = bones![v * perVertex + k];
                        group.Bones.Add(b >= 0 && b < bindMap.Length ? bindMap[b] : 0);
                        group.Weights.Add(total > 0 ? weights[v * perVertex + k] / total : (k == 0 ? 1f : 0f));
                    }
                }
                else
                {
                    var bind = bindMap is null ? Math.Max(0, rigidBind) : 0;
                    group.Bones.Add(bind);
                    group.Bones.Add(0);
                    group.Bones.Add(0);
                    group.Bones.Add(0);
                    group.Weights.Add(1f);
                    group.Weights.Add(0f);
                    group.Weights.Add(0f);
                    group.Weights.Add(0f);
                }
            }
            if (indices is not null && indices.Length > 0)
                foreach (var i in indices) group.Indices.Add(start + i);
            else
                for (var i = 0; i < vertices.Length; i++) group.Indices.Add(start + i);
        }
    }

    private static Variant? Optional(Godot.Collections.Array arrays, Mesh.ArrayType type)
    {
        var value = arrays[(int)type];
        if (value.VariantType == Variant.Type.Nil) return null;
        return value;
    }

    /// <summary>
    /// Which group a surface joins: an untextured opaque <see cref="StandardMaterial3D"/> joins the shared material for its shading, with its colour baked into the vertex
    /// colours; anything else keeps its own material (and its own surface).
    /// </summary>
    private (string Key, Material? Material, Func<Color, Color> Tint) Classify(Material? material)
    {
        if (material is StandardMaterial3D standard && standard.AlbedoTexture is null && standard.Transparency == BaseMaterial3D.TransparencyEnum.Disabled)
        {
            var key = $"baked:{standard.ShadingMode}:{standard.Roughness:0.###}:{standard.Metallic:0.###}:{standard.CullMode}:{standard.VertexColorIsSrgb}:{standard.SpecularMode}:{standard.DiffuseMode}";
            if (!_baked.TryGetValue(key, out var shared))
            {
                shared = (StandardMaterial3D)standard.Duplicate();
                shared.AlbedoColor = Colors.White;
                shared.VertexColorUseAsAlbedo = true;
                _baked[key] = shared;
            }
            // Albedo × vertex colour, done the way the shader does it: the albedo colour is sRGB, vertex colours are linear unless the material says sRGB.
            var albedo = standard.AlbedoColor;
            var useVertex = standard.VertexColorUseAsAlbedo;
            var srgb = standard.VertexColorIsSrgb;
            Color Tint(Color c)
            {
                var linearVertex = useVertex ? (srgb ? c.SrgbToLinear() : c) : Colors.White;
                var linearAlbedo = albedo.SrgbToLinear();
                var product = new Color(linearAlbedo.R * linearVertex.R, linearAlbedo.G * linearVertex.G, linearAlbedo.B * linearVertex.B, 1f);
                return srgb ? product.LinearToSrgb() : product;
            }
            return (key, shared, Tint);
        }
        return ("own:" + (material?.GetInstanceId() ?? 0), material, c => c);
    }
}
