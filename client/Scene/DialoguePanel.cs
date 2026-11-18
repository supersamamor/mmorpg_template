using System.Text;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The dialogue panel (T-601, ARCHITECTURE §7.4): the speaker's portrait, name and title, the line, and the choices as touch targets of at least the touch target
/// size, with a Close button. It sits at the bottom of the HUD's safe area so the world stays in view. It draws only what the server sent
/// (<see cref="ClientSession.Dialogue"/>) and sends only <c>dialogue_choice</c> and <c>dialogue_close</c>: where a choice leads is the server's, and the panel
/// closes when the server ends the dialogue. A portrait the theme does not have becomes the speaker's first letter (§7.7). Code-built controls sized in dp times
/// the UI scale like the other panels; it redraws only when the dialogue's <c>Version</c> moves and does nothing while closed.
/// </summary>
public sealed class DialoguePanel
{
    private const double PanelMaxWidthDp = 680;
    private const double PanelHeightDp = 280;
    private const double PortraitDp = 64;

    private readonly ClientSession _session;
    private readonly DisplayBundle _bundle;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly Control _holder = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly PanelContainer _portraitFrame = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly StyleBoxFlat _portraitStyle = new() { BgColor = new Color(0.22f, 0.25f, 0.3f) };
    private readonly TextureRect _portrait = new() { MouseFilter = Control.MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
    private readonly Label _letter = new() { MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Label _name = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0.65f) };
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _content = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _text = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly List<Button> _choices = new();
    private readonly List<int> _indices = new();
    private readonly StringBuilder _drawn = new();
    private int _shownVersion = -1;
    private float _target = 48;
    private float _width;
    private Vector2 _safeSize;
    private Func<string, Texture2D?>? _iconLoader;

    /// <summary>Builds the panel (closed) under <paramref name="parent"/>; every button it makes goes into <paramref name="interactive"/>.</summary>
    public DialoguePanel(Control parent, ClientSession session, DisplayBundle bundle, List<Control> interactive, Action tap)
    {
        _session = session;
        _bundle = bundle;
        _interactive = interactive;
        _tap = tap;
        parent.AddChild(_holder);
        _holder.AddChild(_panel);
        _panel.AddChild(_box);
        _panel.AddThemeStyleboxOverride("panel", _panelStyle);
        _portraitFrame.AddThemeStyleboxOverride("panel", _portraitStyle);
        _portraitFrame.AddChild(_portrait);
        _portraitFrame.AddChild(_letter);

        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var names = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        names.AddChild(_name);
        names.AddChild(_title);
        header.AddChild(_portraitFrame);
        header.AddChild(names);
        header.AddChild(_close);
        _box.AddChild(header);
        _box.AddChild(_scroll);
        _scroll.AddChild(_content);
        _content.AddChild(_text);
        _close.Pressed += () => { _tap(); _session.CloseDialogue(); };
        _interactive.Add(_close);
        _interactive.Add(_panel);
        _interactive.Add(_scroll);
    }

    /// <summary>Finds a portrait texture by asset key (set once the theme is loaded).</summary>
    public Func<string, Texture2D?>? IconLoader
    {
        get => _iconLoader;
        set
        {
            _iconLoader = value;
            _shownVersion = -1;
        }
    }

    /// <summary>The Close button (for the scripted run).</summary>
    public Button CloseButton => _close;

    /// <summary>True while the panel is open.</summary>
    public bool Open => _holder.Visible;

    /// <summary>The choice buttons now shown, with the index each sends (for the scripted run); at a node without choices, the one Continue / Goodbye button with index 0.</summary>
    public IReadOnlyList<(Button Button, int Index)> ChoiceButtons
    {
        get
        {
            var list = new List<(Button, int)>();
            for (var i = 0; i < _indices.Count; i++) list.Add((_choices[i], _indices[i]));
            return list;
        }
    }

    /// <summary>True when the portrait shows a theme texture, false for the letter fallback.</summary>
    public bool ShowsPortrait => _portrait.Texture is not null;

    /// <summary>The speaker line, the text and the choices as drawn, separated by " | " (for the scripted run).</summary>
    public string Text
    {
        get
        {
            _drawn.Clear().Append(_name.Text).Append(" | ").Append(_title.Text).Append(" | ").Append(_text.Text);
            for (var i = 0; i < _indices.Count; i++) _drawn.Append(" | ").Append(_indices[i]).Append(": ").Append(_choices[i].Text);
            return _drawn.ToString();
        }
    }

