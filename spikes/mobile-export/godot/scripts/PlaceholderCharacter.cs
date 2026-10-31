using System.Collections.Generic;
using Godot;

namespace MobileSpike;

/// <summary>
/// Procedural rigged placeholder (no external assets): a 7-bone humanoid skeleton, one skinned
/// mesh with vertex colors and one material, and a looping walk clip. The mesh, skin, material
/// and animation are built once and shared by every instance, like imported glTF would be.
/// </summary>
public static class PlaceholderCharacter
{
    static readonly string[] BoneNames = { "hips", "spine", "head", "arm_l", "arm_r", "leg_l", "leg_r" };
    static readonly int[] BoneParents = { -1, 0, 1, 1, 1, 0, 0 };
    static readonly Vector3[] BoneLocal =
    {
        new(0, 0.9f, 0), new(0, 0.15f, 0), new(0, 0.5f, 0),
        new(0.3f, 0.45f, 0), new(-0.3f, 0.45f, 0), new(0.12f, 0, 0), new(-0.12f, 0, 0),
    };

    // Two detail levels: High sits just under the §9 per-character cap (5k), Low lets
    // 40 characters fit the 150k visible-triangle budget together with the scene.
    public enum Detail { High, Low }

    static readonly Dictionary<Detail, ArrayMesh> _meshes = new();
    static readonly Dictionary<Detail, int> _triangles = new();
    static StandardMaterial3D? _material;
    static Skin? _skin;
    static AnimationLibrary? _anims;

    public static int Triangles(Detail detail)
    {
        Build(detail);
        return _triangles[detail];
    }

    public static Node3D Create(Detail detail, float animOffset)
    {
        Build(detail);
        var root = new Node3D { Name = "Character" };
        var skel = new Skeleton3D { Name = "Skeleton3D" };
        root.AddChild(skel);
        for (int i = 0; i < BoneNames.Length; i++)
        {
            skel.AddBone(BoneNames[i]);
            skel.SetBoneParent(i, BoneParents[i]);
            skel.SetBoneRest(i, new Transform3D(Basis.Identity, BoneLocal[i]));
        }
        skel.ResetBonePoses();
        skel.AddChild(new MeshInstance3D { Name = "Body", Mesh = _meshes[detail], Skin = _skin });

        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        root.AddChild(player);
        player.AddAnimationLibrary("", _anims);
        player.Play("walk");
        player.Seek(animOffset, true);
        return root;
    }

    static void Build(Detail detail)
    {
        if (_meshes.ContainsKey(detail)) return;
        // (radial, rings) for torso, head, limbs.
        var (tr, tg, hr, hg, lr, lg) = detail == Detail.High ? (22, 10, 20, 11, 14, 8) : (16, 8, 16, 10, 12, 6);

        // Global rest positions (bones are translation-only at rest).
        var global = new Vector3[BoneNames.Length];
        for (int i = 0; i < global.Length; i++)
            global[i] = BoneLocal[i] + (BoneParents[i] >= 0 ? global[BoneParents[i]] : Vector3.Zero);

        var skin = new Color(0.93f, 0.76f, 0.62f);
        var shirt = new Color(0.25f, 0.45f, 0.8f);
        var pants = new Color(0.3f, 0.25f, 0.2f);
        var steel = new Color(0.75f, 0.75f, 0.8f);

        var parts = new List<(PrimitiveMesh mesh, Vector3 at, int bone, Color color)>
        {
            (new CapsuleMesh { Radius = 0.22f, Height = 0.8f, RadialSegments = tr, Rings = tg }, global[1] + new Vector3(0, 0.2f, 0), 1, shirt),
            (new SphereMesh { Radius = 0.17f, Height = 0.34f, RadialSegments = hr, Rings = hg }, global[2] + new Vector3(0, 0.12f, 0), 2, skin),
            (new CapsuleMesh { Radius = 0.07f, Height = 0.62f, RadialSegments = lr, Rings = lg }, global[3] + new Vector3(0, -0.28f, 0), 3, shirt),
            (new CapsuleMesh { Radius = 0.07f, Height = 0.62f, RadialSegments = lr, Rings = lg }, global[4] + new Vector3(0, -0.28f, 0), 4, shirt),
            (new CapsuleMesh { Radius = 0.09f, Height = 0.9f, RadialSegments = lr, Rings = lg }, global[5] + new Vector3(0, -0.45f, 0), 5, pants),
            (new CapsuleMesh { Radius = 0.09f, Height = 0.9f, RadialSegments = lr, Rings = lg }, global[6] + new Vector3(0, -0.45f, 0), 6, pants),
            (new BoxMesh { Size = new Vector3(0.05f, 0.7f, 0.05f) }, global[4] + new Vector3(0, -0.55f, 0.2f), 4, steel),
        };

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var colors = new List<Color>();
        var bones = new List<int>();
        var weights = new List<float>();
        var indices = new List<int>();
        foreach (var (mesh, at, bone, color) in parts)
        {
            var a = mesh.GetMeshArrays();
            var pv = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var pn = a[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var pu = a[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var pi = a[(int)Mesh.ArrayType.Index].AsInt32Array();
            int baseIndex = verts.Count;
            for (int i = 0; i < pv.Length; i++)
            {
                verts.Add(pv[i] + at);
                normals.Add(pn[i]);
                uvs.Add(pu.Length > i ? pu[i] : Vector2.Zero);
                colors.Add(color);
                bones.AddRange(new[] { bone, 0, 0, 0 });
                weights.AddRange(new[] { 1f, 0f, 0f, 0f });
            }
            foreach (int idx in pi) indices.Add(baseIndex + idx);
        }
        _triangles[detail] = indices.Count / 3;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Bones] = bones.ToArray();
        arrays[(int)Mesh.ArrayType.Weights] = weights.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var built = new ArrayMesh();
        built.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        built.SurfaceSetMaterial(0, _material ??= new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.8f });
        _meshes[detail] = built;

        if (_skin != null) return;
        _skin = new Skin();
        for (int i = 0; i < BoneNames.Length; i++)
            _skin.AddNamedBind(BoneNames[i], new Transform3D(Basis.Identity, global[i]).AffineInverse());

        _anims = new AnimationLibrary();
        _anims.AddAnimation("walk", BuildWalk());
    }

    static Animation BuildWalk()
    {
        var anim = new Animation { Length = 1f, LoopMode = Animation.LoopModeEnum.Linear };
        void Swing(string bone, float sign)
        {
            int t = anim.AddTrack(Animation.TrackType.Rotation3D);
            anim.TrackSetPath(t, $"Skeleton3D:{bone}");
            float[] angles = { 0, 30, 0, -30, 0 };
            for (int k = 0; k < angles.Length; k++)
                anim.RotationTrackInsertKey(t, k * 0.25f, new Quaternion(Vector3.Right, Mathf.DegToRad(angles[k] * sign)));
        }
        Swing("leg_l", 1); Swing("leg_r", -1); Swing("arm_l", -0.8f); Swing("arm_r", 0.8f);

        int bob = anim.AddTrack(Animation.TrackType.Position3D);
        anim.TrackSetPath(bob, "Skeleton3D:hips");
        float[] ys = { 0.9f, 0.95f, 0.9f, 0.95f, 0.9f };
        for (int k = 0; k < ys.Length; k++) anim.PositionTrackInsertKey(bob, k * 0.25f, new Vector3(0, ys[k], 0));
        return anim;
    }
}
