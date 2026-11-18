using System.Text;
using Game.Client.Core;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The stats panel (T-503, ARCHITECTURE §7.4): the unspent stat points, the six primary stats each with a + button that shows the server's cost for the next
/// step, and the derived stats that matter (ATK, MATK, DEF, HIT, FLEE, ASPD, max HP and SP). Code-built Godot controls inside the HUD's safe area, sized in dp
/// times the UI scale like the skill panel (every button at least the touch target). It only sends <c>raise_stat</c> through <see cref="ClientSession"/>: the
/// cost, the points and every derived number are the server's, and what changes comes back as a new <c>stats</c> message. It redraws only when the stat
/// book's <c>Version</c> moves, and does nothing while closed.
/// </summary>
public sealed class StatsPanel
{
    private const double PanelMaxWidthDp = 680;

    private readonly ClientSession _session;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly CenterContainer _center = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = Wrapped(UiText.StatsTitle);
    private readonly Label _points = Wrapped(string.Empty);
    private readonly Button _back = new() { Text = UiText.BackToSkills };
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _content = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly GridContainer _grid = new() { Columns = 4, MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _derivedTitle = Wrapped(UiText.DerivedTitle);
    private readonly Label _derived = Wrapped(string.Empty);
    private readonly List<Label> _labels = new();
    private readonly List<Button> _raise = new();
    private readonly List<string> _ids = new();
    private readonly StringBuilder _text = new();
    private int _shownVersion = -1;
    private float _target = 48;
    private float _raiseWidth;   // the + button width in canvas units (rows are made after the metrics were applied)
    private Vector2 _safeSize;

    /// <summary>Builds the panel (closed) under <paramref name="parent"/>; every button it makes goes into <paramref name="interactive"/>.</summary>
    public StatsPanel(Control parent, ClientSession session, List<Control> interactive, Action tap)
    {
        _session = session;
        _interactive = interactive;
        _tap = tap;
        _center.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        parent.AddChild(_center);
        _center.AddChild(_panel);
        _panel.AddChild(_box);
        _panel.AddThemeStyleboxOverride("panel", _panelStyle);

        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(_title);
        header.AddChild(_back);
        header.AddChild(_close);
        _box.AddChild(header);
        _box.AddChild(_points);
        _box.AddChild(_scroll);
        _scroll.AddChild(_content);
        _content.AddChild(_grid);
        _content.AddChild(_derivedTitle);
        _content.AddChild(_derived);
        _close.Pressed += () => { _tap(); SetOpen(false); };
        _back.Pressed += () => { _tap(); SetOpen(false); BackToSkills?.Invoke(); };
        _interactive.Add(_close);
        _interactive.Add(_back);
        _interactive.Add(_panel);
        _interactive.Add(_scroll);
    }

    /// <summary>Raised by the Skills button, after the panel closed: the HUD opens the skill panel.</summary>
    public Action? BackToSkills { get; set; }

    /// <summary>The Close button (for the scripted run).</summary>
    public Button CloseButton => _close;

    /// <summary>True while the panel is open.</summary>
    public bool Open => _center.Visible;

    /// <summary>The + buttons now shown, with the stat each raises (for the scripted run).</summary>
    public IReadOnlyList<(Button Button, string Stat)> RaiseButtons
    {
        get
        {
            var list = new List<(Button, string)>();
            for (var i = 0; i < _ids.Count; i++) list.Add((_raise[i], _ids[i]));
            return list;
        }
    }

    /// <summary>The stat point line, the stat rows and the derived line as drawn (for the scripted run).</summary>
    public string Text
    {
        get
        {
            _text.Clear().Append(_points.Text);
            for (var i = 0; i < _ids.Count; i++) _text.Append(" | ").Append(_labels[i].Text).Append(' ').Append(_raise[i].Text);
            return _text.Append(" | ").Append(_derived.Text).ToString();
        }
    }

    /// <summary>Where the panel and its parts are on screen now (for the scripted run's layout line).</summary>
    public string Layout => $"center {_center.GetGlobalRect()}, panel {_panel.GetGlobalRect()}, scroll {_scroll.GetGlobalRect()}, grid {_grid.GetGlobalRect()}, derived {_derived.GetGlobalRect()}";

    /// <summary>Opens or closes the panel.</summary>
    public void SetOpen(bool open)
    {
        if (open == _center.Visible) return;
        _center.Visible = open;
        _shownVersion = -1;
        Refresh();
    }

    /// <summary>Lays the panel out for the HUD's safe area (canvas units); <paramref name="dp"/> converts dp to canvas units.</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize, int fontSize)
    {
        _target = target;
        var margin = dp(8);
        var width = Mathf.Min(dp(PanelMaxWidthDp), safeSize.X - 2 * margin);
        _panel.CustomMinimumSize = new Vector2(width, safeSize.Y - 2 * margin);
        var gap = Mathf.RoundToInt(dp(6));
        _panelStyle.SetContentMarginAll(dp(8));
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        _box.AddThemeConstantOverride("separation", gap);
        _content.AddThemeConstantOverride("separation", gap);
        _grid.AddThemeConstantOverride("h_separation", gap);
        _grid.AddThemeConstantOverride("v_separation", gap);
        _title.CustomMinimumSize = new Vector2(Mathf.Max(0, width - dp(24) - dp(96) * 2 - gap * 2), 0);
        foreach (var label in new[] { _points, _derived, _derivedTitle }) label.CustomMinimumSize = new Vector2(width - dp(24), 0);
        _close.CustomMinimumSize = new Vector2(dp(96), target);
        _back.CustomMinimumSize = new Vector2(dp(96), target);
        _raiseWidth = dp(96);
        foreach (var label in _labels) label.CustomMinimumSize = new Vector2(dp(72), target);
        foreach (var button in _raise) button.CustomMinimumSize = new Vector2(_raiseWidth, target);
        _safeSize = safeSize;
        _center.Position = Vector2.Zero;
        _center.Size = safeSize;
        _shownVersion = -1;
    }

    /// <summary>Redraws from the stat book when it changed. Nothing while closed.</summary>
    public void Refresh()
    {
        if (!_center.Visible) return;
        if (_center.Size != _safeSize) _center.Size = _safeSize;
        var book = _session.Stats;
        if (_shownVersion == book.Version) return;
        _shownVersion = book.Version;

        _points.Text = UiText.StatPoints(book.Points);
        var stats = book.Stats;
        _ids.Clear();
        for (var i = 0; i < stats.Count; i++)
        {
            Row(i);
            _labels[i].Text = UiText.StatLine(stats[i].Stat, stats[i].Value);
            _raise[i].Text = UiText.RaiseButton(stats[i].NextCost);
            _raise[i].Disabled = !book.Affordable(stats[i]);
            _labels[i].Visible = _raise[i].Visible = true;
            _ids.Add(stats[i].Stat);
        }
        for (var i = stats.Count; i < _raise.Count; i++) _labels[i].Visible = _raise[i].Visible = false;

        _text.Clear();
        foreach (var d in book.Derived) _text.Append(_text.Length == 0 ? string.Empty : "   ").Append(UiText.DerivedLine(d.Stat, d.Value));
        _derived.Text = _text.ToString();
    }

    private void Row(int index)
    {
        while (_labels.Count <= index)
        {
            var at = _labels.Count;
            var label = new Label { VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var button = new Button();
            button.Pressed += () => OnRaise(at);
            label.CustomMinimumSize = new Vector2(0, _target);
            button.CustomMinimumSize = new Vector2(_raiseWidth, _target);
            _labels.Add(label);
            _raise.Add(button);
            _grid.AddChild(label);
            _grid.AddChild(button);
            _interactive.Add(button);
        }
    }

    private void OnRaise(int index)
    {
        _tap();
        if (index >= 0 && index < _ids.Count) _session.RaiseStat(_ids[index]);   // the server prices and checks it; the new stats come back
    }

    private static Label Wrapped(string text) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore,
    };
}
