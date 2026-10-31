namespace Game.Shared.Vocabulary
{
    /// <summary>Primary stat ids. VOCABULARY.md: "Primary stats".</summary>
    public static class PrimaryStats
    {
        /// <summary><c>str</c>: Strength: melee ATK, carry weight.</summary>
        public const string Str = "str";

        /// <summary><c>agi</c>: Agility: ASPD, FLEE.</summary>
        public const string Agi = "agi";

        /// <summary><c>vit</c>: Vitality: MaxHP, DEF, HP regen, status resist.</summary>
        public const string Vit = "vit";

        /// <summary><c>int</c>: Intellect: MATK, MaxSP, SP regen, MDEF.</summary>
        public const string Int = "int";

        /// <summary><c>dex</c>: Dexterity: HIT, ranged ATK, cast time.</summary>
        public const string Dex = "dex";

        /// <summary><c>luk</c>: Luck: CRIT, perfect dodge, status chance.</summary>
        public const string Luk = "luk";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Str,
            Agi,
            Vit,
            Int,
            Dex,
            Luk);
    }
}
