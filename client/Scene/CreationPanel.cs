using System.Collections.Generic;
using Game.Client.Core;
using Game.Client.Core.Accounts;
using Game.Shared.ClientContent;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The character creation screen (T-706, ARCHITECTURE §7.4, §7.5.1): on the left the turning preview and the random button, on the right the name and the choices in a
/// scroll container, and under them, always in reach, the line about what the server said and the Back and Create buttons. The choices come from the bundle
/// (<see cref="CharacterCreator"/>); the server decides. Every option is a button with its name, the colours too, so nobody has to tell colours apart. It redraws only
/// when the choices or the flow change; the preview is the one thing that moves, and only while the screen is open.
/// </summary>
public sealed class CreationPanel
{
    private const double PreviewWidthDp = 260;

    private readonly ClientSession _session;
    private readonly CharacterCreator _creator;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly LookView? _preview;
    private readonly Control _root = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly HBoxContainer _columns = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly VBoxContainer _left = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly VBoxContainer _right = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _choices = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly LineEdit _name = new() { MaxLength = 32, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _random = new() { Text = UiText.RandomLook };
    private readonly Label _notice = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
    private readonly HBoxContainer _buttons = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Button _back = new() { Text = UiText.Back };
    private readonly Button _create = new() { Text = UiText.CreateCharacter, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Group _genders;
    private readonly Group _faces;
    private readonly Group _hairstyles;
    private readonly Group _skins;
    private readonly Group _hairColors;
    private float _target = 48;
    private float _dp = 1;
    private float _optionMax = 400;
    private int _shownFlow = -1;
    private int _shownChoices = -1;
    private bool _samples;

    /// <summary>Builds the screen (hidden) under <paramref name="parent"/>. Without <paramref name="preview"/> (no theme) it has no preview.</summary>
    public CreationPanel(Control parent, ClientSession session, LookView? preview, List<Control> interactive, Action tap)
    {
        _session = session;
        _creator = session.Accounts.Creator!;
        _interactive = interactive;
        _tap = tap;
        _preview = preview;
        parent.AddChild(_root);
        _root.AddChild(_columns);
        _columns.AddChild(_left);
        _columns.AddChild(_right);
        if (preview is not null)
        {
            preview.Frame.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _left.AddChild(preview.Frame);
        }
        _left.AddChild(_random);
        _right.AddChild(_scroll);
        _scroll.AddChild(_choices);
        _choices.AddChild(_name);
        _genders = new Group(this, UiText.CreationGender, id => _creator.SelectGender(id));
        _faces = new Group(this, UiText.CreationFace, id => _creator.SelectFace(id));
        _hairstyles = new Group(this, UiText.CreationHairstyle, id => _creator.SelectHair(id));
        _skins = new Group(this, UiText.CreationSkin, id => _creator.SelectSkin(id));
        _hairColors = new Group(this, UiText.CreationHairColor, id => _creator.SelectHairColor(id));
        _right.AddChild(_notice);
        _right.AddChild(_buttons);
        _buttons.AddChild(_back);
        _buttons.AddChild(_create);
        _name.TextChanged += text => _creator.SetName(text);
        _random.Pressed += () =>
        {
            _tap();
            _creator.Randomize();
        };
        _back.Pressed += () =>
        {
            _tap();
            _session.Accounts.CloseCreation();
        };
        _create.Pressed += () =>
        {
            _tap();
            if (!_samples) _session.Accounts.Create(_creator.Name.Trim(), _creator.Choice);
        };
        foreach (var control in new Control[] { _name, _random, _back, _create }) interactive.Add(control);
        if (preview is not null) interactive.Add(preview.Frame);
    }

    /// <summary>True while it shows.</summary>
    public bool Open => _root.Visible;

    /// <summary>The name field (for the scripted run).</summary>
    public LineEdit NameField => _name;

    /// <summary>The random button (for the scripted run).</summary>
    public Button RandomButton => _random;

    /// <summary>The back button (for the scripted run).</summary>
    public Button BackButton => _back;

    /// <summary>The create button (for the scripted run).</summary>
    public Button CreateButton => _create;

    /// <summary>The preview, or null (for the scripted run).</summary>
    public LookView? Preview => _preview;

    /// <summary>What the line above the buttons says (for the scripted run).</summary>
    public string Notice => _notice.Visible ? _notice.Text : string.Empty;

    /// <summary>The button of an option, by its id, or null (for the scripted run).</summary>
    public Button? OptionButton(string id)
    {
        foreach (var group in Groups())
            if (group.Find(id) is { } button) return button;
        return null;
    }

    /// <summary>Shows or hides the screen. Hidden, the preview is freed and renders nothing.</summary>
    public void SetOpen(bool open)
    {
        if (open == _root.Visible) return;
        _root.Visible = open;
        _shownFlow = _shownChoices = -1;
        if (!open) _preview?.Clear();
        Refresh();
    }

    /// <summary>Lays the screen out for the HUD's safe area (canvas units).</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Rect2 safe)
    {
        _target = target;
        _dp = dp(1);
        var margin = dp(8);
        var gap = Mathf.RoundToInt(dp(8));
        foreach (var box in new BoxContainer[] { _columns, _left, _right, _choices, _buttons }) box.AddThemeConstantOverride("separation", gap);
        var width = safe.Size.X - 2 * margin;
        var left = Mathf.Min(dp(PreviewWidthDp), width * 0.36f);
        _left.CustomMinimumSize = new Vector2(left, 0);
        _optionMax = Mathf.Max(target, width - left - gap - dp(14)); // beside the scroll bar
        foreach (var control in new Control[] { _name, _random, _back, _create }) control.CustomMinimumSize = new Vector2(_target, _target);
        _back.CustomMinimumSize = new Vector2(Mathf.Max(_target, dp(72)), _target);
        foreach (var group in Groups()) group.Size(_target, gap);
        _root.Position = safe.Position;
        _root.Size = safe.Size;
        _columns.Position = new Vector2(margin, margin);
        _columns.Size = new Vector2(width, safe.Size.Y - 2 * margin);
    }

    /// <summary>Redraws what changed.</summary>
    public void Refresh()
    {
        if (!_root.Visible) return;
        var flow = _session.Accounts;
        if (_shownChoices != _creator.Version)
        {
            _shownChoices = _creator.Version;
            if (_name.Text != _creator.Name) _name.Text = _creator.Name;
            _genders.Fill(_creator.Genders, g => g.Gender, g => g.Name, _ => null, _creator.Gender);
            _faces.Fill(_creator.Faces, o => o.Id, o => o.Name, _ => null, _creator.Face);
            _hairstyles.Fill(_creator.Hairstyles, o => o.Id, o => o.Name, _ => null, _creator.Hair);
            _skins.Fill(_creator.SkinTones, o => o.Id, o => o.Name, o => o.Color, _creator.Skin);
            _hairColors.Fill(_creator.HairColors, o => o.Id, o => o.Name, o => o.Color, _creator.HairColor);
            _preview?.Show(_creator.StartJob, _creator.Look);
        }
        if (_shownFlow == flow.Version) return;
        _shownFlow = flow.Version;
        _name.PlaceholderText = UiText.NewCharacterName(flow.NameMin, flow.NameMax);
        if (!_samples) SetNotice(flow.Busy ? UiText.AccountBusy : UiText.Account(flow.Notice, flow.Error, flow.RetryAfterSeconds));
        _create.Disabled = _back.Disabled = flow.Busy;
    }

    /// <summary>Once a frame while it is open: the preview turns.</summary>
    public void Update(double dt)
    {
        if (_root.Visible) _preview?.Update(dt);
    }

    /// <summary>Fills the screen with the longest words it can show, so a layout check sees the worst case.</summary>
    public void ShowSamples(string longName)
    {
        _samples = true;
        SetOpen(true);
        _name.PlaceholderText = UiText.NewCharacterName(88, 88);
        SetNotice(UiText.LongestAccount);
        var options = new List<ClientColorOption>();
        for (var i = 0; i < 6; i++) options.Add(new ClientColorOption("sample" + i, longName, "#888888"));
        foreach (var group in Groups()) group.Fill(options, o => o.Id, o => o.Name, group == _skins || group == _hairColors ? o => o.Color : _ => null, "sample0");
    }

    /// <summary>Back to what was chosen.</summary>
    public void ClearSamples()
    {
        if (!_samples) return;
        _samples = false;
        _shownChoices = _shownFlow = -1;
        SetNotice(string.Empty);
        Refresh();
    }

    private IEnumerable<Group> Groups() => [_genders, _faces, _hairstyles, _skins, _hairColors];

    private void SetNotice(string text)
    {
        _notice.Text = text;
        _notice.Visible = text.Length > 0;
    }

    private void Pressed(Func<string, bool> select, string id)
    {
        _tap();
        if (!_samples) select(id);
        _shownChoices = -1; // the toggles follow the model, also when the pressed one was already chosen
        Refresh();
    }

    /// <summary>One choice: its title and a button per option, the chosen one pressed and marked. A colour's button shows the colour beside its name.</summary>
    private sealed class Group
    {
        private readonly CreationPanel _panel;
        private readonly Func<string, bool> _select;
        private readonly Label _title;
        private readonly HFlowContainer _flow = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        private readonly List<Option> _options = new();
        private float _target = 48;

        public Group(CreationPanel panel, string title, Func<string, bool> select)
        {
            _panel = panel;
            _select = select;
            _title = new Label { Text = title, MouseFilter = Control.MouseFilterEnum.Ignore };
            panel._choices.AddChild(_title);
            panel._choices.AddChild(_flow);
        }

        public Button? Find(string id) => _options.FirstOrDefault(o => o.Button.Visible && o.Id == id)?.Button;

        public void Size(float target, int gap)
        {
            _target = target;
            _flow.AddThemeConstantOverride("h_separation", gap);
            _flow.AddThemeConstantOverride("v_separation", gap);
            foreach (var option in _options) Size(option);
        }

        public void Fill<T>(IReadOnlyList<T> options, Func<T, string> idOf, Func<T, string> nameOf, Func<T, string?> colorOf, string chosen)
        {
            _title.Visible = _flow.Visible = options.Count > 0;
            while (_options.Count < options.Count) Add();
            for (var i = 0; i < _options.Count; i++)
            {
                var option = _options[i];
                option.Button.Visible = i < options.Count;
                if (i >= options.Count) continue;
                option.Id = idOf(options[i]);
                var mine = option.Id == chosen;
                option.Button.Text = mine ? UiText.Chosen(nameOf(options[i])) : nameOf(options[i]);
                option.Button.SetPressedNoSignal(mine);
                var color = colorOf(options[i]);
                option.Chip.Visible = color is not null;
                if (color is not null) option.Chip.Color = new Color(color);
                Pad(option, color is not null);
                Fit(option);
            }
        }

        private void Add()
        {
            var option = new Option();
            option.Button.AddChild(option.Chip);
            option.Button.Pressed += () => _panel.Pressed(_select, option.Id);
            _flow.AddChild(option.Button);
            _options.Add(option);
            _panel._interactive.Add(option.Button);
            Size(option);
        }

        private void Size(Option option)
        {
            Pad(option, option.Chip.Visible);
            Fit(option);
        }

        /// <summary>As wide as its name, so options stand side by side, but never wider than the column: a longer name wraps.</summary>
        private void Fit(Option option)
        {
            var font = option.Button.GetThemeFont("font");
            var text = font.GetStringSize(option.Button.Text, HorizontalAlignment.Left, -1, option.Button.GetThemeFontSize("font_size")).X;
            var box = option.Button.GetThemeStylebox("normal");
            var wanted = Mathf.Ceil(text + box.ContentMarginLeft + box.ContentMarginRight) + 2;
            option.Button.CustomMinimumSize = new Vector2(Mathf.Clamp(wanted, _target, Mathf.Max(_target, _panel._optionMax)), _target);
        }

        /// <summary>The colour fills a strip at the button's left; its name stands beside it.</summary>
        private void Pad(Option option, bool swatch)
        {
            var chip = _panel._dp * 24;
            var inset = _panel._dp * 6;
            option.Chip.AnchorTop = 0;
            option.Chip.AnchorBottom = 1;
            option.Chip.OffsetLeft = inset;
            option.Chip.OffsetRight = inset + chip;
            option.Chip.OffsetTop = inset;
            option.Chip.OffsetBottom = -inset;
            foreach (var (state, shade, border) in new[] { ("normal", 0.16f, 0f), ("hover", 0.2f, 0f), ("pressed", 0.3f, 2f), ("hover_pressed", 0.3f, 2f), ("disabled", 0.12f, 0f) })
            {
                var box = new StyleBoxFlat { BgColor = new Color(shade, shade, shade * 1.1f, 0.95f), BorderColor = new Color(1f, 0.85f, 0.4f) };
                box.SetBorderWidthAll(Mathf.RoundToInt(border * Mathf.Max(1, _panel._dp)));
                box.SetCornerRadiusAll(Mathf.RoundToInt(_panel._dp * 4));
                box.ContentMarginLeft = inset * 1.5f + (swatch ? chip : 0);
                box.ContentMarginRight = inset * 1.5f;
                box.ContentMarginTop = box.ContentMarginBottom = inset * 0.5f;
                option.Button.AddThemeStyleboxOverride(state, box);
            }
        }

        private sealed class Option
        {
            public readonly Button Button = new() { ToggleMode = true, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            public readonly ColorRect Chip = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            public string Id = string.Empty;
        }
    }
}
