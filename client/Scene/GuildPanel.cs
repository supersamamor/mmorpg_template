using Game.Client.Core;
using Game.Client.Core.Guild;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The guild on the phone HUD (T-904, ARCHITECTURE §7.4 and §6.11). Two parts:
/// <list type="bullet">
/// <item><b>The panel</b>, opened from the chat panel's Guild button (beside Party): a heading with the guild's emblem, its name and who is online, Leave and Close; then a scroll list with the
/// notice (and, for a rank that may, a field to write it), and the members highest rank first (online dot, name, rank, level; Up, Down, Kick and Lead where this character's rank may act on
/// that member); then a name field with Invite, and the tapped player's invite. In no guild: a name field with Found, and the theme's emblems to pick from (bundle <c>guild_emblems</c>).</item>
/// <item><b>The invite prompt</b>: who asks, to which guild (its emblem), the seconds left, Join and No, at the top of the safe area while an invite waits.</item>
/// </list>
/// The client only hides what the rank cannot do; the server checks every act in its database. Words come from <see cref="UiText"/>. It redraws only when the guild's version moves.
/// </summary>
public sealed class GuildPanel
{
    private const double PanelMaxWidthDp = 460;
    private const double EmblemDp = 28;

    private readonly ClientSession _session;
    private readonly GuildBook _guild;
    private readonly IReadOnlyList<string> _emblems;
    private readonly List<Control> _interactive;
    private readonly Action _tap;

