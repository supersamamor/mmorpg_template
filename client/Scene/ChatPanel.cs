using Game.Client.Core;
using Game.Client.Core.Chat;
using Game.Client.Core.Net;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Chat on the phone HUD (T-901, ARCHITECTURE §7.4 and "Chat"). Two parts:
/// <list type="bullet">
/// <item><b>The strip</b>, in the bottom row above the flash line: the Chat button and the last two lines beside it. The lines take no touches, so the world under them is still the
/// world's; the strip is one button tall and shows only while playing.</item>
/// <item><b>The panel</b>, opened by the button, at the <b>top</b> of the safe area: the input row first (the channel switch, a whisper target field, the text field and Send), then the log.
/// The on-screen keyboard (Godot's virtual keyboard on Android and iOS) comes up from the bottom when the text field takes focus, so the panel is cut to what is left above it, every frame
/// the keyboard's height changes: the field being typed in is never under the keyboard.</item>
/// </list>
/// The log is one <see cref="RichTextLabel"/> without BBCode: names (the server's, in a colour per channel) and texts go in with <c>AddText</c>, which parses nothing, so a line cannot colour,
/// style or move the name, and the paragraph direction is fixed left to right with the name first. A tap on a name selects that line: Whisper, Report and Block (or Unblock) for it.
/// Words come from <see cref="UiText"/>. It redraws only when the chat's version moves.
/// </summary>
public sealed class ChatPanel
{
    private const double PanelMaxWidthDp = 680;
    private const double StripLogWidthDp = 300;
    private const double LogFontDp = 13;
    private const int StripLines = 2;

    private readonly ClientSession _session;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly ChatBook _chat;

    // the strip
    private readonly HBoxContainer _strip = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly Button _open = new() { Text = UiText.Chat };
    private readonly RichTextLabel _recent = Log();

