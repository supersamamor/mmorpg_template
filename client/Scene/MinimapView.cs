using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Party;
using Game.Client.Core.Ui;
using Game.Content.Maps;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Draws one minimap (T-812): a dark square, the part of the map's picture the frame shows (one texture rectangle), and the marks (a few primitives each). No camera, no viewport:
/// the picture was drawn once by the map export. It redraws only when its owner says something on it moved.
/// </summary>
public partial class MinimapCanvas : Control
{
    private static readonly Color Back = new(0.07f, 0.08f, 0.1f, 0.72f);
    private static readonly Color Edge = new(0.94f, 0.89f, 0.78f, 0.9f);
    private static readonly Color Ink = new(0.16f, 0.13f, 0.15f, 1f);

    /// <summary>The map's picture, or null (nothing is drawn but the background).</summary>
    public Texture2D? Picture { get; set; }

    /// <summary>The map's size in cells.</summary>
    public Vector2I MapSize { get; set; }

    /// <summary>What part of the map shows, and how large.</summary>
    public MinimapFrame Frame { get; set; }

    /// <summary>Where the frame's top-left corner is inside the canvas (the full map centres its frame).</summary>
    public Vector2 Offset { get; set; }

    /// <summary>The marks, in drawing order (the owner's list).</summary>
    public List<MinimapMark> Marks { get; set; } = new();

    /// <summary>The radius of a mark, in canvas units.</summary>
    public float MarkPx { get; set; } = 5;

    /// <summary>How many times it drew (the scripted run's cost line).</summary>
    public int Draws { get; private set; }

    private readonly List<Vector2> _points = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();

