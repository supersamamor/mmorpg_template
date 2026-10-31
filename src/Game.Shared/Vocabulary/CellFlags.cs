namespace Game.Shared.Vocabulary
{
    /// <summary>Flags a logic-grid cell can carry. VOCABULARY.md: "Cell flags".</summary>
    public static class CellFlags
    {
        /// <summary><c>walkable</c>: Entities may stand on and path through the cell.</summary>
        public const string Walkable = "walkable";

        /// <summary><c>blocks_los</c>: The cell blocks line of sight.</summary>
        public const string BlocksLos = "blocks_los";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Walkable,
            BlocksLos);
    }
}
