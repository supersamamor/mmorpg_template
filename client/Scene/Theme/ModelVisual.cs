using Game.Client.Core;
using Game.Client.Core.Animation;
using Game.Client.Core.Theme;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// One entity's model: a root node placed on the ground, holding an instance of the theme's scene (or a built-in marker when the theme has nothing). Pooled.
/// The instance is made when <see cref="TryBuild"/> succeeds; until then the visual is hidden. Setting a pose, a look or a motion allocates nothing.
/// <para>Worn equipment (T-403, §7.5): an attached piece is a model under a <see cref="BoneAttachment3D"/> on its bone of the character's skeleton; a skinned
/// piece's meshes are moved under the character's skeleton and bound to it, so they play the same animation. Pieces change only when the look counter moves,
/// and only the pieces that changed are added or freed. Their scenes come from the same <see cref="ModelStore"/> as characters (same threading rule, one
/// loaded scene per file shared by every character wearing it).</para>
/// </summary>
public sealed class ModelVisual : IEntityVisual
{
    private const string Lod0Name = "Body_LOD0";
    private const string Lod1Name = "Body_LOD1";

    /// <summary>A piece standing in for a missing or wrong key is the theme's missing model, drawn this small so it marks the problem without hiding the character.</summary>
    private const float FallbackPieceScale = 0.3f;

    private static Mesh? s_markerMesh;
    private static StandardMaterial3D? s_markerMaterial;

    private readonly ModelStore _store;
    private readonly ThemeCatalog _catalog;
    private readonly EquipmentCatalog _equipment;
    private readonly Action<ModelVisual> _needsDressing;
    private readonly ModelResolution _resolution;
    private readonly AnimationDriver _driver = new();
    private readonly Wardrobe _wardrobe = new();
    private readonly List<string> _added = new();
    private readonly List<string> _removed = new();
    private readonly List<Piece> _pieces = new();

    private Node3D? _content;
    private Skeleton3D? _skeleton;
    private MeshInstance3D? _lod0;
    private MeshInstance3D? _lod1;
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

