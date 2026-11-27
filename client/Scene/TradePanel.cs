using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Client.Core.Trade;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The trade on the phone HUD (T-903, ARCHITECTURE §6.10 and §7.4). Two parts:
/// <list type="bullet">
/// <item><b>The ask prompt</b>: who asks and the seconds left, Trade and No, at the top of the safe area while an ask waits; it comes up by itself.</item>
/// <item><b>The window</b>, open while a trade is: a heading (whom with, the state line, Cancel); two columns, what this side gives (each line a button that takes it out, and Add, which
/// swaps the column for the bag's tradeable carried entries, with a quantity picker for stacks) and what the other side gives; a footer with the currency field, Set, Lock or Unlock, and
/// Confirm. Both columns are drawn from the server's <c>trade</c> message only, never from what this client asked for; Confirm sends the version of the offers on the screen, so a change
/// that arrives at the last moment makes the server refuse the confirm (<c>stale</c>) and the new offers are drawn instead.</item>
/// </list>
/// Words come from <see cref="UiText"/>. It redraws only when the trade's version or the bag's moves.
/// </summary>
public sealed class TradePanel
{
    private const double PanelMaxWidthDp = 620;
    private const int MaxLines = 16;

    private readonly ClientSession _session;
    private readonly DisplayBundle? _bundle;
    private readonly TradeBook _trade;
    private readonly List<Control> _interactive;
    private readonly Action _tap;

    // the window
    private readonly Control _area = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.96f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly HBoxContainer _head = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly VBoxContainer _headText = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _title = Line();
    private readonly Label _state = Line();
    private readonly Button _cancel = new() { Text = UiText.TradeCancel };
    private readonly HBoxContainer _columns = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly Column _mine;
    private readonly Column _theirs;
    private readonly Button _add = new() { Text = UiText.TradeAdd };
    private readonly HBoxContainer _foot = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly LineEdit _currency = new() { PlaceholderText = UiText.TradeCurrencyHint, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MaxLength = 18, VirtualKeyboardType = LineEdit.VirtualKeyboardTypeEnum.Number };
    private readonly Button _setCurrency = new() { Text = UiText.TradeSetCurrency };
    private readonly Button _lock = new() { Text = UiText.TradeLock };
    private readonly Button _confirm = new() { Text = UiText.TradeConfirm };

