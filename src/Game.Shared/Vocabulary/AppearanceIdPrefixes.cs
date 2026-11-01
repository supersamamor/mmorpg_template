namespace Game.Shared.Vocabulary
{
    /// <summary>Id namespaces of character-creation options, defined inside <c>appearance.yaml</c> (not registry definitions). VOCABULARY.md: "Appearance option namespaces".</summary>
    public static class AppearanceIdPrefixes
    {
        /// <summary><c>face.</c>: Face variant (<c>face.male_calm</c>).</summary>
        public const string Face = "face.";

        /// <summary><c>hair.</c>: Hairstyle (<c>hair.male_crop</c>).</summary>
        public const string Hair = "hair.";

        /// <summary><c>skin.</c>: Skin tone (<c>skin.fair</c>).</summary>
        public const string Skin = "skin.";

        /// <summary><c>haircolor.</c>: Hair colour (<c>haircolor.chestnut</c>).</summary>
        public const string HairColor = "haircolor.";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Face,
            Hair,
            Skin,
            HairColor);
    }
}
