namespace Game.Shared.Vocabulary
{
    /// <summary>Functions formulas may call (whitelist). VOCABULARY.md: "Formula variables (whitelist) — functions".</summary>
    public static class FormulaFunctions
    {
        /// <summary><c>min</c>: <c>min</c> function.</summary>
        public const string Min = "min";

        /// <summary><c>max</c>: <c>max</c> function.</summary>
        public const string Max = "max";

        /// <summary><c>floor</c>: <c>floor</c> function.</summary>
        public const string Floor = "floor";

        /// <summary><c>ceil</c>: <c>ceil</c> function.</summary>
        public const string Ceil = "ceil";

        /// <summary><c>round</c>: <c>round</c> function.</summary>
        public const string Round = "round";

        /// <summary><c>clamp</c>: <c>clamp</c> function.</summary>
        public const string Clamp = "clamp";

        /// <summary><c>pow</c>: <c>pow</c> function.</summary>
        public const string Pow = "pow";

        /// <summary><c>sqrt</c>: <c>sqrt</c> function.</summary>
        public const string Sqrt = "sqrt";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Min,
            Max,
            Floor,
            Ceil,
            Round,
            Clamp,
            Pow,
            Sqrt);
    }
}
