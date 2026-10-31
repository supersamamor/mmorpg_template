namespace Game.Shared.Vocabulary
{
    /// <summary>Element ids. Multipliers live in <c>content/elements.yaml</c>. VOCABULARY.md: "Elements".</summary>
    public static class Elements
    {
        /// <summary><c>neutral</c>: Neutral element.</summary>
        public const string Neutral = "neutral";

        /// <summary><c>fire</c>: Fire element.</summary>
        public const string Fire = "fire";

        /// <summary><c>water</c>: Water element.</summary>
        public const string Water = "water";

        /// <summary><c>earth</c>: Earth element.</summary>
        public const string Earth = "earth";

        /// <summary><c>wind</c>: Wind element.</summary>
        public const string Wind = "wind";

        /// <summary><c>holy</c>: Holy element.</summary>
        public const string Holy = "holy";

        /// <summary><c>shadow</c>: Shadow element.</summary>
        public const string Shadow = "shadow";

        /// <summary><c>poison</c>: Poison element.</summary>
        public const string Poison = "poison";

        /// <summary><c>ghost</c>: Ghost element.</summary>
        public const string Ghost = "ghost";

        /// <summary><c>undead</c>: Undead element.</summary>
        public const string Undead = "undead";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Neutral,
            Fire,
            Water,
            Earth,
            Wind,
            Holy,
            Shadow,
            Poison,
            Ghost,
            Undead);
    }
}
