using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Client.Core.Party;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The party on the phone HUD (T-902, ARCHITECTURE §7.4 and §6.9). Three parts:
/// <list type="bullet">
/// <item><b>The panel</b>, opened from the chat panel's Party button (the HUD's columns have no room for another button, as the wardrobe opens from the bag): a heading with Close, the
/// members in party order (the leader's mark, the name, where it is, HP and SP bars; Lead and Kick beside the others for the leader) in a scroll list, then a name field with Invite, the
/// invite of a player tapped in the world beside it; Leave sits in the heading. At the top of the safe area.</item>
/// <item><b>The invite prompt</b>: who asks and the seconds left, Join and No, at the top of the safe area while an invite waits; it comes up by itself.</item>
/// <item><b>Marks at the screen edge</b> for members on this map who are off the screen: a short label with an arrow, pushed in from the edge toward the member. No minimap: the HUD has no
/// corner free on a 640x360 dp phone at the largest scale, a minimap would need the map drawn a second time (a texture and draw calls every frame), and an arrow says all a party needs to
/// find a member: which way. Members on other maps are listed in the panel with their map's name.</item>
/// </list>
/// Words come from <see cref="UiText"/>. It redraws only when the party's version moves; the marks cost a projection per member on this map each frame (at most four).
/// </summary>
public sealed class PartyPanel
{
    private const double PanelMaxWidthDp = 460;
    private const double BarWidthDp = 64;
    private const double MarkFontDp = 13;
    private const int MaxMarks = 8;

    private readonly ClientSession _session;
    private readonly DisplayBundle? _bundle;
    private readonly PartyBook _party;
    private readonly List<Control> _interactive;
    private readonly Action _tap;

    // the panel
    private readonly Control _area = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly HBoxContainer _head = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _list = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _empty = new() { Text = UiText.PartyNone, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly HBoxContainer _inviteRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly LineEdit _name = new() { PlaceholderText = UiText.PartyNameHint, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MaxLength = Game.Shared.Accounts.CharacterNameRules.LongestAllowed };
    private readonly Button _invite = new() { Text = UiText.PartyInvite };
    private readonly Button _inviteTapped = new() { Text = UiText.PartyInviteTapped, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly Button _leave = new() { Text = UiText.PartyLeave };
    private readonly Button _tradeTapped = new() { Text = UiText.TradeAskButton };   // T-903: ask the tapped player to trade
    private readonly List<Row> _rows = new();

    // the invite prompt
    private readonly PanelContainer _prompt = new() { MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
    private readonly StyleBoxFlat _promptStyle = new() { BgColor = new Color(0.12f, 0.14f, 0.2f, 0.96f) };
    private readonly HBoxContainer _promptRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _promptText = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _accept = new() { Text = UiText.PartyAccept };
    private readonly Button _decline = new() { Text = UiText.PartyDecline };

    // the marks
    private readonly Control _marks = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly List<Label> _markLabels = new();

    private int _shownVersion = -1;
    private int _promptSecond = -1;
    private Func<double, float> _dp = dp => (float)dp;
    private float _target = 48;
    private Vector2 _safeSize;
    private bool _samples;

    /// <summary>Builds the panel (closed), the prompt (hidden) and the marks into <paramref name="safe"/>.</summary>
    public PartyPanel(Control safe, ClientSession session, DisplayBundle? bundle, List<Control> interactive, Action tap)
    {
        _session = session;
        _bundle = bundle;
        _party = session.Party;
        _interactive = interactive;
        _tap = tap;

        safe.AddChild(_marks);
        safe.AddChild(_area);
        _area.AddChild(_panel);
        _panel.AddThemeStyleboxOverride("panel", _panelStyle);
        _panel.AddChild(_box);
        _head.AddChild(_title);
        _head.AddChild(_leave);   // in the heading row, and the tapped player's invite beside the name field: one row fewer, so the list keeps a row on the smallest phone at the largest scale
        _head.AddChild(_close);
        _box.AddChild(_head);
        _scroll.AddChild(_list);
        _box.AddChild(_scroll);
        _list.AddChild(_empty);
        _inviteRow.AddChild(_name);
        _inviteRow.AddChild(_invite);
        _inviteRow.AddChild(_inviteTapped);
        _inviteRow.AddChild(_tradeTapped);
        _box.AddChild(_inviteRow);

        safe.AddChild(_prompt);
        _prompt.AddThemeStyleboxOverride("panel", _promptStyle);
        _prompt.AddChild(_promptRow);
        _promptRow.AddChild(_promptText);
        _promptRow.AddChild(_accept);
        _promptRow.AddChild(_decline);

        for (var i = 0; i < MaxMarks; i++)
        {
            var mark = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, TextDirection = Control.TextDirection.Ltr };
            mark.AddThemeColorOverride("font_color", new Color(0.55f, 1f, 0.7f));
            mark.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0));
            mark.AddThemeConstantOverride("outline_size", 4);
            _marks.AddChild(mark);
            _markLabels.Add(mark);
        }

        _close.Pressed += () => { _tap(); SetOpen(false); };
        _invite.Pressed += () => { _tap(); InviteTyped(); };
        _name.TextSubmitted += _ => InviteTyped();
        _inviteTapped.Pressed += () =>
        {
            _tap();
            if (_party.Tapped != 0) _session.InviteToParty(_party.Tapped);
            _party.Tap(0);
        };
        _tradeTapped.Pressed += () =>
        {
            _tap();
            if (_party.Tapped != 0) _session.AskTrade(_party.Tapped);
            _party.Tap(0);
        };
        _leave.Pressed += () => { _tap(); _session.LeaveParty(); };
        _accept.Pressed += () => { _tap(); _session.AnswerParty(true); };
        _decline.Pressed += () => { _tap(); _session.AnswerParty(false); };
        foreach (var control in new Control[] { _panel, _close, _name, _invite, _inviteTapped, _tradeTapped, _leave, _prompt, _accept, _decline }) _interactive.Add(control);
    }

