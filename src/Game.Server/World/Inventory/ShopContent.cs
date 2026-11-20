using Game.Content.Registry;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Shop;

namespace Game.Server.World.Inventory;

/// <summary>
/// The shops the inventory system trades with (T-603): each shop's stock with its prices resolved (the stock's own price, else the item's <c>buy_price</c>), the buy limit
/// and the talking range. Immutable after construction, so shared by every instance. Prices exist only here: the client is told them in the <c>shop</c> message and never
/// sends one.
/// </summary>
public sealed class ShopContent
{
    private readonly Dictionary<string, Shop> _shops = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates shop content. Throws <see cref="ArgumentException"/> for a shop that lists an unknown item, lists one twice or has a price outside 1 to
    /// <see cref="ShopRules.MaxUnitPrice"/>, and <see cref="ArgumentOutOfRangeException"/> for a buy limit below 1 (the validator reports these first; this is the boot backstop).
    /// </summary>
    public ShopContent(IEnumerable<ShopDef> shops, IReadOnlyDictionary<string, ItemDef> items, ShopBalanceDef balance, int talkRange)
    {
        ArgumentNullException.ThrowIfNull(shops);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(balance);
        if (balance.MaxBuyQuantity < 1) throw new ArgumentOutOfRangeException(nameof(balance), "shop.max_buy_quantity must be at least 1.");
        MaxBuyQuantity = balance.MaxBuyQuantity;
        SellPrices = items.Values.Where(i => i.Kind != ItemKind.Quest && i.SellPrice >= 1).OrderBy(i => i.Id, StringComparer.Ordinal)
            .Select(i => new ShopStockRecord(i.Id, i.SellPrice)).ToList();
        TalkRange = talkRange;
        foreach (var def in shops)
        {
            var stock = new List<ShopStockRecord>(def.Stock.Count);
            var prices = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var row in def.Stock)
            {
                if (!items.TryGetValue(row.ItemId, out var item)) throw new ArgumentException($"{def.Id} sells unknown item '{row.ItemId}'.", nameof(shops));
                var price = row.Price ?? item.BuyPrice;
                if (price < 1 || price > ShopRules.MaxUnitPrice) throw new ArgumentException($"{def.Id}: the price of {row.ItemId} must be from 1 to {ShopRules.MaxUnitPrice}.", nameof(shops));
                if (!prices.TryAdd(row.ItemId, price)) throw new ArgumentException($"{def.Id} lists {row.ItemId} twice.", nameof(shops));
                stock.Add(new ShopStockRecord(row.ItemId, price));
            }
            _shops.Add(def.Id, new Shop(def.Id, stock, prices));
        }
    }

    /// <summary>The shops of a loaded database.</summary>
    public static ShopContent From(ContentDatabase content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ShopContent(content.Shops.All, content.Items.All.ToDictionary(i => i.Id, StringComparer.Ordinal), content.Balance.Shop, content.Balance.Dialogue.TalkRange);
    }

    /// <summary>Most units one <c>shop_buy</c> may ask for (<c>shop.max_buy_quantity</c>).</summary>
    public int MaxBuyQuantity { get; }

    /// <summary>What a shop pays per unit for every item it buys, by item id: items with a sell price, quest items excluded. The same for every shop.</summary>
    public IReadOnlyList<ShopStockRecord> SellPrices { get; }

    /// <summary>How close the player must stay to the shop's NPC (<c>dialogue.talk_range</c>, Chebyshev).</summary>
    public int TalkRange { get; }

    /// <summary>Finds a shop.</summary>
    public bool TryGet(string id, out Shop shop) => _shops.TryGetValue(id, out shop!);

    /// <summary>A shop with its stock priced.</summary>
    /// <param name="Id">Shop id.</param>
    /// <param name="Stock">What it sells, in display order, with the price per unit.</param>
    /// <param name="Prices">Price per unit by item id.</param>
    public sealed record Shop(string Id, IReadOnlyList<ShopStockRecord> Stock, IReadOnlyDictionary<string, long> Prices);
}