    private readonly Control _area = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly HBoxContainer _head = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly TextureRect _emblem = new() { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly Button _leave = new() { Text = UiText.GuildLeave };
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _list = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _empty = new() { Text = UiText.GuildNone, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly HBoxContainer _picker = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _pickerLabel = new() { Text = UiText.GuildEmblem };
    private readonly List<Button> _emblemButtons = new();
    private readonly Label _notice = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly HBoxContainer _noticeRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly LineEdit _noticeField = new() { PlaceholderText = UiText.GuildNoticeHint, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _noticeSave = new() { Text = UiText.GuildNoticeSave };
    private readonly HBoxContainer _bottom = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly LineEdit _name = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MaxLength = Game.Shared.Accounts.CharacterNameRules.LongestAllowed };
    private readonly Button _found = new() { Text = UiText.GuildFound };
    private readonly Button _invite = new() { Text = UiText.GuildInvite };
    private readonly Button _inviteTapped = new() { Text = UiText.PartyInviteTapped, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly List<Row> _rows = new();

    private readonly PanelContainer _prompt = new() { MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
    private readonly StyleBoxFlat _promptStyle = new() { BgColor = new Color(0.12f, 0.14f, 0.2f, 0.96f) };
    private readonly HBoxContainer _promptRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly TextureRect _promptEmblem = new() { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _promptText = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _accept = new() { Text = UiText.GuildAccept };
    private readonly Button _decline = new() { Text = UiText.GuildDecline };

    private string? _chosen;
    private int _shownVersion = -1;
    private int _promptSecond = -1;
    private Func<double, float> _dp = dp => (float)dp;
    private float _target = 48;
    private Vector2 _safeSize;
    private bool _samples;

    /// <summary>Builds the panel (closed) and the prompt (hidden) into <paramref name="safe"/>; <paramref name="emblems"/> are the bundle's.</summary>
    public GuildPanel(Control safe, ClientSession session, IReadOnlyList<string> emblems, List<Control> interactive, Action tap)
    {
        _session = session;
        _guild = session.Guild;
        _emblems = emblems;
        _interactive = interactive;
        _tap = tap;
        _chosen = emblems.Count > 0 ? emblems[0] : null;

        safe.AddChild(_area);
        _area.AddChild(_panel);
        _panel.AddThemeStyleboxOverride("panel", _panelStyle);
        _panel.AddChild(_box);
        _head.AddChild(_emblem);
        _head.AddChild(_title);
        _head.AddChild(_leave);
        _head.AddChild(_close);
        _box.AddChild(_head);
        _scroll.AddChild(_list);
        _box.AddChild(_scroll);
        _list.AddChild(_empty);
        _picker.AddChild(_pickerLabel);
        foreach (var key in emblems)
        {
            var button = new Button { ToggleMode = true, ExpandIcon = true, IconAlignment = HorizontalAlignment.Center, TooltipText = key };
            var chosen = key;
            button.Pressed += () => { _tap(); Choose(chosen); };
            _picker.AddChild(button);
            _emblemButtons.Add(button);
            interactive.Add(button);
        }
        _list.AddChild(_picker);
        _list.AddChild(_notice);
        _noticeRow.AddChild(_noticeField);
        _noticeRow.AddChild(_noticeSave);
        _list.AddChild(_noticeRow);
        _bottom.AddChild(_name);
        _bottom.AddChild(_found);
        _bottom.AddChild(_invite);
        _bottom.AddChild(_inviteTapped);
        _box.AddChild(_bottom);

        safe.AddChild(_prompt);
        _prompt.AddThemeStyleboxOverride("panel", _promptStyle);
        _prompt.AddChild(_promptRow);
        _promptRow.AddChild(_promptEmblem);
        _promptRow.AddChild(_promptText);
        _promptRow.AddChild(_accept);
        _promptRow.AddChild(_decline);

        _close.Pressed += () => { _tap(); SetOpen(false); };
        _leave.Pressed += () => { _tap(); _session.LeaveGuild(); };
        _found.Pressed += () => { _tap(); FoundTyped(); };
        _invite.Pressed += () => { _tap(); InviteTyped(); };
        _name.TextSubmitted += _ => { if (_guild.InGuild) InviteTyped(); else FoundTyped(); };
        _inviteTapped.Pressed += () =>
        {
            _tap();
            if (_session.Party.Tapped != 0) _session.InviteToGuild(_session.Party.Tapped);
            _session.Party.Tap(0);
        };
        _noticeSave.Pressed += () => { _tap(); _session.WriteGuildNotice(_noticeField.Text); _noticeField.ReleaseFocus(); };
        _accept.Pressed += () => { _tap(); _session.AnswerGuild(true); };
        _decline.Pressed += () => { _tap(); _session.AnswerGuild(false); };
        foreach (var control in new Control[] { _panel, _close, _leave, _name, _found, _invite, _inviteTapped, _noticeField, _noticeSave, _prompt, _accept, _decline }) _interactive.Add(control);
    }

    /// <summary>Finds an emblem's texture by asset key (set once the theme is loaded).</summary>
    public Func<string, Texture2D?>? IconLoader { get; set; }

    /// <summary>True while the panel is open.</summary>
    public bool Open => _area.Visible;

    /// <summary>The name field: the guild's name in none, else whom to invite (for the scripted run).</summary>
    public LineEdit NameField => _name;

    /// <summary>The notice field.</summary>
    public LineEdit NoticeField => _noticeField;

    /// <summary>Found.</summary>
    public Button FoundButton => _found;

    /// <summary>Invite.</summary>
    public Button InviteButton => _invite;

    /// <summary>Leave.</summary>
    public Button LeaveButton => _leave;

    /// <summary>Save beside the notice field.</summary>
    public Button NoticeSaveButton => _noticeSave;

    /// <summary>The invite prompt's Join.</summary>
    public Button AcceptButton => _accept;

    /// <summary>True while the invite prompt shows.</summary>
    public bool PromptShown => _prompt.Visible;

    /// <summary>The emblem buttons, in the bundle's order.</summary>
    public IReadOnlyList<Button> EmblemButtons => _emblemButtons;

    /// <summary>The emblem the founder picked.</summary>
    public string? ChosenEmblem => _chosen;

    /// <summary>The heading and the notice as drawn (for the scripted run).</summary>
    public string Text => _title.Text + " | " + _notice.Text + " | " + string.Join(" | ", _rows.Where(r => r.Root.Visible).Select(r => r.Line.Text));

    /// <summary>A member row's Up, or null when it does not show.</summary>
    public Button? PromoteButtonOf(string name) => RowOf(name)?.Promote is { Visible: true } b ? b : null;

    /// <summary>A member row's Kick, or null when it does not show.</summary>
    public Button? KickButtonOf(string name) => RowOf(name)?.Kick is { Visible: true } b ? b : null;

    private Row? RowOf(string name) => _rows.FirstOrDefault(r => r.Root.Visible && r.Name == name);

    /// <summary>Opens or closes the panel.</summary>
    public void SetOpen(bool open)
    {
        if (open == _area.Visible) return;
        _area.Visible = open;
        if (!open)
        {
            _name.ReleaseFocus();
            _noticeField.ReleaseFocus();
        }
        _shownVersion = -1;
        Refresh(ConnectionPhase.Online);
    }

    /// <summary>Lays the panel and the prompt out for the HUD's safe area (canvas units).</summary>
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
        foreach (var box in new BoxContainer[] { _box, _head, _list, _picker, _noticeRow, _bottom, _promptRow }) box.AddThemeConstantOverride("separation", gap);
        foreach (var button in new[] { _close, _leave, _found, _invite, _noticeSave, _accept, _decline }) button.CustomMinimumSize = new Vector2(dp(72), target);
        foreach (var button in _emblemButtons) button.CustomMinimumSize = new Vector2(target, target);
        _inviteTapped.CustomMinimumSize = new Vector2(dp(96), target);
        _name.CustomMinimumSize = new Vector2(dp(96), target);
        _noticeField.CustomMinimumSize = new Vector2(dp(96), target);
        _emblem.CustomMinimumSize = _promptEmblem.CustomMinimumSize = new Vector2(dp(EmblemDp), dp(EmblemDp));
        foreach (var row in _rows) row.Apply(dp, target);
        Place();
        _shownVersion = -1;
        _promptSecond = -1;
        if (_samples) ShowSamples();
    }

    /// <summary>Redraws from the guild when it changed and keeps the prompt's seconds; hides everything off line.</summary>
    public void Refresh(ConnectionPhase phase)
    {
        var online = phase == ConnectionPhase.Online || _samples;
        if (!online && Open) SetOpen(false);
        var invite = _guild.Invite;
        _prompt.Visible = online && (invite is not null || _samples);
        if (invite is not null && !_samples)
        {
            var left = (int)Math.Ceiling(invite.EndsAt - Clocks.Wall());
            if (left != _promptSecond)
            {
                _promptSecond = left;
                _promptText.Text = UiText.GuildInvited(invite.Name, invite.Title, left);
                _promptEmblem.Texture = Icon(invite.Emblem);
            }
        }
        if (_shownVersion == _guild.Version || _samples) return;
        _shownVersion = _guild.Version;
        Draw(_guild.State, _session.Party.Tapped != 0);
    }

    /// <summary>Fills the panel and the prompt with the longest words they show (the layout check): the longest names, a long notice, every button on every row.</summary>
    public void ShowSamples()
    {
        _samples = true;
        _area.Visible = true;
        var name = new string('W', Game.Shared.Accounts.CharacterNameRules.LongestAllowed);
        var rank = new string('W', 16);
        var ranks = new[] { new GuildRankRecord(rank, []), new GuildRankRecord(rank, []), new GuildRankRecord(rank, []), new GuildRankRecord(rank, []) };
        var members = new List<GuildMemberRecord> { new(1, name, 0, 99, true) };
        for (var i = 2; i <= 8; i++) members.Add(new GuildMemberRecord(i, name, 2, 99, i % 2 == 0));
        Draw(new GuildMessage(1, UiText.LongestGuildNameSample, _emblems.Count > 0 ? _emblems[0] : "", new string('W', 120), 1, ranks, members), tapped: true, leaderView: true);
        _prompt.Visible = true;
        _promptText.Text = UiText.GuildInvited(name, UiText.LongestGuildNameSample, 30);
        Place();
    }

    /// <summary>Ends <see cref="ShowSamples"/>.</summary>
    public void ClearSamples()
    {
        _samples = false;
        _area.Visible = false;
        _prompt.Visible = false;
        _shownVersion = -1;
        _promptSecond = -1;
    }

    /// <summary>The founding form as the layout check sees it: no guild, the emblem picker.</summary>
    public void ShowFoundingSample()
    {
        _samples = true;
        _area.Visible = true;
        Draw(GuildMessage.None, tapped: false);
        _prompt.Visible = false;
        Place();
    }

    private void Choose(string emblem)
    {
        _chosen = emblem;
        for (var i = 0; i < _emblemButtons.Count; i++) _emblemButtons[i].ButtonPressed = _emblems[i] == emblem;
    }

    private void FoundTyped()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0 || _chosen is null) return;
        _session.FoundGuild(name, _chosen);
        _name.Clear();
    }

    private void InviteTyped()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) return;
        _session.InviteToGuild(name);
        _name.Clear();
    }