    /// <summary>True while the panel is open.</summary>
    public bool Open => _area.Visible;

    /// <summary>The name field (for the scripted run).</summary>
    public LineEdit NameField => _name;

    /// <summary>Invite beside the name field.</summary>
    public Button InviteButton => _invite;

    /// <summary>Close.</summary>
    public Button CloseButton => _close;

    /// <summary>Leave.</summary>
    public Button LeaveButton => _leave;

    /// <summary>Trade beside the tapped player's invite (T-903).</summary>
    public Button TradeTappedButton => _tradeTapped;

    /// <summary>Trade on the row of the member <paramref name="id"/> (T-903), or null.</summary>
    public Button? TradeButtonOf(long id) => _rows.FirstOrDefault(r => r.Root.Visible && r.Id == id)?.Trade;

    /// <summary>The invite prompt's Join.</summary>
    public Button AcceptButton => _accept;

    /// <summary>The invite prompt's No.</summary>
    public Button DeclineButton => _decline;

    /// <summary>True while the invite prompt shows.</summary>
    public bool PromptShown => _prompt.Visible;

    /// <summary>The members' lines as drawn (names and where; for the scripted run).</summary>
    public string Text => string.Join(" | ", _rows.Where(r => r.Root.Visible).Select(r => r.Name.Text + " " + r.Where.Text + " " + r.Hp.Value + "/" + r.Sp.Value));

    /// <summary>The marks shown now, as their texts (for the scripted run).</summary>
    public IReadOnlyList<string> Marks => _markLabels.Where(m => m.Visible).Select(m => m.Text).ToList();

    /// <summary>Opens or closes the panel.</summary>
    public void SetOpen(bool open)
    {
        if (open == _area.Visible) return;
        _area.Visible = open;
        if (!open) _name.ReleaseFocus();
        _shownVersion = -1;
        Refresh(ConnectionPhase.Online);
    }

