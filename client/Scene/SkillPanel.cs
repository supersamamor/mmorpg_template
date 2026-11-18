using System.Text;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Skills;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The skill tree panel (T-502, ARCHITECTURE §7.4): the job, the unspent skill points, one row for every skill of the job's tree and its parents' trees with its
/// level and, while locked, what it needs (greyed out), and a Learn button for the selected row. When the server says the player can change job, one button per
/// job offers it. Code-built Godot controls inside the HUD's safe area, sized in dp times the UI scale like the inventory panel. It only sends intents through
/// <see cref="ClientSession"/>; what changes comes back from the server as a new <c>skills</c> message. It redraws only when the book's <c>Version</c> moves
/// or the selection changes, and does nothing while closed.
/// </summary>
public sealed class SkillPanel
{
    private const double PanelMaxWidthDp = 680;
    private const int MaxJobButtons = 4;

    private readonly ClientSession _session;
    private readonly DisplayBundle _bundle;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly CenterContainer _center = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = Wrapped(UiText.SkillsTitle);
    private readonly Label _status = Wrapped(string.Empty);
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _content = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _hint = Wrapped(UiText.PickASkill);
    private readonly HFlowContainer _actions = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Button _learn = new() { Text = UiText.Learn };
    private readonly List<Button> _jobButtons = new();
    private readonly List<string> _jobIds = new();
    private readonly List<Button> _rowButtons = new();
    private readonly List<string> _rowSkills = new();
    private readonly Dictionary<string, Texture2D?> _icons = new(StringComparer.Ordinal);
    private string? _selected;
    private int _shownVersion = -1;
    private float _target = 48;
    private Vector2 _safeSize;