    private Texture2D? Icon(string key) => key.Length == 0 ? null : IconLoader?.Invoke(key);

    private void Draw(GuildMessage state, bool tapped, bool leaderView = false)
    {
        var inGuild = state.Guild is not null;
        _emblem.Visible = inGuild;
        _emblem.Texture = inGuild ? Icon(state.Emblem) : null;
        _title.Text = inGuild ? UiText.GuildTitle(state.Name, state.Members.Count, state.Members.Count(m => m.Online)) : UiText.Guild;
        _leave.Visible = inGuild;
        _empty.Visible = _picker.Visible = !inGuild;
        _found.Visible = !inGuild;
        _name.PlaceholderText = inGuild ? UiText.GuildInviteHint : UiText.GuildNameHint;
        for (var i = 0; i < _emblemButtons.Count; i++)
        {
            _emblemButtons[i].Icon = Icon(_emblems[i]);
            _emblemButtons[i].ButtonPressed = _emblems[i] == _chosen;
        }
        _notice.Visible = inGuild && state.Notice.Length > 0;
        _notice.Text = state.Notice;
        var mayNotice = leaderView || _guild.May(GuildPermissions.EditNotice);
        _noticeRow.Visible = inGuild && mayNotice;
        if (_noticeRow.Visible && !_noticeField.HasFocus()) _noticeField.Text = state.Notice;
        var mayInvite = leaderView || _guild.May(GuildPermissions.Invite);
        _invite.Visible = inGuild && mayInvite;
        _inviteTapped.Visible = inGuild && mayInvite && tapped;
        _name.Visible = !inGuild || mayInvite;
        _bottom.Visible = _name.Visible || _inviteTapped.Visible;

        var members = state.Members;
        while (_rows.Count < members.Count)
        {
            var row = new Row(_session, _tap, _interactive);
            row.Apply(_dp, _target);
            _list.AddChild(row.Root);
            _rows.Add(row);
        }
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.Root.Visible = i < members.Count;
            if (i >= members.Count) continue;
            var m = members[i];
            var mine = m.Member == state.Own;
            row.Member = m.Member;
            row.Rank = m.Rank;
            row.Name = m.Name;
            var rankName = m.Rank >= 0 && m.Rank < state.Ranks.Count ? state.Ranks[m.Rank].Name : string.Empty;
            row.Line.Text = UiText.GuildMemberLine(m.Name, rankName, m.Level, m.Online, mine);
            var setRank = leaderView ? !mine : _guild.MayActOn(m, GuildPermissions.SetRank);
            row.Promote.Visible = setRank && (leaderView || m.Rank - 1 > _guild.OwnRank) && m.Rank > 1;
            row.Demote.Visible = setRank && m.Rank + 1 < state.Ranks.Count;
            row.Kick.Visible = leaderView ? !mine : _guild.MayActOn(m, GuildPermissions.Kick);
            row.Lead.Visible = (leaderView || _guild.Leading) && !mine;
        }
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
        _prompt.CustomMinimumSize = new Vector2(width, 0);
        _prompt.Position = new Vector2((_safeSize.X - width) / 2, margin);
        _prompt.Size = new Vector2(width, 0);
    }

    /// <summary>One member's row: its line, then Up, Down, Kick and Lead where this character's rank may act on it.</summary>
    private sealed class Row
    {
        public readonly HBoxContainer Root = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        public readonly Label Line = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        public readonly Button Promote = new() { Text = UiText.GuildPromote };
        public readonly Button Demote = new() { Text = UiText.GuildDemote };
        public readonly Button Kick = new() { Text = UiText.GuildKick };
        public readonly Button Lead = new() { Text = UiText.GuildLead };
        public long Member;
        public int Rank;
        public string Name = string.Empty;

        public Row(ClientSession session, Action tap, List<Control> interactive)
        {
            Root.AddChild(Line);
            Root.AddChild(Promote);
            Root.AddChild(Demote);
            Root.AddChild(Kick);
            Root.AddChild(Lead);
            Promote.Pressed += () => { tap(); session.SetGuildRank(Member, Rank - 1); };
            Demote.Pressed += () => { tap(); session.SetGuildRank(Member, Rank + 1); };
            Kick.Pressed += () => { tap(); session.KickFromGuild(Member); };
            Lead.Pressed += () => { tap(); session.HandGuildLead(Member); };
            foreach (var b in new[] { Promote, Demote, Kick, Lead }) interactive.Add(b);
        }

        public void Apply(Func<double, float> dp, float target)
        {
            Root.AddThemeConstantOverride("separation", Mathf.RoundToInt(dp(6)));
            Line.CustomMinimumSize = new Vector2(dp(80), 0);
            foreach (var button in new[] { Promote, Demote, Kick, Lead }) button.CustomMinimumSize = new Vector2(Mathf.Max(target, dp(56)), target);   // never under the touch minimum at a small UI scale
        }
    }
}
