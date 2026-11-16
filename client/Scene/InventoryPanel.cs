using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Items;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The inventory and equipment panel (T-401, ARCHITECTURE §7.4): the weight bar, the ten equipment slots, the carried items with icon, name and count,
/// and the actions for the selected item. Equipment shows its refine level and card slots (T-402); a card offers the equipment it fits, and Refine shows
/// the next step's chance, material and failure outcome from the bundle before it is sent. Code-built Godot controls inside the HUD's safe area, sized in dp times the UI scale like the rest of the HUD.
/// It only sends intents through <see cref="ClientSession"/>; what changes comes back from the server. It redraws only when the inventory's
/// <c>Version</c> moves or the layout changes, and does nothing while closed.
/// </summary>
public sealed class InventoryPanel
{
    private const double CellDp = 150;
    private const double IconDp = 32;
    private const double PanelMaxWidthDp = 680;   // a tablet or desktop window gets a panel, not a wall of buttons

    private readonly ClientSession _session;
    private readonly DisplayBundle _bundle;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly CenterContainer _center = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _panel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly StyleBoxFlat _panelStyle = new() { BgColor = new Color(0.1f, 0.11f, 0.13f, 0.95f) };   // nearly opaque: the hotbar must not show through
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _title = Label(UiText.BagTitle);
    private readonly Label _count = Label(string.Empty);
    private readonly Button _close = new() { Text = UiText.Close };
    private readonly Label _weight = Label(string.Empty);
    private readonly ProgressBar _weightBar = new() { ShowPercentage = false, MaxValue = 1, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _content = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly GridContainer _slotsGrid = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly GridContainer _itemsGrid = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _hint = Label(UiText.PickAnItem);
    private readonly HFlowContainer _actions = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Button _use = new() { Text = UiText.Use };
    private readonly Button _equip = new() { Text = UiText.Equip };
    private readonly Button _unequip = new() { Text = UiText.Unequip };
    private readonly Button _drop = new() { Text = UiText.Drop };
    private readonly Button _insertCard = new() { Text = UiText.InsertCard };
    private readonly Button _refine = new() { Text = UiText.Refine };
    private readonly Button _confirm = new() { Text = UiText.ConfirmRefine };
    private readonly Button _cancel = new() { Text = UiText.Cancel };
    private readonly GridContainer _choicesGrid = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly List<Button> _choiceButtons = new();
    private readonly List<long> _choiceIds = new();
    private readonly Dictionary<string, Button> _slotButtons = new(StringComparer.Ordinal);
    private readonly List<Button> _itemButtons = new();
    private readonly List<long> _itemIds = new();
    private Mode _mode = Mode.Actions;
    private readonly Dictionary<string, Texture2D?> _icons = new(StringComparer.Ordinal);
    private int _shownVersion = -1;
    private float _cellWidth = 150;
    private float _target = 48;
    private bool _samples;
    private string _sampleName = string.Empty;

    /// <summary>Builds the panel (closed) under <paramref name="parent"/>; every button it makes goes into <paramref name="interactive"/>.</summary>
    public InventoryPanel(Control parent, ClientSession session, DisplayBundle bundle, List<Control> interactive, Action tap)
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
        _box.AddChild(_count);
        _box.AddChild(_weight);
        _weightBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.85f, 0.7f, 0.35f) });
        _box.AddChild(_weightBar);

        _box.AddChild(_scroll);
        _scroll.AddChild(_content);
        _content.AddChild(_choicesGrid);
        _content.AddChild(_slotsGrid);
        _content.AddChild(_itemsGrid);
        foreach (var slot in EquipSlots.All.Ordered)
        {
            var button = NewButton();
            var name = slot;
            button.Pressed += () => OnSlot(name);
            _slotButtons[slot] = button;
            _slotsGrid.AddChild(button);
        }

        _box.AddChild(_hint);
        foreach (var action in ActionButtons)
        {
            _actions.AddChild(action);
            _interactive.Add(action);
        }
        _box.AddChild(_actions);
        _use.Pressed += () => Act(entry => _session.UseItem(entry.Id));
        _equip.Pressed += () => Act(entry => _session.EquipItem(entry.Id));
        _unequip.Pressed += () => Act(entry => _session.UnequipSlot(entry.Slot!));
        _drop.Pressed += () => Act(entry => _session.DropItem(entry.Id, entry.Quantity));
        _insertCard.Pressed += () => SetMode(Mode.CardTargets);
        _refine.Pressed += () => SetMode(Mode.RefineOffer);
        _cancel.Pressed += () => SetMode(Mode.Actions);
        _confirm.Pressed += () =>
        {
            _tap();
            if (_samples || _session.Inventory.SelectedEntry is not { } entry) return;
            _session.Refine(entry.Id);   // the server rolls; the selection stays, so the next step shows when the result is in
            SetMode(Mode.Actions, tap: false);
        };
        _close.Pressed += () => { _tap(); SetOpen(false); };
        _interactive.Add(_close);
        _interactive.Add(_panel);
        _interactive.Add(_scroll);
    }

    /// <summary>Finds an icon texture by asset key (the theme), or null; the HUD sets it once the theme is loaded.</summary>
    public Func<string, Texture2D?>? IconLoader { get; set; }

    /// <summary>True while the panel is open.</summary>
    public bool Open => _center.Visible;

    /// <summary>The equipment slot buttons, in "Equipment slots" order (for the scripted run).</summary>
    public IReadOnlyDictionary<string, Button> SlotButtons => _slotButtons;

    /// <summary>The carried-item buttons now shown, with the entry each one stands for (for the scripted run).</summary>
    public IReadOnlyList<(Button Button, long Entry)> ItemButtons
    {
        get
        {
            var list = new List<(Button, long)>();
            for (var i = 0; i < _itemIds.Count; i++) list.Add((_itemButtons[i], _itemIds[i]));
            return list;
        }
    }

    /// <summary>The action buttons (for the scripted run).</summary>
    public (Button Use, Button Equip, Button Unequip, Button Drop) Actions => (_use, _equip, _unequip, _drop);

    /// <summary>The card and refine buttons: Set card, Refine, the refine confirmation and Cancel (for the scripted run).</summary>
    public (Button InsertCard, Button Refine, Button Confirm, Button Cancel) CardAndRefine => (_insertCard, _refine, _confirm, _cancel);

    /// <summary>The equipment a selected card is offered for, with the entry each button stands for (for the scripted run).</summary>
    public IReadOnlyList<(Button Button, long Entry)> CardTargetButtons
    {
        get
        {
            var list = new List<(Button, long)>();
            for (var i = 0; i < _choiceIds.Count; i++) list.Add((_choiceButtons[i], _choiceIds[i]));
            return list;
        }
    }

    /// <summary>The line under the items: the hint, the selected item, a card's targets or the refine offer (for the scripted run).</summary>
    public string HintText => _hint.Text;

    private Button[] ActionButtons => new[] { _use, _equip, _unequip, _drop, _insertCard, _refine, _confirm, _cancel };

    /// <summary>Opens or closes the panel; opening clears the selection.</summary>
    public void SetOpen(bool open)
    {
        if (open == _center.Visible) return;
        _center.Visible = open;
        if (open) _session.Inventory.Select(null);
        _mode = Mode.Actions;
        _shownVersion = -1;
        Refresh();
    }

    /// <summary>Lays the panel out for the HUD's safe area (canvas units) with <paramref name="dp"/> as dp times scale and units, and the touch target height.</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Vector2 safeSize, int fontSize)
    {
        _target = target;
        var margin = dp(8);
        var width = Mathf.Min(dp(PanelMaxWidthDp), safeSize.X - 2 * margin);
        var height = safeSize.Y - 2 * margin;
        _panel.CustomMinimumSize = new Vector2(width, height);
        var gap = Mathf.RoundToInt(dp(6));
        _panelStyle.SetContentMarginAll(dp(8));   // the label widths below assume 8 dp of padding on each side
        _panelStyle.SetCornerRadiusAll(Mathf.RoundToInt(dp(6)));
        _box.AddThemeConstantOverride("separation", gap);
        _content.AddThemeConstantOverride("separation", gap * 2);
        foreach (var grid in new[] { _slotsGrid, _itemsGrid, _choicesGrid })
        {
            grid.AddThemeConstantOverride("h_separation", gap);
            grid.AddThemeConstantOverride("v_separation", gap);
        }
        // As many columns as fit the panel's inner width (minus room for the scroll bar), at least one; each cell is as wide as that allows.
        var inner = width - dp(16) - dp(12);
        // Every label wraps at a width fixed here, so its height is right the first time it is laid out (no waiting for the containers to settle).
        _title.CustomMinimumSize = new Vector2(Mathf.Max(0, width - dp(24) - dp(96) - gap), 0);
        foreach (var label in new[] { _count, _weight, _hint }) label.CustomMinimumSize = new Vector2(width - dp(24), 0);
        var columns = Mathf.Max(1, Mathf.FloorToInt((inner + gap) / (dp(CellDp) + gap)));
        _cellWidth = Mathf.Floor((inner - gap * (columns - 1)) / columns);
        _slotsGrid.Columns = columns;
        _itemsGrid.Columns = columns;
        _choicesGrid.Columns = columns;
        _weightBar.CustomMinimumSize = new Vector2(0, dp(10));
        _close.CustomMinimumSize = new Vector2(dp(96), target);
        _actions.AddThemeConstantOverride("h_separation", gap);
        _actions.AddThemeConstantOverride("v_separation", gap);
        foreach (var action in ActionButtons) action.CustomMinimumSize = new Vector2(dp(80), target);
        foreach (var button in _slotButtons.Values) Size(button, fontSize);
        foreach (var button in _itemButtons) Size(button, fontSize);
        foreach (var button in _choiceButtons) Size(button, fontSize);
        _iconSize = Mathf.RoundToInt(dp(IconDp));
        // Last: a control's size is clamped to its minimum, so the container is sized only once the panel's new (smaller) minimum is set.
        _center.Position = Vector2.Zero;
        _center.Size = safeSize;
        _shownVersion = -1;
    }

    private int _iconSize = 32;
    private long? _shownSelection;
    private readonly List<string> _cardNames = new();

    /// <summary>What the area under the items shows: the selected item's actions, a card's targets, or a refine offer to confirm.</summary>
    private enum Mode
    {
        Actions,
        CardTargets,
        RefineOffer,
    }

    /// <summary>Redraws from the inventory when it changed. Nothing while closed; allocates only when something changed.</summary>
    public void Refresh()
    {
        if (!_center.Visible) return;
        var inventory = _session.Inventory;
        if (_shownVersion == inventory.Version && !_samples) return;
        _shownVersion = inventory.Version;
        if (_samples)
        {
            DrawSamples();
            return;
        }

        _count.Text = UiText.EntryCount(inventory.Entries.Count, inventory.Slots);
        _weight.Text = UiText.Weight(inventory.Weight, inventory.WeightLimit, inventory.Load);
        _weightBar.Value = inventory.WeightFraction;
        _weightBar.Modulate = inventory.Load switch
        {
            WeightStates.Overweight => new Color(1f, 0.45f, 0.4f),
            WeightStates.Burdened => new Color(1f, 0.8f, 0.45f),
            _ => Colors.White,
        };
        var selected = inventory.SelectedEntry;
        if (selected is null || _shownSelection != selected.Id) _mode = Mode.Actions;   // a new selection starts with its actions
        _shownSelection = selected?.Id;
        var choosing = _mode != Mode.Actions;
        _slotsGrid.Visible = _itemsGrid.Visible = !choosing;
        _choicesGrid.Visible = _mode == Mode.CardTargets;

        foreach (var (slot, button) in _slotButtons)
        {
            var worn = inventory.InSlot(slot);
            button.Text = UiText.SlotButton(slot, worn is null ? null : Line(worn));
            button.Icon = worn is null ? null : IconOf(worn.Item);
            button.Modulate = worn is null ? new Color(1, 1, 1, 0.65f) : worn.Id == inventory.Selected ? new Color(1f, 0.9f, 0.5f) : Colors.White;
        }

        _itemIds.Clear();
        var shown = 0;
        var entries = inventory.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Slot is not null) continue;
            var button = ItemButton(shown++);
            button.Text = Line(entry);
            button.Icon = IconOf(entry.Item);
            button.Modulate = inventory.Selected == entry.Id ? new Color(1f, 0.9f, 0.5f) : Colors.White;
            button.Visible = true;
            _itemIds.Add(entry.Id);
        }
        for (var i = shown; i < _itemButtons.Count; i++) _itemButtons[i].Visible = false;

        var actions = selected is null ? ItemActions.None : InventoryView.ActionsFor(selected, _bundle.Item(selected.Item));
        _hint.Text = selected is null ? UiText.PickAnItem : Line(selected);
        _use.Visible = !choosing && (actions & ItemActions.Use) != 0;
        _equip.Visible = !choosing && (actions & ItemActions.Equip) != 0;
        _unequip.Visible = !choosing && (actions & ItemActions.Unequip) != 0;
        _drop.Visible = !choosing && (actions & ItemActions.Drop) != 0;
        _insertCard.Visible = !choosing && (actions & ItemActions.InsertCard) != 0;
        _refine.Visible = !choosing && (actions & ItemActions.Refine) != 0;
        _cancel.Visible = choosing;
        _confirm.Visible = false;
        _choiceIds.Clear();
        if (selected is not null && _mode == Mode.CardTargets) DrawCardTargets(selected);
        else if (selected is not null && _mode == Mode.RefineOffer) DrawRefineOffer(selected);
        for (var i = _choiceIds.Count; i < _choiceButtons.Count; i++) _choiceButtons[i].Visible = false;
    }

    /// <summary>A card's targets: the equipment it fits that has a free card slot, carried or worn. A tap on one sends <c>insert_card</c>.</summary>
    private void DrawCardTargets(InventoryEntryRecord card)
    {
        var targets = _session.Inventory.CardTargets(_bundle.Item(card.Item), _bundle.Item);
        _hint.Text = UiText.ItemCount(_bundle.NameOf(card.Item), card.Quantity) + ". " + (targets.Count > 0 ? UiText.PickEquipmentForCard : UiText.NothingFitsCard);
        for (var i = 0; i < targets.Count; i++)
        {
            var button = ChoiceButton(i);
            button.Text = UiText.CardTarget(Line(targets[i]), targets[i].Slot);
            button.Icon = IconOf(targets[i].Item);
            button.Visible = true;
            _choiceIds.Add(targets[i].Id);
        }
    }

    /// <summary>The next refine step from the bundle (chance, material and what a failure does), and the button that sends <c>refine</c>.</summary>
    private void DrawRefineOffer(InventoryEntryRecord entry)
    {
        var name = Line(entry);
        if (_bundle.RefineStepTo(entry.Refine + 1) is not { } step)
        {
            _hint.Text = UiText.RefineMaxed(name);
            return;
        }
        _hint.Text = UiText.RefineOffer(name, step.Level, step.SuccessPercent, _bundle.NameOf(step.Material), step.Quantity,
            _session.Inventory.Carried(step.Material), step.Failure);
        _confirm.Visible = true;
    }

    /// <summary>What an entry is called in the panel: name and stack count, and for equipment its refine level and card slots.</summary>
    private string Line(InventoryEntryRecord entry)
    {
        var item = _bundle.Item(entry.Item);
        var name = _bundle.NameOf(entry.Item);
        if (item?.Kind != ItemKinds.Equip) return UiText.ItemCount(name, entry.Quantity);
        _cardNames.Clear();
        for (var i = 0; i < entry.CardCount; i++) _cardNames.Add(_bundle.NameOf(entry.Cards![i]));
        return UiText.EquipmentLine(name, entry.Refine, _cardNames, item.CardSlots);
    }

    private void SetMode(Mode mode, bool tap = true)
    {
        if (tap) _tap();
        if (_samples) return;
        _mode = _session.Inventory.SelectedEntry is null ? Mode.Actions : mode;
        _shownVersion = -1;
        Refresh();
    }

    private Button ChoiceButton(int index)
    {
        while (_choiceButtons.Count <= index)
        {
            var button = NewButton();
            var at = _choiceButtons.Count;
            button.Pressed += () => OnChoice(at);
            _choiceButtons.Add(button);
            _choicesGrid.AddChild(button);
            Size(button, 0);
        }
        return _choiceButtons[index];
    }

    /// <summary>A card target was tapped: socket the selected card there. The panel goes back to the bag; the server's answer updates it.</summary>
    private void OnChoice(int index)
    {
        _tap();
        if (_samples || index >= _choiceIds.Count || _session.Inventory.SelectedEntry is not { } card) return;
        _session.InsertCard(_choiceIds[index], card.Id);
        _session.Inventory.Select(null);
        _mode = Mode.Actions;
        Refresh();
    }

    /// <summary>Fills the panel with the longest words it can show (and opens it), so a layout check sees the worst case; <see cref="ClearSamples"/> ends it.</summary>
    public void ShowSamples(string longName)
    {
        _samples = true;
        _sampleName = longName;
        _center.Visible = true;
        Refresh();
    }

    /// <summary>Ends <see cref="ShowSamples"/> and closes the panel.</summary>
    public void ClearSamples()
    {
        _samples = false;
        SetOpen(false);
    }

    private void DrawSamples()
    {
        _count.Text = UiText.EntryCount(100, 100);
        _weight.Text = UiText.Weight(99999, 99999, WeightStates.Overweight);
        _weightBar.Value = 1;
        foreach (var (slot, button) in _slotButtons) button.Text = UiText.SlotButton(slot, _sampleName);
        _itemIds.Clear();
        for (var i = 0; i < 12; i++)
        {
            var button = ItemButton(i);
            button.Text = UiText.ItemCount(_sampleName, 999);
            button.Visible = true;
            _itemIds.Add(-1 - i);
        }
        for (var i = 12; i < _itemButtons.Count; i++) _itemButtons[i].Visible = false;
        // The longest line the hint shows (a refine offer), and every action of the normal row at once.
        _hint.Text = UiText.RefineOffer(UiText.EquipmentLine(_sampleName, 99, new[] { _sampleName }, 2), 99, 100, _sampleName, 999, 999, RefineFailures.Break);
        _use.Visible = _equip.Visible = _unequip.Visible = _drop.Visible = _insertCard.Visible = _refine.Visible = true;
        _confirm.Visible = _cancel.Visible = false;
        _choicesGrid.Visible = false;
        _slotsGrid.Visible = _itemsGrid.Visible = true;
    }

    private Button ItemButton(int index)
    {
        while (_itemButtons.Count <= index)
        {
            var button = NewButton();
            var at = _itemButtons.Count;
            button.Pressed += () => OnItem(at);
            _itemButtons.Add(button);
            _itemsGrid.AddChild(button);
            Size(button, 0);
        }
        return _itemButtons[index];
    }

    private Button NewButton()
    {
        var button = new Button
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart, ClipText = false, Alignment = HorizontalAlignment.Left, ExpandIcon = false,
            IconAlignment = HorizontalAlignment.Left,
        };
        _interactive.Add(button);
        return button;
    }

    private void Size(Button button, int fontSize)
    {
        button.CustomMinimumSize = new Vector2(_cellWidth, _target);
        button.AddThemeConstantOverride("icon_max_width", _iconSize);
    }

    private Texture2D? IconOf(string itemId)
    {
        if (_bundle.Item(itemId)?.Icon is not { } key || IconLoader is null) return null;
        if (!_icons.TryGetValue(key, out var texture)) _icons[key] = texture = IconLoader(key);
        return texture;
    }

    private void OnItem(int index)
    {
        _tap();
        if (_samples || index >= _itemIds.Count) return;
        _session.Inventory.Select(_itemIds[index]);
        Refresh();
    }

    /// <summary>
    /// A tap on a worn slot selects what is worn there, like a carried item, so its actions show: Take off and, for refinable gear, Refine (T-402; T-401 took
    /// it off at once). An empty slot does nothing.
    /// </summary>
    private void OnSlot(string slot)
    {
        _tap();
        if (_samples || _session.Inventory.InSlot(slot) is not { } worn) return;
        _session.Inventory.Select(worn.Id);
        Refresh();
    }

    private void Act(Action<InventoryEntryRecord> send)
    {
        _tap();
        if (_samples || _session.Inventory.SelectedEntry is not { } entry) return;
        send(entry);
        _session.Inventory.Select(null);
        Refresh();
    }

    private static Label Label(string text, HorizontalAlignment alignment = HorizontalAlignment.Left) =>
        new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = alignment, MouseFilter = Control.MouseFilterEnum.Ignore };
}
