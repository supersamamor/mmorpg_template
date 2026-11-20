namespace Game.Shared.Shop
{
    /// <summary>
    /// The pure arithmetic of buying and selling (T-603): totals and balances that never wrap around. Prices come from server data only; this is the one place a
    /// quantity meets a price, so the overflow guard lives here (hard rule 10: a transaction that cannot be exact does not happen).
    /// </summary>
    public static class ShopRules
    {
        /// <summary>The highest unit price content may set (one thousand million); a validated price times <c>max_buy_quantity</c> then stays far inside <see cref="long"/>.</summary>
        public const long MaxUnitPrice = 1_000_000_000L;

        /// <summary>
        /// <paramref name="unitPrice"/> × <paramref name="quantity"/> in <paramref name="total"/>. False (and 0) when either is below 1 or the product does not fit a
        /// <see cref="long"/>.
        /// </summary>
        public static bool TryTotal(long unitPrice, int quantity, out long total)
        {
            total = 0;
            if (unitPrice < 1 || quantity < 1) return false;
            if (unitPrice > long.MaxValue / quantity) return false;
            total = unitPrice * quantity;
            return true;
        }

        /// <summary>Whether <paramref name="balance"/> can pay <paramref name="total"/> (both non-negative).</summary>
        public static bool CanAfford(long balance, long total) => total >= 0 && balance >= total;

        /// <summary><paramref name="balance"/> + <paramref name="gain"/> in <paramref name="result"/>; false (and unchanged) when the sum does not fit a <see cref="long"/> or an input is negative.</summary>
        public static bool TryCredit(long balance, long gain, out long result)
        {
            result = balance;
            if (balance < 0 || gain < 0 || long.MaxValue - balance < gain) return false;
            result = balance + gain;
            return true;
        }
    }
}
