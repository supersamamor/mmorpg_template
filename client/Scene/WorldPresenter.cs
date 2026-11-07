using Game.Client.Core;
using Game.Client.Core.World;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Reads the view model and draws it, and nothing else: entity visuals, one batched layer of HP bars, the tap marker and the target ring. It touches
/// only what changed, so a still scene costs a few comparisons (§9), and it allocates nothing per frame after warm-up.
/// </summary>
public sealed class WorldPresenter
{
    private const int MaxBars = 256;
    private const float BarHeight = 2.05f;

    private readonly WorldView _world;
    private readonly UiState _ui;
    private readonly IEntityVisualSource _visuals;
    private readonly MultiMesh _bars;
    private readonly MeshInstance3D _marker;
    private readonly MeshInstance3D _ring;
    private bool _barsDirty;
    private int _uiVersion = -1;

    /// <summary>Builds the bar layer and markers under <paramref name="parent"/>.</summary>
    public WorldPresenter(Node3D parent, WorldView world, UiState ui, IEntityVisualSource visuals)
    {
        _world = world;
        _ui = ui;
        _visuals = visuals;

        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, shadows_disabled;
                varying float fill;
                void vertex() {
                    fill = INSTANCE_CUSTOM.r;
                    vec3 center = MODEL_MATRIX[3].xyz;
                    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], vec4(center, 1.0));
                }
                void fragment() {
                    ALBEDO = UV.x < fill ? vec3(0.25, 0.85, 0.3) : vec3(0.12, 0.12, 0.12);
                }
                """,
        };
        _bars = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = new Vector2(0.9f, 0.1f) },
            InstanceCount = MaxBars,
            VisibleInstanceCount = 0,
        };
        parent.AddChild(new MultiMeshInstance3D
        {
            Multimesh = _bars,
            MaterialOverride = new ShaderMaterial { Shader = shader },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(-4096, -8, -4096), new Vector3(8192, 16, 8192)),
        });

        _marker = Disc(parent, 0.8f, PlaceholderColors.Marker);
        _ring = Disc(parent, 1.1f, PlaceholderColors.TargetRing);
    }

    /// <summary>Brings the screen up to date. Allocates nothing once visuals are pooled.</summary>
    public void Sync()
    {
        var removed = _world.Removed;
        for (var i = 0; i < removed.Count; i++)
        {
            var entity = removed[i];
            if (entity.Visual is IEntityVisual visual) _visuals.Release(entity.Kind, visual);
            _barsDirty = true;
        }
        if (removed.Count > 0) _world.ClearRemoved();

        var entities = _world.Entities;
        var ownId = _world.OwnId;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (entity.Visual is null)
            {
                entity.Visual = _visuals.Acquire(entity.Kind);
                entity.PoseDirty = entity.LookDirty = true;
            }
            var visual = (IEntityVisual)entity.Visual;
            if (entity.LookDirty)
            {
                visual.SetLook(new EntityLook(entity.Id == ownId, entity.Dead, entity.Mine));
                entity.LookDirty = false;
                _barsDirty = true;
            }
            if (entity.PoseDirty)
            {
                visual.SetPose((float)entity.X, (float)entity.Z, (float)entity.Yaw);
                entity.PoseDirty = false;
                if (entity.Hp.HasValue) _barsDirty = true;
            }
        }

        if (_barsDirty) RewriteBars();
        SyncMarkers();
    }

    private void RewriteBars()
    {
        _barsDirty = false;
        var entities = _world.Entities;
        var ownId = _world.OwnId;
        var count = 0;
        for (var i = 0; i < entities.Count && count < MaxBars; i++)
        {
            var entity = entities[i];
            if (entity.Hp is not { } hp || entity.Dead || (hp >= 1 && entity.Id != ownId)) continue;
            _bars.SetInstanceTransform(count, new Transform3D(Basis.Identity, new Vector3((float)entity.X, BarHeight, (float)entity.Z)));
            _bars.SetInstanceCustomData(count, new Color((float)hp, 0, 0, 0));
            count++;
        }
        _bars.VisibleInstanceCount = count;
    }

    private void SyncMarkers()
    {
        if (_uiVersion != _ui.Version)
        {
            _uiVersion = _ui.Version;
            if (_ui.Marker is { } cell)
            {
                _marker.Position = new Vector3(cell.X + 0.5f, 0.03f, cell.Y + 0.5f);
                _marker.Visible = true;
            }
            else
            {
                _marker.Visible = false;
            }
            _ring.Visible = _ui.Target.HasValue;
        }
        if (_ui.Target is { } target && _world.Find(target) is { } entity)
            _ring.Position = new Vector3((float)entity.X, 0.04f, (float)entity.Z);
    }

    private static MeshInstance3D Disc(Node3D parent, float size, Color color)
    {
        var node = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(size, size) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        parent.AddChild(node);
        return node;
    }
}