        public bool Built;
        public bool Using;
        public MeshInstance3D? Lod0;
        public MeshInstance3D? Lod1;
        public readonly List<Node3D> Owned = new();
    }

    /// <summary>Creates a visual for a resolved model. It is hidden and not ready until <see cref="TryBuild"/> succeeds.</summary>
    /// <param name="store">Loaded scenes.</param>
    /// <param name="catalog">The theme chain.</param>
    /// <param name="equipment">Worn items to pieces.</param>
    /// <param name="needsDressing">Told when pieces wait for their scene, so the source keeps calling <see cref="TryDress"/>.</param>
    /// <param name="resolution">The character's model.</param>
    public ModelVisual(ModelStore store, ThemeCatalog catalog, EquipmentCatalog equipment, Action<ModelVisual> needsDressing, ModelResolution resolution)
    {
        _store = store;
        _catalog = catalog;
        _equipment = equipment;
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

    /// <summary>Worn pieces on the character now (built or waiting), for measuring.</summary>
    public int PieceCount => _pieces.Count;

    /// <summary>True while a worn piece waits for its scene or for its turn to be made.</summary>
    public bool Dressing
    {
        get
        {
            for (var i = 0; i < _pieces.Count; i++)
                if (!_pieces[i].Built) return true;
            return false;
        }
    }

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
        for (var i = 0; i < _pieces.Count; i++) UsePiece(_pieces[i]);
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
        for (var i = 0; i < _pieces.Count; i++) UnusePiece(_pieces[i]);
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
        if (Dressing) _needsDressing(this);
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
        for (var i = 0; i < _removed.Count; i++)
        {
            for (var p = 0; p < _pieces.Count; p++)
            {
                if (_pieces[p].ItemId != _removed[i]) continue;
                RemovePiece(_pieces[p]);
                _pieces.RemoveAt(p);
                break;
            }
        }
        var waiting = false;
        for (var i = 0; i < _added.Count; i++)
        {
            if (_equipment.Resolve(_added[i], _resolution.Asset?.Rig) is not { } wornPiece) continue;
            _pieces.Add(new Piece(_added[i], wornPiece));
            if (wornPiece.Model.Path is not null) _store.Request(wornPiece.Model.Path);
            waiting = true;
        }
        if (waiting) _needsDressing(this);
    }

    /// <summary>
    /// Makes the worn pieces that are still waiting, at most until <paramref name="created"/> reaches <paramref name="limit"/>. True when none is left waiting;
    /// false while the character itself or a piece's scene is still loading.
    /// </summary>
    public bool TryDress(ref int created, int limit)
    {
        if (!_ready) return _pieces.Count == 0;
        var done = true;
        for (var i = 0; i < _pieces.Count; i++)
        {
            var piece = _pieces[i];
            if (piece.Built) continue;
            if (created >= limit || !BuildPiece(piece, ref created)) done = false;
        }
        return done;
    }

    private bool BuildPiece(Piece piece, ref int created)
    {
        var path = piece.Worn.Model.Path;
        Node3D node;
        if (path is null)
        {
            node = Marker();
        }
        else if (_store.TryGetScene(path, out var scene, out var failed))
        {
            node = scene!.Instantiate<Node3D>();
            created++;
        }
        else if (failed)
        {
            node = Marker();
            path = null;
        }
        else
        {
            return false; // still loading
        }

        if (path is not null)
        {
            piece.Using = false;
            UsePieceAt(piece, path);
        }
        if (piece.Worn.Mode == WornMode.Skinned && _skeleton is not null && path is not null) Skin(piece, node);
        else AttachTo(piece, node);
        piece.Built = true;
        ApplyPiece(piece);
        return true;
    }

    /// <summary>Moves a skinned piece's meshes under the character's skeleton and binds them to it; the rest of the piece's scene is freed.</summary>
    private void Skin(Piece piece, Node3D node)
    {
        foreach (var child in node.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            var transform = mesh.Transform;
            mesh.GetParent().RemoveChild(mesh);
            mesh.Owner = null;
            _skeleton!.AddChild(mesh);
            mesh.Transform = transform;
            mesh.Skeleton = new NodePath("..");
            Assign(piece, mesh);
            piece.Owned.Add(mesh);
        }
        node.Free();
    }

    /// <summary>Puts a rigid piece on its bone (or the default point), or on the model's root when the character has no skeleton.</summary>
    private void AttachTo(Piece piece, Node3D node)
    {
        var bone = AttachRules.BoneFor(piece.Worn.Mode == WornMode.Skinned ? null : piece.Worn.Bone, _skeleton?.GetBoneCount() ?? 0,
            name => _skeleton!.FindBone(name), PoolKey, _catalog);
        if (piece.Worn.Fallback) node.Scale = new Vector3(FallbackPieceScale, FallbackPieceScale, FallbackPieceScale);
        Node3D holder;
        if (bone >= 0 && _skeleton is not null)
        {
            var attachment = new BoneAttachment3D { Name = "Worn" };
            _skeleton.AddChild(attachment);
            attachment.BoneName = _skeleton.GetBoneName(bone);
            attachment.AddChild(node);
            holder = attachment;
        }
        else
        {
            (_content ?? Root).AddChild(node);
            holder = node;
        }
        piece.Lod0 = node.FindChild(Lod0Name, true, false) as MeshInstance3D ?? node as MeshInstance3D;
        piece.Lod1 = node.FindChild(Lod1Name, true, false) as MeshInstance3D;
        piece.Owned.Add(holder);
    }

    private static void Assign(Piece piece, MeshInstance3D mesh)
    {
        if (mesh.Name == Lod1Name) piece.Lod1 = mesh;
        else piece.Lod0 ??= mesh;
    }

    private static Node3D Marker()
    {
        s_markerMesh ??= new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) };
        s_markerMaterial ??= new StandardMaterial3D { AlbedoColor = new Color(1, 0, 1), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        return new MeshInstance3D { Mesh = s_markerMesh, MaterialOverride = s_markerMaterial, Name = Lod0Name };
    }

    private void RemovePiece(Piece piece)
    {
        UnusePiece(piece);
        for (var i = 0; i < piece.Owned.Count; i++)
        {
            piece.Owned[i].Visible = false;
            piece.Owned[i].QueueFree();
        }
        piece.Owned.Clear();
    }

    private void UsePiece(Piece piece)
    {
        if (piece.Built && piece.Worn.Model.Path is { } path) UsePieceAt(piece, path);
    }

    private void UsePieceAt(Piece piece, string path)
    {
        if (piece.Using) return;
        _store.Use(path);
        piece.Using = true;
    }

    private void UnusePiece(Piece piece)
    {
        if (!piece.Using || piece.Worn.Model.Path is not { } path) return;
        _store.Unuse(path);
        piece.Using = false;
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
        for (var i = 0; i < _pieces.Count; i++)
        {
            if (_pieces[i].Lod0 is { } a) a.Transparency = amount;
            if (_pieces[i].Lod1 is { } b) b.Transparency = amount;
        }
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
        for (var i = 0; i < _pieces.Count; i++) ApplyPieceDetail(_pieces[i]);
    }

    private void ApplyPieceDetail(Piece piece)
    {
        // A piece with a LOD1 mesh swaps with the body; one without keeps its only mesh at both levels.
        var low = _lod == 1 && piece.Lod1 is not null;
        if (piece.Lod0 is not null) piece.Lod0.Visible = !low;
        if (piece.Lod1 is not null) piece.Lod1.Visible = low;
    }

    private void ApplyLook()
    {
        var shadow = Shadow;
        if (_lod0 is not null) _lod0.CastShadow = shadow;
        if (_lod1 is not null) _lod1.CastShadow = shadow;
        for (var i = 0; i < _pieces.Count; i++) ApplyPieceShadow(_pieces[i]);
        // The rings under the player and under items this client may take are drawn by WorldPresenter, all in one multimesh (T-220).
    }

    private GeometryInstance3D.ShadowCastingSetting Shadow =>
        _look.Own ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off; // §9: the player only

    private void ApplyPieceShadow(Piece piece)
    {
        if (piece.Lod0 is not null) piece.Lod0.CastShadow = Shadow;
        if (piece.Lod1 is not null) piece.Lod1.CastShadow = Shadow;
    }

    private void ApplyPiece(Piece piece)
    {
        ApplyPieceDetail(piece);
        ApplyPieceShadow(piece);
        if (_fade != 0)
        {
            if (piece.Lod0 is not null) piece.Lod0.Transparency = _fade;
            if (piece.Lod1 is not null) piece.Lod1.Transparency = _fade;
        }
    }
}