    /// <summary>Lays the panel, the prompt and the marks out for the HUD's safe area (canvas units).</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize)
    {
        _dp = dp;
        _target = target;
        _safeSize = safeSize;
        var gap = Mathf.RoundToInt(dp(6));
        foreach (var style in new[] { _panelStyle, _promptStyle })
        {
            style.SetContentMarginAll(dp(8));
            style.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        }
        foreach (var box in new BoxContainer[] { _box, _head, _list, _inviteRow, _promptRow }) box.AddThemeConstantOverride("separation", gap);
        foreach (var button in new[] { _close, _invite, _leave, _accept, _decline, _tradeTapped }) button.CustomMinimumSize = new Vector2(dp(72), target);
        _inviteTapped.CustomMinimumSize = new Vector2(dp(96), target);
        _name.CustomMinimumSize = new Vector2(dp(96), target);
        foreach (var row in _rows) row.Apply(dp, target);
        var font = Mathf.Max(8, Mathf.RoundToInt(dp(MarkFontDp)));
        foreach (var mark in _markLabels) mark.AddThemeFontSizeOverride("font_size", font);
        _marks.Position = Vector2.Zero;
        _marks.Size = safeSize;
        Place();
        _shownVersion = -1;
        _promptSecond = -1;
        if (_samples) ShowSamples();   // the layout check: laid out again for this screen
    }

    /// <summary>Redraws from the party when it changed and keeps the prompt's seconds; hides everything off line.</summary>
    public void Refresh(ConnectionPhase phase)
    {
        var online = phase == ConnectionPhase.Online || _samples;
        if (!online && Open) SetOpen(false);
        var invite = _party.Invite;
        _prompt.Visible = online && (invite is not null || _samples);
        if (invite is not null && !_samples)
        {
            var left = (int)Math.Ceiling(invite.EndsAt - Clocks.Wall());
            if (left != _promptSecond)
            {
                _promptSecond = left;
                _promptText.Text = UiText.PartyInvited(invite.Name, left);
            }
        }
        if (_shownVersion == _party.Version || _samples) return;
        _shownVersion = _party.Version;
        Draw(_party.Members, _party.Leading, _party.InParty, _party.Tapped != 0);
    }

    /// <summary>
    /// Puts a mark at the screen's edge for each member on this map whose cell is off the screen (or behind the camera), pointing toward it. Called every frame after the camera
    /// moved; with nobody to mark it hides what it showed and returns.
    /// </summary>
    /// <param name="onMinimap">T-812: true for a member the corner minimap shows; it gets no edge mark (the minimap already says where it is).</param>
    public void UpdateMarks(Camera3D camera, string? mapId, Vector2 safeOrigin, Func<double, double, float> groundAt, Func<long, bool>? onMinimap = null)
    {
        var shown = 0;
        if (_party.InParty && mapId is not null && !_samples)
        {
            var rect = new Rect2(Vector2.Zero, _safeSize);
            var inset = _dp(24);
            var center = rect.GetCenter();
            foreach (var member in _party.Members)
            {
                if (shown >= MaxMarks) break;
                if (member.Mine || !member.Known || member.Away || member.Map != mapId || member.Cell is not { } cell) continue;
                if (onMinimap?.Invoke(member.Id) == true) continue;
                var world = new Vector3(cell.X + 0.5f, groundAt(cell.X + 0.5, cell.Y + 0.5) + 0.9f, cell.Y + 0.5f);
                var behind = camera.IsPositionBehind(world);
                var at = camera.UnprojectPosition(world) - safeOrigin;
                if (!behind && rect.Grow(-inset).HasPoint(at)) continue;   // on the screen: the player sees the character itself
                var direction = (behind ? center - at : at - center).Normalized();
                if (direction == Vector2.Zero) direction = Vector2.Up;
                var mark = _markLabels[shown++];
                mark.Text = UiText.PartyMark(Arrow(direction), member.Name);
                mark.ResetSize();
                var size = mark.GetCombinedMinimumSize();
                // Walk from the centre along the direction to the inset rectangle, then keep the label inside the safe area.
                var half = rect.Size / 2 - new Vector2(inset, inset);
                var scale = Mathf.Min(direction.X == 0 ? float.MaxValue : half.X / Mathf.Abs(direction.X), direction.Y == 0 ? float.MaxValue : half.Y / Mathf.Abs(direction.Y));
                var point = center + direction * scale;
                mark.Position = new Vector2(Mathf.Clamp(point.X - size.X / 2, 0, Mathf.Max(0, _safeSize.X - size.X)), Mathf.Clamp(point.Y - size.Y / 2, 0, Mathf.Max(0, _safeSize.Y - size.Y)));
                mark.Visible = true;
            }
        }
        for (var i = shown; i < _markLabels.Count; i++)
            if (_markLabels[i].Visible) _markLabels[i].Visible = false;
    }

