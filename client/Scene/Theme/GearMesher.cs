using Game.Client.Core.Theme;
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
/// <param name="Colors">What its painted colours become (a face's skin, a hairstyle's hair colour; T-706), or null for none.</param>
public readonly record struct GearPart(Mesh Mesh, Skin? Skin, Transform3D Transform, int Bone, Recolor? Colors = null);

/// <summary>What one merge needs of a character at one level of detail: its body's mesh and skin, and the parts to merge in.</summary>
public readonly record struct BodySource(Mesh Mesh, Skin Skin, IReadOnlyList<GearPart> Parts);

/// <summary>A merged character: the full mesh and, when the body has one, the far one, each with its skin.</summary>
public sealed record DressedMesh(ArrayMesh Mesh0, Skin Skin0, ArrayMesh? Mesh1, Skin? Skin1)
{
    internal long LastUsed { get; set; }
}

/// <summary>
/// Merges a character's body and its worn pieces into one skinned mesh per level of detail (T-403, §9). Attached pieces become parts of the skin weighted fully to their
/// bone, so they follow it exactly as a bone attachment would; skinned pieces keep their weights, with their skin's binds mapped onto the character's. Surfaces whose
/// materials differ only in colour (untextured <see cref="StandardMaterial3D"/>, opaque, the same shading) are joined into one surface with the colour baked into the
/// vertex colours, so a character costs one draw call whatever it wears. Since T-711 textured materials merge the same way when they draw the same image
/// (<see cref="ImageIdentity"/>: the same pixels in any number of files, since every .glb embeds its own copy), so a character whose parts share the theme's
/// atlas is one draw call too; each other image is one surface more (docs/THEMES.md 1.9). Since T-706 the face and hairstyle are parts like the gear, and the skin, hair and cloth colours are baked into the vertex colours
/// (<see cref="Recolor"/>). Results are cached by <see cref="WornSet"/> key, both levels of detail together, at most <c>look_cache_limit</c> of them (the least recently
/// used go first), so a crowd with the same looks and gear shares one mesh and one skin.
/// <para><b>Off the main thread</b> (T-222, ADR-002 row 223): the merge used to take one 60–105 ms frame. Now each source mesh's arrays are read once (on the main
/// thread, then cached), the merge itself runs on a worker over plain arrays (no engine object is touched there, so it is safe with the headless dummy renderer,
/// §7.7), and the main thread only turns the result into a mesh and a skin. <see cref="TryGet"/> says "not yet" until then; the character keeps its old look for
/// those frames. The vertex outline direction (the tangent, T-222) and the glow mark (vertex alpha) are carried through. At most <see cref="MaxRunning"/> merges run
/// at once, so a crowd coming into view neither floods the workers nor reads every source in one frame.</para>
/// </summary>
public sealed class GearMesher
{
    /// <summary>Most merges running on workers at once.</summary>
    public const int MaxRunning = 2;

    private const int MaxSources = 256;          // source meshes whose arrays are kept; past it they are read again when needed
    private const int ForgetAfterCalls = 4096;   // a finished merge nobody has asked for in this many calls belongs to a character that left view

    private sealed class Waiting
    {
        public required Task<(Merged Lod0, Merged? Lod1)> Task { get; init; }

        public long LastAsked;
    }

    private readonly Dictionary<string, DressedMesh> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Waiting> _pending = new(StringComparer.Ordinal);
    private long _clock;
    private readonly Dictionary<string, StandardMaterial3D> _baked = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, SourceMesh> _sources = new();
    private readonly int _limit;
    private readonly bool _threaded;
    private readonly ImageIdentity _images;

    /// <summary>Creates a mesher that keeps at most <paramref name="limit"/> merged characters (the least recently used go first; a character still showing one keeps it).
    /// Without <paramref name="threaded"/> the merge runs at once on the calling thread. <paramref name="warn"/> hears about textures that cannot be read (once each).</summary>
    public GearMesher(int limit, bool threaded = true, Action<string>? warn = null)
    {
        _limit = Math.Max(1, limit);
        _threaded = threaded;
        _images = new ImageIdentity(warn ?? (message => GD.PushWarning(message)));
    }

    /// <summary>Which textures are the same image (for measuring and checking, T-711).</summary>
    public ImageIdentity Images => _images;

    private bool _byImage = true;

