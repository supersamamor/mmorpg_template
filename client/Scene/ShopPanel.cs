using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Shop;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The shop panel (T-603, ARCHITECTURE §7.4, §6.8): a Buy tab with the shop's stock and prices, a Sell tab with the player's bag and what the shop pays, a quantity
/// picker with the total, the currency balance and the Buy / Sell button. It opens when the server sends <c>shop</c> and closes when the dialogue or service ends (the
/// panel follows <see cref="ClientSession.Dialogue"/>'s service). It sends only <c>shop_buy</c> (item, quantity) and <c>shop_sell</c> (entry, quantity): never a price. The
/// totals it shows are a preview; the server prices every trade and the balance and bag come back from it. Code-built controls sized in dp times the UI scale like the
/// other panels, every button at least the touch target; it redraws only when the shop's, the inventory's or the dialogue's version moves and does nothing while closed.
/// </summary>
public sealed class ShopPanel
{
    private const double PanelMaxWidthDp = 680;
    private const double IconDp = 32;

    private readonly ClientSession _session;
    private readonly DisplayBundle _bundle;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly CenterContainer _center = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = Label(UiText.ShopTitle);   // the heading and the balance on one line: a small phone screen has no height to spare
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly Button _buyTab = new() { Text = UiText.ShopBuyTab };
    private readonly Button _sellTab = new() { Text = UiText.ShopSellTab };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _rowsBox = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _hint = Label(string.Empty);
    private readonly HBoxContainer _picker = new() { MouseFilter = Control.MouseFilterEnum.Ignore };   // one row, never wrapping: a second row would not fit a 360 dp screen
    private readonly Button _less = new() { Text = UiText.QuantityLess };
    private readonly Label _quantity = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Button _more = new() { Text = UiText.QuantityMore };
    private readonly Button _most = new() { Text = UiText.QuantityMax };
    private readonly Button _action = new() { Text = UiText.ShopBuyButton };
    private readonly List<Button> _rows = new();
    private readonly List<string> _rowItems = new();
    private readonly List<long> _rowEntries = new();
    private readonly Dictionary<string, Texture2D?> _icons = new(StringComparer.Ordinal);
    private int _shownShop = -1;
    private int _shownBag = -1;
    private int _shownDialogue = -1;
    private float _target = 48;
    private float _rowWidth = 200;
    private int _iconSize = 32;
    private bool _samples;
    private string _sampleName = string.Empty;

    /// <summary>Builds the panel (closed) under <paramref name="parent"/>; every button it makes goes into <paramref name="interactive"/>.</summary>
    public ShopPanel(Control parent, ClientSession session, DisplayBundle bundle, List<Control> interactive, Action tap)
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
        var tabs = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _buyTab.SizeFlagsHorizontal = _sellTab.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        tabs.AddChild(_buyTab);
        tabs.AddChild(_sellTab);
        _box.AddChild(tabs);
        _box.AddChild(_scroll);
        _scroll.AddChild(_rowsBox);
        _box.AddChild(_hint);
        _quantity.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _picker.AddChild(_less);
        _picker.AddChild(_quantity);
        _picker.AddChild(_more);
        _picker.AddChild(_most);
        _picker.AddChild(_action);
        _box.AddChild(_picker);

