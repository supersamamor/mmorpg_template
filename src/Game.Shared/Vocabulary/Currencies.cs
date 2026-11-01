namespace Game.Shared.Vocabulary
{
    /// <summary>Currency ids. The display name (default "crowns", balance.yaml currency_name) is data; the engine id is fixed. VOCABULARY.md: "Currency".</summary>
    public static class Currencies
    {
        /// <summary><c>currency</c>: Engine id of the single currency.</summary>
        public const string Currency = "currency";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Currency);
    }
}