    /// <summary>
    /// Merge textured parts by the image they draw (T-711); false groups them by texture object as before T-711, so every file's texture is a surface of its own (for measuring the
    /// difference on one crowd). Changing it forgets every merged character and source.
    /// </summary>
    public bool ByImage
    {
        get => _byImage;
        set
        {
            if (value == _byImage) return;
            _byImage = value;
            Clear();
        }
    }

    /// <summary>Merged characters cached now (for measuring).</summary>
    public int Cached => _cache.Count;

    /// <summary>Merges built so far (for measuring).</summary>
    public int Built { get; private set; }

    /// <summary>Main-thread milliseconds spent on merges so far: reading source arrays and making the meshes (for measuring, T-222).</summary>
    public double MainThreadMs { get; private set; }

    /// <summary>Merges running on a worker now.</summary>
    public int Pending => _pending.Count;

    /// <summary>
    /// The merged character for <paramref name="key"/>: true with the result when it is cached or its merge has finished; false while it is being merged or waits for a
    /// free worker (call again: the first call with a free worker starts it). <paramref name="sources"/> is asked for the body and parts of a level of detail only when
    /// a merge starts (null for a level the body lacks).
    /// </summary>
    public bool TryGet(string key, Skeleton3D skeleton, Func<int, BodySource?> sources, Recolor? bodyColors, out DressedMesh result)
    {
        _clock++;
        if (_cache.TryGetValue(key, out result!))
        {
            result.LastUsed = _clock;
            return true;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!_pending.TryGetValue(key, out var waiting))
            {
                if (_threaded && Running() >= MaxRunning) return false;
                var near = Snapshot(sources(0)!.Value, skeleton, bodyColors);
                var far = sources(1) is { } low ? Snapshot(low, skeleton, bodyColors) : null;
                (Merged, Merged?) Work() => (Merge(near), far is null ? null : Merge(far));
                waiting = new Waiting { Task = _threaded ? Task.Run(Work) : Task.FromResult(Work()) };
                _pending[key] = waiting;
            }
            waiting.LastAsked = _clock;
            if (!waiting.Task.IsCompleted) return false;
            _pending.Remove(key);
            var (lod0, lod1) = waiting.Task.Result;
            var (mesh0, skin0) = Build(lod0);
            (ArrayMesh Mesh, Skin Skin)? second = lod1 is null ? null : Build(lod1);
            result = new DressedMesh(mesh0, skin0, second?.Mesh, second?.Skin) { LastUsed = _clock };
            Built++;
            _cache[key] = result;
            Trim(key);
            return true;
        }
        finally
        {
            MainThreadMs += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    private int Running()
    {
        var running = 0;
        List<string>? stale = null;
        foreach (var (key, waiting) in _pending)
        {
            if (!waiting.Task.IsCompleted) running++;
            else if (_clock - waiting.LastAsked > ForgetAfterCalls) (stale ??= new List<string>()).Add(key);
        }
        if (stale is not null)
            foreach (var key in stale) _pending.Remove(key);
        return running;
    }

    private void Trim(string keep)
    {
        while (_cache.Count > _limit)
        {
            string? victim = null;
            var oldest = long.MaxValue;
            foreach (var (key, dressed) in _cache)
                if (key != keep && dressed.LastUsed < oldest) (victim, oldest) = (key, dressed.LastUsed);
            if (victim is null) return;
            _cache.Remove(victim);
        }
    }

    /// <summary>Reads a preloaded piece's arrays ahead of need (T-222), so a merge that wears it later only has the worker's part and the mesh to make.</summary>
    public void Warm(PieceMeshes piece)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Source(piece.Lod0);
        if (piece.Lod1 is { } low) Source(low);
        MainThreadMs += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>Lets go of every merged mesh (merges still running finish and are dropped).</summary>
    public void Clear()
    {
        foreach (var waiting in _pending.Values) waiting.Task.Wait();
        _pending.Clear();
        _cache.Clear();
        _baked.Clear();
        _sources.Clear();
        _images.Clear();
    }

    // ---- plain data: what a worker may touch ----

    private sealed record Bind(string Name, int Bone, Transform3D Pose);

    /// <summary>One source surface's arrays, read once, and how it joins a group.</summary>
    private sealed class SourceSurface
    {
        public Vector3[] Vertices = System.Array.Empty<Vector3>();
        public Vector3[]? Normals;
        public float[]? Tangents;
        public Color[]? Colors;
        public Vector2[]? Uvs;
        public int[]? Bones;
        public float[]? Weights;
        public int[]? Indices;
        public string GroupKey = string.Empty;
        public Material? Target;
        public Color LinearAlbedo = Godot.Colors.White;
        public bool Bake;
        public bool Srgb;
    }

    private sealed class SourceMesh
    {
        public readonly List<SourceSurface> Surfaces = new();
    }

    private sealed record Part(SourceMesh Mesh, Bind[]? Binds, Transform3D Transform, string BoneName, int Bone, Recolor Colors);

    private sealed record Job(bool Named, Part Body, Part[] Parts);

    private sealed class Group
    {
        public Material? Material;
        public readonly List<Vector3> Vertices = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<float> Tangents = new();
        public readonly List<Color> Colors = new();
        public readonly List<Vector2> Uvs = new();
        public readonly List<int> Bones = new();
        public readonly List<float> Weights = new();
        public readonly List<int> Indices = new();
        public bool HasUv;
    }

    private sealed record Merged(List<Bind> Binds, List<Group> Groups);

    // ---- main thread: read the sources ----

    private Job Snapshot(BodySource source, Skeleton3D skeleton, Recolor? bodyColors)
    {
        var (body, bodySkin, parts) = source;
        var named = bodySkin.GetBindCount() > 0 && bodySkin.GetBindName(0).ToString().Length > 0;
        var list = new Part[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            list[i] = part.Skin is { } skin
                ? new Part(Source(part.Mesh), Binds(skin), Transform3D.Identity, string.Empty, -1, part.Colors ?? Recolor.None)
                : new Part(Source(part.Mesh), null, part.Transform, named ? skeleton.GetBoneName(part.Bone).ToString() : string.Empty, part.Bone, part.Colors ?? Recolor.None);
        }
        return new Job(named, new Part(Source(body), Binds(bodySkin), Transform3D.Identity, string.Empty, -1, bodyColors ?? Recolor.None), list);
    }

    private static Bind[] Binds(Skin skin)
    {
        var binds = new Bind[skin.GetBindCount()];
        for (var i = 0; i < binds.Length; i++) binds[i] = new Bind(skin.GetBindName(i).ToString(), skin.GetBindBone(i), skin.GetBindPose(i));
        return binds;
    }

    private SourceMesh Source(Mesh mesh)
    {
        var id = mesh.GetInstanceId();
        if (_sources.TryGetValue(id, out var known)) return known;
        if (_sources.Count >= MaxSources) _sources.Clear(); // a merge already running holds the ones it needs
        var source = new SourceMesh();
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            var surface = new SourceSurface
            {
                Vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array(),
                Normals = Optional(arrays, Mesh.ArrayType.Normal)?.AsVector3Array(),
                Tangents = Optional(arrays, Mesh.ArrayType.Tangent)?.AsFloat32Array(),
                Colors = Optional(arrays, Mesh.ArrayType.Color)?.AsColorArray(),
                Uvs = Optional(arrays, Mesh.ArrayType.TexUV)?.AsVector2Array(),
                Bones = Optional(arrays, Mesh.ArrayType.Bones)?.AsInt32Array(),
                Weights = Optional(arrays, Mesh.ArrayType.Weights)?.AsFloat32Array(),
                Indices = Optional(arrays, Mesh.ArrayType.Index)?.AsInt32Array(),
            };
            Classify(mesh.SurfaceGetMaterial(s), surface);
            source.Surfaces.Add(surface);
        }
        return _sources[id] = source;
    }

