namespace Game.Shared.Vocabulary
{
    /// <summary>How one resolved attack landed (<c>DamageDealt</c> <c>Result</c>). VOCABULARY.md: "Hit results".</summary>
    public static class HitResults
    {
        /// <summary><c>hit</c>: the attack landed.</summary>
        public const string Hit = "hit";

        /// <summary><c>crit</c>: the attack landed as a critical hit.</summary>
        public const string Crit = "crit";

        /// <summary><c>miss</c>: the attack missed; the amount is 0.</summary>
        public const string Miss = "miss";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Hit,
            Crit,
            Miss);
    }
}
