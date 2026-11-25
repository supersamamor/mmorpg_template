using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Outfits;
using Game.Shared.ClientContent;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The wardrobe (T-708, ARCHITECTURE §7.4, §7.5.2): on the left a turning preview of the own character as everyone sees it, and the switch that hides every outfit; on the right, in a
/// scroll container, one row per outfit slot with a button per owned outfit that has something for it, and "My gear". The chosen one is framed and marked in words, so nothing depends on
/// telling colours apart. It only sends <c>set_outfit</c> through <see cref="ClientSession"/>: what shows is what the server sent back, and the preview draws the own character's own view
/// record, the one others get. It redraws only when the wardrobe or that record changes; the preview is the one thing that moves, and only while the panel is open.
/// </summary>
public sealed class WardrobePanel
{
    private const double PanelMaxWidthDp = 760;
    private const double PreviewWidthDp = 240;
    private const double IconDp = 28;

    private readonly ClientSession _session;
    private readonly DisplayBundle _bundle;
    private readonly LookView? _preview;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly CenterContainer _center = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = Wrapped(UiText.WardrobeTitle);
    private readonly Button _back = new() { Text = UiText.BackToBag };
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly HBoxContainer _columns = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _left = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Button _hide = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly ScrollContainer _scroll = new()
    {
        HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };
    private readonly VBoxContainer _content = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _note = Wrapped(string.Empty);
    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, Texture2D?> _icons = new(StringComparer.Ordinal);
    private Func<string, Texture2D?>? _iconLoader;
    private int _shownVersion = -1;
    private string? _shownGender;
    private bool _samples;
    private float _target = 48;
    private float _dp = 1;
    private float _optionMax = 300;
    private int _gap = 6;
    private Vector2 _safeSize;

    /// <summary>Builds the panel (closed) under <paramref name="parent"/>. Without <paramref name="preview"/> (no theme) it has no preview.</summary>
    public WardrobePanel(Control parent, ClientSession session, DisplayBundle bundle, LookView? preview, List<Control> interactive, Action tap)
    {
        _session = session;
        _bundle = bundle;
        _preview = preview;
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
        _box.AddChild(_columns);
        _columns.AddChild(_left);
        _columns.AddChild(_scroll);
        if (preview is not null)
        {
            preview.Frame.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _left.AddChild(preview.Frame);
            interactive.Add(preview.Frame);
        }
        _left.AddChild(_hide);
        _scroll.AddChild(_content);
        _content.AddChild(_note);
        _close.Pressed += () => { _tap(); SetOpen(false); };
        _back.Pressed += () => { _tap(); SetOpen(false); BackToBag?.Invoke(); };
        _hide.Pressed += () =>
        {
            _tap();
            if (!_samples) _session.HideOutfits(!_session.Wardrobe.Hidden);
        };
        foreach (var control in new Control[] { _close, _back, _hide, _panel, _scroll }) interactive.Add(control);
    }

    /// <summary>Raised by the Bag button, after the panel closed: the HUD opens the bag.</summary>
    public Action? BackToBag { get; set; }

    /// <summary>True while the panel is open.</summary>
    public bool Open => _center.Visible;

    /// <summary>The Close button (for the scripted run).</summary>
    public Button CloseButton => _close;

    /// <summary>The switch that hides every outfit (for the scripted run).</summary>
    public Button HideButton => _hide;

    /// <summary>The preview, or null (for the scripted run).</summary>
    public LookView? Preview => _preview;

    /// <summary>The line under the choices (for the scripted run).</summary>
    public string Note => _note.Text;

    /// <summary>How often the rows were drawn (for the scripted run: nothing is redrawn while nothing changes).</summary>
    public int Drawn { get; private set; }

    /// <summary>Finds an icon texture by asset key (set once the theme is loaded).</summary>
    public Func<string, Texture2D?>? IconLoader
    {
        get => _iconLoader;
        set
        {
            _iconLoader = value;
            _icons.Clear();
            _shownVersion = -1;
        }
    }

    /// <summary>The button for an outfit in a slot (null: "My gear"), or null when it is not there (for the scripted run).</summary>
    public Button? OptionButton(string slot, string? outfit)
    {
        foreach (var row in _rows)
            if (row.Shown && row.Slot == slot)
                foreach (var option in row.Options)
                    if (option.Button.Visible && option.Outfit == outfit) return option.Button;
        return null;
    }

