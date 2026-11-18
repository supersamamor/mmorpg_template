namespace Game.Shared.Vocabulary
{
    /// <summary>Dialogue condition types: the <c>type</c> discriminator of <see cref="Game.Shared.Definitions.DialogueConditionDef"/>. VOCABULARY.md: "Dialogue condition types".</summary>
    public static class DialogueConditionTypes
    {
        /// <summary><c>base_level</c>: the base level is within <c>min</c> and <c>max</c>.</summary>
        public const string BaseLevel = "base_level";

        /// <summary><c>job_level</c>: the job level is within <c>min</c> and <c>max</c>.</summary>
        public const string JobLevel = "job_level";

        /// <summary><c>job_is</c>: the character has this job.</summary>
        public const string JobIs = "job_is";

        /// <summary><c>job_is_not</c>: the character does not have this job.</summary>
        public const string JobIsNot = "job_is_not";

        /// <summary><c>has_item</c>: the bag holds at least <c>quantity</c> of the item (worn pieces do not count).</summary>
        public const string HasItem = "has_item";

        /// <summary><c>has_status</c>: the character has this status active.</summary>
        public const string HasStatus = "has_status";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(BaseLevel, JobLevel, JobIs, JobIsNot, HasItem, HasStatus);
    }
}