    // the panel
    private readonly Control _area = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly HBoxContainer _inputRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Button _channel = new();
    private readonly LineEdit _to = new() { PlaceholderText = UiText.ChatWhisperHint, Visible = false };
    private readonly LineEdit _text = new() { PlaceholderText = UiText.ChatHint, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _send = new() { Text = UiText.ChatSend };
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly HBoxContainer _actions = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly Label _selected = new() { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _whisper = new() { Text = UiText.ChatReply };
    private readonly Button _report = new() { Text = UiText.ChatReport };
    private readonly Button _block = new() { Text = UiText.ChatBlock };
    private readonly RichTextLabel _log = Log();

    private int _shownVersion = -1;
    private ChatEntry? _selectedEntry;
    private Func<double, float> _dp = dp => (float)dp;
    private float _target = 48;
    private Vector2 _safeSize;
    private float _canvasPerPixel = 1;
    private float _keyboard = -1;   // the keyboard's height in canvas units last laid out for
    private bool _samples;

    /// <summary>Builds the strip into <paramref name="bottom"/> (above the flash line) and the panel (closed) into <paramref name="safe"/>.</summary>
    public ChatPanel(Control safe, VBoxContainer bottom, ClientSession session, List<Control> interactive, Action tap)
    {
        _session = session;
        _chat = session.Chat;
        _interactive = interactive;
        _tap = tap;

        _strip.AddChild(_open);
        _strip.AddChild(_recent);
        _recent.MouseFilter = Control.MouseFilterEnum.Ignore;   // the world's taps go through the lines
        _recent.ScrollFollowing = true;
        bottom.AddChild(_strip);
        bottom.MoveChild(_strip, 0);
        _open.Pressed += () => { _tap(); SetOpen(!Open); };

        safe.AddChild(_area);
        _area.AddChild(_panel);
        _panel.AddChild(_box);
        _panel.AddThemeStyleboxOverride("panel", _panelStyle);
        _inputRow.AddChild(_channel);
        _inputRow.AddChild(_to);
        _inputRow.AddChild(_text);
        _inputRow.AddChild(_send);
        _inputRow.AddChild(_close);
        _box.AddChild(_inputRow);
        _actions.AddChild(_selected);
        _actions.AddChild(_whisper);
        _actions.AddChild(_report);
        _actions.AddChild(_block);
        _box.AddChild(_actions);
        _box.AddChild(_log);
        _log.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _log.ScrollFollowing = true;
        _log.MetaUnderlined = false;
        _log.MetaClicked += meta => Select(meta.AsInt32());

        _channel.Pressed += () =>
        {
            _tap();
            _chat.SetChannel(_chat.Channel == ChatChannels.Map ? ChatChannels.Whisper : ChatChannels.Map);
        };
        _to.TextChanged += name => _chat.SetWhisperTo(name);
        _text.TextSubmitted += _ => Send();
        _send.Pressed += () => { _tap(); Send(); };
        _close.Pressed += () => { _tap(); SetOpen(false); };
        _whisper.Pressed += () =>
        {
            _tap();
            if (_selectedEntry is { } e)
            {
                _to.Text = e.Name;
                _chat.SetWhisperTo(e.Name);
                _text.GrabFocus();
            }
            Select(-1);
        };
        _report.Pressed += () =>
        {
            _tap();
            if (_selectedEntry is { Line: > 0 } e) _session.Report(e.Line);
            Select(-1);
        };
        _block.Pressed += () =>
        {
            _tap();
            if (_selectedEntry is { } e)
            {
                if (_chat.IsBlocked(e.Name)) _session.Unblock(e.Name);
                else _session.Block(e.Name);
            }
            Select(-1);
        };
        foreach (var control in new Control[] { _open, _panel, _channel, _to, _text, _send, _close, _whisper, _report, _block, _log }) _interactive.Add(control);
    }

    /// <summary>True while the panel is open.</summary>
    public bool Open => _area.Visible;

    /// <summary>The Chat button, the text field, the whisper field, Send, the channel switch and the line actions (for the scripted run).</summary>
    public Button OpenButton => _open;

    /// <summary>The text field.</summary>
    public LineEdit TextField => _text;

    /// <summary>The whisper target field.</summary>
    public LineEdit WhisperField => _to;

    /// <summary>Send.</summary>
    public Button SendButton => _send;

    /// <summary>The channel switch.</summary>
    public Button ChannelButton => _channel;

    /// <summary>Report, for the selected line.</summary>
    public Button ReportButton => _report;

    /// <summary>Block or Unblock, for the selected line.</summary>
    public Button BlockButton => _block;

    /// <summary>The log as drawn (names and texts, one line each; for the scripted run).</summary>
    public string LogText => _log.GetParsedText();

    /// <summary>The strip's lines as drawn.</summary>
    public string StripText => _recent.GetParsedText();

    /// <summary>Where the panel and its parts are now (for the scripted run's layout line).</summary>
    public string Layout => $"panel {_panel.GetGlobalRect()}, input {_inputRow.GetGlobalRect()}, text {_text.GetGlobalRect()}, log {_log.GetGlobalRect()}, strip {_strip.GetGlobalRect()}";

    /// <summary>The panel's rectangle and the text field's (canvas units), for the layout check's keyboard rule.</summary>
    public (Rect2 Panel, Rect2 Field) Rects => (_panel.GetGlobalRect(), _text.GetGlobalRect());

    /// <summary>The keyboard height (canvas units) the layout check pretends is up; null: ask the engine.</summary>
    public float? SimulatedKeyboard { get; set; }

    /// <summary>Opens or closes the panel; opening marks the log read.</summary>
    public void SetOpen(bool open)
    {
        if (open == _area.Visible) return;
        _area.Visible = open;
        if (!open)
        {
            _text.ReleaseFocus();
            _to.ReleaseFocus();
            Select(-1);
        }
        else _chat.MarkRead();
        _shownVersion = -1;
        _keyboard = -1;
        Refresh(ConnectionPhase.Online);
    }

    /// <summary>Selects the line at <paramref name="index"/> of the log (its name was tapped), or none for -1: a player's line other than one's own shows its actions.</summary>
    public void Select(int index)
    {
        var lines = _chat.Lines;
        _selectedEntry = index >= 0 && index < lines.Count && lines[index] is { Mine: false, Line: > 0 } e ? e : null;
        _actions.Visible = _selectedEntry is not null;
        if (_selectedEntry is { } entry)
        {
            _selected.Text = UiText.ChatSpeaker(entry.Channel, entry.Name, entry.To);
            _block.Text = _chat.IsBlocked(entry.Name) ? UiText.ChatUnblock : UiText.ChatBlock;
        }
    }

    /// <summary>Selects the newest line said by <paramref name="name"/> (for the scripted run, as a tap on its name would).</summary>
    public bool SelectLast(Func<ChatEntry, bool> which)
    {
        var lines = _chat.Lines;
        for (var i = lines.Count - 1; i >= 0; i--)
            if (which(lines[i]))
            {
                Select(i);
                return _selectedEntry is not null;
            }
        return false;
    }

    /// <summary>Lays the strip and the panel out for the HUD's safe area (canvas units).</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize, float canvasPerPixel)
    {
        _dp = dp;
        _target = target;
        _safeSize = safeSize;
        _canvasPerPixel = canvasPerPixel;
        var font = Mathf.Max(8, Mathf.RoundToInt(dp(LogFontDp)));
        foreach (var log in new[] { _log, _recent })
        {
            log.AddThemeFontSizeOverride("normal_font_size", font);
            log.AddThemeConstantOverride("line_separation", 0);
        }
        _open.CustomMinimumSize = new Vector2(dp(104), target);
        _recent.CustomMinimumSize = new Vector2(Mathf.Min(dp(StripLogWidthDp), safeSize.X * 0.45f), target);
        _strip.AddThemeConstantOverride("separation", Mathf.RoundToInt(dp(6)));
        var gap = Mathf.RoundToInt(dp(6));
        _panelStyle.SetContentMarginAll(dp(8));
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        _box.AddThemeConstantOverride("separation", gap);
        _inputRow.AddThemeConstantOverride("separation", gap);
        _actions.AddThemeConstantOverride("separation", gap);
        _channel.CustomMinimumSize = new Vector2(dp(96), target);
        _to.CustomMinimumSize = new Vector2(dp(120), target);
        _text.CustomMinimumSize = new Vector2(dp(120), target);
        foreach (var button in new[] { _send, _close, _whisper, _report, _block }) button.CustomMinimumSize = new Vector2(dp(88), target);
        _keyboard = -1;
        _shownVersion = -1;
        Place();
    }

    /// <summary>Redraws from the chat when it changed, follows the keyboard, and shows the strip only while playing.</summary>
    public void Refresh(ConnectionPhase phase)
    {
        var online = phase == ConnectionPhase.Online;
        _strip.Visible = online || _samples;
        if (!online && !_samples && Open) SetOpen(false);
        if (Open) Place();
        if (_shownVersion == _chat.Version && !_samples) return;
        _shownVersion = _chat.Version;
        if (_samples) return;
        _channel.Text = UiText.ChatChannel(_chat.Channel);
        _to.Visible = _chat.Channel == ChatChannels.Whisper;
        if (_to.Text != _chat.WhisperTo && !_to.HasFocus()) _to.Text = _chat.WhisperTo;
        _text.MaxLength = _chat.MaxLength;   // characters; the server counts UTF-16 units and the client checks them before sending
        _open.Text = _chat.Unread && !Open ? UiText.ChatUnread : UiText.Chat;
        if (Open) _chat.MarkRead();
        Draw(_log, _chat.Lines, 0, meta: true);
        var lines = _chat.Lines;
        Draw(_recent, lines, Math.Max(0, lines.Count - StripLines), meta: false);
    }

    /// <summary>Fills the strip and the open panel with the longest lines they show (the layout check); <see cref="ClearSamples"/> ends it.</summary>
    public void ShowSamples(string longName)
    {
        _samples = true;
        _area.Visible = true;
        _strip.Visible = true;
        _channel.Text = UiText.ChatChannel(ChatChannels.Whisper);
        _to.Visible = true;
        _to.Text = longName;
        var text = new string('W', 40) + " " + new string('m', 40) + " " + new string('W', 39);
        var samples = new List<ChatEntry>();
        for (var i = 0; i < 6; i++) samples.Add(new ChatEntry(i + 1, ChatChannels.Whisper, 1, new string('W', Game.Shared.Accounts.CharacterNameRules.LongestAllowed), text,
            new string('W', Game.Shared.Accounts.CharacterNameRules.LongestAllowed), false));
        samples.Add(new ChatEntry(0, ChatChannels.System, 0, string.Empty, UiText.ChatRefused(ChatReasons.Muted, 86_400 * 3, null), null, false));
        Draw(_log, samples, 0, meta: true);
        Draw(_recent, samples, samples.Count - StripLines, meta: false);
        _selectedEntry = samples[0];
        _actions.Visible = true;
        _selected.Text = UiText.LongestChatSpeaker;
        _keyboard = -1;
        Place();
    }

    /// <summary>Ends <see cref="ShowSamples"/>.</summary>
    public void ClearSamples()
    {
        _samples = false;
        SimulatedKeyboard = null;
        SetOpen(false);
        Select(-1);
        _shownVersion = -1;
    }

    private void Send()
    {
        if (_session.Say(_text.Text)) _text.Clear();   // what was typed goes; the line shows when the server sends it back
    }

    /// <summary>The panel at the top of the safe area, as wide as fits, as tall as is left above the keyboard (at most two thirds of the screen).</summary>
    private void Place()
    {
        var keyboard = SimulatedKeyboard ?? DisplayServer.VirtualKeyboardGetHeight() * _canvasPerPixel;
        if (Mathf.IsEqualApprox(keyboard, _keyboard)) return;
        _keyboard = keyboard;
        var margin = _dp(8);
        var width = Mathf.Min(_dp(PanelMaxWidthDp), _safeSize.X - 2 * margin);
        var free = _safeSize.Y - keyboard - 2 * margin;
        var height = Mathf.Max(_target + 2 * _dp(8), Mathf.Min(_safeSize.Y * 2 / 3f, free));
        _area.Position = new Vector2((_safeSize.X - width) / 2, margin);
        _area.Size = new Vector2(width, height);
        _panel.Position = Vector2.Zero;
        _panel.Size = new Vector2(width, height);
        _panel.CustomMinimumSize = new Vector2(width, height);
        _log.CustomMinimumSize = new Vector2(0, Mathf.Max(0, height - 2 * _target - _dp(40)));
    }

    private static void Draw(RichTextLabel label, IReadOnlyList<ChatEntry> lines, int from, bool meta)
    {
        label.Clear();
        for (var i = from; i < lines.Count; i++)
        {
            var e = lines[i];
            if (i > from) label.Newline();
            if (meta && e.Line > 0) label.PushMeta(i);
            label.PushColor(ColorOf(e.Channel));
            label.AddText(UiText.ChatSpeaker(e.Channel, e.Name, e.To));   // AddText: nothing in a name or a line is read as markup
            label.Pop();
            if (meta && e.Line > 0) label.Pop();
            label.AddText(": ");
            label.AddText(e.Text);
        }
    }

    private static Color ColorOf(string channel) => channel switch
    {
        ChatChannels.Whisper => new Color(0.85f, 0.6f, 1f),
        ChatChannels.System => new Color(1f, 0.8f, 0.4f),
        _ => new Color(0.55f, 0.85f, 1f),
    };

    private static RichTextLabel Log() => new()
    {
        BbcodeEnabled = false,
        FitContent = false,
        ScrollActive = true,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        TextDirection = Control.TextDirection.Ltr,
        SelectionEnabled = false,
        ContextMenuEnabled = false,
        ShortcutKeysEnabled = false,
        MouseFilter = Control.MouseFilterEnum.Stop,
    };
}