    /// <inheritdoc />
    public override void _Draw()
    {
        // Three canvas commands whatever the number of marks: the background, the picture, and every mark and the border as one triangle array (one draw call). Drawn with a
        // primitive each, 20 marks cost 22 draw calls on the Mobile renderer (measured, ADR-002 row 534).
        Draws++;
        DrawRect(new Rect2(Vector2.Zero, Size), Back);
        if (Picture is not null && MapSize.X > 0)
        {
            var (x, y, w, h) = Frame.VisibleCells(MapSize.X, MapSize.Y);
            if (w > 0)
            {
                var perCell = (float)Picture.GetWidth() / MapSize.X;
                var (px, py) = Frame.ToPx(x, y);
                DrawTextureRectRegion(Picture, new Rect2(Offset + new Vector2((float)px, (float)py), new Vector2((float)(w * Frame.CellPx), (float)(h * Frame.CellPx))),
                    new Rect2((float)x * perCell, (float)y * perCell, (float)w * perCell, (float)h * perCell));
            }
        }
        _points.Clear();
        _colors.Clear();
        _indices.Clear();
        var r = MarkPx;
        foreach (var mark in Marks)
        {
            if (!Frame.Shows(mark.X, mark.Z)) continue;
            var (mx, my) = Frame.ToPx(mark.X, mark.Z);
            var at = Offset + new Vector2((float)mx, (float)my);
            switch (mark.Kind)
            {
                case MinimapMarkKind.Warp:
                    Ring(at, r, Mathf.Max(1.5f, r * 0.35f), new Color(0.66f, 0.55f, 0.85f));
                    break;
                case MinimapMarkKind.Npc:
                    Quad(at - new Vector2(r * 0.6f, r * 0.6f), new Vector2(r * 1.2f, r * 1.2f), new Color(0.94f, 0.89f, 0.78f));
                    break;
                case MinimapMarkKind.QuestOffer or MinimapMarkKind.QuestTurnIn:
                    Disc(at, r * 0.95f, Ink);
                    Disc(at, r * 0.75f, mark.Kind == MinimapMarkKind.QuestOffer ? new Color(0.83f, 0.66f, 0.26f) : new Color(0.5f, 0.85f, 0.4f));
                    Quad(at + new Vector2(-r * 0.12f, -r * 0.5f), new Vector2(r * 0.24f, r * 0.6f), Ink);   // the "!" of the quest marker, as a bar and a dot
                    Quad(at + new Vector2(-r * 0.12f, r * 0.22f), new Vector2(r * 0.24f, r * 0.24f), Ink);
                    break;
                case MinimapMarkKind.Monster:
                    Disc(at, r * 0.6f, new Color(0.88f, 0.33f, 0.3f));
                    break;
                case MinimapMarkKind.Player:
                    Disc(at, r * 0.6f, new Color(0.95f, 0.95f, 0.95f));
                    break;
                case MinimapMarkKind.Party:
                    Triangle(at + new Vector2(0, -r), at + new Vector2(r, 0), at + new Vector2(0, r), new Color(0.45f, 0.85f, 0.95f));
                    Triangle(at + new Vector2(0, -r), at + new Vector2(0, r), at + new Vector2(-r, 0), new Color(0.45f, 0.85f, 0.95f));
                    break;
                case MinimapMarkKind.Own:
                    var (hx, hy) = MinimapMarks.Heading(mark.Yaw);
                    var ahead = new Vector2((float)hx, (float)hy);
                    var side = new Vector2(-ahead.Y, ahead.X);
                    var tip = at + ahead * r * 1.5f;
                    var left = at - ahead * r * 0.9f + side * r;
                    var right = at - ahead * r * 0.9f - side * r;
                    Triangle(tip + ahead * 1.5f, left + (left - tip).Normalized() * 1.5f, right + (right - tip).Normalized() * 1.5f, Ink);
                    Triangle(tip, left, right, new Color(1f, 0.85f, 0.3f));
                    break;
            }
        }
        var edge = Mathf.Max(1f, r * 0.25f);
        Quad(Vector2.Zero, new Vector2(Size.X, edge), Edge);
        Quad(new Vector2(0, Size.Y - edge), new Vector2(Size.X, edge), Edge);
        Quad(new Vector2(0, edge), new Vector2(edge, Size.Y - 2 * edge), Edge);
        Quad(new Vector2(Size.X - edge, edge), new Vector2(edge, Size.Y - 2 * edge), Edge);
        RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), _indices.ToArray(), _points.ToArray(), _colors.ToArray());
    }

    private int Vertex(Vector2 at, Color color)
    {
        _points.Add(at);
        _colors.Add(color);
        return _points.Count - 1;
    }

    private void Triangle(Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        _indices.Add(Vertex(a, color));
        _indices.Add(Vertex(b, color));
        _indices.Add(Vertex(c, color));
    }

    private void Quad(Vector2 at, Vector2 size, Color color)
    {
        var a = Vertex(at, color);
        var b = Vertex(at + new Vector2(size.X, 0), color);
        var c = Vertex(at + size, color);
        var d = Vertex(at + new Vector2(0, size.Y), color);
        _indices.AddRange(new[] { a, b, c, a, c, d });
    }

    private const int Segments = 10;

    private void Disc(Vector2 at, float radius, Color color)
    {
        var centre = Vertex(at, color);
        var first = _points.Count;
        for (var i = 0; i < Segments; i++) Vertex(at + Vector2.FromAngle(Mathf.Tau * i / Segments) * radius, color);
        for (var i = 0; i < Segments; i++)
        {
            _indices.Add(centre);
            _indices.Add(first + i);
            _indices.Add(first + (i + 1) % Segments);
        }
    }

    private void Ring(Vector2 at, float radius, float width, Color color)
    {
        var first = _points.Count;
        for (var i = 0; i < Segments; i++)
        {
            var direction = Vector2.FromAngle(Mathf.Tau * i / Segments);
            Vertex(at + direction * radius, color);
            Vertex(at + direction * (radius - width), color);
        }
        for (var i = 0; i < Segments; i++)
        {
            int o0 = first + 2 * i, i0 = o0 + 1, o1 = first + 2 * ((i + 1) % Segments), i1 = o1 + 1;
            _indices.AddRange(new[] { o0, o1, i1, o0, i1, i0 });
        }
    }
}

