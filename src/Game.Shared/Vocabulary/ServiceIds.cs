namespace Game.Shared.Vocabulary
{
    /// <summary>NPC services an <c>open_service</c> dialogue action can open, and the client opens a panel for. VOCABULARY.md: "Service ids".</summary>
    public static class ServiceIds
    {
        /// <summary><c>refine</c>: refining equipment. While it is open, the <c>refine</c> intent works (when <c>dialogue.refine_needs_service</c> is on).</summary>
        public const string Refine = "refine";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Refine);
    }
}
