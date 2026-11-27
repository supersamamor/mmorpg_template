using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Guild;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The guild's storage window on the phone HUD (T-905, ARCHITECTURE §7.4 and §6.11), opened from the guild panel's Storage button: a heading (entries out of the size, the storage's
/// currency), three tabs (Storage: what it holds, tap to take; Bag: what this character carries and may put in, tap to put; Log: the newest moves, only for a rank with <c>storage_log</c>),
/// Close; the scroll list; the quantity picker (−, units, +, Take or Put) once a stack is tapped; and a coins row (an amount, Put, Take). The client only hides what the rank cannot do and what
/// cannot go in (worn, not tradeable); the server checks every move in its database. Words come from <see cref="UiText"/>. It redraws only when the guild or the bag changed.
/// </summary>
public sealed class GuildStoragePanel
{
    private const double PanelMaxWidthDp = 460;

    private enum Tab
    {
        Storage,
        Bag,
        Log,
    }

    private readonly ClientSession _session;
    private readonly GuildBook _guild;
    private readonly DisplayBundle? _bundle;
    private readonly List<Control> _interactive;
    private readonly Action _tap;

    private readonly Control _area = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = new() { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly HBoxContainer _head = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Button _storageTab = new() { Text = UiText.GuildStorage, ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _bagTab = new() { Text = UiText.GuildStorageBag, ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _logTab = new() { Text = UiText.GuildStorageLog, ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _list = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _empty = new() { Text = UiText.GuildStorageEmpty };
    private readonly List<Button> _entries = new();
    private readonly List<Label> _logLines = new();
    private readonly HBoxContainer _quantityRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly Label _quantityItem = new() { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _less = new() { Text = "−" };
    private readonly Label _quantity = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _more = new() { Text = "+" };
    private readonly Button _move = new() { Text = UiText.GuildStorageTake };
    private readonly HBoxContainer _coins = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly LineEdit _amount = new() { PlaceholderText = UiText.GuildStorageAmountHint, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MaxLength = 12 };
    private readonly Button _putCoins = new() { Text = UiText.GuildStoragePut };
    private readonly Button _takeCoins = new() { Text = UiText.GuildStorageTake };

    private Tab _tab = Tab.Storage;
    private long _picked;
    private int _pickedMax;
    private int _chosen = 1;
    private int _shownGuild = -1;
    private int _shownBag = -1;
    private Func<double, float> _dp = dp => (float)dp;
    private float _target = 48;
    private Vector2 _safeSize;
    private bool _samples;

    /// <summary>Builds the window (closed) into <paramref name="safe"/>.</summary>
    public GuildStoragePanel(Control safe, ClientSession session, DisplayBundle? bundle, List<Control> interactive, Action tap)
    {
        _session = session;
        _guild = session.Guild;
        _bundle = bundle;
        _interactive = interactive;
        _tap = tap;

        safe.AddChild(_area);
        _area.AddChild(_panel);
        _panel.AddThemeStyleboxOverride("panel", _panelStyle);
        _panel.AddChild(_box);
        _box.AddChild(_title);
        foreach (var c in new Control[] { _storageTab, _bagTab, _logTab, _close }) _head.AddChild(c);
        _box.AddChild(_head);
        _scroll.AddChild(_list);
        _box.AddChild(_scroll);
        _list.AddChild(_empty);
        foreach (var c in new Control[] { _quantityItem, _less, _quantity, _more, _move }) _quantityRow.AddChild(c);
        _box.AddChild(_quantityRow);
        foreach (var c in new Control[] { _amount, _putCoins, _takeCoins }) _coins.AddChild(c);
        _box.AddChild(_coins);

        _close.Pressed += () => { _tap(); SetOpen(false); };
        _storageTab.Pressed += () => { _tap(); Show(Tab.Storage); };
        _bagTab.Pressed += () => { _tap(); Show(Tab.Bag); };
        _logTab.Pressed += () => { _tap(); Show(Tab.Log); };
        _less.Pressed += () => { _tap(); Choose(_chosen - 1); };
        _more.Pressed += () => { _tap(); Choose(_chosen + 1); };
        _move.Pressed += () => { _tap(); MovePicked(); };
        _putCoins.Pressed += () => { _tap(); Coins(put: true); };
        _takeCoins.Pressed += () => { _tap(); Coins(put: false); };
        foreach (var control in new Control[] { _panel, _storageTab, _bagTab, _logTab, _close, _less, _more, _move, _amount, _putCoins, _takeCoins }) _interactive.Add(control);
    }

    /// <summary>True while the window is open.</summary>
    public bool Open => _area.Visible;

    /// <summary>Close.</summary>
    public Button CloseButton => _close;

    /// <summary>The Bag tab.</summary>
    public Button BagTab => _bagTab;

    /// <summary>The Storage tab.</summary>
    public Button StorageTab => _storageTab;

    /// <summary>The Log tab (hidden without <c>storage_log</c>).</summary>
    public Button LogTab => _logTab;

    /// <summary>Take or Put on the quantity picker.</summary>
    public Button MoveButton => _move;

    /// <summary>The coins amount.</summary>
    public LineEdit AmountField => _amount;

    /// <summary>Put beside the amount.</summary>
    public Button PutCoinsButton => _putCoins;

    /// <summary>Take beside the amount.</summary>
    public Button TakeCoinsButton => _takeCoins;

    /// <summary>The entry buttons shown in the list.</summary>
    public IReadOnlyList<Button> EntryButtons => _entries.Where(b => b.Visible).ToList();

    /// <summary>The heading and the list as drawn (for the scripted run).</summary>
    public string Text => _title.Text + " | " + string.Join(" | ", _entries.Where(b => b.Visible).Select(b => b.Text).Concat(_logLines.Where(l => l.Visible).Select(l => l.Text)));

    /// <summary>Opens (and asks the server for the storage) or closes the window.</summary>
    public void SetOpen(bool open)
    {
        if (open == _area.Visible) return;
        _area.Visible = open;
        _amount.ReleaseFocus();
        if (open)
        {
            _tab = Tab.Storage;
            _session.OpenGuildStorage();
        }
        Unpick();
        _shownGuild = -1;
    }

    /// <summary>Lays the window out for the HUD's safe area (canvas units).</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize)
    {
        _dp = dp;
        _target = target;
        _safeSize = safeSize;
        var gap = Mathf.RoundToInt(dp(6));
        _panelStyle.SetContentMarginAll(dp(8));
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        foreach (var box in new BoxContainer[] { _box, _head, _list, _quantityRow, _coins }) box.AddThemeConstantOverride("separation", gap);
        foreach (var button in new[] { _storageTab, _bagTab, _logTab, _close, _move, _putCoins, _takeCoins }) button.CustomMinimumSize = new Vector2(Mathf.Max(target, dp(64)), target);
        foreach (var button in new[] { _less, _more }) button.CustomMinimumSize = new Vector2(target, target);
        foreach (var button in _entries) button.CustomMinimumSize = new Vector2(0, target);
        _quantity.CustomMinimumSize = new Vector2(dp(56), 0);
        _quantityItem.CustomMinimumSize = new Vector2(dp(40), 0);
        _amount.CustomMinimumSize = new Vector2(dp(88), target);
        Place();
        _shownGuild = _shownBag = -1;
    }

    /// <summary>Redraws when the guild or the bag changed; closes off line and out of a guild.</summary>
    public void Refresh(ConnectionPhase phase)
    {
        if (_samples) return;
        if (Open && (phase != ConnectionPhase.Online || !_guild.InGuild))
        {
            SetOpen(false);
            return;
        }
        if (!Open || (_shownGuild == _guild.Version && _shownBag == _session.Inventory.Version)) return;
        _shownGuild = _guild.Version;
        _shownBag = _session.Inventory.Version;
        Draw(_guild.Storage, leaderView: false);
    }

    /// <summary>Fills the window with the longest words it shows (the layout check): a full storage of the longest lines with the quantity picker, or the full log.</summary>
    public void ShowSamples(bool log)
    {
        _samples = true;
        _area.Visible = true;
        _tab = log ? Tab.Log : Tab.Storage;
        var lines = Enumerable.Range(0, 50).Select(i => new InventoryEntryRecord(i + 1, "item.sample", 99, null, ["a", "b", "c", "d"], 10)).ToList();
        var entries = new GuildStorageLogRecord(0, new string('W', Game.Shared.Accounts.CharacterNameRules.LongestAllowed), GuildStorageMoves.Withdraw, "item.sample", 1, 99, 0);
        Draw(new GuildStorageMessage(50, long.MaxValue, lines, Enumerable.Repeat(entries, 50).ToList()), leaderView: true);
        _title.Text = UiText.GuildStorageTitle(500, 500, _bundle?.CurrencyName ?? string.Empty, long.MaxValue);
        if (!log)
        {
            _picked = 1;
            _pickedMax = 99;
            _quantityItem.Text = UiText.LongestTradeLine;
            _quantity.Text = UiText.TradeQuantity(99, 99);
            _quantityRow.Visible = true;
            _coins.Visible = false;
        }
        _amount.Text = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Place();
    }

    /// <summary>Ends <see cref="ShowSamples"/>.</summary>
    public void ClearSamples()
    {
        _samples = false;
        _area.Visible = false;
        _amount.Text = string.Empty;
        Unpick();
        _shownGuild = _shownBag = -1;
    }

    private void Show(Tab tab)
    {
        _tab = tab;
        Unpick();
        _shownGuild = -1;
        if (_samples) return;
        Draw(_guild.Storage, leaderView: false);
    }

    private string NameOf(string itemId) => _samples ? new string('W', 24) : _bundle?.NameOf(itemId) ?? itemId;

    private bool May(string permission, bool leaderView) => leaderView || _guild.May(permission);

    private void Draw(GuildStorageMessage? storage, bool leaderView)
    {
        var currencyName = _bundle?.CurrencyName ?? string.Empty;
        var mayLog = storage?.Log is not null && May(GuildPermissions.StorageLog, leaderView);
        if (_tab == Tab.Log && !mayLog) _tab = Tab.Storage;
        _title.Text = storage is null ? UiText.GuildStorage : UiText.GuildStorageTitle(storage.Entries.Count, storage.Size, currencyName, storage.Currency);
        _storageTab.ButtonPressed = _tab == Tab.Storage;
        _bagTab.ButtonPressed = _tab == Tab.Bag;
        _logTab.ButtonPressed = _tab == Tab.Log;
        _logTab.Visible = mayLog;
        _bagTab.Visible = May(GuildPermissions.StorageDeposit, leaderView);
        _putCoins.Visible = May(GuildPermissions.StorageDeposit, leaderView);
        _takeCoins.Visible = May(GuildPermissions.StorageCurrency, leaderView);
        _coins.Visible = _tab != Tab.Log && !_quantityRow.Visible && (_putCoins.Visible || _takeCoins.Visible);
        _move.Text = _tab == Tab.Bag ? UiText.GuildStoragePut : UiText.GuildStorageTake;

        var rows = new List<(long Id, string Text, int Quantity, bool Enabled)>();
        if (_tab == Tab.Storage && storage is not null)
        {
            var mayTake = May(GuildPermissions.StorageItems, leaderView);
            foreach (var e in storage.Entries) rows.Add((e.Id, UiText.TradeLine(NameOf(e.Item), e.Quantity, e.Refine, e.Cards?.Count ?? 0), e.Quantity, mayTake));
        }
        else if (_tab == Tab.Bag)
            foreach (var e in _session.Inventory.Entries)
            {
                if (e.Slot is not null || _bundle?.Item(e.Item) is { Tradeable: false }) continue;   // worn, or not tradeable: the server would refuse it
                rows.Add((e.Id, UiText.TradeLine(NameOf(e.Item), e.Quantity, e.Refine, e.Cards?.Count ?? 0), e.Quantity, true));
            }
        while (_entries.Count < rows.Count)
        {
            var button = new Button { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            button.CustomMinimumSize = new Vector2(0, _target);
            var index = _entries.Count;
            button.Pressed += () => { _tap(); Pick(index); };
            _list.AddChild(button);
            _entries.Add(button);
            _interactive.Add(button);
        }
        for (var i = 0; i < _entries.Count; i++)
        {
            var button = _entries[i];
            button.Visible = i < rows.Count;
            if (i >= rows.Count) continue;
            button.Text = rows[i].Text;
            button.Disabled = !rows[i].Enabled;
            button.SetMeta("entry", rows[i].Id);
            button.SetMeta("quantity", rows[i].Quantity);
        }

        var log = _tab == Tab.Log && storage?.Log is { } moves ? moves : (IReadOnlyList<GuildStorageLogRecord>)Array.Empty<GuildStorageLogRecord>();
        while (_logLines.Count < log.Count)
        {
            var line = new Label { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _list.AddChild(line);
            _logLines.Add(line);
        }
        for (var i = 0; i < _logLines.Count; i++)
        {
            _logLines[i].Visible = i < log.Count;
            if (i < log.Count)
                _logLines[i].Text = _samples ? UiText.LongestGuildStorageLogLine
                    : UiText.GuildStorageLogLine(log[i].At, log[i].Name, log[i].Move, log[i].Item is { } item ? NameOf(item) : null, log[i].Quantity, currencyName, log[i].Currency);
        }
        _empty.Visible = rows.Count == 0 && log.Count == 0;
        if (_picked != 0 && !rows.Any(r => r.Id == _picked)) Unpick();
    }

    private void Pick(int index)
    {
        var button = _entries[index];
        var entry = (long)button.GetMeta("entry");
        var quantity = (int)button.GetMeta("quantity");
        if (quantity <= 1)
        {
            Send(entry, 1);
            return;
        }
        _picked = entry;
        _pickedMax = quantity;
        _quantityItem.Text = button.Text;
        _quantityRow.Visible = true;
        _coins.Visible = false;   // one row at a time below the list, so the list keeps rows on the smallest phone at the largest scale
        Choose(quantity);
    }

    private void Choose(int quantity)
    {
        _chosen = Math.Clamp(quantity, 1, Math.Max(1, _pickedMax));
        _quantity.Text = UiText.TradeQuantity(_chosen, _pickedMax);
    }

    private void MovePicked()
    {
        if (_picked == 0) return;
        Send(_picked, _chosen);
    }

    private void Send(long entry, int quantity)
    {
        if (!_samples)
        {
            if (_tab == Tab.Bag) _session.DepositToGuild(entry, quantity);
            else _session.WithdrawFromGuild(entry, quantity);
        }
        Unpick();
    }

    private void Coins(bool put)
    {
        if (long.TryParse(_amount.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount > 0 && !_samples)
        {
            if (put) _session.DepositCurrencyToGuild(amount);
            else _session.WithdrawCurrencyFromGuild(amount);
        }
        _amount.Clear();
        _amount.ReleaseFocus();
    }

    private void Unpick()
    {
        _picked = 0;
        _pickedMax = 0;
        _quantityRow.Visible = false;
        _shownGuild = -1;   // the coins row comes back with the next draw
    }

    /// <summary>The window centred in the safe area, as wide as fits, as tall as the safe area less margins.</summary>
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
}
