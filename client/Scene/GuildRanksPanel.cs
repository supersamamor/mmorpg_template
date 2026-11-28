using Game.Client.Core;
using Game.Client.Core.Guild;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The leader's rank editor on the phone HUD (T-810, ARCHITECTURE §7.4 and §6.11), opened from the guild panel's Ranks button: a heading (Add, Save, Close), the server's answer to Save in
/// words, and a scroll list: what happens to members, then per rank below the leader's its name, Up, Down and Remove, the members its place would hold, and a box per "Guild permissions"
/// term. Nothing is sent before Save, which sends the whole list as one <c>guild_ranks</c>; the server checks the leader, the count and every name. The draft (<see cref="RankDraft"/>) is
/// not touched by what arrives while editing; only the member counts follow, and a saved answer reads the ranks back as the server holds them. It redraws only when the guild changed.
/// </summary>
public sealed class GuildRanksPanel
{
    private const double PanelMaxWidthDp = 460;

    private readonly ClientSession _session;
    private readonly GuildBook _guild;
    private readonly List<Control> _interactive;
    private readonly Action _tap;

    private readonly Control _area = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly HBoxContainer _head = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = new() { Text = UiText.GuildRanks, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly Button _add = new() { Text = UiText.GuildRankAdd };
    private readonly Button _save = new() { Text = UiText.GuildRanksSave };
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _list = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _hint = new() { Text = UiText.GuildRanksHint, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly List<Row> _rows = new();

    private RankDraft? _draft;
    private GuildMessage _shown = GuildMessage.None;
    private GuildRankAnswer? _answer;
    private int _shownVersion = -1;
    private Func<double, float> _dp = dp => (float)dp;
    private float _target = 48;
    private Vector2 _safeSize;
    private bool _samples;

    /// <summary>Builds the editor (closed) into <paramref name="safe"/>.</summary>
    public GuildRanksPanel(Control safe, ClientSession session, List<Control> interactive, Action tap)
    {
        _session = session;
        _guild = session.Guild;
        _interactive = interactive;
        _tap = tap;

        safe.AddChild(_area);
        _area.AddChild(_panel);
        _panel.AddThemeStyleboxOverride("panel", _panelStyle);
        _panel.AddChild(_box);
        foreach (var c in new Control[] { _title, _add, _save, _close }) _head.AddChild(c);
        _box.AddChild(_head);
        _box.AddChild(_status);
        _scroll.AddChild(_list);
        _box.AddChild(_scroll);
        _list.AddChild(_hint);

        _close.Pressed += () => { _tap(); SetOpen(false); };
        _add.Pressed += () => { _tap(); Edit(d => d.Add(UiText.GuildRankNew)); };
        _save.Pressed += () => { _tap(); Save(); };
        foreach (var control in new Control[] { _panel, _add, _save, _close }) _interactive.Add(control);
    }

    /// <summary>True while the editor is open.</summary>
    public bool Open => _area.Visible;

    /// <summary>Add.</summary>
    public Button AddButton => _add;

    /// <summary>Save.</summary>
    public Button SaveButton => _save;

    /// <summary>Close.</summary>
    public Button CloseButton => _close;

    /// <summary>The words under the heading (the server's answer to Save), or empty.</summary>
    public string Status => _status.Visible ? _status.Text : string.Empty;

    /// <summary>The ranks in the editor.</summary>
    public int Count => _draft?.Ranks.Count ?? 0;

    /// <summary>The name field of the rank at <paramref name="index"/> (0 is place 1).</summary>
    public LineEdit NameFieldOf(int index) => _rows[index].Name;

    /// <summary>The box of <paramref name="permission"/> on the rank at <paramref name="index"/>.</summary>
    public CheckBox BoxOf(int index, string permission) => _rows[index].Boxes[permission];

    /// <summary>Types <paramref name="name"/> into the rank at <paramref name="index"/>, as the field does (the scripted run).</summary>
    public void TypeName(int index, string name)
    {
        _rows[index].Name.Text = name;
        _rows[index].Name.EmitSignal(LineEdit.SignalName.TextChanged, name);
    }

    /// <summary>Opens the editor with the guild's ranks, or closes it (what was not saved is dropped).</summary>
    public void SetOpen(bool open)
    {
        if (open == _area.Visible) return;
        _area.Visible = open;
        foreach (var row in _rows) row.Name.ReleaseFocus();
        if (!open)
        {
            _draft = null;
            return;
        }
        _guild.AwaitRanks(sent: false);   // an old answer is not this window's
        _draft = RankDraft.From(_guild.State);
        _shown = _guild.State;
        _answer = null;
        _status.Visible = false;
        _shownVersion = _guild.Version;
        _scroll.ScrollVertical = 0;
        Draw();
    }

    /// <summary>Lays the editor out for the HUD's safe area (canvas units).</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize)
    {
        _dp = dp;
        _target = target;
        _safeSize = safeSize;
        var gap = Mathf.RoundToInt(dp(6));
        _panelStyle.SetContentMarginAll(dp(8));
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        foreach (var box in new BoxContainer[] { _box, _head, _list }) box.AddThemeConstantOverride("separation", gap);
        foreach (var button in new[] { _add, _save, _close }) button.CustomMinimumSize = new Vector2(Mathf.Max(target, dp(64)), target);
        foreach (var row in _rows) row.Apply(dp, target);
        Place();
    }

    /// <summary>Follows the guild: closes off line, out of the guild or no longer leading; shows the server's answer to Save and the member counts.</summary>
    public void Refresh(ConnectionPhase phase)
    {
        if (_samples || !Open) return;
        if (phase != ConnectionPhase.Online || !_guild.Leading)
        {
            SetOpen(false);
            return;
        }
        if (_shownVersion == _guild.Version) return;
        _shownVersion = _guild.Version;
        _shown = _guild.State;
        if (!ReferenceEquals(_answer, _guild.RankAnswer))
        {
            _answer = _guild.RankAnswer;
            if (_answer is { } answer)
            {
                if (answer.Saved) _draft = RankDraft.From(_shown);   // as the server holds them now (it cleans names)
                Say(answer.Words);
            }
        }
        Draw();
    }

    /// <summary>Fills the editor with the longest words it shows (the layout check): five ranks of the longest names with every box ticked, large counts and the longest refusal.</summary>
    public void ShowSamples()
    {
        _samples = true;
        _area.Visible = true;
        var name = new string('W', 16);
        var ranks = new List<GuildRankRecord> { new(name, []) };
        for (var i = 0; i < 5; i++) ranks.Add(new GuildRankRecord(name, GuildPermissions.All.ToList()));
        var members = new List<GuildMemberRecord>();
        for (var i = 1; i <= 30; i++) members.Add(new GuildMemberRecord(i, name, 5, 99, true));
        _shown = new GuildMessage(1, UiText.LongestGuildNameSample, "", "", 0, ranks, members);
        _draft = RankDraft.From(_shown);
        Say(UiText.GuildRefused(GuildReasons.InvalidRanks, null, null));
        Draw();
        Place();
    }

    /// <summary>Ends <see cref="ShowSamples"/>.</summary>
    public void ClearSamples()
    {
        _samples = false;
        _area.Visible = false;
        _draft = null;
        _shown = GuildMessage.None;
        _status.Visible = false;
    }

    private void Say(string words)
    {
        _status.Text = words;
        _status.Visible = words.Length > 0;
    }

    private void Edit(Action<RankDraft> change)
    {
        if (_draft is null) return;
        change(_draft);
        Say(string.Empty);
        Draw();
    }

    private void Save()
    {
        foreach (var row in _rows) row.Name.ReleaseFocus();
        if (_draft is null || _samples) return;
        _session.SetGuildRanks(_draft.ToRecords());
        _answer = null;
        Say(UiText.GuildRanksSaving);
    }

    private void Draw()
    {
        if (_draft is not { } draft) return;
        while (_rows.Count < draft.Ranks.Count)
        {
            var row = new Row(this, _rows.Count);
            row.Apply(_dp, _target);
            _list.AddChild(row.Root);
            _rows.Add(row);
        }
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.Root.Visible = i < draft.Ranks.Count;
            if (i >= draft.Ranks.Count) continue;
            var rank = draft.Ranks[i];
            if (row.Name.Text != rank.Name) row.Name.Text = rank.Name;   // only when it differs: setting the text moves the caret
            row.Up.Disabled = i == 0;
            row.Down.Disabled = i == draft.Ranks.Count - 1;
            row.Remove.Disabled = !draft.CanRemove;
            row.Count.Text = UiText.GuildRankMembers(i + 1, draft.MembersAt(_shown, i));
            foreach (var (permission, box) in row.Boxes) box.SetPressedNoSignal(rank.Has(permission));
        }
    }

    /// <summary>The editor centred in the safe area, as wide as fits, as tall as the safe area less margins.</summary>
    private void Place()
    {
        var margin = _dp(8);
        var width = Mathf.Min(_dp(PanelMaxWidthDp), _safeSize.X - 2 * margin);
        var height = Mathf.Max(0, _safeSize.Y - 2 * margin);
        _area.Position = new Vector2((_safeSize.X - width) / 2, margin);
        _area.Size = new Vector2(width, height);
        _panel.CustomMinimumSize = new Vector2(width, height);
        _panel.Position = Vector2.Zero;
        _panel.Size = new Vector2(width, height);
    }

    /// <summary>One rank: the name with Up, Down and Remove, the members its place would hold, then its permission boxes, wrapping to the width.</summary>
    private sealed class Row
    {
        public readonly VBoxContainer Root = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        public readonly HBoxContainer Top = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        public readonly LineEdit Name = new() { PlaceholderText = UiText.GuildRankNameHint, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        public readonly Button Up = new() { Text = UiText.GuildPromote };
        public readonly Button Down = new() { Text = UiText.GuildDemote };
        public readonly Button Remove = new() { Text = UiText.GuildRankRemove };
        public readonly Label Count = new() { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        public readonly HFlowContainer Flow = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        public readonly Dictionary<string, CheckBox> Boxes = new(StringComparer.Ordinal);

        public Row(GuildRanksPanel panel, int index)
        {
            foreach (var c in new Control[] { Name, Up, Down, Remove }) Top.AddChild(c);
            Root.AddChild(Top);
            Root.AddChild(Count);
            Root.AddChild(Flow);
            // A row stands for an index of the draft, whichever rank is there after a move.
            Name.TextChanged += text =>
            {
                if (panel._draft is { } d && index < d.Ranks.Count) d.Ranks[index].Name = text;
            };
            Up.Pressed += () => { panel._tap(); panel.Edit(d => d.Move(index, -1)); };
            Down.Pressed += () => { panel._tap(); panel.Edit(d => d.Move(index, 1)); };
            Remove.Pressed += () => { panel._tap(); Name.ReleaseFocus(); panel.Edit(d => d.Remove(index)); };
            foreach (var permission in GuildPermissions.All)
            {
                var box = new CheckBox { Text = UiText.GuildPermission(permission) };
                var term = permission;
                box.Toggled += on =>
                {
                    panel._tap();
                    panel.Edit(d => { if (index < d.Ranks.Count) d.Ranks[index].Set(term, on); });
                };
                Boxes[term] = box;
                Flow.AddChild(box);
                panel._interactive.Add(box);
            }
            foreach (var c in new Control[] { Name, Up, Down, Remove }) panel._interactive.Add(c);
        }

        public void Apply(Func<double, float> dp, float target)
        {
            var gap = Mathf.RoundToInt(dp(6));
            Root.AddThemeConstantOverride("separation", gap);
            Top.AddThemeConstantOverride("separation", gap);
            Flow.AddThemeConstantOverride("h_separation", gap);
            Flow.AddThemeConstantOverride("v_separation", gap);
            Name.CustomMinimumSize = new Vector2(dp(96), target);
            foreach (var button in new[] { Up, Down, Remove }) button.CustomMinimumSize = new Vector2(Mathf.Max(target, dp(56)), target);
            foreach (var box in Boxes.Values) box.CustomMinimumSize = new Vector2(Mathf.Max(target, dp(128)), target);
        }
    }
}