    private static string Arrow(Vector2 d)
    {
        var angle = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(d.Y, d.X)) + 360, 360);   // 0 = right, 90 = down (screen)
        return ((int)Mathf.Round(angle / 45) % 8) switch
        {
            0 => "→", 1 => "↘", 2 => "↓", 3 => "↙", 4 => "←", 5 => "↖", 6 => "↑", _ => "↗",
        };
    }

    /// <summary>Fills the panel, the prompt and the marks with the longest words they show (the layout check): a full party of the longest names, the leader's buttons on every row.</summary>
    public void ShowSamples()
    {
        _samples = true;
        _area.Visible = true;
        var name = new string('W', Game.Shared.Accounts.CharacterNameRules.LongestAllowed);
        var members = new List<PartyMemberView>();
        for (var i = 0; i < 5; i++)
            members.Add(new PartyMemberView(i + 1, name, i == 0, i == 0, 100, 100, string.Empty, new Game.Shared.World.GridCell(1, 1), false, false, true));
        Draw(members, leading: true, inParty: true, tapped: true);
        _rows[1].Where.Text = UiText.PartyWhere(true, false, true, UiText.LongestMapNameSample, false);
        _prompt.Visible = true;
        _promptText.Text = UiText.PartyInvited(name, 30);
        for (var i = 0; i < 4; i++)
        {
            var mark = _markLabels[i];
            mark.Text = UiText.PartyMark(Arrow(new Vector2(1, -1)), name);
            mark.ResetSize();
            var size = mark.GetCombinedMinimumSize();
            mark.Position = new Vector2(i % 2 == 0 ? 0 : _safeSize.X - size.X, i < 2 ? _safeSize.Y / 3 : _safeSize.Y * 2 / 3);
            mark.Visible = true;
        }
        Place();
    }

    /// <summary>Ends <see cref="ShowSamples"/>.</summary>
    public void ClearSamples()
    {
        _samples = false;
        _area.Visible = false;
        _prompt.Visible = false;
        foreach (var mark in _markLabels) mark.Visible = false;
        _shownVersion = -1;
        _promptSecond = -1;
    }

    private void InviteTyped()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) return;
        _session.InviteToParty(name);
        _name.Clear();
    }

    private void Draw(IReadOnlyList<PartyMemberView> members, bool leading, bool inParty, bool tapped)
    {
        _title.Text = inParty ? UiText.PartyTitle(members.Count, 0) : UiText.Party;
        _empty.Visible = !inParty;
        while (_rows.Count < members.Count)
        {
            var row = new Row(_session, _tap, _interactive);
            row.Apply(_dp, _target);
            _list.AddChild(row.Root);
            _rows.Add(row);
        }
        var own = _session.World.MapId;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.Root.Visible = i < members.Count;
            if (i >= members.Count) continue;
            var m = members[i];
            row.Id = m.Id;
            row.Name.Text = UiText.PartyMemberName(m.Name, m.Leader, m.Mine);
            var mapName = m.Map is { } map && _bundle is not null && _bundle.TryGetMap(map, out var data) && data is not null ? data.Name : m.Map;
            row.Where.Text = m.Mine ? string.Empty : UiText.PartyWhere(m.Known, m.Away, m.Dead, mapName, m.Map == own);
            row.Where.Visible = !m.Mine;
            row.Bars.Visible = !m.Mine;   // one's own bars are the HUD's
            row.Hp.Value = m.Hp;
            row.Sp.Value = m.Sp;
            row.Lead.Visible = row.Kick.Visible = leading && !m.Mine;
            row.Trade.Visible = !m.Mine;   // T-903: a member may be asked to trade from here (the server checks it is near)
        }
        _leave.Visible = inParty;
        _inviteRow.Visible = !inParty || leading || tapped;
        _name.Visible = _invite.Visible = !inParty || leading;
        _inviteTapped.Visible = tapped && (!inParty || leading);
        _tradeTapped.Visible = tapped;
    }

    /// <summary>The panel centred in the safe area, as wide as fits, at most as tall as the safe area less margins.</summary>
    private void Place()
    {
        var margin = _dp(8);
        var width = Mathf.Min(_dp(PanelMaxWidthDp), _safeSize.X - 2 * margin);
        var height = Mathf.Max(2 * _target + 3 * _dp(6) + 2 * _dp(8) + _dp(40), _safeSize.Y - 2 * margin);
        _area.Position = new Vector2((_safeSize.X - width) / 2, margin);
        _area.Size = new Vector2(width, height);
        _panel.CustomMinimumSize = new Vector2(width, height);
        _panel.Position = Vector2.Zero;
        _panel.Size = new Vector2(width, height);
        var promptWidth = Mathf.Min(_dp(PanelMaxWidthDp), _safeSize.X - 2 * margin);
        _prompt.CustomMinimumSize = new Vector2(promptWidth, 0);
        _prompt.Position = new Vector2((_safeSize.X - promptWidth) / 2, margin);
        _prompt.Size = new Vector2(promptWidth, 0);
    }

    /// <summary>One member's row: name and where in a column, the bars, then the leader's Lead and Kick.</summary>
    private sealed class Row
    {
        public readonly HBoxContainer Root = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        public readonly VBoxContainer Text = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        public readonly Label Name = new() { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TextDirection = Control.TextDirection.Ltr };
        public readonly Label Where = new() { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        public readonly VBoxContainer Bars = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        public readonly ProgressBar Hp = Bar(new Color(0.35f, 0.8f, 0.4f));
        public readonly ProgressBar Sp = Bar(new Color(0.35f, 0.6f, 0.95f));
        public readonly Button Lead = new() { Text = UiText.PartyLead };
        public readonly Button Kick = new() { Text = UiText.PartyKick };
        public readonly Button Trade = new() { Text = UiText.TradeAskButton };
        public long Id;

        public Row(ClientSession session, Action tap, List<Control> interactive)
        {
            Text.AddChild(Name);
            Text.AddChild(Where);
            Root.AddChild(Text);
            Bars.AddChild(Hp);
            Bars.AddChild(Sp);
            Root.AddChild(Bars);
            Root.AddChild(Lead);
            Root.AddChild(Kick);
            Root.AddChild(Trade);
            Trade.Pressed += () => { tap(); session.AskTrade(Id); };
            interactive.Add(Trade);
            Lead.Pressed += () => { tap(); session.HandPartyLead(Id); };
            Kick.Pressed += () => { tap(); session.KickFromParty(Id); };
            interactive.Add(Lead);
            interactive.Add(Kick);
        }

        public void Apply(Func<double, float> dp, float target)
        {
            Root.AddThemeConstantOverride("separation", Mathf.RoundToInt(dp(6)));
            Text.AddThemeConstantOverride("separation", 0);
            Bars.AddThemeConstantOverride("separation", Mathf.RoundToInt(dp(3)));
            Name.CustomMinimumSize = new Vector2(dp(60), 0);
            foreach (var bar in new[] { Hp, Sp }) bar.CustomMinimumSize = new Vector2(dp(BarWidthDp), dp(7));
            foreach (var button in new[] { Lead, Kick, Trade }) button.CustomMinimumSize = new Vector2(dp(64), target);
        }

        private static ProgressBar Bar(Color fill)
        {
            var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = fill });
            bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f) });
            return bar;
        }
    }
}
