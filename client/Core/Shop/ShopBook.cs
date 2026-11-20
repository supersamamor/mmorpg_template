using System;
using System.Collections.Generic;
using Game.Shared.Protocol;
using Game.Shared.Shop;

namespace Game.Client.Core.Shop;

/// <summary>Which list the shop panel shows.</summary>
public enum ShopTab
{
    /// <summary>The shop's stock, to buy.</summary>
    Buy,

    /// <summary>The player's bag, to sell.</summary>
    Sell,
}

/// <summary>
/// The shop the server opened for the player (T-603, ARCHITECTURE §6.8): its stock and prices, what it pays for each item, and the panel's tab, selection and
/// quantity. A plain holder the panel draws from. The totals it shows are a preview only (the server computes every price and refuses anything it cannot do), so the
/// arithmetic here is <see cref="ShopRules"/>, the same guard the server uses. <see cref="Version"/> moves on every change so the panel redraws only then.
/// </summary>
public sealed class ShopBook
{
    private readonly Dictionary<string, long> _sell = new(StringComparer.Ordinal);

    /// <summary>Moves on every change.</summary>
    public int Version { get; private set; }

    /// <summary>True from a <c>shop</c> message until the dialogue or service ends (the HUD also checks the dialogue's own service).</summary>
    public bool Open { get; private set; }

    /// <summary>The NPC entity the shop belongs to.</summary>
    public long Npc { get; private set; }

    /// <summary>The shop id (<c>shop.*</c>).</summary>
    public string ShopId { get; private set; } = string.Empty;

    /// <summary>Most units one purchase may ask for, as the server said.</summary>
    public int MaxQuantity { get; private set; } = 1;

    /// <summary>What it sells, in display order, with the unit prices.</summary>
    public IReadOnlyList<ShopStockRecord> Stock { get; private set; } = Array.Empty<ShopStockRecord>();

    /// <summary>The tab shown.</summary>
    public ShopTab Tab { get; private set; } = ShopTab.Buy;

    /// <summary>The item picked on the buy tab, or null.</summary>
    public string? SelectedItem { get; private set; }

    /// <summary>The bag entry picked on the sell tab, or null.</summary>
    public long? SelectedEntry { get; private set; }

    /// <summary>How many units the buy or sell would be for (at least 1).</summary>
    public int Quantity { get; private set; } = 1;

    /// <summary>A <c>shop</c> arrived: the panel opens on the buy tab with nothing picked.</summary>
    public void Apply(ShopMessage message)
    {
        Open = true;
        Npc = message.Npc;
        ShopId = message.Shop;
        MaxQuantity = Math.Max(1, message.MaxQuantity);
        Stock = message.Stock;
        _sell.Clear();
        foreach (var row in message.Sell) _sell[row.Item] = row.Price;
        Tab = ShopTab.Buy;
        SelectedItem = null;
        SelectedEntry = null;
        Quantity = 1;
        Version++;
    }

    /// <summary>The server ended the dialogue or service: the panel closes.</summary>
    public void Close()
    {
        if (!Open) return;
        Open = false;
        SelectedItem = null;
        SelectedEntry = null;
        Quantity = 1;
        Version++;
    }

    /// <summary>Forgets everything (a new login).</summary>
    public void Reset()
    {
        Close();
        Stock = Array.Empty<ShopStockRecord>();
        _sell.Clear();
    }

    /// <summary>Switches tab; the picks and the quantity start over.</summary>
    public void SetTab(ShopTab tab)
    {
        if (tab == Tab) return;
        Tab = tab;
        SelectedItem = null;
        SelectedEntry = null;
        Quantity = 1;
        Version++;
    }

    /// <summary>Picks a stock item to buy (picking it again clears it); the quantity starts at 1.</summary>
    public void SelectItem(string? item)
    {
        var next = item is not null && item != SelectedItem && PriceOf(item) is not null ? item : null;
        if (next == SelectedItem) return;
        SelectedItem = next;
        Quantity = 1;
        Version++;
    }

    /// <summary>Picks a bag entry to sell (picking it again clears it); the quantity starts at 1.</summary>
    public void SelectEntry(long? entry)
    {
        var next = entry is { } id && id != SelectedEntry ? entry : null;
        if (next == SelectedEntry) return;
        SelectedEntry = next;
        Quantity = 1;
        Version++;
    }

    /// <summary>Sets the quantity, clamped to 1 and <paramref name="most"/> (the purchase limit, or the stack being sold).</summary>
    public void SetQuantity(int quantity, int most)
    {
        var next = Math.Max(1, Math.Min(quantity, Math.Max(1, most)));
        if (next == Quantity) return;
        Quantity = next;
        Version++;
    }

    /// <summary>The price of one unit of a stock item, or null when the shop does not sell it.</summary>
    public long? PriceOf(string item)
    {
        for (var i = 0; i < Stock.Count; i++)
            if (string.Equals(Stock[i].Item, item, StringComparison.Ordinal)) return Stock[i].Price;
        return null;
    }

    /// <summary>What the shop pays for one unit of an item, or null when it does not buy it.</summary>
    public long? SellPriceOf(string item) => _sell.TryGetValue(item, out var price) ? price : null;

    /// <summary>The total for <paramref name="quantity"/> units at <paramref name="unit"/>, or null when it does not fit a number (the server would refuse it).</summary>
    public static long? Total(long unit, int quantity) => ShopRules.TryTotal(unit, quantity, out var total) ? total : null;
}