    private static Variant? Optional(Godot.Collections.Array arrays, Mesh.ArrayType type)
    {
        var value = arrays[(int)type];
        if (value.VariantType == Variant.Type.Nil) return null;
        return value;
    }

    /// <summary>
    /// Which group a surface joins: an opaque <see cref="StandardMaterial3D"/> with no texture, or with only an albedo texture, joins the shared material for its
    /// shading and its image (none, or the same pixels whatever file they came from: <see cref="ImageIdentity"/>), with its colour baked into the vertex colours;
    /// anything else (see-through, a shader, other textures, an image that cannot be read) keeps its own material and its own surface.
    /// </summary>
    private void Classify(Material? material, SourceSurface surface)
    {
        if (material is StandardMaterial3D standard && standard.Transparency == BaseMaterial3D.TransparencyEnum.Disabled && OnlyAlbedo(standard))
        {
            var image = string.Empty;
            if (standard.AlbedoTexture is { } texture)
            {
                if (!_byImage || ImageOf(texture) is not { } known)
                {
                    Own(material, surface);
                    return;
                }
                // How the image is sampled is part of the material too.
                image = $"{known}:{standard.TextureFilter}:{standard.TextureRepeat}:{standard.Uv1Scale}:{standard.Uv1Offset}:{standard.Uv1Triplanar}";
            }
            var key = $"baked:{image}:{standard.ShadingMode}:{standard.Roughness:0.###}:{standard.Metallic:0.###}:{standard.CullMode}:{standard.VertexColorIsSrgb}:{standard.SpecularMode}:{standard.DiffuseMode}";
            if (!_baked.TryGetValue(key, out var shared))
            {
                shared = (StandardMaterial3D)standard.Duplicate(); // a textured one keeps the first file's copy of the image: every other copy is the same pixels
                shared.AlbedoColor = Colors.White;
                shared.VertexColorUseAsAlbedo = true;
                _baked[key] = shared;
            }
            surface.GroupKey = key;
            surface.Target = shared;
            surface.Bake = true;
            surface.LinearAlbedo = standard.AlbedoColor.SrgbToLinear();
            surface.Srgb = standard.VertexColorIsSrgb;
            return;
        }
        Own(material, surface);
    }