    /// <summary>Opens or closes the panel. Closed, the preview is freed and renders nothing.</summary>
    public void SetOpen(bool open)
    {
        if (open == _center.Visible) return;
        _center.Visible = open;
        _shownVersion = -1;
        if (!open) _preview?.Clear();
        Refresh(0);
    }

    /// <summary>Lays the panel out for the HUD's safe area (canvas units); <paramref name="dp"/> converts dp to canvas units.</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize)
    {
        _target = target;
        _dp = dp(1);
        _gap = Mathf.RoundToInt(dp(6));
        var margin = dp(8);
        var width = Mathf.Min(dp(PanelMaxWidthDp), safeSize.X - 2 * margin);
        _panel.CustomMinimumSize = new Vector2(width, safeSize.Y - 2 * margin);
        _panelStyle.SetContentMarginAll(dp(8));
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        foreach (var box in new BoxContainer[] { _box, _columns, _left, _content }) box.AddThemeConstantOverride("separation", _gap);
        var inner = width - dp(16);
        var left = Mathf.Min(dp(PreviewWidthDp), inner * 0.36f);
        _left.CustomMinimumSize = new Vector2(left, 0);
        _hide.CustomMinimumSize = new Vector2(left, target);
        _optionMax = Mathf.Max(target, inner - left - _gap - dp(14)); // beside the scroll bar
        _note.CustomMinimumSize = new Vector2(_optionMax, 0);
        _title.CustomMinimumSize = new Vector2(Mathf.Max(0, inner - dp(96) * 2 - _gap * 2 - dp(8)), 0);
        _back.CustomMinimumSize = _close.CustomMinimumSize = new Vector2(dp(96), target);
        foreach (var row in _rows) Size(row);
        _safeSize = safeSize;
        _center.Position = Vector2.Zero;
        _center.Size = safeSize;
        _shownVersion = -1;
    }

    /// <summary>Once a frame: redraws what changed and turns the preview. Nothing while closed.</summary>
    public void Refresh(double dt)
    {
        if (!_center.Visible) return;
        if (_center.Size != _safeSize) _center.Size = _safeSize;
        var own = _session.World.Own;
        if (_preview is not null && own?.Appearance is { } looks)
        {
            // What others see, from the record they get: the same ids, gear and outfits, dressed by the same code.
            _preview.Dress(own.Weapon, own.Worn, own.Outfit, own.LookVersion);
            _preview.Show(own.DisplayKey, looks);
        }
        _preview?.Update(dt);
        var book = _session.Wardrobe;
        var gender = own?.Appearance?.Gender;
        if (_samples || (_shownVersion == book.Version && _shownGender == gender)) return;
        _shownVersion = book.Version;
        _shownGender = gender;
        Draw(book.Rows(gender), book.Hidden);
    }

    /// <summary>Fills the panel with the longest words it can show (and opens it), so a layout check sees the worst case; <see cref="ClearSamples"/> ends it.</summary>
    public void ShowSamples(string longName)
    {
        _samples = true;
        _center.Visible = true;
        var outfits = new List<ClientOutfit>();
        for (var i = 0; i < 4; i++) outfits.Add(new ClientOutfit("sample" + i, longName, null, string.Empty, Array.Empty<string>(), Array.Empty<ClientOutfitPiece>()));
        var rows = new List<WardrobeRow>();
        foreach (var slot in Game.Shared.Vocabulary.OutfitSlots.All.Ordered) rows.Add(new WardrobeRow(slot, outfits, outfits[0].Id));
        Draw(rows, true);
        _hide.Text = UiText.HideOutfits(true);
    }

    /// <summary>Ends <see cref="ShowSamples"/> and closes the panel.</summary>
    public void ClearSamples()
    {
        _samples = false;
        SetOpen(false);
    }

