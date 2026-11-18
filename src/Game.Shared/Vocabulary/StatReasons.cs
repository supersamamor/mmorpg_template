namespace Game.Shared.Vocabulary
{
    /// <summary>Why a <c>raise_stat</c> was refused (<c>StatRaiseRefused</c>, and <c>rejected</c> with <c>intent</c> <c>raise_stat</c>). VOCABULARY.md: "Stat reasons".</summary>
    public static class StatReasons
    {
        /// <summary><c>unknown_stat</c>: not one of the six primary stat ids.</summary>
        public const string UnknownStat = "unknown_stat";

        /// <summary><c>invalid_amount</c>: the amount is below 1 or above the most a single intent may raise.</summary>
        public const string InvalidAmount = "invalid_amount";

        /// <summary><c>at_max_value</c>: the raise would pass <c>progression.max_stat_value</c>.</summary>
        public const string AtMaxValue = "at_max_value";

        /// <summary><c>not_enough_points</c>: fewer unspent stat points than the raise costs.</summary>
        public const string NotEnoughPoints = "not_enough_points";

        /// <summary><c>dead</c>: the character is dead.</summary>
        public const string Dead = "dead";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(UnknownStat, InvalidAmount, AtMaxValue, NotEnoughPoints, Dead);

        /// <summary>The most steps one <c>raise_stat</c> may ask for (a bound on the cost loop, not a balance number).</summary>
        public const int MaxAmount = 99;
    }
}
