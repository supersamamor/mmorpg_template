using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Placeholder shapes by kind: capsule (players, NPCs), sphere (monsters), small box (ground items), each with a nose so facing shows. One
/// shared mesh and a few shared materials, and pooled nodes. Far under the §9 budgets at 40 entities: each shape is a few hundred triangles at most.
/// </summary>
public sealed class PlaceholderEntities : IEntityVisualSource
{
    private readonly Node3D _parent;
    private readonly Mesh _capsule;
    private readonly Mesh _sphere;
    private readonly Mesh _box;
    private readonly Dictionary<string, Stack<IEntityVisual>> _pools = new(StringComparer.Ordinal);
    private readonly Dictionary<Color, StandardMaterial3D> _materials = new();

    /// <summary>Creates the source; its nodes go under <paramref name="parent"/>.</summary>
    public PlaceholderEntities(Node3D parent)
    {
        _parent = parent;
        _capsule = WithNose(new CapsuleMesh { Radius = 0.35f, Height = 1.7f, RadialSegments = 12, Rings = 2 }, 0.85f, 0.35f);
        _sphere = WithNose(new SphereMesh { Radius = 0.45f, Height = 0.9f, RadialSegments = 12, Rings = 6 }, 0.45f, 0.45f);
        _box = new BoxMesh { Size = new Vector3(0.35f, 0.35f, 0.35f) };
    }

    /// <inheritdoc />
    public IEntityVisual Acquire(string kind)
    {
        if (_pools.TryGetValue(kind, out var pool) && pool.Count > 0)
        {
            var pooled = pool.Pop();
            pooled.SetVisible(true);
            return pooled;
        }

        var (mesh, height) = kind switch
        {
            EntityKinds.Item => (_box, 0.175f),
            EntityKinds.Monster => (_sphere, 0.45f),
            _ => (_capsule, 0.85f),
        };
        var node = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _parent.AddChild(node);
        return new Visual(node, kind, height, this);
    }

    /// <inheritdoc />
    public void Release(string kind, IEntityVisual visual)
    {
        visual.SetVisible(false);
        if (!_pools.TryGetValue(kind, out var pool)) _pools[kind] = pool = new Stack<IEntityVisual>();
        pool.Push(visual);
    }

    private StandardMaterial3D Material(Color color)
    {
        if (!_materials.TryGetValue(color, out var material))
        {
            material = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.9f, MetallicSpecular = 0.1f };
            _materials[color] = material;
        }
        return material;
    }

    /// <summary>Merges a shape and a small box in front of it (north, -Z) into one surface.</summary>
    private static ArrayMesh WithNose(PrimitiveMesh shape, float noseHeight, float bodyRadius)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.AppendFrom(shape, 0, Transform3D.Identity);
        var nose = new BoxMesh { Size = new Vector3(0.14f, 0.14f, 0.2f) };
        tool.AppendFrom(nose, 0, new Transform3D(Basis.Identity, new Vector3(0, noseHeight - bodyRadius, -bodyRadius)));
        return tool.Commit();
    }

    private sealed class Visual : IEntityVisual
    {
        private readonly MeshInstance3D _node;
        private readonly string _kind;
        private readonly float _height;
        private readonly PlaceholderEntities _source;
        private bool _dead;
        private float _x, _z, _yaw;

        public Visual(MeshInstance3D node, string kind, float height, PlaceholderEntities source)
        {
            _node = node;
            _kind = kind;
            _height = height;
            _source = source;
        }

        public void SetPose(float x, float z, float yaw)
        {
            _x = x;
            _z = z;
            _yaw = yaw;
            Apply();
        }

        public void SetLook(EntityLook look)
        {
            _dead = look.Dead;
            var color = _kind switch
            {
                _ when look.Dead => PlaceholderColors.Dead,
                _ when look.Own => PlaceholderColors.Own,
                EntityKinds.Player => PlaceholderColors.Player,
                EntityKinds.Monster => PlaceholderColors.Monster,
                EntityKinds.Npc => PlaceholderColors.Npc,
                EntityKinds.Item => look.Mine ? PlaceholderColors.ItemMine : PlaceholderColors.Item,
                _ => PlaceholderColors.Item,
            };
            _node.MaterialOverride = _source.Material(color);
            _node.CastShadow = look.Own ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off; // §9: player only
            _node.Scale = look.Dead ? new Vector3(1.2f, 0.25f, 1.2f) : Vector3.One;
            Apply();
        }

        public void SetVisible(bool visible) => _node.Visible = visible;

        private void Apply()
        {
            _node.Position = new Vector3(_x, _dead ? _height * 0.25f : _height, _z);
            _node.Rotation = new Vector3(0, _yaw, 0);
        }
    }
}
