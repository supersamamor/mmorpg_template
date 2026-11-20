namespace Game.Shared.Vocabulary
{
    /// <summary>NPC services an <c>open_service</c> dialogue action can open, and the client opens a panel for. VOCABULARY.md: "Service ids".</summary>
    public static class ServiceIds
    {
        /// <summary><c>refine</c>: refining equipment. While it is open, the <c>refine</c> intent works (when <c>dialogue.refine_needs_service</c> is on).</summary>
        public const string Refine = "refine";

        /// <summary><c>shop</c> (T-603): buying and selling. The <c>open_service</c> action names the shop (<c>shop_id</c>); while it is open the <c>shop_buy</c> and <c>shop_sell</c> intents work.</summary>
        public const string Shop = "shop";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Refine, Shop);
    }
}
