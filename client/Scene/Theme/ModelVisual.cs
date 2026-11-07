using Game.Client.Core;
using Game.Client.Core.Animation;
using Game.Client.Core.Theme;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// One entity's model: a root node placed on the ground, holding an instance of the theme's scene (or a built-in marker when the theme has nothing). Pooled.
/// The instance is made when <see cref="TryBuild"/> succeeds; until then the visual is hidden. Setting a pose, a look or a motion allocates nothing.
/// </summary>
public sealed class ModelVisual : IEntityVisual
{
    private const string Lod0Name = "Body_LOD0";
    private const string Lod1Name = "Body_LOD1";

    private static Mesh? s_markerMesh;
    private static Mesh? s_ringMesh;
    private static StandardMaterial3D? s_markerMaterial;
    private static StandardMaterial3D? s_ownMaterial;
    private static StandardMaterial3D? s_mineMaterial;

    private readonly ModelStore _store;
    private readonly ThemeCatalog _catalog;
    private readonly ModelResolution _resolution;
    private readonly AnimationDriver _driver = new();

    private Node3D? _content;
    private MeshInstance3D? _lod0;
    private MeshInstance3D? _lod1;
    private MeshInstance3D? _ring;
    private AnimationPlayer? _player;
    private AnimSet? _anims;
    private bool _ready;
    private bool _using;
    private bool _wantVisible;
    private bool _baseline = true;
    private bool _firstClip = true;
    private EntityLook _look;
    private int _lod;
    private double _pending;

    /// <summary>Creates a visual for a resolved model. It is hidden and not ready until <see cref="TryBuild"/> succeeds.</summary>
    public ModelVisual(ModelStore store, ThemeCatalog catalog, ModelResolution resolution)
    {
        _store = store;
        _catalog = catalog;
        _resolution = resolution;
        Root = new Node3D { Visible = false, Name = "Entity" };
    }

    /// <summary>The node that is placed on the ground.</summary>
    public Node3D Root { get; }

    /// <summary>True once the model is in place (or the built-in marker stands in for it).</summary>
    public bool Ready => _ready;

    /// <summary>Which pool this belongs to: the model's file, or empty for the built-in marker.</summary>
    public string PoolKey => _resolution.Path ?? string.Empty;

    /// <inheritdoc />
    public bool Animated => _player is not null;

    /// <summary>Takes the visual out of the pool for a new entity.</summary>
    public void Attach()
    {
        _baseline = true;
        _firstClip = true;
        _pending = 0;
        if (_ready && !_using && _resolution.Path is not null)
        {
            _store.Use(_resolution.Path);
            _using = true;
        }
    }

    /// <summary>Puts the visual away: hidden, still, and no longer counted as using its model.</summary>
    public void Detach()
    {
        _wantVisible = false;
        Root.Visible = false;
        _player?.Stop();
        if (_ring is not null) _ring.Visible = false;
        if (_using && _resolution.Path is not null) _store.Unuse(_resolution.Path);
        _using = false;
        _look = default;
    }

    /// <summary>Frees the nodes.</summary>
    public void Free()
    {
        Detach();
        Root.QueueFree();
    }

    /// <summary>
    /// Tries to make the visual ready. True when it is: instantiated (<paramref name="created"/> goes up), or built-in because the model can never load.
    /// False while the model or its animations are still loading.
    /// </summary>
    public bool TryBuild(ref int created)
    {
        if (_ready) return true;
        if (_resolution.Path is null || _resolution.Asset is null)
        {
            BuildBuiltIn();
            return true;
        }
        if (!_store.TryGetScene(_resolution.Path, out var scene, out var failed))
        {
            if (!failed) return false;
            BuildBuiltIn();
            return true;
        }
        if (_resolution.Asset.Anims is not null && _catalog.ResolveAnimations(_resolution.Asset, out var libraryPath) is { } library)
        {
            if (!_store.TryGetAnimations(libraryPath, library, out _anims)) return false;
        }

        var node = scene!.Instantiate<Node3D>();
        created++;
        _content = node;
        Root.AddChild(node);
        _lod0 = node.FindChild(Lod0Name, true, false) as MeshInstance3D;
        _lod1 = node.FindChild(Lod1Name, true, false) as MeshInstance3D;
        if (_lod0 is null && node is MeshInstance3D whole) _lod0 = whole;
        if (_anims is not null && _anims.Names[(int)AnimClip.Idle] is not null)
        {
            _player = new AnimationPlayer { CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual, Name = "Animations" };
            node.AddChild(_player);
            _player.AddAnimationLibrary(string.Empty, _anims.Library);
            _player.AnimationFinished += _ => _driver.OnFinished();
        }
        _store.Use(_resolution.Path);
        _using = true;
        Ready_();
        return true;
    }

