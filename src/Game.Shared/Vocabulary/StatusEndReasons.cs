namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// Why a status ended before its time (<c>StatusRemoved</c>); a status that runs out raises <c>StatusExpired</c> instead.
    /// VOCABULARY.md: "Status end reasons".
    /// </summary>
    public static class StatusEndReasons
    {
        /// <summary><c>removed</c>: a <c>remove_status</c> effect cleansed it (by id or tag).</summary>
        public const string Removed = "removed";

        /// <summary><c>died</c>: its bearer died; every status ends on death.</summary>
        public const string Died = "died";

        /// <summary><c>warped</c>: its bearer moved to another map (statuses keep only within one map instance, whose tick clock times them).</summary>
        public const string Warped = "warped";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Removed,
            Died,
            Warped);
    }
}
