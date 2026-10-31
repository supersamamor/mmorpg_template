namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// Every stat id a modifier, monster stat block or formula may name:
    /// <see cref="PrimaryStats"/> followed by <see cref="DerivedStats"/>.
    /// </summary>
    public static class Stats
    {
        /// <summary>Primary then derived stat ids, in documentation order.</summary>
        public static readonly VocabularySet All = VocabularySet.Union(PrimaryStats.All, DerivedStats.All);
    }
}
