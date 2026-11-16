using Game.Client.Core;
using Game.Client.Core.Animation;
using Game.Client.Core.Theme;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// One entity's model: a root node placed on the ground, holding an instance of the theme's scene (or a built-in marker when the theme has nothing). Pooled.
/// The instance is made when <see cref="TryBuild"/> succeeds; until then the visual is hidden. Setting a pose, a look or a motion allocates nothing.
/// <para>Worn equipment (T-403, §7.5): each worn item resolves to a piece (<see cref="EquipmentCatalog"/>). An attached piece rides its bone of the character's
/// skeleton; a skinned piece is bound to the same skeleton, so it plays the same animation. Both are merged with the body into the body's own skinned mesh
/// (<see cref="GearMesher"/>), so a dressed character still costs one draw call (§9). The look counter decides when to re-dress; a change loads only the pieces
/// that changed, and the merged mesh comes from a cache shared by every character with the same body and pieces.</para>
/// </summary>
public sealed class ModelVisual : IEntityVisual
{
    private const string Lod0Name = "Body_LOD0";
    private const string Lod1Name = "Body_LOD1";

    /// <summary>A piece standing in for a missing or wrong key is the theme's missing model, drawn this small so it marks the problem without hiding the character.</summary>
    private const float FallbackPieceScale = 0.3f;

    private static BoxMesh? s_markerMesh;
    private static StandardMaterial3D? s_markerMaterial;

    private readonly ModelStore _store;
    private readonly ThemeCatalog _catalog;
    private readonly EquipmentCatalog _equipment;
    private readonly GearMesher _mesher;
    private readonly Action<ModelVisual> _needsDressing;
    private readonly ModelResolution _resolution;
    private readonly AnimationDriver _driver = new();
    private readonly Wardrobe _wardrobe = new();
    private readonly List<string> _added = new();
    private readonly List<string> _removed = new();
    private readonly List<Piece> _pieces = new();
    private readonly List<Node3D> _looseNodes = new();

    private Node3D? _content;
    private Skeleton3D? _skeleton;
    private MeshInstance3D? _lod0;
    private MeshInstance3D? _lod1;
    private Mesh? _bodyMesh0;
    private Mesh? _bodyMesh1;
    private Skin? _bodySkin0;
    private Skin? _bodySkin1;
    private AnimationPlayer? _player;
    private AnimSet? _anims;
    private string?[]? _clipNames;
    private string? _weapon;
    private bool _ready;
    private bool _using;
    private bool _wantVisible;
    private bool _baseline = true;
    private bool _firstClip = true;
    private bool _playing;
    private bool _gearDirty;
    private EntityLook _look;
    private int _lod;
    private double _pending;
    private float _fade;

    /// <summary>One worn item on this character.</summary>
    private sealed class Piece
    {
        public Piece(string itemId, WornPiece worn)
        {
            ItemId = itemId;
            Worn = worn;
        }

        public string ItemId { get; }

        public WornPiece Worn { get; }

        /// <summary>The meshes are in (or will never come: <see cref="Meshes"/> stays null and the marker stands in).</summary>
        public bool Loaded;

        public PieceMeshes? Meshes;
    }

    /// <summary>Creates a visual for a resolved model. It is hidden and not ready until <see cref="TryBuild"/> succeeds.</summary>
    /// <param name="store">Loaded scenes.</param>
    /// <param name="catalog">The theme chain.</param>
    /// <param name="equipment">Worn items to pieces.</param>
    /// <param name="mesher">Merges the body and its pieces; shared by every visual.</param>
    /// <param name="needsDressing">Told when the gear changed, so the source calls <see cref="TryDress"/> until it is done.</param>
    /// <param name="resolution">The character's model.</param>
    public ModelVisual(ModelStore store, ThemeCatalog catalog, EquipmentCatalog equipment, GearMesher mesher, Action<ModelVisual> needsDressing, ModelResolution resolution)
    {
        _store = store;
        _catalog = catalog;
        _equipment = equipment;
        _mesher = mesher;
        _needsDressing = needsDressing;
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

    /// <summary>Worn pieces on the character now, for measuring.</summary>
    public int PieceCount => _pieces.Count;

    /// <summary>The clip names in use for the weapon type (for checking).</summary>
    public string? ClipName(AnimClip clip) => _anims is null ? null : (_clipNames ?? _anims.NamesFor(_weapon))[(int)clip];

    /// <summary>Takes the visual out of the pool for a new entity.</summary>
    public void Attach()
    {
        _baseline = true;
        _firstClip = true;
        _playing = false;
        _pending = 0;
        SetFade(0);
        if (_ready && !_using && _resolution.Path is not null)
        {
            _store.Use(_resolution.Path);
            _using = true;
        }
        // The pieces stay on: the new entity's look swaps only what differs (its look counter may be any number, so it is forgotten).
        _wardrobe.Rebase();
    }

    /// <summary>Puts the visual away: hidden, still, and no longer counted as using its model.</summary>
    public void Detach()
    {
        _wantVisible = false;
        Root.Visible = false;
        SetFade(0);
        _player?.Stop();
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
        _skeleton = node.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() as Skeleton3D;
        if (_lod0 is null && node is MeshInstance3D whole) _lod0 = whole;
        _bodyMesh0 = _lod0?.Mesh;
        _bodySkin0 = _lod0?.Skin;
        _bodyMesh1 = _lod1?.Mesh;
        _bodySkin1 = _lod1?.Skin;
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
        _lod0 = new MeshInstance3D { Mesh = MarkerMesh(), Position = new Vector3(0, 0.4f, 0), Name = "Missing" };
        Root.AddChild(_lod0);
        Ready_();
    }

    private static BoxMesh MarkerMesh()
    {
        s_markerMaterial ??= new StandardMaterial3D { AlbedoColor = new Color(1, 0, 1), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        return s_markerMesh ??= new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f), Material = s_markerMaterial };
    }

