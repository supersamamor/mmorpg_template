using System;
using System.Collections.Generic;
using Game.Client.Core;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Reads the view model and draws it, and nothing else: entity visuals, one batched layer of HP bars, one batched layer of the rings under the player and
/// under ground items this client may pick up (T-220: a ring each was one draw call, and a crowd fight leaves many drops), the tap marker and the target ring. It touches
/// only what changed, so a still scene costs a few comparisons (§9), and it allocates nothing per frame after warm-up.
/// </summary>
public sealed class WorldPresenter
{
    private const int MaxBars = 256;
    private const float BarHeight = 2.05f;
    private const float StatusMarkAbove = 0.22f;   // the status dot sits just above the HP bar

    private readonly WorldView _world;
    private readonly UiState _ui;
    private readonly IEntityVisualSource _visuals;
    private readonly ClientConfig _config;
    private readonly GroundHeights _ground;
    private readonly DetailPlanner _planner;
    private readonly MultiMesh _bars;
    private readonly MultiMesh _rings;
    private readonly MultiMesh _statusMarks;
    private readonly MeshInstance3D _marker;
    private readonly MeshInstance3D _ring;
    private bool[] _fullRate = new bool[64];
    private bool _barsDirty;
    private bool _replan = true;
    private int _uiVersion = -1;
    private int _visualVersion;
    private int _groundVersion;
    private long _frame;
    private readonly List<Corpse> _corpses = new();

    /// <summary>A death became visible (after its killing blow landed): world x, ground height, world z. The combat effects play there.</summary>
    public event Action<float, float, float>? DeathShown;

    /// <summary>A status started on an entity (T-501): status id, world x, ground height, world z, and whether it is the own character. Its vfx plays there.</summary>
    public event Action<string, float, float, float, bool>? StatusStarted;

    /// <summary>Status markers drawn above other characters this frame (for the scripted run and measuring).</summary>
    public int StatusMarkCount => _statusMarks.VisibleInstanceCount;

    private IReadOnlyList<string>? _dressOverride;
    private int _dressEpoch;

    /// <summary>
    /// Measuring only (<c>--measure-dress</c>): every player is drawn wearing these item ids instead of what the server says, so a crowd's cost can be measured
    /// fully equipped without server changes. Null (the default) draws what the server says. Never set while playing.
    /// </summary>
    public IReadOnlyList<string>? DressOverride
    {
        get => _dressOverride;
        set
        {
            _dressOverride = value;
            _dressEpoch++;
            var entities = _world.Entities;
            for (var i = 0; i < entities.Count; i++) entities[i].LookDirty = true;
        }
    }

    /// <summary>Corpses fading out after leaving view (for measuring).</summary>
    public int FadingCorpses => _corpses.Count;

    private readonly record struct Corpse(IEntityVisual Visual, double Start);