    /// <summary>Where the panel and its parts are on screen now (for the scripted run's layout line).</summary>
    public string Layout => $"panel {_panel.GetGlobalRect()}, scroll {_scroll.GetGlobalRect()}, portrait {_portraitFrame.GetGlobalRect()}";

    /// <summary>Lays the panel out for the HUD's safe area (canvas units); <paramref name="dp"/> converts dp to canvas units.</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize, int fontSize)
    {
        _target = target;
        var margin = dp(8);
        _width = Mathf.Min(dp(PanelMaxWidthDp), safeSize.X - 2 * margin);
        var height = Mathf.Min(dp(PanelHeightDp), safeSize.Y - 2 * margin);
        _panel.CustomMinimumSize = new Vector2(_width, height);
        _panel.Size = new Vector2(_width, height);
        _panel.Position = new Vector2((safeSize.X - _width) / 2, safeSize.Y - height - margin);
        var gap = Mathf.RoundToInt(dp(6));
        _panelStyle.SetContentMarginAll(dp(8));
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        _portraitStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        _box.AddThemeConstantOverride("separation", gap);
        _content.AddThemeConstantOverride("separation", gap);
        _portraitFrame.CustomMinimumSize = new Vector2(dp(PortraitDp), dp(PortraitDp));
        _letter.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(fontSize * 2f));
        _name.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(fontSize * 1.2f));
        _close.CustomMinimumSize = new Vector2(dp(96), target);
        _text.CustomMinimumSize = new Vector2(_width - dp(28), 0);
        foreach (var button in _choices) button.CustomMinimumSize = new Vector2(0, target);
        _safeSize = safeSize;
        _holder.Position = Vector2.Zero;
        _holder.Size = safeSize;
        _shownVersion = -1;
    }

    /// <summary>Redraws from the dialogue view when it changed. Nothing while closed.</summary>
    public void Refresh()
    {
        var view = _session.Dialogue;
        if (_holder.Visible != view.Open)
        {
            _holder.Visible = view.Open;
            _shownVersion = -1;
        }
        if (!view.Open) return;
        if (_holder.Size != _safeSize) _holder.Size = _safeSize;
        if (_shownVersion == view.Version) return;
        _shownVersion = view.Version;

        var npc = _bundle.Npc(view.Speaker);
        var name = npc?.Name ?? UiText.DialogueUnknownSpeaker;
        _name.Text = name;
        _title.Text = npc?.Title ?? string.Empty;
        _title.Visible = _title.Text.Length > 0;
        _text.Text = view.Text;

        // The portrait: the node's key, else the speaker's; no theme texture means the speaker's first letter (§7.7).
        var key = view.Portrait ?? npc?.Portrait;
        var texture = key is not null ? _iconLoader?.Invoke(key) : null;
        _portrait.Texture = texture;
        _portrait.Visible = texture is not null;
        _letter.Text = UiText.StatusLetter(name);
        _letter.Visible = texture is null;

        _indices.Clear();
        var shown = view.Choices.Count > 0 ? view.Choices.Count : 1;
        for (var i = 0; i < shown; i++)
        {
            var button = ChoiceButton(i);
            if (view.Choices.Count > 0)
            {
                button.Text = view.Choices[i].Text;
                _indices.Add(view.Choices[i].Index);
            }
            else
            {
                button.Text = view.More ? UiText.DialogueContinue : UiText.DialogueDone;   // the server ends it on index 0 when there is no next node
                _indices.Add(0);
            }
            button.Visible = true;
        }
        for (var i = shown; i < _choices.Count; i++) _choices[i].Visible = false;
        _scroll.ScrollVertical = 0;
    }

    private Button ChoiceButton(int index)
    {
        while (_choices.Count <= index)
        {
            var at = _choices.Count;
            var button = new Button { Alignment = HorizontalAlignment.Left, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            button.CustomMinimumSize = new Vector2(0, _target);
            button.Pressed += () => OnChoice(at);
            _choices.Add(button);
            _content.AddChild(button);
            _interactive.Add(button);
        }
        return _choices[index];
    }

    private void OnChoice(int at)
    {
        _tap();
        if (at >= 0 && at < _indices.Count) _session.ChooseDialogue(_indices[at]);   // the server decides where it leads; the next node (or the end) comes back
    }
}
