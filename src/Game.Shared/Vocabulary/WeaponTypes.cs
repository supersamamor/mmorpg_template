namespace Game.Shared.Vocabulary
{
    /// <summary>Weapon type ids. VOCABULARY.md: "Weapon types".</summary>
    public static class WeaponTypes
    {
        /// <summary><c>unarmed</c>: No weapon.</summary>
        public const string Unarmed = "unarmed";

        /// <summary><c>dagger</c>: Dagger.</summary>
        public const string Dagger = "dagger";

        /// <summary><c>sword_1h</c>: One-handed sword.</summary>
        public const string Sword1h = "sword_1h";

        /// <summary><c>sword_2h</c>: Two-handed sword.</summary>
        public const string Sword2h = "sword_2h";

        /// <summary><c>spear</c>: Spear.</summary>
        public const string Spear = "spear";

        /// <summary><c>axe</c>: Axe.</summary>
        public const string Axe = "axe";

        /// <summary><c>mace</c>: Mace.</summary>
        public const string Mace = "mace";

        /// <summary><c>staff</c>: Staff.</summary>
        public const string Staff = "staff";

        /// <summary><c>bow</c>: Bow.</summary>
        public const string Bow = "bow";

        /// <summary><c>knuckle</c>: Knuckle.</summary>
        public const string Knuckle = "knuckle";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Unarmed,
            Dagger,
            Sword1h,
            Sword2h,
            Spear,
            Axe,
            Mace,
            Staff,
            Bow,
            Knuckle);
    }
}
