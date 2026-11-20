using Game.Shared.Shop;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Shops (T-603): every shop sells at least one item, lists no item twice, and prices every item it lists from 1 to <see cref="ShopRules.MaxUnitPrice"/> (the stock's own
/// <c>price</c>, else the item's <c>buy_price</c>). Unknown items are the reference rule's. A dialogue's <c>shop_id</c> is checked by the dialogue and reference rules.
/// </summary>
public sealed class ShopRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "shops";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;
        foreach (var shop in db.Shops.All)
        {
            if (shop.Stock.Count == 0) context.Error(shop.Id, "stock", ValidationCodes.ShopInvalid, $"{shop.Id} sells nothing");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < shop.Stock.Count; i++)
            {
                var row = shop.Stock[i];
                var path = $"stock[{i}]";
                if (!seen.Add(row.ItemId)) context.Error(shop.Id, path + ".item_id", ValidationCodes.ShopInvalid, $"{shop.Id} lists {row.ItemId} twice");
                if (row.Price is { } own)
                {
                    if (own < 1 || own > ShopRules.MaxUnitPrice)
                        context.Error(shop.Id, path + ".price", ValidationCodes.ShopInvalid, $"price must be from 1 to {ShopRules.MaxUnitPrice}");
                }
                else if (db.Items.TryGet(row.ItemId, out var item) && (item.BuyPrice < 1 || item.BuyPrice > ShopRules.MaxUnitPrice))
                    context.Error(shop.Id, path + ".price", ValidationCodes.ShopInvalid,
                        $"{row.ItemId} has no usable buy_price ({item.BuyPrice}); give the stock row a price from 1 to {ShopRules.MaxUnitPrice}");
            }
        }
    }
}
