using Game.Client.Core;
using Game.Client.Core.Theme;
using Game.Client.Core.Ui;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// A character drawn inside a control (T-706, ARCHITECTURE §7.5.1): the creation screen's turning preview, and the still pictures of the character list. It uses
/// <see cref="ThemedEntities.Preview"/>, so the body, merge, colours and toon look are the world's own code. It has a small 3D world of its own, lit by the theme's
/// environment scene, in a <see cref="SubViewport"/>.
/// <para><b>Cost:</b> nothing is rendered while it is hidden. A still picture is rendered once, when the character is fully dressed, and again only when the look or
/// the size changes. A turning preview renders every frame, only while it shows.</para>
/// </summary>
public sealed class LookView
{
    private const float BodyHeight = 2.15f;     // the humanoid placeholder with headroom; the camera frames it
    private const float HeadHeight = 1.72f;     // where a still picture looks
    private const float Distance = 6f;

    private readonly ThemedEntities _entities;
    private readonly ToonLook? _look;
    private readonly ClientConfig _config;
    private readonly bool _turning;
    private readonly SubViewport _viewport;
    private readonly Camera3D _camera;
    private readonly Turntable _table;
    private Node3D? _stage;
    private ModelVisual? _visual;
    private AppearanceRecord? _looks;
    private string? _job;
    private bool _shown;
    private string? _weapon;
    private IReadOnlyList<string> _worn = Array.Empty<string>();
    private IReadOnlyList<ShownOutfit> _outfit = Array.Empty<ShownOutfit>();
    private int _gear;
    private bool _settled;
    private bool _renderPending;
    private bool _renderNow;

    /// <summary>Builds a view; its <see cref="Frame"/> goes into a layout. A turning one takes drags.</summary>
    public LookView(Node host, ThemedEntities entities, ToonLook? look, ClientConfig config, bool turning)
    {
        _entities = entities;
        _look = look;
        _config = config;
        _turning = turning;
        _table = new Turntable(config.PreviewTurnDegreesPerSecond, config.PreviewDragDegrees);
        _viewport = new SubViewport
        {
            OwnWorld3D = true, World3D = new World3D(), Size = new Vector2I(64, 64), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, HandleInputLocally = false,
            GuiDisableInput = true, PositionalShadowAtlasSize = 0,
        };
        // Outside the layout: the viewport itself is no control, and the picture below shows its texture.
        _camera = new Camera3D { Current = true, Fov = 24, KeepAspect = Camera3D.KeepAspectEnum.Height, Near = 0.1f, Far = 30f };
        _viewport.AddChild(_camera);
        host.AddChild(_viewport);
        Frame = new TextureRect
        {
            Texture = _viewport.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = turning ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore,
        };
        Frame.Resized += Resize;
        if (turning) Frame.GuiInput += OnInput;
    }

    /// <summary>The control that shows it.</summary>
    public TextureRect Frame { get; }

    /// <summary>The visual drawn, or null (for the scripted run).</summary>
    public ModelVisual? Visual => _visual;

    /// <summary>How it turns (for the scripted run).</summary>
    public Turntable Table => _table;

    /// <summary>The looks it was last told to show, or null.</summary>
    public AppearanceRecord? Looks => _looks;

    /// <summary>True once what it shows is what was asked for, fully dressed.</summary>
    public bool Settled => _visual is { Settled: true };

    /// <summary>True while the viewport renders every frame (for measuring: only a turning view that shows).</summary>
    public bool Rendering => _viewport.RenderTargetUpdateMode == SubViewport.UpdateMode.Always;

    /// <summary>Shows a character, or nothing (<paramref name="looks"/> null). The same character again does nothing.</summary>
    public void Show(string? jobId, AppearanceRecord? looks)
    {
        if (looks is null)
        {
            Clear();
            return;
        }
        _shown = true;
        if (_visual is not null && looks.Equals(_looks) && jobId == _job) return;
        _looks = looks;
        _job = jobId;
        EnsureStage();
        Resize();
        if (_visual is not null && _visual.PoolKey == _entities.BodyOf(jobId, looks)) _visual.SetCharacter(jobId, looks); // the same body: the old look stays until the new one is merged
        else
        {
            if (_visual is not null) _entities.Discard(_visual);
            _visual = _entities.Preview(_stage!, jobId, looks);
            PutOn(_visual);
            _visual.SetDetail(0);
            _visual.SetOutline(true); // as the world draws a character near the player
            _visual.SetMotion(false, 0, 0);
        }
        _settled = false;
        Pose();
        SetMode();
    }

