namespace Game.Shared.Vocabulary
{
    /// <summary>Why the server refused a trade intent (T-903, VOCABULARY.md "Trade reasons"): the <c>reason</c> of a <c>trade_refused</c>. Nothing changed.</summary>
    public static class TradeReasons
    {
        /// <summary><c>not_online</c>: the asked character does not play now or is not on this map instance; also what an ask to someone who blocks the asker answers (a block does not show).</summary>
        public const string NotOnline = "not_online";

        /// <summary><c>invalid_target</c>: an ask of oneself, or of something that is not a player.</summary>
        public const string InvalidTarget = "invalid_target";

        /// <summary><c>busy</c>: the asked character (for an answer: the asker) is in a trade or has an ask under way.</summary>
        public const string Busy = "busy";

        /// <summary><c>too_far</c>: farther apart than <c>trade.range</c> cells.</summary>
        public const string TooFar = "too_far";

        /// <summary><c>dead</c>: one of the two is dead.</summary>
        public const string Dead = "dead";

        /// <summary><c>ask_pending</c>: this character already waits for an answer to an ask.</summary>
        public const string AskPending = "ask_pending";

        /// <summary><c>no_ask</c>: there is no ask to answer: none came, it expired, or its asker left.</summary>
        public const string NoAsk = "no_ask";

        /// <summary><c>rate_limited</c>: too many asks in a short time; <c>retry_after</c> says when the next may go.</summary>
        public const string RateLimited = "rate_limited";

        /// <summary><c>not_trading</c>: an offer, lock, confirm or cancel without an open trade.</summary>
        public const string NotTrading = "not_trading";

        /// <summary><c>not_tradeable</c>: content marks the item as not tradeable (<c>tradeable: false</c>).</summary>
        public const string NotTradeable = "not_tradeable";

        /// <summary><c>item_gone</c>: no such entry is carried.</summary>
        public const string ItemGone = "item_gone";

        /// <summary><c>item_equipped</c>: the entry is worn; take it off first.</summary>
        public const string ItemEquipped = "item_equipped";

        /// <summary><c>invalid_quantity</c>: below 0, or more than the entry holds; a currency amount below 0 or above what is carried.</summary>
        public const string InvalidQuantity = "invalid_quantity";

        /// <summary><c>offer_full</c>: the offer has <c>trade.max_entries</c> entries.</summary>
        public const string OfferFull = "offer_full";

        /// <summary><c>not_locked</c>: a confirm before both sides locked their offers.</summary>
        public const string NotLocked = "not_locked";

        /// <summary><c>stale</c>: a confirm of offers that changed since the client saw them (its <c>version</c> is not the trade's).</summary>
        public const string Stale = "stale";

        /// <summary><c>swapping</c>: both confirmed and the swap is under way: nothing changes now.</summary>
        public const string Swapping = "swapping";

        /// <summary><c>cannot_trade</c>: one of the two is saved and the other is not (a development login): nothing may move between them.</summary>
        public const string CannotTrade = "cannot_trade";

        /// <summary>Every reason, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(NotOnline, InvalidTarget, Busy, TooFar, Dead, AskPending, NoAsk, RateLimited, NotTrading, NotTradeable, ItemGone,
            ItemEquipped, InvalidQuantity, OfferFull, NotLocked, Stale, Swapping, CannotTrade);
    }

    /// <summary>What a <c>trade_notice</c> tells about an ask (T-903, VOCABULARY.md "Trade notices").</summary>
    public static class TradeNotices
    {
        /// <summary><c>ask_sent</c>: the ask went out (to the asker).</summary>
        public const string AskSent = "ask_sent";

        /// <summary><c>declined</c>: the asked character said no (to the asker).</summary>
        public const string Declined = "declined";

        /// <summary><c>expired</c>: nobody answered in time, or the asker went away (to both).</summary>
        public const string Expired = "expired";

        /// <summary>Every notice, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(AskSent, Declined, Expired);
    }

    /// <summary>Why an open trade ended (T-903, VOCABULARY.md "Trade ends"): the <c>reason</c> of a <c>trade_closed</c>. Only <c>done</c> moved anything.</summary>
    public static class TradeEnds
    {
        /// <summary><c>done</c>: both confirmed the same offers and they were swapped.</summary>
        public const string Done = "done";

        /// <summary><c>cancelled</c>: a side cancelled.</summary>
        public const string Cancelled = "cancelled";

        /// <summary><c>too_far</c>: the two moved more than <c>trade.range</c> cells apart.</summary>
        public const string TooFar = "too_far";

        /// <summary><c>dead</c>: a side died.</summary>
        public const string Dead = "dead";

        /// <summary><c>left_map</c>: a side warped, to another map or another cell.</summary>
        public const string LeftMap = "left_map";

        /// <summary><c>disconnected</c>: a side's connection dropped, or another session took the character over.</summary>
        public const string Disconnected = "disconnected";

        /// <summary><c>left</c>: a side left the world (logged out, the server stopped, staff).</summary>
        public const string Left = "left";

        /// <summary><c>too_heavy</c>: at the swap, what a side would get passes its weight limit.</summary>
        public const string TooHeavy = "too_heavy";

        /// <summary><c>inventory_full</c>: at the swap, a side would have more than <c>inventory.max_entries</c> entries.</summary>
        public const string InventoryFull = "inventory_full";

        /// <summary><c>currency_full</c>: at the swap, a side's currency would pass the most it can hold.</summary>
        public const string CurrencyFull = "currency_full";

        /// <summary><c>offer_changed</c>: at the swap, a side no longer carried exactly what it offered.</summary>
        public const string OfferChanged = "offer_changed";

        /// <summary>Every end, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Done, Cancelled, TooFar, Dead, LeftMap, Disconnected, Left, TooHeavy, InventoryFull, CurrencyFull, OfferChanged);
    }
}
