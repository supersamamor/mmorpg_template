namespace Game.Shared.Vocabulary
{
    /// <summary>Why a monster gained or lost a target (<c>AggroGained</c>, <c>AggroLost</c>). VOCABULARY.md: "Aggro reasons".</summary>
    public static class AggroReasons
    {
        /// <summary><c>sighted</c>: gained: an aggressive monster saw a player within its aggro range.</summary>
        public const string Sighted = "sighted";

        /// <summary><c>attacked</c>: gained: the monster was damaged by the target.</summary>
        public const string Attacked = "attacked";

        /// <summary><c>target_gone</c>: lost: the target left the map, died or was despawned.</summary>
        public const string TargetGone = "target_gone";

        /// <summary><c>leash</c>: lost: the monster got farther from home than its chase range.</summary>
        public const string Leash = "leash";

        /// <summary><c>unreachable</c>: lost: no path leads to the target (path search refused the chase).</summary>
        public const string Unreachable = "unreachable";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Sighted,
            Attacked,
            TargetGone,
            Leash,
            Unreachable);
    }
}