    /// <summary>Builds the panel (closed) under <paramref name="parent"/>; every button it makes goes into <paramref name="interactive"/>.</summary>
    public SkillPanel(Control parent, ClientSession session, DisplayBundle bundle, List<Control> interactive, Action tap)
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
        _box.AddChild(_status);
        _box.AddChild(_scroll);
        _scroll.AddChild(_content);
        _box.AddChild(_hint);
        _actions.AddChild(_learn);
        _box.AddChild(_actions);
        for (var i = 0; i < MaxJobButtons; i++)
        {
            var button = new Button { Visible = false };
            var index = i;
            button.Pressed += () => OnJob(index);
            _jobButtons.Add(button);
            _actions.AddChild(button);
            _interactive.Add(button);
        }
        _learn.Pressed += OnLearn;
        _close.Pressed += () => { _tap(); SetOpen(false); };
        _interactive.Add(_learn);
        _interactive.Add(_close);
        _interactive.Add(_panel);
        _interactive.Add(_scroll);
    }

    /// <summary>Finds an icon texture by asset key (the theme), or null; the HUD sets it once the theme is loaded.</summary>
    public Func<string, Texture2D?>? IconLoader { get; set; }

    /// <summary>True while the panel is open.</summary>
    public bool Open => _center.Visible;

    /// <summary>The skill rows now shown, with the skill each stands for (for the scripted run).</summary>
    public IReadOnlyList<(Button Button, string Skill)> RowButtons
    {
        get
        {
            var list = new List<(Button, string)>();
            for (var i = 0; i < _rowSkills.Count; i++) list.Add((_rowButtons[i], _rowSkills[i]));
            return list;
        }
    }

    /// <summary>Where the panel, the hint and the Learn button are on screen now (for the scripted run's layout line).</summary>
    public string Layout => $"center {_center.GetGlobalRect()}, panel {_panel.GetGlobalRect()}, box {_box.GetGlobalRect()}, status {_status.GetGlobalRect()}, scroll {_scroll.GetGlobalRect()}, hint {_hint.GetGlobalRect()}, learn {_learn.GetGlobalRect()} (visible {_learn.Visible})";

    /// <summary>The Learn button (for the scripted run).</summary>
    public Button LearnButton => _learn;

    /// <summary>The job-change buttons now shown, with the job each stands for (for the scripted run).</summary>
    public IReadOnlyList<(Button Button, string Job)> JobButtons
    {
        get
        {
            var list = new List<(Button, string)>();
            for (var i = 0; i < _jobIds.Count; i++) list.Add((_jobButtons[i], _jobIds[i]));
            return list;
        }
    }

    /// <summary>The line under the title (job and points) and the hint under the rows (for the scripted run).</summary>
    public string StatusText => _status.Text + " | " + _hint.Text;

    /// <summary>Opens or closes the panel; opening clears the selection.</summary>
    public void SetOpen(bool open)
    {
        if (open == _center.Visible) return;
        _center.Visible = open;
        if (open) _selected = null;
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
        _title.CustomMinimumSize = new Vector2(Mathf.Max(0, width - dp(24) - dp(96) - gap), 0);
        foreach (var label in new[] { _status, _hint }) label.CustomMinimumSize = new Vector2(width - dp(24), 0);
        _close.CustomMinimumSize = new Vector2(dp(96), target);
        _actions.AddThemeConstantOverride("h_separation", gap);
        _actions.AddThemeConstantOverride("v_separation", gap);
        _learn.CustomMinimumSize = new Vector2(dp(96), target);
        foreach (var button in _jobButtons) button.CustomMinimumSize = new Vector2(dp(120), target);
        foreach (var button in _rowButtons) Size(button, fontSize);
        _safeSize = safeSize;
        _center.Position = Vector2.Zero;
        _center.Size = safeSize;
        _shownVersion = -1;
    }

    /// <summary>Redraws from the skill book when it changed. Nothing while closed.</summary>
    public void Refresh()
    {
        if (!_center.Visible) return;
        // A container that was asked for more than the safe area while its labels had no width yet keeps that size: bring it back once they have one.
        if (_center.Size != _safeSize) _center.Size = _safeSize;
        var book = _session.Skills;
        if (_shownVersion == book.Version) return;
        _shownVersion = book.Version;

        _status.Text = UiText.JobLine(_bundle.NameOf(book.JobId)) + "   " + UiText.SkillPoints(book.Points);
        var rows = book.Rows;
        _rowSkills.Clear();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var button = RowButton(i);
            button.Text = RowText(row);
            button.Icon = IconOf(_bundle.Skill(row.SkillId)?.Icon);
            button.Modulate = row.SkillId == _selected ? new Color(1f, 0.9f, 0.5f)
                : row.State == SkillState.Locked ? new Color(1, 1, 1, 0.45f)
                : row.State == SkillState.Learnable ? new Color(0.75f, 1f, 0.75f)
                : Colors.White;
            button.Visible = true;
            _rowSkills.Add(row.SkillId);
        }
        for (var i = rows.Count; i < _rowButtons.Count; i++) _rowButtons[i].Visible = false;

        var selected = _selected is null ? null : FindRow(_selected);
        _hint.Text = selected is null ? UiText.PickASkill : Describe(selected);
        _learn.Visible = selected is { State: SkillState.Learnable };

        _jobIds.Clear();
        var shown = 0;
        foreach (var job in book.ChangeableJobs)
        {
            if (shown >= _jobButtons.Count) break;
            _jobButtons[shown].Text = UiText.BecomeJob(_bundle.NameOf(job));
            _jobButtons[shown].Visible = true;
            _jobIds.Add(job);
            shown++;
        }
        for (var i = shown; i < _jobButtons.Count; i++) _jobButtons[i].Visible = false;
    }

    private SkillRow? FindRow(string skillId)
    {
        foreach (var row in _session.Skills.Rows)
            if (row.SkillId == skillId) return row;
        return null;
    }

    private string RowText(SkillRow row)
    {
        var skill = _bundle.Skill(row.SkillId);
        var text = new StringBuilder(skill?.Name ?? row.SkillId).Append("   ").Append(UiText.SkillLevel(row.Level, row.Max));
        if (skill is { Passive: true }) text.Append("  (").Append(UiText.Passive).Append(')');
        foreach (var need in row.Missing) text.Append('\n').Append(UiText.Needs(_bundle.NameOf(need.SkillId), need.Level));
        return text.ToString();
    }

    private string Describe(SkillRow row)
    {
        var skill = _bundle.Skill(row.SkillId);
        var text = new StringBuilder(skill?.Name ?? row.SkillId).Append("  ").Append(UiText.SkillLevel(row.Level, row.Max));
        if (skill?.Description is { Length: > 0 } description) text.Append(": ").Append(description);
        foreach (var need in row.Missing) text.Append("  ").Append(UiText.Needs(_bundle.NameOf(need.SkillId), need.Level)).Append('.');
        return text.ToString();
    }

    private void OnRow(int index)
    {
        _tap();
        if (index < 0 || index >= _rowSkills.Count) return;
        _selected = _rowSkills[index];
        _shownVersion = -1;
        Refresh();
    }

    private void OnLearn()
    {
        _tap();
        if (_selected is not null) _session.LearnSkill(_selected);   // the server spends the point; the new list comes back
    }

    private void OnJob(int index)
    {
        _tap();
        if (index >= 0 && index < _jobIds.Count) _session.ChangeJob(_jobIds[index]);
    }

    private Button RowButton(int index)
    {
        while (_rowButtons.Count <= index)
        {
            var button = new Button
            {
                Alignment = HorizontalAlignment.Left, ExpandIcon = false, ClipText = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            var at = _rowButtons.Count;
            button.Pressed += () => OnRow(at);
            Size(button, _target);
            _rowButtons.Add(button);
            _content.AddChild(button);
            _interactive.Add(button);
        }
        return _rowButtons[index];
    }

    private void Size(Button button, float size) => button.CustomMinimumSize = new Vector2(0, Mathf.Max(_target, size));

    private Texture2D? IconOf(string? key)
    {
        if (key is null || IconLoader is null) return null;
        if (!_icons.TryGetValue(key, out var texture))
        {
            texture = IconLoader(key);
            _icons[key] = texture;
        }
        return texture;
    }

    private static Label Wrapped(string text) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore,
    };
}
