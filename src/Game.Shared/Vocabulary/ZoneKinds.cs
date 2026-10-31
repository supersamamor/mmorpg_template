namespace Game.Shared.Vocabulary
{
    /// <summary>Kinds of map zones (map object type <c>zone</c>). VOCABULARY.md: "Map object types — zone kinds".</summary>
    public static class ZoneKinds
    {
        /// <summary><c>safe</c>: No combat.</summary>
        public const string Safe = "safe";

        /// <summary><c>pvp</c>: Player-versus-player allowed.</summary>
        public const string Pvp = "pvp";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Safe,
            Pvp);
    }
}