    private static void Own(Material? material, SourceSurface surface)
    {
        surface.GroupKey = "own:" + (material?.GetInstanceId() ?? 0);
        surface.Target = material;
    }

    /// <summary>True when the material uses no texture but the albedo one (a normal or emission map would be lost on the shared material).</summary>
    private static bool OnlyAlbedo(StandardMaterial3D material)
    {
        for (var p = 0; p < (int)BaseMaterial3D.TextureParam.Max; p++)
            if (p != (int)BaseMaterial3D.TextureParam.Albedo && material.GetTexture((BaseMaterial3D.TextureParam)p) is not null) return false;
        return true;
    }

    /// <summary>
    /// The image key of a texture, read once: the bytes of its imported file when it has one (no GPU read-back, and the same pixels imported the same way are the
    /// same bytes), else its pixels; null when neither can be read.
    /// </summary>
    private string? ImageOf(Texture2D texture) =>
        _images.KeyOf(texture.GetInstanceId(), texture.ResourcePath.Length > 0 ? texture.ResourcePath : texture.ResourceName, () =>
        {
            if (texture is CompressedTexture2D compressed && compressed.LoadPath is { Length: > 0 } path && Godot.FileAccess.FileExists(path))
                return ("file", Godot.FileAccess.GetFileAsBytes(path));
            if (texture.GetImage() is { } pixels && !pixels.IsEmpty())
                return ($"pixels:{pixels.GetWidth()}x{pixels.GetHeight()}:{(int)pixels.GetFormat()}:{pixels.HasMipmaps()}", pixels.GetData());
            return null;
        });

    // ---- worker: the merge over plain arrays ----

    private static Merged Merge(Job job)
    {
        var binds = new List<Bind>();
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var order = new List<Group>();
        AddMesh(groups, order, job.Body, Transform3D.Identity, MapBinds(binds, job.Body.Binds!, job.Named), -1);
        foreach (var part in job.Parts)
        {
            if (part.Binds is { } partBinds)
                AddMesh(groups, order, part, Transform3D.Identity, MapBinds(binds, partBinds, job.Named), -1);
            else
                AddMesh(groups, order, part, part.Transform, null,
                    job.Named ? BindIndex(binds, part.BoneName, -1, Transform3D.Identity) : BindIndex(binds, string.Empty, part.Bone, Transform3D.Identity));
        }
        return new Merged(binds, order);
    }

    /// <summary>Copies a source skin's binds into the merged list (a bind the same as one already there is reused) and returns old index to new.</summary>
    private static int[] MapBinds(List<Bind> into, Bind[] from, bool named)
    {
        var map = new int[from.Length];
        for (var i = 0; i < map.Length; i++) map[i] = BindIndex(into, named ? from[i].Name : string.Empty, named ? -1 : from[i].Bone, from[i].Pose);
        return map;
    }

    private static int BindIndex(List<Bind> binds, string name, int bone, Transform3D pose)
    {
        for (var i = 0; i < binds.Count; i++)
        {
            var same = name.Length > 0 ? binds[i].Name == name : binds[i].Bone == bone;
            if (same && binds[i].Pose.IsEqualApprox(pose)) return i;
        }
        binds.Add(new Bind(name, bone, pose));
        return binds.Count - 1;
    }