    private void Draw(IReadOnlyList<WardrobeRow> rows, bool hidden)
    {
        Drawn++;
        while (_rows.Count < rows.Count) AddRow();
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.Shown = i < rows.Count;
            row.Title.Visible = row.Flow.Visible = row.Shown;
            if (!row.Shown) continue;
            row.Slot = rows[i].Slot;
            row.Title.Text = UiText.OutfitSlot(row.Slot);
            while (row.Options.Count < rows[i].Outfits.Count + 1) AddOption(row);
            for (var o = 0; o < row.Options.Count; o++)
            {
                var option = row.Options[o];
                option.Button.Visible = o <= rows[i].Outfits.Count;
                if (!option.Button.Visible) continue;
                var outfit = o == 0 ? null : rows[i].Outfits[o - 1]; // the first is the gear
                option.Outfit = outfit?.Id;
                var name = outfit?.Name ?? UiText.OutfitNone;
                var mine = option.Outfit == rows[i].Chosen;
                option.Button.Text = mine ? UiText.Chosen(name) : name;
                option.Button.SetPressedNoSignal(mine);
                option.Button.TooltipText = outfit?.Description ?? string.Empty;
                option.Button.Icon = outfit is null ? null : Icon(outfit.IconAssetKey);
                Size(option);
            }
        }
        _hide.Text = UiText.HideOutfits(hidden);
        _hide.Disabled = rows.Count == 0;
        _note.Text = rows.Count == 0 ? UiText.WardrobeEmpty : hidden ? UiText.WardrobeHidden : UiText.WardrobeHint;
        _content.MoveChild(_note, -1);
    }

    private Texture2D? Icon(string key)
    {
        if (key.Length == 0) return null;
        if (!_icons.TryGetValue(key, out var icon)) _icons[key] = icon = _iconLoader?.Invoke(key);
        return icon;
    }

    private void AddRow()
    {
        var row = new Row();
        _content.AddChild(row.Title);
        _content.AddChild(row.Flow);
        _rows.Add(row);
        Size(row);
    }

    private void AddOption(Row row)
    {
        var option = new Option();
        option.Button.Pressed += () =>
        {
            _tap();
            if (!_samples) _session.SetOutfit(row.Slot, option.Outfit);
            _shownVersion = -1; // the toggles follow the server, also when it refuses
        };
        row.Flow.AddChild(option.Button);
        row.Options.Add(option);
        _interactive.Add(option.Button);
        Size(option);
    }

    private void Size(Row row)
    {
        row.Flow.AddThemeConstantOverride("h_separation", _gap);
        row.Flow.AddThemeConstantOverride("v_separation", _gap);
        foreach (var option in row.Options) Size(option);
    }

    /// <summary>As wide as its name and icon, so options stand side by side, but never wider than the column: a longer name wraps.</summary>
    private void Size(Option option)
    {
        var button = option.Button;
        var icon = Mathf.RoundToInt(_dp * (float)IconDp);
        button.AddThemeConstantOverride("icon_max_width", icon);
        foreach (var (state, shade, border) in new[] { ("normal", 0.16f, 0f), ("hover", 0.2f, 0f), ("pressed", 0.3f, 2f), ("hover_pressed", 0.3f, 2f), ("disabled", 0.12f, 0f) })
        {
            var box = new StyleBoxFlat { BgColor = new Color(shade, shade, shade * 1.1f, 0.95f), BorderColor = new Color(1f, 0.85f, 0.4f) };
            box.SetBorderWidthAll(Mathf.RoundToInt(border * Mathf.Max(1, _dp)));
            box.SetCornerRadiusAll(Mathf.RoundToInt(_dp * 4));
            box.ContentMarginLeft = box.ContentMarginRight = _dp * 9;
            box.ContentMarginTop = box.ContentMarginBottom = _dp * 3;
            button.AddThemeStyleboxOverride(state, box);
        }
        var font = button.GetThemeFont("font");
        var text = font.GetStringSize(button.Text, HorizontalAlignment.Left, -1, button.GetThemeFontSize("font_size")).X;
        var wanted = Mathf.Ceil(text + _dp * 18 + (button.Icon is null ? 0 : icon + button.GetThemeConstant("h_separation"))) + 2;
        button.CustomMinimumSize = new Vector2(Mathf.Clamp(wanted, _target, Mathf.Max(_target, _optionMax)), _target);
    }

    private static Label Wrapped(string text) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private sealed class Row
    {
        public readonly Label Title = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        public readonly HFlowContainer Flow = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        public readonly List<Option> Options = new();
        public string Slot = string.Empty;
        public bool Shown;
    }

    private sealed class Option
    {
        public readonly Button Button = new() { ToggleMode = true, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        public string? Outfit;
    }
}