    private void Ready_()
    {
        _ready = true;
        Root.Visible = _wantVisible;
        ApplyDetail();
        ApplyLook();
        if (_gearDirty) _needsDressing(this);
    }

    /// <inheritdoc />
    public void SetPose(float x, float y, float z, float yaw)
    {
        Root.Position = new Vector3(x, y, z);
        Root.Rotation = new Vector3(0, yaw, 0);
    }

    /// <inheritdoc />
    public void SetLook(EntityLook look)
    {
        _look = look;
        if (_ready) ApplyLook();
    }

    /// <inheritdoc />
    public void SetGear(string? weaponType, IReadOnlyList<string> worn, int version)
    {
        if (weaponType != _weapon)
        {
            _weapon = weaponType;
            _clipNames = null;
            if (_playing && !_driver.OneShotActive) Play(_driver.Current); // idle or walk with the new weapon's clip
        }
        if (!_wardrobe.Update(version, worn, _added, _removed)) return;
        if (_added.Count == 0 && _removed.Count == 0) return; // the same items (a pooled visual meeting a look it already wears)
        for (var i = 0; i < _removed.Count; i++)
        {
            for (var p = 0; p < _pieces.Count; p++)
            {
                if (_pieces[p].ItemId != _removed[i]) continue;
                _pieces.RemoveAt(p);
                break;
            }
        }
        for (var i = 0; i < _added.Count; i++)
        {
            if (_equipment.Resolve(_added[i], _resolution.Asset?.Rig) is not { } wornPiece) continue;
            _pieces.Add(new Piece(_added[i], wornPiece));
            if (wornPiece.Model.Path is not null) _store.Request(wornPiece.Model.Path);
        }
        _gearDirty = true;
        _needsDressing(this);
    }

    /// <summary>
    /// Puts the changed gear on once every piece's meshes are in. True when done (nothing left to do); false while the character or a piece is still loading, or when
    /// <paramref name="created"/> has reached <paramref name="limit"/> this frame (a merge counts as one).
    /// </summary>
    public bool TryDress(ref int created, int limit)
    {
        if (!_gearDirty) return true;
        if (!_ready) return false;
        var loaded = true;
        for (var i = 0; i < _pieces.Count; i++)
        {
            var piece = _pieces[i];
            if (piece.Loaded) continue;
            if (piece.Worn.Model.Path is not { } path)
            {
                piece.Loaded = true;
                continue;
            }
            if (_store.TryGetPiece(path, out piece.Meshes)) piece.Loaded = true;
            else loaded = false;
        }
        if (!loaded || created >= limit) return false;
        Dress();
        created++;
        _gearDirty = false;
        return true;
    }

