namespace Game.Shared.Vocabulary
{
    /// <summary>Item kind ids. Mirrors <see cref="Game.Shared.Definitions.ItemKind"/> (snake_case names). VOCABULARY.md: "Item kinds".</summary>
    public static class ItemKinds
    {
        /// <summary><c>consumable</c>: Used up for effects.</summary>
        public const string Consumable = "consumable";

        /// <summary><c>equip</c>: Worn in equipment slots.</summary>
        public const string Equip = "equip";

        /// <summary><c>card</c>: Socketed into equipment.</summary>
        public const string Card = "card";

        /// <summary><c>material</c>: Crafting / trade material.</summary>
        public const string Material = "material";

        /// <summary><c>quest</c>: Quest item.</summary>
        public const string Quest = "quest";

        /// <summary><c>ammo</c>: Ammunition.</summary>
        public const string Ammo = "ammo";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Consumable,
            Equip,
            Card,
            Material,
            Quest,
            Ammo);
    }
}