    /// <summary>
    /// What the character shown wears (T-708: the wardrobe's preview shows exactly what others see): its weapon type, worn items and shown outfits. <paramref name="version"/> moves when
    /// the gear changed. Without this a view shows no gear and no outfit, as the creation screen's does.
    /// </summary>
    public void Dress(string? weapon, IReadOnlyList<string> worn, IReadOnlyList<ShownOutfit> outfit, int version)
    {
        if (weapon == _weapon && version == _gear && ReferenceEquals(outfit, _outfit)) return;
        _weapon = weapon;
        _worn = worn;
        _outfit = outfit;
        _gear = version;
        if (_visual is null) return;
        PutOn(_visual);
        _settled = false;
    }

    private void PutOn(ModelVisual visual)
    {
        visual.SetOutfit(_outfit);
        visual.SetGear(_weapon, _worn, _gear);
    }

    /// <summary>Frees the character and stops rendering.</summary>
    public void Clear()
    {
        _shown = false;
        if (_visual is not null) _entities.Discard(_visual);
        _visual = null;
        _looks = null;
        _weapon = null;
        _worn = Array.Empty<string>();
        _outfit = Array.Empty<ShownOutfit>();
        _gear = 0;
        _settled = false;
        _renderPending = _renderNow = false;
        _table.Reset();
        SetMode();
        _viewport.Size = new Vector2I(2, 2); // the render targets are given back while nothing shows
    }

    /// <summary>Once a frame while its screen is open. A still picture that is drawn costs two comparisons.</summary>
    public void Update(double dt)
    {
        if (_visual is null) return;
        if (!_settled && _visual.Settled)
        {
            _settled = true;
            _visual.SetMotion(false, 0, 0); // the idle clip starts once the model is in
            _visual.Animate(0.4, true, 0, 1);
            _renderPending = true;
        }
        if (_turning)
        {
            _table.Advance(dt);
            Pose();
            if (_settled)
            {
                _visual.SetMotion(false, 0, 0);
                _visual.Animate(dt, true, 0, 1);
            }
            return;
        }
        if (_renderNow)
        {
            _renderNow = false;
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once; // a frame after the mesh changed, so the new one is what is drawn
        }
        else if (_renderPending)
        {
            _renderPending = false;
            _renderNow = true;
        }
    }

    /// <summary>Frees everything it made.</summary>
    public void Free()
    {
        Clear();
        _viewport.QueueFree();
    }

    private void EnsureStage()
    {
        if (_stage is not null) return;
        _stage = new Node3D { Name = "Stage" };
        _viewport.AddChild(_stage);
        ToonLook.AddEnvironment(_viewport, _look?.Plan.Environment);
    }

    private void Pose()
    {
        // Models face -Z; the camera stands on +Z, where the key light comes from in the game (§7.2), so the character is turned to it.
        _visual?.SetPose(0, 0, 0, Mathf.Pi + Mathf.DegToRad((float)_table.Degrees));
    }

    private void Resize()
    {
        if (!_shown) return;
        var size = Frame.Size;
        if (size.X < 1 || size.Y < 1) return;
        var pixels = new Vector2I(Mathf.Clamp(Mathf.RoundToInt(size.X), 16, 1024), Mathf.Clamp(Mathf.RoundToInt(size.Y), 16, 1024));
        if (_viewport.Size == pixels) return;
        _viewport.Size = pixels;
        // The whole body is framed whatever the shape: a narrow preview steps back until its width holds the arms.
        var aspect = pixels.X / (float)pixels.Y;
        if (_turning)
        {
            var tall = Mathf.RadToDeg(2 * Mathf.Atan(BodyHeight * 0.56f / Distance));
            var wide = Mathf.RadToDeg(2 * Mathf.Atan(0.5f / Distance / aspect));
            _camera.Fov = Mathf.Max(tall, wide);
            _camera.Position = new Vector3(0, BodyHeight * 0.5f + 0.55f, Distance);
            _camera.LookAtFromPosition(_camera.Position, new Vector3(0, BodyHeight * 0.5f, 0), Vector3.Up);
        }
        else
        {
            _camera.Fov = Mathf.RadToDeg(2 * Mathf.Atan(0.62f / Distance / Mathf.Min(1f, aspect)));
            _camera.Position = new Vector3(0, HeadHeight + 0.35f, Distance);
            _camera.LookAtFromPosition(_camera.Position, new Vector3(0, HeadHeight - 0.05f, 0), Vector3.Up);
        }
        _renderPending = _visual is not null;
    }

    /// <summary>A turning view renders every frame, only while it shows something; a still one only when <see cref="Update"/> asks for one picture.</summary>
    private void SetMode()
    {
        if (!_turning) return;
        _viewport.RenderTargetUpdateMode = _shown && _visual is not null ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
    }

    private void OnInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button: // a finger too: the engine makes mouse events of touches for controls
                if (button.Pressed) _table.Press();
                else _table.Release();
                Frame.AcceptEvent();
                break;
            case InputEventMouseMotion motion when _table.Held:
                _table.Drag(motion.Relative.X, Frame.Size.X);
                Frame.AcceptEvent();
                break;
        }
    }
}