    /// <summary>Puts the current pieces on: merged into the body's skinned mesh, or, for a model without a skin, as plain meshes on its root.</summary>
    private void Dress()
    {
        for (var i = 0; i < _looseNodes.Count; i++) _looseNodes[i].QueueFree();
        _looseNodes.Clear();
        if (_lod0 is null) return;
        if (_skeleton is null || _bodyMesh0 is null || _bodySkin0 is null)
        {
            DressLoose();
            return;
        }
        if (_pieces.Count == 0)
        {
            _lod0.Mesh = _bodyMesh0;
            _lod0.Skin = _bodySkin0;
            if (_lod1 is not null)
            {
                _lod1.Mesh = _bodyMesh1;
                _lod1.Skin = _bodySkin1;
            }
            return;
        }
        var worn = new List<WornPiece>(_pieces.Count);
        foreach (var piece in _pieces) worn.Add(piece.Worn);
        var (mesh0, skin0) = _mesher.Get(WornSet.Key(PoolKey, 0, worn), _bodyMesh0, _bodySkin0, _skeleton, Parts(0));
        _lod0.Mesh = mesh0;
        _lod0.Skin = skin0;
        if (_lod1 is not null && _bodyMesh1 is not null && _bodySkin1 is not null)
        {
            var (mesh1, skin1) = _mesher.Get(WornSet.Key(PoolKey, 1, worn), _bodyMesh1, _bodySkin1, _skeleton, Parts(1));
            _lod1.Mesh = mesh1;
            _lod1.Skin = skin1;
        }
    }

    private List<GearPart> Parts(int lod)
    {
        var parts = new List<GearPart>(_pieces.Count);
        foreach (var piece in _pieces)
        {
            var meshes = piece.Meshes;
            Mesh mesh = MarkerMesh();
            Skin? skin = null;
            var transform = Transform3D.Identity;
            if (meshes is not null)
            {
                var low = lod == 1 && meshes.Lod1 is not null;
                mesh = low ? meshes.Lod1! : meshes.Lod0;
                skin = low ? meshes.Skin1 : meshes.Skin0;
                transform = low ? meshes.Transform1 : meshes.Transform0;
            }
            if (piece.Worn.Mode == WornMode.Skinned && skin is not null)
            {
                parts.Add(new GearPart(mesh, skin, Transform3D.Identity, -1));
                continue;
            }
            // Attached (or a skinned file with no skin, or the marker): rigid on its bone, or on the default point.
            var bone = AttachRules.BoneFor(piece.Worn.Mode == WornMode.Attached ? piece.Worn.Bone : null, _skeleton!.GetBoneCount(), name => _skeleton.FindBone(name), PoolKey, _catalog);
            if (piece.Worn.Fallback || meshes is null) transform = Transform3D.Identity.Scaled(Vector3.One * FallbackPieceScale) * transform;
            parts.Add(new GearPart(mesh, null, transform, Math.Max(0, bone)));
        }
        return parts;
    }

    /// <summary>A model without a skinned body: each piece is a plain mesh on its root (warned once by <see cref="AttachRules"/>).</summary>
    private void DressLoose()
    {
        foreach (var piece in _pieces)
        {
            AttachRules.BoneFor(piece.Worn.Bone ?? string.Empty, 0, _ => -1, PoolKey, _catalog);
            var node = new MeshInstance3D { Mesh = piece.Meshes?.Lod0 ?? MarkerMesh(), Name = "Worn" };
            node.Transform = piece.Meshes?.Transform0 ?? Transform3D.Identity.Scaled(Vector3.One * FallbackPieceScale);
            node.CastShadow = Shadow;
            (_content ?? Root).AddChild(node);
            _looseNodes.Add(node);
        }
    }

    /// <inheritdoc />
    public void SetVisible(bool visible)
    {
        _wantVisible = visible;
        Root.Visible = visible && _ready;
    }

    /// <inheritdoc />
    public void SetFade(float amount)
    {
        amount = Mathf.Clamp(amount, 0f, 1f);
        if (amount == _fade) return;
        _fade = amount;
        if (_lod0 is not null) _lod0.Transparency = amount;
        if (_lod1 is not null) _lod1.Transparency = amount;
        for (var i = 0; i < _looseNodes.Count; i++)
            if (_looseNodes[i] is GeometryInstance3D g) g.Transparency = amount;
        if (_content is not null) _content.Position = new Vector3(0, -0.5f * amount, 0); // it sinks as it fades, so the fade reads even where transparency is not drawn
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
        if (_player is null || _anims is null) return;
        _clipNames ??= _anims.NamesFor(_weapon);
        var name = _clipNames[(int)clip];
        if (name is null)
        {
            _driver.OnFinished(); // the theme has no such clip: carry on as if it had ended
            return;
        }
        _player.Play(name);
        _playing = true;
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

    private GeometryInstance3D.ShadowCastingSetting Shadow =>
        _look.Own ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off; // §9: the player only

    private void ApplyLook()
    {
        var shadow = Shadow;
        if (_lod0 is not null) _lod0.CastShadow = shadow;
        if (_lod1 is not null) _lod1.CastShadow = shadow;
        for (var i = 0; i < _looseNodes.Count; i++)
            if (_looseNodes[i] is GeometryInstance3D g) g.CastShadow = shadow;
        // The rings under the player and under items this client may take are drawn by WorldPresenter, all in one multimesh (T-220).
    }
}
