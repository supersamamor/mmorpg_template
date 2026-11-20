using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Quests;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The quest log (T-602, ARCHITECTURE §7.4): every active quest with its current step's text (from the display bundle) and the server's counters, and the completed
/// quests. Code-built Godot controls inside the HUD's safe area, sized in dp times the UI scale like the stats panel (every button at least the touch target). Each
/// active quest has Track (the HUD's tracker follows it) and Abandon, which asks once more before it sends <c>abandon_quest</c>. It decides nothing: counts, steps and
/// completion are the server's. It rebuilds its rows only when the quest book's <c>Version</c> moves, and does nothing while closed.
/// </summary>
public sealed class QuestPanel
{
    private const double PanelMaxWidthDp = 680;

    private readonly ClientSession _session;
    private readonly DisplayBundle _bundle;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly CenterContainer _center = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = Wrapped(UiText.QuestLogTitle);
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _content = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly List<Control> _rows = new();
    private readonly List<Button> _rowButtons = new();
    private readonly List<(Button Track, Button Abandon, string Quest)> _questButtons = new();
    private string? _confirming;   // the quest whose Abandon was pressed once
    private int _shownVersion = -1;
    private bool _samples;
    private float _target = 48;
    private float _width = 400;
    private Func<double, float> _dp = d => (float)d;
    private Vector2 _safeSize;

    /// <summary>Builds the panel (closed) under <paramref name="parent"/>; every button it makes goes into <paramref name="interactive"/>.</summary>
    public QuestPanel(Control parent, ClientSession session, DisplayBundle bundle, List<Control> interactive, Action tap)
    {
        _session = session;
        _bundle = bundle;
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
        header.AddChild(_close);
        _box.AddChild(header);
        _box.AddChild(_scroll);
        _scroll.AddChild(_content);
        _close.Pressed += () => { _tap(); SetOpen(false); };
        _interactive.Add(_close);
        _interactive.Add(_panel);
        _interactive.Add(_scroll);
    }

    /// <summary>True while the panel is open.</summary>
    public bool Open => _center.Visible;

    /// <summary>The Close button (for the scripted run).</summary>
    public Button CloseButton => _close;

    /// <summary>Each active quest's Track and Abandon buttons (for the scripted run).</summary>
    public IReadOnlyList<(Button Track, Button Abandon, string Quest)> QuestButtons => _questButtons;

    /// <summary>Every line the panel shows, joined with <c> | </c> (for the scripted run).</summary>
    public string Text => string.Join(" | ", _rows.OfType<Label>().Select(l => l.Text));

    /// <summary>Opens or closes the panel.</summary>
    public void SetOpen(bool open)
    {
        if (open == _center.Visible) return;
        _center.Visible = open;
        _confirming = null;
        _shownVersion = -1;
        Refresh();
    }

    /// <summary>Lays the panel out for the HUD's safe area (canvas units); <paramref name="dp"/> converts dp to canvas units.</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize, int fontSize)
    {
        _dp = dp;
        _target = target;
        var margin = dp(8);
        _width = Mathf.Min(dp(PanelMaxWidthDp), safeSize.X - 2 * margin);
        _panel.CustomMinimumSize = new Vector2(_width, safeSize.Y - 2 * margin);
        var gap = Mathf.RoundToInt(dp(6));
        _panelStyle.SetContentMarginAll(dp(8));
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        _box.AddThemeConstantOverride("separation", gap);
        _content.AddThemeConstantOverride("separation", gap);
        _title.CustomMinimumSize = new Vector2(Mathf.Max(0, _width - dp(24) - dp(96) - gap), 0);
        _close.CustomMinimumSize = new Vector2(dp(96), target);
        _safeSize = safeSize;
        _center.Position = Vector2.Zero;
        _center.Size = safeSize;
        _shownVersion = -1;
    }

