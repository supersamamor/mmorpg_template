namespace Game.Shared.Vocabulary
{
    /// <summary>Quest objective types: the <c>type</c> discriminator of <see cref="Game.Shared.Definitions.QuestObjectiveDef"/>. VOCABULARY.md: "Quest objective types".</summary>
    public static class QuestObjectiveTypes
    {
        /// <summary><c>kill</c>: Kill N of a monster.</summary>
        public const string Kill = "kill";

        /// <summary><c>collect</c>: Hold N of an item.</summary>
        public const string Collect = "collect";

        /// <summary><c>talk</c>: Talk to an NPC.</summary>
        public const string Talk = "talk";

        /// <summary><c>reach</c>: Reach a cell on a map.</summary>
        public const string Reach = "reach";

        /// <summary><c>level</c>: Reach a base level.</summary>
        public const string Level = "level";

        /// <summary><c>job</c>: Have a job.</summary>
        public const string Job = "job";

        /// <summary><c>use_item</c>: Use N of an item.</summary>
        public const string UseItem = "use_item";

        /// <summary><c>equip</c>: Equip an item.</summary>
        public const string Equip = "equip";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Kill,
            Collect,
            Talk,
            Reach,
            Level,
            Job,
            UseItem,
            Equip);
    }
}