    private void BuildBuiltIn()
    {
        s_markerMesh ??= new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) };
        s_markerMaterial ??= new StandardMaterial3D { AlbedoColor = new Color(1, 0, 1), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _lod0 = new MeshInstance3D { Mesh = s_markerMesh, MaterialOverride = s_markerMaterial, Position = new Vector3(0, 0.4f, 0), Name = "Missing" };
        Root.AddChild(_lod0);
        Ready_();
    }

    private void Ready_()
    {
        _ready = true;
        Root.Visible = _wantVisible;
        ApplyDetail();
        ApplyLook();
    }

    /// <inheritdoc />
    public void SetPose(float x, float z, float yaw)
    {
        Root.Position = new Vector3(x, 0, z);
        Root.Rotation = new Vector3(0, yaw, 0);
    }

    /// <inheritdoc />
    public void SetLook(EntityLook look)
    {
        _look = look;
        if (_ready) ApplyLook();
    }

    /// <inheritdoc />
    public void SetVisible(bool visible)
    {
        _wantVisible = visible;
        Root.Visible = visible && _ready;
    }

    /// <inheritdoc />
    public void SetDetail(int lod)
    {
        if (_lod == lod) return;
        _lod = lod;
        if (_ready) ApplyDetail();
    }

    /// <inheritdoc />
    public void SetMotion(bool walking, int attackCount, int hitCount)
    {
        if (_player is null || !_ready) return;
        if (_baseline)
        {
            _driver.Reset(attackCount, hitCount);
            _baseline = false;
        }
        if (_driver.Update(_look.Dead, walking, attackCount, hitCount, out var clip)) Play(clip);
    }

    /// <inheritdoc />
    public void Animate(double dt, bool fullRate, long frame, int reducedEvery)
    {
        if (_player is null || !_ready || !_wantVisible) return;
        _pending += dt;
        if (!fullRate && frame % Math.Max(1, reducedEvery) != 0) return;
        _player.Advance(_pending);
        _pending = 0;
    }

    private void Play(AnimClip clip)
    {
        var name = _anims!.Names[(int)clip];
        if (name is null)
        {
            _driver.OnFinished(); // the theme has no such clip: carry on as if it had ended
            return;
        }
        _player!.Play(name);
        if (clip == AnimClip.Die && _firstClip) _player.Seek(_player.GetAnimation(name).Length, true); // it appeared already dead: lie down at once
        else _player.Advance(0);
        _firstClip = false;
        _pending = 0;
    }

    private void ApplyDetail()
    {
        if (_lod0 is null) return;
        var low = _lod == 1 && _lod1 is not null;
        _lod0.Visible = !low;
        if (_lod1 is not null) _lod1.Visible = low;
    }

    private void ApplyLook()
    {
        var shadow = _look.Own ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off; // §9: the player only
        if (_lod0 is not null) _lod0.CastShadow = shadow;
        if (_lod1 is not null) _lod1.CastShadow = shadow;

        if (_look.Own || _look.Mine)
        {
            if (_ring is null)
            {
                s_ringMesh ??= new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.62f, Rings = 20, RingSegments = 4 };
                s_ownMaterial ??= new StandardMaterial3D { AlbedoColor = PlaceholderColors.Own, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
                s_mineMaterial ??= new StandardMaterial3D { AlbedoColor = PlaceholderColors.ItemMine, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
                _ring = new MeshInstance3D { Mesh = s_ringMesh, Position = new Vector3(0, 0.03f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Ring" };
                Root.AddChild(_ring);
            }
            _ring.MaterialOverride = _look.Own ? s_ownMaterial : s_mineMaterial;
            _ring.Visible = true;
        }
        else if (_ring is not null)
        {
            _ring.Visible = false;
        }
    }
}