    /// <summary>Rebuilds the rows from the quest book when it changed. Nothing while closed.</summary>
    public void Refresh()
    {
        if (!_center.Visible) return;
        if (_center.Size != _safeSize) _center.Size = _safeSize;
        var book = _session.Quests;
        if (_samples)
        {
            if (_shownVersion == -2) return;
            _shownVersion = -2;
            Clear();
            DrawSamples();
            return;
        }
        if (_shownVersion == book.Version) return;
        _shownVersion = book.Version;
        Clear();

        Heading(UiText.ActiveQuests);
        if (book.Active.Count == 0) Line(UiText.NoActiveQuests, dim: true);
        foreach (var entry in book.Active)
        {
            var quest = _bundle.Quest(entry.Quest);
            Line(quest?.Name ?? entry.Quest, bold: true);
            Line(UiText.QuestStep(entry.Step, quest?.Steps.Count ?? 1, quest?.Step(entry.Step) ?? string.Empty));
            foreach (var objective in entry.Objectives) Line("  " + QuestBook.ObjectiveLine(_bundle, objective), dim: objective.Met);
            if (entry.Held is { } held) Line(UiText.QuestHeld(held));
            var buttons = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            var track = Button(UiText.Track);
            var abandon = Button(_confirming == entry.Quest ? UiText.ConfirmAbandon : UiText.Abandon);
            track.Disabled = book.Tracked == entry.Quest;
            var id = entry.Quest;
            track.Pressed += () => { _tap(); _session.Quests.Track(id); };
            abandon.Pressed += () => OnAbandon(id);
            buttons.AddChild(track);
            buttons.AddChild(abandon);
            Add(buttons);
            _questButtons.Add((track, abandon, id));
        }

        Heading(UiText.CompletedQuests);
        if (book.Completed.Count == 0) Line(UiText.NoCompletedQuests, dim: true);
        foreach (var id in book.Completed) Line(_bundle.Quest(id)?.Name ?? id, dim: true);
    }

    /// <summary>Fills the log with the longest lines it can show (and opens it), so a layout check sees the worst case; <see cref="ClearSamples"/> ends it (T-406).</summary>
    public void ShowSamples(string longName)
    {
        _samples = true;
        _sampleName = longName;
        _center.Visible = true;
        _shownVersion = -1;
        Refresh();
    }

    /// <summary>Ends <see cref="ShowSamples"/> and closes the log.</summary>
    public void ClearSamples()
    {
        _samples = false;
        SetOpen(false);
        _shownVersion = -1;
    }

    private void DrawSamples()
    {
        Heading(UiText.ActiveQuests);
        for (var q = 0; q < 4; q++)
        {
            Line(_sampleName + " of the Far Northern Reaches", bold: true);
            Line(UiText.QuestStep(9, 9, _sampleName + " " + _sampleName + " " + _sampleName));
            for (var o = 0; o < 3; o++) Line("  " + UiText.QuestObjective(QuestObjectiveTypes.Kill, _sampleName, 99_999, 99_999));
            Line(UiText.QuestHeld(QuestReasons.InventoryFull));
            var buttons = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            buttons.AddChild(Button(UiText.Track));
            buttons.AddChild(Button(UiText.ConfirmAbandon));
            Add(buttons);
        }
        Heading(UiText.CompletedQuests);
        for (var c = 0; c < 6; c++) Line(_sampleName + " " + _sampleName, dim: true);
    }

    private string _sampleName = string.Empty;

    private void OnAbandon(string questId)
    {
        _tap();
        if (_confirming != questId)
        {
            _confirming = questId;   // ask once more: progress is lost and nothing comes back
            _shownVersion = -1;
            Refresh();
            return;
        }
        _confirming = null;
        _session.AbandonQuest(questId);   // the server answers with the quest gone, or a refusal on the flash line
    }

    private void Clear()
    {
        foreach (var button in _rowButtons) _interactive.Remove(button);
        _rowButtons.Clear();
        _questButtons.Clear();
        foreach (var row in _rows) row.QueueFree();
        _rows.Clear();
    }

    private void Heading(string text) => Line(text, bold: true, color: new Color(1f, 0.85f, 0.45f));

    private void Line(string text, bool bold = false, bool dim = false, Color? color = null)
    {
        var label = Wrapped(text);
        label.CustomMinimumSize = new Vector2(_width - _dp(24), 0);
        if (color is { } c) label.AddThemeColorOverride("font_color", c);
        else if (dim) label.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        else if (bold) label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        Add(label);
    }

    private Button Button(string text)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(_dp(140), _target) };
        _rowButtons.Add(button);
        _interactive.Add(button);
        return button;
    }

    private void Add(Control control)
    {
        _rows.Add(control);
        _content.AddChild(control);
    }

    private static Label Wrapped(string text) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore,
    };
}