    /// <summary>Adds every surface of <paramref name="part"/> to its group, in its own colours or the character's. <paramref name="bindMap"/> remaps a skinned source's bones; otherwise every vertex goes fully to <paramref name="rigidBind"/>.</summary>
    private static void AddMesh(Dictionary<string, Group> groups, List<Group> order, Part part, Transform3D transform, int[]? bindMap, int rigidBind)
    {
        var recolor = part.Colors;
        var dominant = recolor.NeedsDominant ? Recolor.Dominant(Painted(part.Mesh)) : default;
        foreach (var surface in part.Mesh.Surfaces)
        {
            var vertices = surface.Vertices;
            if (vertices.Length == 0) continue;
            var normals = surface.Normals;
            var tangents = surface.Tangents;
            var colors = surface.Colors;
            var uvs = surface.Uvs;
            var bones = surface.Bones;
            var weights = surface.Weights;
            var indices = surface.Indices;
            if (!groups.TryGetValue(surface.GroupKey, out var group))
            {
                groups[surface.GroupKey] = group = new Group { Material = surface.Target };
                order.Add(group);
            }
            var start = group.Vertices.Count;
            var perVertex = bones is not null && bones.Length >= vertices.Length * 4 ? bones.Length / vertices.Length : 0;
            var basis = transform.Basis;
            for (var v = 0; v < vertices.Length; v++)
            {
                group.Vertices.Add(transform * vertices[v]);
                var normal = normals is not null && v < normals.Length ? (basis * normals[v]).Normalized() : Vector3.Up;
                group.Normals.Add(normal);
                // The tangent carries the outline direction (T-222: the normal averaged at hard edges); a source without one uses its normal.
                var outline = tangents is not null && tangents.Length >= (v + 1) * 4 ? (basis * new Vector3(tangents[v * 4], tangents[v * 4 + 1], tangents[v * 4 + 2])).Normalized() : normal;
                group.Tangents.Add(outline.X);
                group.Tangents.Add(outline.Y);
                group.Tangents.Add(outline.Z);
                group.Tangents.Add(1f);
                var color = colors is not null && v < colors.Length ? colors[v] : Colors.White;
                group.Colors.Add(Tint(surface, color, recolor, dominant));
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

    /// <summary>
    /// Albedo × vertex colour, done the way the shader does it: the albedo colour is sRGB, vertex colours are linear unless the material says sRGB. Vertex colours
    /// always count: glTF says COLOR_0 multiplies the base colour, though Godot's importer leaves the flag off, and the T-222 placeholders keep their colours there.
    /// The alpha is kept: 0 marks a glowing part (T-222), which takes no character colour (T-706). A merged textured surface (T-711) bakes its albedo colour the same
    /// way and keeps the image in the shared material, which multiplies it by the vertex colour; a surface of its own keeps its albedo in its material.
    /// </summary>
    private static Color Tint(SourceSurface surface, Color c, Recolor recolor, LinearColor dominant)
    {
        if (!surface.Bake && recolor.IsNone) return c;
        var painted = Paint(surface, c);
        if (!recolor.IsNone && c.A > 0) painted = recolor.Apply(painted, dominant);
        var result = new Color(painted.R, painted.G, painted.B, c.A);
        return surface.Srgb ? result.LinearToSrgb() : result;
    }

    /// <summary>A vertex's painted colour, linear: with the material's colour where that is baked in.</summary>
    private static LinearColor Paint(SourceSurface surface, Color c)
    {
        var v = surface.Srgb ? c.SrgbToLinear() : c;
        var a = surface.LinearAlbedo;
        return surface.Bake ? new LinearColor(a.R * v.R, a.G * v.G, a.B * v.B) : new LinearColor(v.R, v.G, v.B);
    }

    private static IEnumerable<LinearColor> Painted(SourceMesh mesh)
    {
        foreach (var surface in mesh.Surfaces)
            for (var v = 0; v < surface.Vertices.Length; v++)
            {
                var color = surface.Colors is not null && v < surface.Colors.Length ? surface.Colors[v] : Colors.White;
                if (color.A > 0) yield return Paint(surface, color);
            }
    }

    // ---- main thread: the engine objects ----

    private static (ArrayMesh, Skin) Build(Merged merged)
    {
        var skin = new Skin();
        foreach (var bind in merged.Binds)
        {
            if (bind.Name.Length > 0) skin.AddNamedBind(bind.Name, bind.Pose);
            else skin.AddBind(bind.Bone, bind.Pose);
        }
        var mesh = new ArrayMesh();
        foreach (var g in merged.Groups)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = g.Vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = g.Normals.ToArray();
            arrays[(int)Mesh.ArrayType.Tangent] = g.Tangents.ToArray();
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
}
