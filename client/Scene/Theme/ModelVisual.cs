using Game.Client.Core;
using Game.Client.Core.Animation;
using Game.Client.Core.Theme;
using Game.Shared.Protocol;
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
    private readonly AppearanceCatalog _appearance;
    private readonly ModelResolution _resolution;
    private readonly ToonLook? _toon;
    private bool _outlined;
    private readonly AnimationDriver _driver = new();
    private readonly Wardrobe _wardrobe = new();
    private readonly List<string> _added = new();
    private readonly List<string> _removed = new();
    private readonly List<Piece> _pieces = new();
    private readonly List<PartModel> _dressed = new();
    private readonly List<DressPart> _plan = new();
    private readonly List<WornItem> _wornItems = new();
    private readonly List<OutfitPart> _outfit = new();
    private IReadOnlyList<ShownOutfit> _shownOutfit = System.Array.Empty<ShownOutfit>();
    private CharacterLook? _character;
    private string _dressKey = string.Empty;
    private AppearanceRecord? _looksSeen;
    private string? _jobSeen;
    private bool _fresh = true;
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
    private sealed record Piece(string ItemId, WornPiece Worn);

    /// <summary>One part to merge (T-706: the face, the hairstyle and the job's pieces as well as worn items) and its meshes.</summary>
    private sealed class PartModel
    {
        public PartModel(DressPart part) => Part = part;

        public DressPart Part { get; }

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
    /// <param name="resolution">The character's model: a job's, a monster's, or a character's own body (T-706).</param>
    /// <param name="appearance">Appearance ids to looks (T-706).</param>
    /// <param name="look">The toon look, or null.</param>
    public ModelVisual(ModelStore store, ThemeCatalog catalog, EquipmentCatalog equipment, GearMesher mesher, Action<ModelVisual> needsDressing, ModelResolution resolution,
        AppearanceCatalog appearance, ToonLook? look = null)
    {
        _appearance = appearance;
        _store = store;
        _catalog = catalog;
        _equipment = equipment;
        _mesher = mesher;
        _needsDressing = needsDressing;
        _resolution = resolution;
        _toon = look;
        Root = new Node3D { Visible = false, Name = "Entity" };
    }

    /// <summary>The node that is placed on the ground.</summary>
    public Node3D Root { get; }

    /// <summary>True once the model is in place (or the built-in marker stands in for it).</summary>
    public bool Ready => _ready;

    /// <summary>Which pool this belongs to: the model's file, or empty for the built-in marker.</summary>
    public string PoolKey => _resolution.PoolKey;

    /// <inheritdoc />
    public bool Animated => _player is not null;

    /// <summary>Main-thread milliseconds of the last re-dress (the swap to the merged mesh and its materials), for measuring (T-222, ADR-002 row 223).</summary>
    public static double LastDressMs { get; private set; }

    /// <summary>True while the outline is drawn (for measuring).</summary>
    public bool Outlined => _outlined;

    /// <summary>
    /// Draws the theme's outline over this model or stops (T-222, ADR-003 point 2): the outline material becomes the overlay of the model's meshes, one more draw
    /// call while it is on. Does nothing when the state is unchanged or the theme has no outline.
    /// </summary>
    public void SetOutline(bool on)
    {
        if (_toon?.Outline is null || _resolution.Asset is { OutlineOff: true }) on = false; // a variant may turn its outline off (T-224)
        if (on == _outlined) return;
        _outlined = on;
        ApplyOutline();
    }

    private void ApplyOutline()
    {
        var overlay = _outlined ? _toon?.OutlineFor(_resolution.Asset?.OutlineColor) : null;
        if (_lod0 is not null) _lod0.MaterialOverlay = overlay;
        if (_lod1 is not null) _lod1.MaterialOverlay = overlay;
        for (var i = 0; i < _looseNodes.Count; i++)
            if (_looseNodes[i] is GeometryInstance3D g) g.MaterialOverlay = overlay;
    }

    /// <summary>The toon material on every surface of the model and its loose pieces (after building and after every re-dress, since the mesh changes).</summary>
    private void ApplyMaterials()
    {
        if (_toon is null) return;
        var tint = _resolution.Asset?.Tint; // a variant's colour (T-224): one more material per variant, never per instance
        if (_lod0?.Mesh is not BoxMesh) _toon.Apply(_lod0, tint); // the built-in marker stays unshaded magenta
        _toon.Apply(_lod1, tint);
        for (var i = 0; i < _looseNodes.Count; i++)
            if (_looseNodes[i] is MeshInstance3D m) _toon.Apply(m, tint);
        ApplyOutline();
    }

    /// <summary>Worn pieces on the character now, for measuring.</summary>
    public int PieceCount => _pieces.Count;

    /// <summary>The character's look now drawn or being put on, or null when it is drawn by its model alone (for checking and measuring, T-706).</summary>
    public CharacterLook? Character => _character;

    /// <summary>The parts merged into the body now or about to be (for checking, T-706).</summary>
    public IReadOnlyList<DressPart> Dressed => _plan;

    /// <summary>True when the model is in place and everything on it is put on: what shows is what was asked for.</summary>
    public bool Settled => _ready && !_gearDirty;

    /// <summary>Surfaces drawn at the full level of detail (for checking: one per character, §9).</summary>
    public int Surfaces => _lod0?.Mesh?.GetSurfaceCount() ?? 0;

    /// <summary>The distinct vertex colours of the drawn mesh (for checking that the character's colours are baked in, T-706).</summary>
    public IReadOnlyCollection<Color> DrawnColors()
    {
        var found = new HashSet<Color>();
        if (_lod0?.Mesh is not { } mesh) return found;
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var colors = mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Color];
            if (colors.VariantType != Variant.Type.Nil)
                foreach (var color in colors.AsColorArray()) found.Add(color);
        }
        return found;
    }

    /// <summary>The clip names in use for the weapon type (for checking).</summary>
    public string? ClipName(AnimClip clip) => _anims is null ? null : (_clipNames ?? _anims.NamesFor(_weapon))[(int)clip];

    /// <summary>Takes the visual out of the pool for a new entity.</summary>
    public void Attach()
    {
        _baseline = true;
        _firstClip = true;
        _playing = false;
        _pending = 0;
        _fresh = true; // until it is dressed it would show the last entity's face (T-706)
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
        SetOutline(false);
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
        if (_resolution.Asset.Scale != 1) node.Scale = Vector3.One * (float)_resolution.Asset.Scale; // a variant's size (T-224); the root stays on the ground
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
        ApplyMaterials();
        ApplyVisible();
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
    public void SetCharacter(string? jobId, AppearanceRecord? looks)
    {
        // Asked on every look change (HP included): the same objects mean nothing changed.
        if (ReferenceEquals(looks, _looksSeen) && ReferenceEquals(jobId, _jobSeen)) return;
        _looksSeen = looks;
        _jobSeen = jobId;
        var look = _appearance.Resolve(looks, jobId);
        if (look is not null && look.Body.PoolKey != PoolKey) look = null; // another body: the source gave this one for a reason (a missing body, drawing switched off)
        if (ReferenceEquals(look, _character)) return;
        _character = look;
        Redress();
    }

    /// <inheritdoc />
    public void SetOutfit(IReadOnlyList<ShownOutfit> shown)
    {
        if (ReferenceEquals(shown, _shownOutfit)) return; // asked on every look change: the same list means nothing changed
        var same = shown.Count == _shownOutfit.Count;
        for (var i = 0; same && i < shown.Count; i++) same = shown[i].Equals(_shownOutfit[i]);
        _shownOutfit = shown;
        if (!same) Redress();
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
        }
        Redress();
    }

    /// <summary>Plans what is merged into the body now (<see cref="DressPlan"/>), starts loading what is new, and asks to be dressed.</summary>
    private void Redress()
    {
        _wornItems.Clear();
        foreach (var piece in _pieces) _wornItems.Add(new WornItem(piece.Worn, _equipment.SlotsOf(piece.ItemId)));
        _appearance.ResolveOutfit(_shownOutfit, _character?.Ids.Gender, _resolution.Asset?.Rig, _outfit);
        DressPlan.Fill(_character, _wornItems, _outfit, _plan);
        _dressKey = WornSet.Key(PoolKey, _character, _plan); // once: a crowd waiting for its merges asks every frame
        for (var i = _dressed.Count - 1; i >= 0; i--)
            if (!_plan.Contains(_dressed[i].Part)) _dressed.RemoveAt(i); // a part still worn keeps its meshes
        foreach (var part in _plan)
        {
            if (_dressed.Exists(d => d.Part == part)) continue;
            _dressed.Add(new PartModel(part));
            if (part.Piece.Model.Path is not null) _store.Request(part.Piece.Model.Path);
        }
        _gearDirty = true;
        ApplyVisible();
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
        for (var i = 0; i < _dressed.Count; i++)
        {
            var piece = _dressed[i];
            if (piece.Loaded) continue;
            if (piece.Part.Piece.Model.Path is not { } path)
            {
                piece.Loaded = true;
                continue;
            }
            if (_store.TryGetPiece(path, out piece.Meshes)) piece.Loaded = true;
            else loaded = false;
        }
        if (!loaded || created >= limit) return false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Dress()) return false; // the merge is still running on a worker (T-222): the old look stays until it is done
        ApplyMaterials();
        LastDressMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        created++;
        _gearDirty = false;
        _fresh = false;
        ApplyVisible();
        return true;
    }

    /// <summary>Puts the current pieces on: merged into the body's skinned mesh, or, for a model without a skin, as plain meshes on its root. False while the merge runs.</summary>
    private bool Dress()
    {
        if (_lod0 is null) return true;
        if (_skeleton is null || _bodyMesh0 is null || _bodySkin0 is null)
        {
            ClearLoose();
            DressLoose();
            return true;
        }
        if (_dressed.Count == 0 && _character is null)
        {
            ClearLoose();
            _lod0.Mesh = _bodyMesh0;
            _lod0.Skin = _bodySkin0;
            if (_lod1 is not null)
            {
                _lod1.Mesh = _bodyMesh1;
                _lod1.Skin = _bodySkin1;
            }
            return true;
        }
        var low = _lod1 is not null && _bodyMesh1 is not null && _bodySkin1 is not null;
        BodySource? Source(int lod) => lod == 0 ? new BodySource(_bodyMesh0!, _bodySkin0!, Parts(0)) : low ? new BodySource(_bodyMesh1!, _bodySkin1!, Parts(1)) : null;
        if (!_mesher.TryGet(_dressKey, _skeleton, Source, _character?.BodyColors, out var dressed)) return false;
        ClearLoose();
        _lod0.Mesh = dressed.Mesh0;
        _lod0.Skin = dressed.Skin0;
        if (low && dressed.Mesh1 is not null)
        {
            _lod1!.Mesh = dressed.Mesh1;
            _lod1.Skin = dressed.Skin1;
        }
        return true;
    }

    private void ClearLoose()
    {
        for (var i = 0; i < _looseNodes.Count; i++) _looseNodes[i].QueueFree();
        _looseNodes.Clear();
    }

    private List<GearPart> Parts(int lod)
    {
        var parts = new List<GearPart>(_dressed.Count);
        foreach (var model in _dressed)
        {
            var piece = model.Part.Piece;
            var colors = _character?.ColorsOf(model.Part);
            var meshes = model.Meshes;
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
            if (piece.Mode == WornMode.Skinned && skin is not null)
            {
                parts.Add(new GearPart(mesh, skin, Transform3D.Identity, -1, colors));
                continue;
            }
            // Attached (or a skinned file with no skin, or the marker): rigid on its bone, or on the default point.
            var bone = AttachRules.BoneFor(piece.Mode == WornMode.Attached ? piece.Bone : null, _skeleton!.GetBoneCount(), name => _skeleton.FindBone(name), PoolKey, _catalog);
            if (piece.Fallback || meshes is null) transform = Transform3D.Identity.Scaled(Vector3.One * FallbackPieceScale) * transform;
            parts.Add(new GearPart(mesh, null, transform, Math.Max(0, bone), piece.Fallback ? null : colors));
        }
        return parts;
    }

    /// <summary>A model without a skinned body: each piece is a plain mesh on its root (warned once by <see cref="AttachRules"/>).</summary>
    private void DressLoose()
    {
        foreach (var piece in _dressed)
        {
            AttachRules.BoneFor(piece.Part.Piece.Bone ?? string.Empty, 0, _ => -1, PoolKey, _catalog);
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
        ApplyVisible();
    }

    /// <summary>A character with looks of its own stays hidden until they are on (T-706): a pooled one would show the last entity's face for a few frames.</summary>
    private void ApplyVisible()
    {
        Root.Visible = _wantVisible && _ready && !(_fresh && _gearDirty && _character is not null);
        if (Root.Visible) _fresh = false; // from here on a change keeps the old look until the new one is merged
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
