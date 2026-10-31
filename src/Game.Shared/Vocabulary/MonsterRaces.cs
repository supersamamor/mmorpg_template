namespace Game.Shared.Vocabulary
{
    /// <summary>Monster race ids. VOCABULARY.md: "Monster races / sizes — races".</summary>
    public static class MonsterRaces
    {
        /// <summary><c>formless</c>: Formless.</summary>
        public const string Formless = "formless";

        /// <summary><c>beast</c>: Beast.</summary>
        public const string Beast = "beast";

        /// <summary><c>plant</c>: Plant.</summary>
        public const string Plant = "plant";

        /// <summary><c>insect</c>: Insect.</summary>
        public const string Insect = "insect";

        /// <summary><c>fish</c>: Fish.</summary>
        public const string Fish = "fish";

        /// <summary><c>demon</c>: Demon.</summary>
        public const string Demon = "demon";

        /// <summary><c>undead</c>: Undead.</summary>
        public const string Undead = "undead";

        /// <summary><c>humanoid</c>: Humanoid.</summary>
        public const string Humanoid = "humanoid";

        /// <summary><c>dragon</c>: Dragon.</summary>
        public const string Dragon = "dragon";

        /// <summary><c>angel</c>: Angel.</summary>
        public const string Angel = "angel";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Formless,
            Beast,
            Plant,
            Insect,
            Fish,
            Demon,
            Undead,
            Humanoid,
            Dragon,
            Angel);
    }
}
