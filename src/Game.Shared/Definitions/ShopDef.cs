using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// A shop (T-603): what an NPC's <c>shop</c> service sells. A dialogue opens it with an <c>open_service</c> action whose <c>service</c> is <c>shop</c> and whose
    /// <c>shop_id</c> names this definition. Players <b>buy</b> the items listed here, each at its <see cref="ShopStockDef.Price"/> (or, without one, the item's
    /// <c>buy_price</c>); players <b>sell</b> any carried item that has a <c>sell_price</c> to any shop, at that price (selling is not limited to the stock).
    /// </summary>
    public sealed record ShopDef : IDefinition
    {
        /// <summary>Id namespace for shops.</summary>
        public const string IdPrefix = IdPrefixes.Shop;

        /// <summary>Shop id, e.g. <c>shop.meadow_goods</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Editor-facing name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>The items for sale, in the order the client lists them. At least one; no item twice.</summary>
        public IReadOnlyList<ShopStockDef> Stock { get; init; } = Empty.List<ShopStockDef>();
    }

    /// <summary>One item a shop sells.</summary>
    public sealed record ShopStockDef
    {
        /// <summary>Item id (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>Price per unit in currency, above 0; overrides the item's <c>buy_price</c> in this shop. Left out: the item's <c>buy_price</c> (which must then be above 0).</summary>
        public long? Price { get; init; }
    }

    /// <summary>The <c>shop</c> section of <c>balance.yaml</c> (T-603).</summary>
    public sealed record ShopBalanceDef
    {
        /// <summary>Most units one <c>shop_buy</c> may ask for; more is refused <c>quantity_limit</c>. At least 1.</summary>
        public int MaxBuyQuantity { get; init; } = 99;
    }
}
