namespace Game.Shared.Vocabulary
{
    /// <summary>Monster size ids. VOCABULARY.md: "Monster races / sizes — sizes".</summary>
    public static class MonsterSizes
    {
        /// <summary><c>small</c>: Small.</summary>
        public const string Small = "small";

        /// <summary><c>medium</c>: Medium.</summary>
        public const string Medium = "medium";

        /// <summary><c>large</c>: Large.</summary>
        public const string Large = "large";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Small,
            Medium,
            Large);
    }
}
