namespace Game.Shared.Vocabulary
{
    /// <summary>Character genders chosen at creation: one body type and rig each (cosmetic only). VOCABULARY.md: "Genders".</summary>
    public static class Genders
    {
        /// <summary><c>male</c>: Male body type.</summary>
        public const string Male = "male";

        /// <summary><c>female</c>: Female body type.</summary>
        public const string Female = "female";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Male,
            Female);
    }
}
