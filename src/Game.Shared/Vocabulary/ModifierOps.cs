namespace Game.Shared.Vocabulary
{
    /// <summary>Modifier operations (ARCHITECTURE §6.1). Mirrors <see cref="Game.Shared.Definitions.ModifierOp"/>. VOCABULARY.md: "Modifier ops".</summary>
    public static class ModifierOps
    {
        /// <summary><c>flat</c>: Added to the base value.</summary>
        public const string Flat = "flat";

        /// <summary><c>percent</c>: Summed, then applied as <c>× (1 + Σpercent)</c>.</summary>
        public const string Percent = "percent";

        /// <summary><c>override</c>: Replaces the final value.</summary>
        public const string Override = "override";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Flat,
            Percent,
            Override);
    }
}
