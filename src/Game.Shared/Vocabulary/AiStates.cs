namespace Game.Shared.Vocabulary
{
    /// <summary>States of the monster AI state machine (ARCHITECTURE §6.6). VOCABULARY.md: "Monster AI states".</summary>
    public static class AiStates
    {
        /// <summary><c>idle</c>: standing near home; waits a random pause, then wanders.</summary>
        public const string Idle = "idle";

        /// <summary><c>wander</c>: walking to a random cell near home.</summary>
        public const string Wander = "wander";

        /// <summary><c>chase</c>: walking toward its target.</summary>
        public const string Chase = "chase";

        /// <summary><c>attack</c>: its target is within attack range; requests attacks at its aspd.</summary>
        public const string Attack = "attack";

        /// <summary><c>return</c>: gave up and walks home, ignoring players.</summary>
        public const string Return = "return";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Idle,
            Wander,
            Chase,
            Attack,
            Return);
    }
}