    /// <summary>Builds the bar layer and markers under <paramref name="parent"/>.</summary>
    public WorldPresenter(Node3D parent, ClientConfig config, WorldView world, UiState ui, IEntityVisualSource visuals, GroundHeights ground)
    {
        _config = config;
        _ground = ground;
        _planner = new DetailPlanner(config.Lod0Count, config.FullRateAnimatedCount);
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

        _rings = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.62f, Rings = 20, RingSegments = 4 },
            InstanceCount = MaxBars,
            VisibleInstanceCount = 0,
        };
        parent.AddChild(new MultiMeshInstance3D
        {
            Multimesh = _rings,
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(-4096, -8, -4096), new Vector3(8192, 16, 8192)),
        });

        // Status markers (T-501): one small dot above every other character with a status, coloured debuff or buff. One multimesh, so one draw call for
        // the whole crowd (ADR-002 row 164). The own character's statuses are icons in the HUD instead.
        var dotShader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, shadows_disabled;
                void vertex() {
                    vec3 center = MODEL_MATRIX[3].xyz;
                    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], vec4(center, 1.0));
                }
                void fragment() {
                    if (distance(UV, vec2(0.5)) > 0.5) discard;
                    ALBEDO = COLOR.rgb;
                }
                """,
        };
        _statusMarks = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new QuadMesh { Size = new Vector2(0.22f, 0.22f) },
            InstanceCount = MaxBars,
            VisibleInstanceCount = 0,
        };
        parent.AddChild(new MultiMeshInstance3D
        {
            Multimesh = _statusMarks,
            MaterialOverride = new ShaderMaterial { Shader = dotShader },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(-4096, -8, -4096), new Vector3(8192, 16, 8192)),
        });

        _marker = Disc(parent, 0.8f, PlaceholderColors.Marker);
        _ring = Disc(parent, 1.1f, PlaceholderColors.TargetRing);
    }

    /// <summary>Visuals currently at the full level of detail, and animated characters at the reduced rate (for measuring).</summary>
    public (int Lod0, int Lod1, int Reduced) DetailCounts()
    {
        int lod0 = 0, lod1 = 0, reduced = 0;
        for (var i = 0; i < _slots.Length && i < _world.Entities.Count; i++)
        {
            if (_slots[i] < 0) continue;
            if (_planner.LodOf(_slots[i]) == 0) lod0++;
            else lod1++;
            if (!_planner.FullRate(_slots[i])) reduced++;
        }
        return (lod0, lod1, reduced);
    }

    private int[] _slots = new int[64];

    /// <summary>Brings the screen up to date. Allocates nothing once visuals are pooled. <paramref name="dt"/> is the frame time in seconds.</summary>
    public void Sync(double dt)
    {
        _frame++;
        _visuals.Update();
        var removed = _world.Removed;
        for (var i = 0; i < removed.Count; i++)
        {
            var entity = removed[i];
            if (entity.Visual is IEntityVisual visual)
            {
                // A body that leaves view fades out instead of vanishing (§7.6); anything else goes back to the pool at once.
                if (entity.DeadShown) _corpses.Add(new Corpse(visual, Clocks.View()));
                else _visuals.Release(visual);
            }
            _barsDirty = true;
            _replan = true;
        }
        if (removed.Count > 0) _world.ClearRemoved();

        if (_corpses.Count > 0) FadeCorpses(dt);

        var entities = _world.Entities;
        var ownId = _world.OwnId;
        if (_groundVersion != _ground.Version)
        {
            // The heights arrive a few frames after the scene: everything on the ground is placed again.
            _groundVersion = _ground.Version;
            for (var i = 0; i < entities.Count; i++) entities[i].PoseDirty = true;
            _barsDirty = true;
            _uiVersion = -1;
        }
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (entity.Visual is null)
            {
                entity.Visual = _visuals.Acquire(entity.Kind, entity.DisplayKey);
                entity.PoseDirty = entity.LookDirty = true;
                _replan = true;
            }
            var visual = (IEntityVisual)entity.Visual;
            if (entity.LookDirty)
            {
                visual.SetLook(new EntityLook(entity.Id == ownId, entity.DeadShown, entity.Mine));
                // Worn equipment (T-403): the visual re-dresses only when the look counter moved, and swaps only the changed pieces.
                var worn = _dressOverride is not null && entity.Kind == EntityKinds.Player ? _dressOverride : entity.Worn;
                visual.SetGear(entity.Weapon, worn, unchecked(entity.LookVersion + _dressEpoch * 1_000_003));
                entity.LookDirty = false;
                _barsDirty = true;
            }
            if (entity.PoseDirty)
            {
                visual.SetPose((float)entity.X, _ground.At(entity.X, entity.Z), (float)entity.Z, (float)entity.Yaw);
                entity.PoseDirty = false;
                _replan = true; // distances to the player changed
                if (entity.Hp.HasValue || entity.Mine) _barsDirty = true; // bars and rings follow
            }
            visual.SetMotion(entity.IsWalking, entity.AttackCount, entity.HitCount);
            if (entity.StartedStatuses.Count > 0)
            {
                foreach (var status in entity.StartedStatuses)
                    StatusStarted?.Invoke(status, (float)entity.X, _ground.At(entity.X, entity.Z), (float)entity.Z, entity.Id == ownId);
                entity.StartedStatuses.Clear();
            }
            if (entity.DeathFxPending)
            {
                entity.DeathFxPending = false;
                DeathShown?.Invoke((float)entity.X, _ground.At(entity.X, entity.Z), (float)entity.Z);
            }
        }

        if (_visualVersion != _visuals.Version)
        {
            _visualVersion = _visuals.Version;
            _replan = true;
        }
        if (_replan) Replan();
        for (var i = 0; i < entities.Count; i++)
            ((IEntityVisual)entities[i].Visual!).Animate(dt, _fullRate[i], _frame, _config.ReducedAnimationDivisor);

        if (_barsDirty) RewriteBars();
        SyncMarkers();
    }

    private void FadeCorpses(double dt)
    {
        var now = Clocks.View();
        var seconds = Math.Max(0.01, _config.CorpseFadeSeconds);
        for (var i = _corpses.Count - 1; i >= 0; i--)
        {
            var corpse = _corpses[i];
            var t = (float)((now - corpse.Start) / seconds);
            if (t >= 1f)
            {
                _visuals.Release(corpse.Visual);
                _corpses.RemoveAt(i);
                continue;
            }
            corpse.Visual.SetFade(t);
            corpse.Visual.Animate(dt, true, _frame, 1); // a dying body may still be going down
        }
    }

    /// <summary>Ranks the animated characters by distance to the player: the nearest few get the full level of detail, the nearest 40 the full animation rate (§9).</summary>
    private void Replan()
    {
        _replan = false;
        var entities = _world.Entities;
        if (entities.Count > _fullRate.Length)
        {
            Array.Resize(ref _fullRate, entities.Count * 2);
            Array.Resize(ref _slots, entities.Count * 2);
        }
        var own = _world.Own;
        var ox = own?.X ?? 0;
        var oz = own?.Z ?? 0;
        _planner.Begin();
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (((IEntityVisual)entity.Visual!).Animated)
            {
                var dx = entity.X - ox;
                var dz = entity.Z - oz;
                _slots[i] = _planner.Add(entity.Id, dx * dx + dz * dz);
            }
            else
            {
                _slots[i] = -1;
            }
        }
        _planner.Plan();
        for (var i = 0; i < entities.Count; i++)
        {
            var slot = _slots[i];
            _fullRate[i] = slot < 0 || _planner.FullRate(slot);
            if (slot >= 0) ((IEntityVisual)entities[i].Visual!).SetDetail(_planner.LodOf(slot));
        }
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
            _bars.SetInstanceTransform(count, new Transform3D(Basis.Identity, new Vector3((float)entity.X, _ground.At(entity.X, entity.Z) + BarHeight, (float)entity.Z)));
            _bars.SetInstanceCustomData(count, new Color((float)hp, 0, 0, 0));
            count++;
        }
        _bars.VisibleInstanceCount = count;

        var rings = 0;
        for (var i = 0; i < entities.Count && rings < MaxBars; i++)
        {
            var entity = entities[i];
            var own = entity.Id == ownId;
            if (!own && !entity.Mine) continue;
            _rings.SetInstanceTransform(rings, new Transform3D(Basis.Identity, new Vector3((float)entity.X, _ground.At(entity.X, entity.Z) + 0.03f, (float)entity.Z)));
            _rings.SetInstanceColor(rings, own ? PlaceholderColors.Own : PlaceholderColors.ItemMine);
            rings++;
        }
        _rings.VisibleInstanceCount = rings;

        var marks = 0;
        for (var i = 0; i < entities.Count && marks < MaxBars; i++)
        {
            var entity = entities[i];
            if (entity.Statuses.Count == 0 || entity.Dead || entity.Id == ownId) continue;
            _statusMarks.SetInstanceTransform(marks, new Transform3D(Basis.Identity,
                new Vector3((float)entity.X, _ground.At(entity.X, entity.Z) + BarHeight + StatusMarkAbove, (float)entity.Z)));
            _statusMarks.SetInstanceColor(marks, entity.HasDebuff ? PlaceholderColors.StatusDebuff : PlaceholderColors.StatusBuff);
            marks++;
        }
        _statusMarks.VisibleInstanceCount = marks;
    }

    private void SyncMarkers()
    {
        if (_uiVersion != _ui.Version)
        {
            _uiVersion = _ui.Version;
            if (_ui.Marker is { } cell)
            {
                _marker.Position = new Vector3(cell.X + 0.5f, _ground.At(cell.X + 0.5, cell.Y + 0.5) + 0.03f, cell.Y + 0.5f);
                _marker.Visible = true;
            }
            else
            {
                _marker.Visible = false;
            }
            _ring.Visible = _ui.Target.HasValue;
        }
        if (_ui.Target is { } target && _world.Find(target) is { } entity)
            _ring.Position = new Vector3((float)entity.X, _ground.At(entity.X, entity.Z) + 0.04f, (float)entity.Z);
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