/// <summary>
/// The minimap (T-812, ARCHITECTURE §7.4): a corner map in the right column, under Log out, where the HUD has room (<see cref="Hud"/> places it), and the full map, a panel over the
/// safe area opened by a tap on the corner map or the chat panel's Map button. Both draw the map's picture (made at map export, theme key <see cref="MinimapKeys"/>) and the same
/// marks (<see cref="MinimapMarks"/>: only what the client already has). A tap on the full map walks there (one <c>move</c>, the server decides). A map without a picture shows no corner
/// map and the Map button is disabled. Per frame: the marks are collected (no allocation) and compared with what was drawn; a canvas is redrawn only when one moved by a pixel.
/// </summary>
public sealed class MinimapView
{
    private const double MarkDp = 4;
    private const double PanelMaxWidthDp = 760;

    private readonly ClientSession _session;
    private readonly DisplayBundle _bundle;
    private readonly ClientConfig _config;
    private readonly Action _tap;
    private readonly Button _corner = new() { Flat = true, Visible = false, FocusMode = Control.FocusModeEnum.None, ClipContents = true };
    private readonly MinimapCanvas _cornerCanvas = new() { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
    private readonly CenterContainer _fullArea = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };   // re-fits the panel at every layout, as the other panels
    private readonly PanelContainer _fullPanel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _hint = new() { Text = UiText.MapTapToWalk, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly MinimapCanvas _fullCanvas = new() { MouseFilter = Control.MouseFilterEnum.Stop, ClipContents = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly List<MinimapMark> _marks = new();
    private IReadOnlyList<PartyMemberView> _members = Array.Empty<PartyMemberView>();
    private int _partyVersion = -1;
    private string? _mapId;
    private MapData? _map;
    private Texture2D? _picture;
    private int _cornerShown = int.MinValue;
    private int _fullShown = int.MinValue;
    private float _markPx = 5;

    /// <summary>Builds both (hidden) under <paramref name="parent"/>, the HUD's safe area; their touch areas go into <paramref name="interactive"/>.</summary>
    public MinimapView(Control parent, ClientSession session, DisplayBundle bundle, ClientConfig config, List<Control> interactive, Action tap)
    {
        _session = session;
        _bundle = bundle;
        _config = config;
        _tap = tap;
        parent.AddChild(_corner);
        _cornerCanvas.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _corner.AddChild(_cornerCanvas);
        _corner.Pressed += () => { _tap(); SetOpen(true); };

        parent.AddChild(_fullArea);
        _fullArea.AddChild(_fullPanel);
        _fullPanel.AddThemeStyleboxOverride("panel", _panelStyle);
        _fullPanel.AddChild(_box);
        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        header.AddChild(_title);
        header.AddChild(_close);
        _box.AddChild(header);
        _box.AddChild(_hint);
        _box.AddChild(_fullCanvas);
        _close.Pressed += () => { _tap(); SetOpen(false); };
        _fullCanvas.GuiInput += OnFullInput;
        interactive.Add(_corner);
        interactive.Add(_fullPanel);
        interactive.Add(_close);
    }

    /// <summary>Loads a texture by asset key through the theme chain (the HUD's icon loader), or null.</summary>
    public Func<string, Texture2D?>? PictureLoader { get; set; }

    /// <summary>True when this map has a picture: the corner map may show and the Map button works.</summary>
    public bool HasPicture => _picture is not null;

    /// <summary>The corner map's button (for the HUD's checks and the scripted run).</summary>
    public Button Corner => _corner;

    /// <summary>True while the corner map shows.</summary>
    public bool CornerShown => _corner.IsVisibleInTree();

    /// <summary>True while the full map is open.</summary>
    public bool Open => _fullArea.Visible;

    /// <summary>The full map's Close button.</summary>
    public Button CloseButton => _close;

    /// <summary>The full map's canvas (the scripted run taps it).</summary>
    public Control FullCanvas => _fullCanvas;

    /// <summary>The marks drawn now.</summary>
    public IReadOnlyList<MinimapMark> Marks => _marks;

    /// <summary>The corner map's frame now.</summary>
    public MinimapFrame CornerFrame => _cornerCanvas.Frame;

    /// <summary>How many times each canvas drew (the cost line of the scripted run).</summary>
    public (int Corner, int Full) Draws => (_cornerCanvas.Draws, _fullCanvas.Draws);

    /// <summary>The marks of the corner map now, as canvas positions inside it (the scripted run checks the player's mark moves).</summary>
    public Vector2? CornerMarkOf(MinimapMarkKind kind, long id = 0)
    {
        foreach (var mark in _marks)
            if (mark.Kind == kind && (id == 0 || mark.Id == id) && _cornerCanvas.Frame.Shows(mark.X, mark.Z))
            {
                var (x, y) = _cornerCanvas.Frame.ToPx(mark.X, mark.Z);
                return new Vector2((float)x, (float)y);
            }
        return null;
    }

    /// <summary>True when a party member's mark is inside the corner map's frame (the HUD leaves out its edge mark then).</summary>
    public bool CornerShows(long memberId)
    {
        if (!CornerShown) return false;
        foreach (var mark in _marks)
            if (mark.Kind == MinimapMarkKind.Party && mark.Id == memberId) return _cornerCanvas.Frame.Shows(mark.X, mark.Z);
        return false;
    }

    /// <summary>Opens or closes the full map (it opens only on a map with a picture).</summary>
    public void SetOpen(bool open)
    {
        open &= _picture is not null;
        if (open == _fullArea.Visible) return;
        _fullArea.Visible = open;
        _fullShown = int.MinValue;
    }

    /// <summary>Lays the full map out for the HUD's safe area (canvas units); <paramref name="dp"/> converts dp to canvas units.</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize)
    {
        var margin = dp(8);
        _markPx = Mathf.Max(3f, dp(MarkDp));
        _cornerCanvas.MarkPx = _markPx;
        var width = Mathf.Min(dp(PanelMaxWidthDp), safeSize.X - 2 * margin);
        _fullPanel.CustomMinimumSize = new Vector2(width, safeSize.Y - 2 * margin);
        _panelStyle.SetContentMarginAll(dp(8));
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        _box.AddThemeConstantOverride("separation", Mathf.RoundToInt(dp(6)));
        _close.CustomMinimumSize = new Vector2(dp(96), target);
        _title.CustomMinimumSize = new Vector2(Mathf.Max(0, width - dp(16) - dp(96) - dp(8)), 0);
        _hint.CustomMinimumSize = new Vector2(width - dp(16), 0);
        _hint.AddThemeFontSizeOverride("font_size", Mathf.Max(8, Mathf.RoundToInt(dp(13))));
        _cornerShown = _fullShown = int.MinValue;
        // Last, once every minimum inside is the new one: a size under an old, larger minimum would be held at it.
        _fullArea.Position = Vector2.Zero;
        _fullArea.Size = safeSize;
    }

    /// <summary>Puts the corner map at <paramref name="rect"/> (in the safe area's coordinates), or hides it (null). The HUD decides where it has room.</summary>
    public void PlaceCorner(Rect2? rect)
    {
        var show = rect is not null && _picture is not null;
        if (show)
        {
            // The minimum first: a size below the old minimum would be held at it (a smaller corner after a larger one kept the larger size).
            _corner.CustomMinimumSize = rect!.Value.Size;
            _corner.Position = rect.Value.Position;
            _corner.Size = rect.Value.Size;
        }
        if (_corner.Visible != show) _corner.Visible = show;
        _cornerShown = int.MinValue;
    }

    /// <summary>Once a frame: the map's picture when the map changed, the marks, and a redraw of what moved.</summary>
    public void Refresh(bool online)
    {
        var world = _session.World;
        if (world.MapId != _mapId)
        {
            _mapId = world.MapId;
            _map = _mapId is not null && _bundle.TryGetMap(_mapId, out var map) ? map : null;
            _picture = _map is not null ? PictureLoader?.Invoke(MinimapKeys.For(_map.Id)) : null;
            foreach (var canvas in new[] { _cornerCanvas, _fullCanvas })
            {
                canvas.Picture = _picture;
                canvas.MapSize = _map is null ? Vector2I.Zero : new Vector2I(_map.Width, _map.Height);
            }
            _title.Text = _map?.Name ?? string.Empty;
            if (_picture is null)
            {
                _corner.Visible = false;
                SetOpen(false);
            }
            _cornerShown = _fullShown = int.MinValue;
        }
        if (!online) SetOpen(false);
        var corner = _corner.Visible;
        if (_map is null || (!corner && !_fullArea.Visible)) return;

        if (_partyVersion != _session.Party.Version)
        {
            _partyVersion = _session.Party.Version;
            _members = _session.Party.InParty ? _session.Party.Members : Array.Empty<PartyMemberView>();
        }
        MinimapMarks.Collect(_marks, _map, world, _members, _session.Quests);
        if (corner)
        {
            var own = world.Own;
            var frame = MinimapFrame.Corner(_map.Width, _map.Height, own?.X ?? _map.Width / 2.0, own?.Z ?? _map.Height / 2.0, _corner.Size.X, _config.MinimapCornerCells);
            _cornerCanvas.Frame = frame;
            var signature = Signature(frame, Vector2.Zero);
            if (signature != _cornerShown)
            {
                _cornerShown = signature;
                _cornerCanvas.QueueRedraw();
            }
        }
        if (_fullArea.Visible)
        {
            var size = _fullCanvas.Size;
            var frame = MinimapFrame.Full(_map.Width, _map.Height, size.X, size.Y);
            _fullCanvas.Frame = frame;
            _fullCanvas.MarkPx = Mathf.Max(_markPx * 1.5f, (float)frame.CellPx * 0.45f);   // the full map's cells are larger: its marks grow with them
            _fullCanvas.Offset = new Vector2((float)(size.X - frame.WidthPx) / 2, (float)(size.Y - frame.HeightPx) / 2);
            var signature = Signature(frame, size);
            if (signature != _fullShown)
            {
                _fullShown = signature;
                _fullCanvas.QueueRedraw();
            }
        }
        _cornerCanvas.Marks = _marks;
        _fullCanvas.Marks = _marks;
    }

    /// <summary>A tap on the full map at a point of its canvas: walk to the cell under it (a cell off the map is not sent) and close the map, so the walk is in view.</summary>
    public bool TapFull(Vector2 local)
    {
        if (_map is null || !_fullArea.Visible) return false;
        var cell = _fullCanvas.Frame.CellAt(local.X - _fullCanvas.Offset.X, local.Y - _fullCanvas.Offset.Y);
        if (!_map.Inside(cell.X, cell.Y)) return false;
        _tap();
        _session.WalkTo(cell, Clocks.View());
        SetOpen(false);
        return true;
    }

    /// <summary>The canvas position of a cell's centre on the full map (the scripted run taps it).</summary>
    public Vector2 FullPointOf(int x, int y)
    {
        var (px, py) = _fullCanvas.Frame.ToPx(x + 0.5, y + 0.5);
        return _fullCanvas.Offset + new Vector2((float)px, (float)py);
    }

    private void OnFullInput(InputEvent e)
    {
        // A finger on a phone arrives as an emulated mouse button too (the project's defaults), so the release of the left button is the one tap.
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } button && TapFull(button.Position)) _fullCanvas.AcceptEvent();
    }

    /// <summary>Everything a redraw depends on, at whole pixels: the frame's place and every mark's (and the player's facing in eighths).</summary>
    private int Signature(MinimapFrame frame, Vector2 size)
    {
        var hash = new HashCode();
        hash.Add((int)Math.Round(frame.Left * frame.CellPx));
        hash.Add((int)Math.Round(frame.Top * frame.CellPx));
        hash.Add((int)Math.Round(frame.CellPx * 64));
        hash.Add((int)size.X);
        hash.Add((int)size.Y);
        foreach (var mark in _marks)
        {
            var (x, y) = frame.ToPx(mark.X, mark.Z);
            hash.Add((int)mark.Kind);
            hash.Add((int)Math.Round(x));
            hash.Add((int)Math.Round(y));
            if (mark.Kind == MinimapMarkKind.Own) hash.Add((int)Math.Round(mark.Yaw * 4 / Math.PI));
        }
        return hash.ToHashCode();
    }
}