    // the picker (in the window, in the columns' place)
    private readonly VBoxContainer _picker = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Visible = false };
    private readonly HBoxContainer _pickHead = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _pickTitle = Line(UiText.TradeAdd);
    private readonly Button _back = new() { Text = UiText.TradeBack };
    private readonly ScrollContainer _pickScroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _pickList = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _pickEmpty = new() { Text = UiText.TradeNothingToOffer, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly HBoxContainer _quantityRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly Label _quantityItem = Line();
    private readonly Button _less = new() { Text = UiText.TradeLess };
    private readonly Label _quantity = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _more = new() { Text = UiText.TradeMore };
    private readonly Button _offer = new() { Text = UiText.TradeOfferButton };
    private readonly List<Button> _pickButtons = new();

    // the ask prompt
    private readonly PanelContainer _prompt = new() { MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
    private readonly StyleBoxFlat _promptStyle = new() { BgColor = new Color(0.14f, 0.13f, 0.08f, 0.96f) };
    private readonly HBoxContainer _promptRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _promptText = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _accept = new() { Text = UiText.TradeAccept };
    private readonly Button _decline = new() { Text = UiText.TradeDecline };

    private int _shownTrade = -1;
    private int _shownBag = -1;
    private int _promptSecond = -1;
    private long _picked;
    private int _chosen = 1;
    private Func<double, float> _dp = dp => (float)dp;
    private float _target = 48;
    private Vector2 _safeSize;
    private bool _samples;

    /// <summary>Builds the window (closed) and the prompt (hidden) into <paramref name="safe"/>.</summary>
    public TradePanel(Control safe, ClientSession session, DisplayBundle? bundle, List<Control> interactive, Action tap)
    {
        _session = session;
        _bundle = bundle;
        _trade = session.Trade;
        _interactive = interactive;
        _tap = tap;
        _mine = new Column(interactive, tap, entry => _session.OfferTrade(entry, 0));
        _theirs = new Column(interactive, tap, null);

        safe.AddChild(_area);
        _area.AddChild(_panel);
        _panel.AddThemeStyleboxOverride("panel", _panelStyle);
        _panel.AddChild(_box);
        _headText.AddChild(_title);
        _headText.AddChild(_state);
        _headText.AddThemeConstantOverride("separation", 0);
        _head.AddChild(_headText);
        _head.AddChild(_cancel);
        _box.AddChild(_head);
        _mine.Head.AddChild(_add);
        _columns.AddChild(_mine.Root);
        _columns.AddChild(new VSeparator { MouseFilter = Control.MouseFilterEnum.Ignore });
        _columns.AddChild(_theirs.Root);
        _box.AddChild(_columns);
        _pickHead.AddChild(_pickTitle);
        _pickHead.AddChild(_back);
        _picker.AddChild(_pickHead);
        _pickScroll.AddChild(_pickList);
        _pickList.AddChild(_pickEmpty);
        _picker.AddChild(_pickScroll);
        _quantityRow.AddChild(_quantityItem);
        _quantityRow.AddChild(_less);
        _quantityRow.AddChild(_quantity);
        _quantityRow.AddChild(_more);
        _quantityRow.AddChild(_offer);
        _picker.AddChild(_quantityRow);
        _box.AddChild(_picker);
        _foot.AddChild(_currency);
        _foot.AddChild(_setCurrency);
        _foot.AddChild(_lock);
        _foot.AddChild(_confirm);
        _box.AddChild(_foot);

        safe.AddChild(_prompt);
        _prompt.AddThemeStyleboxOverride("panel", _promptStyle);
        _prompt.AddChild(_promptRow);
        _promptRow.AddChild(_promptText);
        _promptRow.AddChild(_accept);
        _promptRow.AddChild(_decline);

        _cancel.Pressed += () => { _tap(); _session.CancelTrade(); };
        _add.Pressed += () => { _tap(); SetPicking(true); };
        _back.Pressed += () => { _tap(); SetPicking(false); };
        _less.Pressed += () => { _tap(); Choose(_chosen - 1); };
        _more.Pressed += () => { _tap(); Choose(_chosen + 1); };
        _offer.Pressed += () =>
        {
            _tap();
            if (_picked != 0) _session.OfferTrade(_picked, _chosen);
            SetPicking(false);
        };
        _setCurrency.Pressed += () => { _tap(); SetCurrency(); };
        _currency.TextSubmitted += _ => SetCurrency();
        _lock.Pressed += () =>
        {
            _tap();
            _session.LockTrade(!(_trade.Open?.Mine.Locked ?? false));
        };
        _confirm.Pressed += () => { _tap(); _session.ConfirmTrade(); };
        _accept.Pressed += () => { _tap(); _session.AnswerTrade(true); };
        _decline.Pressed += () => { _tap(); _session.AnswerTrade(false); };
        foreach (var control in new Control[] { _panel, _cancel, _add, _back, _less, _more, _offer, _currency, _setCurrency, _lock, _confirm, _prompt, _accept, _decline })
            _interactive.Add(control);
    }

    /// <summary>True while the window shows.</summary>
    public bool Open => _area.Visible;

    /// <summary>True while the ask prompt shows.</summary>
    public bool PromptShown => _prompt.Visible;

    /// <summary>True while the bag's list replaces the columns.</summary>
    public bool Picking => _picker.Visible;

    /// <summary>The ask prompt's Trade.</summary>
    public Button AcceptButton => _accept;

    /// <summary>The ask prompt's No.</summary>
    public Button DeclineButton => _decline;

    /// <summary>Cancel.</summary>
    public Button CancelButton => _cancel;

    /// <summary>Add (opens the bag's list).</summary>
    public Button AddButton => _add;

    /// <summary>Lock or Unlock.</summary>
    public Button LockButton => _lock;

    /// <summary>Confirm.</summary>
    public Button ConfirmButton => _confirm;

    /// <summary>The currency field.</summary>
    public LineEdit CurrencyField => _currency;

    /// <summary>Set (the currency).</summary>
    public Button SetCurrencyButton => _setCurrency;

    /// <summary>The picker's Offer.</summary>
    public Button OfferButton => _offer;

    /// <summary>The picker's +.</summary>
    public Button MoreButton => _more;

    /// <summary>The bag's list as drawn: one button per entry that can be offered (for the scripted run).</summary>
    public IReadOnlyList<Button> PickButtons => _pickButtons.Where(b => b.Visible).ToList();

    /// <summary>What this side gives, as drawn (for the scripted run).</summary>
    public string MineText => _mine.Text;

    /// <summary>What the other side gives, as drawn.</summary>
    public string TheirsText => _theirs.Text;

    /// <summary>The state line as drawn.</summary>
    public string StateText => _state.Text;

    /// <summary>Lays the window and the prompt out for the HUD's safe area (canvas units).</summary>
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
        foreach (var box in new BoxContainer[] { _box, _head, _columns, _foot, _picker, _pickHead, _pickList, _quantityRow, _promptRow }) box.AddThemeConstantOverride("separation", gap);
        foreach (var button in new[] { _cancel, _add, _back, _offer, _setCurrency, _lock, _confirm, _accept, _decline }) button.CustomMinimumSize = new Vector2(dp(72), target);
        foreach (var button in new[] { _less, _more }) button.CustomMinimumSize = new Vector2(target, target);
        _quantity.CustomMinimumSize = new Vector2(dp(56), 0);
        _quantityItem.CustomMinimumSize = new Vector2(dp(40), 0);
        _currency.CustomMinimumSize = new Vector2(dp(88), target);
        foreach (var button in _pickButtons) button.CustomMinimumSize = new Vector2(0, target);
        _mine.Apply(dp, target, gap);
        _theirs.Apply(dp, target, gap);
        Place();
        _shownTrade = _shownBag = -1;
        _promptSecond = -1;
        if (_samples) ShowSamples(Picking);   // the layout check: laid out again for this screen
    }

    /// <summary>Opens the window when a trade is open and closes it when it ended; redraws when the trade or the bag changed; keeps the prompt's seconds. Hidden off line.</summary>
    public void Refresh(ConnectionPhase phase)
    {
        if (_samples) return;
        var online = phase == ConnectionPhase.Online;
        var ask = online ? _trade.Ask : null;
        _prompt.Visible = ask is not null;
        if (ask is not null)
        {
            var left = (int)Math.Ceiling(ask.EndsAt - Clocks.Wall());
            if (left != _promptSecond)
            {
                _promptSecond = left;
                _promptText.Text = UiText.TradeAsked(ask.Name, left);
            }
        }
        var open = online ? _trade.Open : null;
        if (open is null)
        {
            if (_area.Visible)
            {
                _area.Visible = false;
                _currency.ReleaseFocus();
                SetPicking(false);
            }
            return;
        }
        if (!_area.Visible) _area.Visible = true;
        if (_shownTrade == _trade.Version && _shownBag == _session.Inventory.Version) return;
        _shownTrade = _trade.Version;
        _shownBag = _session.Inventory.Version;
        Draw(open);
    }

    /// <summary>Fills the window and the prompt with the longest words they show (the layout check): both offers full of the longest lines, the longest name and state; or the picker.</summary>
    public void ShowSamples(bool picker)
    {
        _samples = true;
        _area.Visible = true;
        var name = new string('W', Game.Shared.Accounts.CharacterNameRules.LongestAllowed);
        var lines = Enumerable.Range(0, 10).Select(i => UiText.LongestTradeLine).ToList();
        _title.Text = UiText.TradeTitle(name);
        _state.Text = UiText.TradeState(name, false, false, false, true);
        _mine.Draw(UiText.TradeYouOffer, lines.Select(l => (0L, l)).ToList(), UiText.Currency(_bundle?.CurrencyName ?? string.Empty, long.MaxValue), clickable: true);
        _theirs.Draw(UiText.TradeTheyOffer(name), lines.Select(l => (0L, l)).ToList(), UiText.Currency(_bundle?.CurrencyName ?? string.Empty, long.MaxValue), clickable: false);
        _lock.Text = UiText.TradeUnlock;
        _confirm.Disabled = false;
        _currency.Text = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetPicking(picker);
        if (picker)
        {
            DrawPicker(Enumerable.Range(0, 20).Select(i => ((long)i + 1, UiText.LongestTradeLine, 99)).ToList());
            _picked = 1;
            _quantityItem.Text = UiText.LongestTradeLine;
            _quantity.Text = UiText.TradeQuantity(99, 99);
            _quantityRow.Visible = true;
        }
        _prompt.Visible = true;
        _promptText.Text = UiText.TradeAsked(name, 20);
        Place();
    }

    /// <summary>Ends <see cref="ShowSamples"/>.</summary>
    public void ClearSamples()
    {
        _samples = false;
        _area.Visible = false;
        _prompt.Visible = false;
        _currency.Text = string.Empty;
        SetPicking(false);
        _shownTrade = _shownBag = -1;
        _promptSecond = -1;
    }

    private void SetPicking(bool picking)
    {
        _picker.Visible = picking;
        _columns.Visible = !picking;
        _foot.Visible = !picking;   // one row fewer, so the list keeps a row on the smallest phone at the largest scale
        _quantityRow.Visible = false;
        _picked = 0;
        _shownTrade = _shownBag = -1;
    }

    private void SetCurrency()
    {
        if (long.TryParse(_currency.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var amount)) _session.OfferTradeCurrency(amount);
        _currency.Clear();
        _currency.ReleaseFocus();
    }

    private void Choose(int quantity)
    {
        var carried = _picked != 0 ? _session.Inventory.Find(_picked)?.Quantity ?? 1 : 1;
        _chosen = Math.Clamp(quantity, 1, Math.Max(1, carried));
        _quantity.Text = UiText.TradeQuantity(_chosen, carried);
    }

    private string NameOf(string itemId) => _bundle?.NameOf(itemId) ?? itemId;

    private void Draw(TradeMessage trade)
    {
        var currencyName = _bundle?.CurrencyName ?? string.Empty;
        _title.Text = UiText.TradeTitle(trade.Name);
        _state.Text = UiText.TradeState(trade.Name, trade.Mine.Locked, trade.Theirs.Locked, trade.Mine.Confirmed, trade.Theirs.Confirmed);
        _mine.Draw(UiText.TradeYouOffer, Lines(trade.Mine), UiText.Currency(currencyName, trade.Mine.Currency), clickable: !trade.Mine.Confirmed);
        _theirs.Draw(UiText.TradeTheyOffer(trade.Name), Lines(trade.Theirs), UiText.Currency(currencyName, trade.Theirs.Currency), clickable: false);
        _lock.Text = trade.Mine.Locked ? UiText.TradeUnlock : UiText.TradeLock;
        _confirm.Disabled = !_trade.CanConfirm;
        _add.Disabled = trade.Mine.Confirmed;
        if (!_picker.Visible) return;
        // The bag's carried, tradeable entries; one already offered shows what is offered of it.
        var offered = trade.Mine.Items.ToDictionary(i => i.Entry, i => i.Quantity);
        var entries = new List<(long, string, int)>();
        foreach (var e in _session.Inventory.Entries)
        {
            if (e.Slot is not null || _bundle?.Item(e.Item) is { Tradeable: false }) continue;
            var line = UiText.TradeLine(NameOf(e.Item), e.Quantity, e.Refine, e.Cards?.Count ?? 0);
            entries.Add((e.Id, offered.TryGetValue(e.Id, out var units) ? "✓ " + line + " (" + units.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : line, e.Quantity));
        }
        DrawPicker(entries);
        if (_picked != 0 && _session.Inventory.Find(_picked) is null) SetPicking(true);
    }

    private List<(long, string)> Lines(TradeSideRecord side) =>
        side.Items.Select(i => (i.Entry, UiText.TradeLine(NameOf(i.Item), i.Quantity, i.Refine, i.Cards?.Count ?? 0))).ToList();

    private void DrawPicker(IReadOnlyList<(long Id, string Text, int Quantity)> entries)
    {
        _pickEmpty.Visible = entries.Count == 0;
        while (_pickButtons.Count < entries.Count)
        {
            var button = new Button { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            button.CustomMinimumSize = new Vector2(0, _target);
            var index = _pickButtons.Count;
            button.Pressed += () => { _tap(); Pick(index); };
            _pickList.AddChild(button);
            _pickButtons.Add(button);
            _interactive.Add(button);
        }
        for (var i = 0; i < _pickButtons.Count; i++)
        {
            var button = _pickButtons[i];
            button.Visible = i < entries.Count;
            if (i >= entries.Count) continue;
            button.Text = entries[i].Text;
            button.SetMeta("entry", entries[i].Id);
            button.SetMeta("quantity", entries[i].Quantity);
        }
    }

    private void Pick(int index)
    {
        var button = _pickButtons[index];
        var entry = (long)button.GetMeta("entry");
        var quantity = (int)button.GetMeta("quantity");
        if (quantity <= 1)
        {
            _session.OfferTrade(entry, 1);
            SetPicking(false);
            return;
        }
        _picked = entry;
        _quantityItem.Text = button.Text;
        _quantityRow.Visible = true;
        var offered = _trade.Open?.Mine.Items.FirstOrDefault(i => i.Entry == entry)?.Quantity ?? 1;
        Choose(offered);
    }

    /// <summary>The window centred in the safe area, as wide as fits, as tall as the safe area less margins; the prompt at the top.</summary>
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
        var promptWidth = Mathf.Min(_dp(460), _safeSize.X - 2 * margin);
        _prompt.CustomMinimumSize = new Vector2(promptWidth, 0);
        _prompt.Position = new Vector2((_safeSize.X - promptWidth) / 2, margin);
        _prompt.Size = new Vector2(promptWidth, 0);
    }

    private static Label Line(string text = "") =>
        new() { Text = text, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

    /// <summary>One side's column: its heading (with Add on this side's), its lines in a scroll list (buttons that take a line out on this side, labels on the other), its currency.</summary>
    private sealed class Column
    {
        public readonly VBoxContainer Root = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1 };
        public readonly HBoxContainer Head = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        private readonly Label _heading = Line();
        private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        private readonly VBoxContainer _list = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        private readonly Label _nothing = new() { Text = UiText.TradeNothing, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        private readonly Label _money = Line();
        private readonly List<Button> _lines = new();
        private readonly List<Control> _interactive;
        private readonly Action _tap;
        private readonly Action<long>? _remove;
        private float _target = 48;

        public Column(List<Control> interactive, Action tap, Action<long>? remove)
        {
            _interactive = interactive;
            _tap = tap;
            _remove = remove;
            Head.AddChild(_heading);
            Root.AddChild(Head);
            _scroll.AddChild(_list);
            _list.AddChild(_nothing);
            Root.AddChild(_scroll);
            Root.AddChild(_money);
        }

        public string Text => string.Join(" | ", _lines.Where(l => l.Visible).Select(l => l.Text)) + " | " + _money.Text;

        public void Apply(Func<double, float> dp, float target, int gap)
        {
            _target = target;
            foreach (var box in new BoxContainer[] { Root, Head, _list }) box.AddThemeConstantOverride("separation", gap);
            Head.CustomMinimumSize = new Vector2(0, target);
            foreach (var line in _lines) line.CustomMinimumSize = new Vector2(0, target);
        }

        public void Draw(string heading, IReadOnlyList<(long Entry, string Text)> lines, string money, bool clickable)
        {
            _heading.Text = heading;
            _money.Text = money;
            _nothing.Visible = lines.Count == 0;
            while (_lines.Count < Math.Min(lines.Count, MaxLines))
            {
                var button = new Button { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                button.CustomMinimumSize = new Vector2(0, _target);
                if (_remove is not null)
                {
                    var index = _lines.Count;
                    button.Pressed += () =>
                    {
                        _tap();
                        if (_lines[index].HasMeta("entry")) _remove((long)_lines[index].GetMeta("entry"));
                    };
                    _interactive.Add(button);
                }
                else button.MouseFilter = Control.MouseFilterEnum.Ignore;   // the other side's: shown, never pressed
                _list.AddChild(button);
                _lines.Add(button);
            }
            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                line.Visible = i < lines.Count;
                if (i >= lines.Count) continue;
                line.Text = lines[i].Text;
                line.SetMeta("entry", lines[i].Entry);
                line.Disabled = !clickable;
            }
        }
    }
}