        _close.Pressed += () => { _tap(); if (!_samples) _session.CloseDialogue(); else ClearSamples(); };
        _buyTab.Pressed += () => { _tap(); if (!_samples) _session.Shop.SetTab(ShopTab.Buy); };
        _sellTab.Pressed += () => { _tap(); if (!_samples) _session.Shop.SetTab(ShopTab.Sell); };
        _less.Pressed += () => Step(-1);
        _more.Pressed += () => Step(1);
        _most.Pressed += () => { _tap(); if (!_samples) _session.Shop.SetQuantity(Limit(), Limit()); };
        _action.Pressed += OnAction;
        foreach (var control in new Control[] { _close, _buyTab, _sellTab, _less, _more, _most, _action, _panel, _scroll })
            _interactive.Add(control);
    }

    /// <summary>Finds an icon texture by asset key (the theme), or null; the HUD sets it once the theme is loaded.</summary>
    public Func<string, Texture2D?>? IconLoader { get; set; }

    /// <summary>True while the panel is open.</summary>
    public bool Open => _center.Visible;

    /// <summary>The rows now shown: the stock items on the Buy tab (<c>Item</c>), the carried entries on the Sell tab (<c>Entry</c>), for the scripted run.</summary>
    public IReadOnlyList<(Button Button, string? Item, long Entry)> Rows
    {
        get
        {
            var list = new List<(Button, string?, long)>();
            for (var i = 0; i < _rows.Count; i++)
                if (_rows[i].Visible) list.Add((_rows[i], i < _rowItems.Count ? _rowItems[i] : null, i < _rowEntries.Count ? _rowEntries[i] : 0));
            return list;
        }
    }

    /// <summary>The Buy and Sell tab buttons (for the scripted run).</summary>
    public (Button Buy, Button Sell) Tabs => (_buyTab, _sellTab);

    /// <summary>The picker's buttons: less, more, most, and the Buy / Sell button (for the scripted run).</summary>
    public (Button Less, Button More, Button Most, Button Action) Picker => (_less, _more, _most, _action);

    /// <summary>The close button (for the scripted run).</summary>
    public Button CloseButton => _close;

    /// <summary>The balance line, the hint and the picker line as drawn, separated by " | " (for the scripted run).</summary>
    public string Text => _title.Text + " | " + _hint.Text + " | " + _quantity.Text + " | " + _action.Text;

    /// <summary>Where the panel is on screen now (for the scripted run's layout line).</summary>
    public string Layout => $"panel {_panel.GetGlobalRect()}, scroll {_scroll.GetGlobalRect()}";

    /// <summary>Lays the panel out for the HUD's safe area (canvas units) with <paramref name="dp"/> as dp times scale and units, and the touch target height.</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize, int fontSize)
    {
        _target = target;
        var margin = dp(8);
        var width = Mathf.Min(dp(PanelMaxWidthDp), safeSize.X - 2 * margin);
        var height = safeSize.Y - 2 * margin;
        _panel.CustomMinimumSize = new Vector2(width, height);
        var gap = Mathf.RoundToInt(dp(6));
        _panelStyle.SetContentMarginAll(dp(8));
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        _box.AddThemeConstantOverride("separation", Mathf.RoundToInt(dp(3)));   // tight: a 360 dp screen at the largest scale has no height to spare
        _rowsBox.AddThemeConstantOverride("separation", gap);
        _picker.AddThemeConstantOverride("h_separation", gap);
        _rowWidth = width - dp(16) - dp(12);   // the panel's padding and room for the scroll bar
        _title.CustomMinimumSize = new Vector2(Mathf.Max(0, width - dp(24) - dp(96) - gap), 0);
        _hint.CustomMinimumSize = new Vector2(width - dp(24), 0);
        _quantity.CustomMinimumSize = new Vector2(dp(56), target);
        _close.CustomMinimumSize = new Vector2(dp(96), target);
        _buyTab.CustomMinimumSize = _sellTab.CustomMinimumSize = new Vector2(dp(96), target);
        foreach (var button in new[] { _less, _more }) button.CustomMinimumSize = new Vector2(Mathf.Max(target, dp(56)), target);
        _most.CustomMinimumSize = new Vector2(dp(72), target);
        _action.CustomMinimumSize = new Vector2(dp(96), target);
        _iconSize = Mathf.RoundToInt(dp(IconDp));
        foreach (var row in _rows) SizeRow(row);
        _center.Position = Vector2.Zero;
        _center.Size = safeSize;
        _shownShop = -1;
    }

    /// <summary>Redraws from the shop, the inventory and the dialogue when one changed. Nothing while closed.</summary>
    public void Refresh()
    {
        if (_samples)
        {
            DrawSamples();
            return;
        }
        var shop = _session.Shop;
        var open = shop.Open && _session.Dialogue.Service == ServiceIds.Shop;
        if (open != _center.Visible)
        {
            _center.Visible = open;
            _shownShop = -1;
        }
        if (!open) return;
        var bag = _session.Inventory;
        if (_shownShop == shop.Version && _shownBag == bag.Version && _shownDialogue == _session.Dialogue.Version) return;
        _shownShop = shop.Version;
        _shownBag = bag.Version;
        _shownDialogue = _session.Dialogue.Version;

        var currency = _bundle.CurrencyName;
        _title.Text = UiText.ShopHeader(currency, bag.Currency);
        _buyTab.Modulate = shop.Tab == ShopTab.Buy ? new Color(1f, 0.9f, 0.5f) : Colors.White;
        _sellTab.Modulate = shop.Tab == ShopTab.Sell ? new Color(1f, 0.9f, 0.5f) : Colors.White;
        _rowItems.Clear();
        _rowEntries.Clear();
        var shown = 0;
        if (shop.Tab == ShopTab.Buy)
        {
            foreach (var row in shop.Stock)
            {
                var button = Row(shown++);
                button.Text = UiText.ShopRow(_bundle.NameOf(row.Item), currency, row.Price);
                button.Icon = IconOf(row.Item);
                button.Disabled = false;
                button.Modulate = shop.SelectedItem == row.Item ? new Color(1f, 0.9f, 0.5f) : Colors.White;
                _rowItems.Add(row.Item);
                _rowEntries.Add(0);
            }
        }
        else
        {
            foreach (var entry in bag.Entries)
            {
                if (entry.Slot is not null) continue;   // worn gear is not for sale: it comes off first
                var price = shop.SellPriceOf(entry.Item);
                var button = Row(shown++);
                button.Text = UiText.ShopRow(UiText.ItemCount(_bundle.NameOf(entry.Item), entry.Quantity), currency, price);
                button.Icon = IconOf(entry.Item);
                button.Disabled = price is null;
                button.Modulate = shop.SelectedEntry == entry.Id ? new Color(1f, 0.9f, 0.5f) : Colors.White;
                _rowItems.Add(entry.Item);
                _rowEntries.Add(entry.Id);
            }
        }
        for (var i = shown; i < _rows.Count; i++) _rows[i].Visible = false;

        var buying = shop.Tab == ShopTab.Buy;
        var unit = buying ? (shop.SelectedItem is { } item ? shop.PriceOf(item) : null) : SelectedSellPrice();
        var picked = buying ? shop.SelectedItem is not null : shop.SelectedEntry is not null && bag.Find(shop.SelectedEntry.Value) is not null;
        if (picked) shop.SetQuantity(shop.Quantity, Limit());   // the stack may have shrunk (or the balance changed): keep the pick within what is possible
        var total = unit is { } price1 ? ShopBook.Total(price1, shop.Quantity) : null;
        _hint.Text = picked ? UiText.ShopPick(PickedName(shop), shop.Quantity, currency, total) : !buying && shown == 0 ? UiText.ShopNothingToSell : buying ? UiText.ShopPickToBuy : UiText.ShopPickToSell;
        _picker.Visible = picked;
        _action.Text = buying ? UiText.ShopBuyButton : UiText.ShopSellButton;
        _quantity.Text = UiText.QuantityLabel(shop.Quantity);
    }

    /// <summary>Fills the panel with the longest words it can show (and opens it), so a layout check sees the worst case; <see cref="ClearSamples"/> ends it.</summary>
    public void ShowSamples(string longName)
    {
        _samples = true;
        _sampleName = longName;
        _center.Visible = true;
    }

    /// <summary>Ends <see cref="ShowSamples"/> and closes the panel.</summary>
    public void ClearSamples()
    {
        _samples = false;
        _center.Visible = false;
        _shownShop = -1;
    }

    private void DrawSamples()
    {
        _center.Visible = true;
        _title.Text = UiText.ShopHeader(_bundle.CurrencyName.Length > 0 ? _bundle.CurrencyName : "crowns", 999999999);
        for (var i = 0; i < 12; i++)
        {
            var button = Row(i);
            button.Text = UiText.ShopRow(UiText.ItemCount(_sampleName, 999), _bundle.CurrencyName, 999999);
            button.Icon = null;
            button.Disabled = false;
            button.Visible = true;
        }
        for (var i = 12; i < _rows.Count; i++) _rows[i].Visible = false;
        _hint.Text = UiText.ShopPick(_sampleName, 99, _bundle.CurrencyName, 99999999);
        _picker.Visible = true;
        _quantity.Text = UiText.QuantityLabel(99);
        _action.Text = UiText.ShopBuyButton;
    }

    private string PickedName(ShopBook shop)
    {
        var item = shop.Tab == ShopTab.Buy ? shop.SelectedItem : _session.Inventory.Find(shop.SelectedEntry ?? -1)?.Item;
        return item is null ? string.Empty : _bundle.NameOf(item);
    }

    /// <summary>The unit price the shop pays for the picked bag entry, or null.</summary>
    private long? SelectedSellPrice() =>
        _session.Shop.SelectedEntry is { } id && _session.Inventory.Find(id) is { } entry ? _session.Shop.SellPriceOf(entry.Item) : null;

    /// <summary>The most the picker may go to: the purchase limit when buying (and what the balance affords, at least 1), the carried stack when selling.</summary>
    private int Limit()
    {
        var shop = _session.Shop;
        if (shop.Tab == ShopTab.Sell) return _session.Inventory.Find(shop.SelectedEntry ?? -1)?.Quantity ?? 1;
        return shop.MaxQuantity;
    }

    private void Step(int by)
    {
        _tap();
        if (_samples) return;
        var shop = _session.Shop;
        shop.SetQuantity(shop.Quantity + by, Limit());
    }

    private void OnAction()
    {
        _tap();
        if (_samples) return;
        var shop = _session.Shop;
        if (shop.Tab == ShopTab.Buy)
        {
            if (shop.SelectedItem is { } item) _session.ShopBuy(item, shop.Quantity);   // the item and the count: the server knows the price
            return;
        }
        if (shop.SelectedEntry is { } entry) _session.ShopSell(entry, shop.Quantity);
    }

    private Button Row(int index)
    {
        while (_rows.Count <= index)
        {
            var at = _rows.Count;
            var button = new Button
            {
                AutowrapMode = TextServer.AutowrapMode.WordSmart, ClipText = false, Alignment = HorizontalAlignment.Left, ExpandIcon = false,
                IconAlignment = HorizontalAlignment.Left, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            button.Pressed += () => OnRow(at);
            _rows.Add(button);
            _rowsBox.AddChild(button);
            _interactive.Add(button);
            SizeRow(button);
        }
        var row = _rows[index];
        row.Visible = true;
        return row;
    }

    private void SizeRow(Button button)
    {
        button.CustomMinimumSize = new Vector2(_rowWidth, _target);
        button.AddThemeConstantOverride("icon_max_width", _iconSize);
    }

    private void OnRow(int at)
    {
        _tap();
        if (_samples) return;
        if (_session.Shop.Tab == ShopTab.Buy)
        {
            if (at < _rowItems.Count) _session.Shop.SelectItem(_rowItems[at]);
        }
        else if (at < _rowEntries.Count) _session.Shop.SelectEntry(_rowEntries[at]);
    }

    private Texture2D? IconOf(string itemId)
    {
        if (_bundle.Item(itemId)?.Icon is not { } key || IconLoader is null) return null;
        if (!_icons.TryGetValue(key, out var texture)) _icons[key] = texture = IconLoader(key);
        return texture;
    }

    private static Label Label(string text, HorizontalAlignment alignment = HorizontalAlignment.Left) =>
        new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = alignment, MouseFilter = Control.MouseFilterEnum.Ignore };
}
