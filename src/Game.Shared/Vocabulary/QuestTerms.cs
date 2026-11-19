namespace Game.Shared.Vocabulary
{
    /// <summary>Where a character stands with a quest (T-602). VOCABULARY.md: "Quest states". Used by the <c>quest_state</c> dialogue condition.</summary>
    public static class QuestStates
    {
        /// <summary><c>not_started</c>: neither active nor completed (an abandoned quest is not started again).</summary>
        public const string NotStarted = "not_started";

        /// <summary><c>available</c>: it would start now: prerequisites met, not active, not completed unless <c>repeatable</c>, and below <c>quests.max_active</c>.</summary>
        public const string Available = "available";

        /// <summary><c>active</c>: started, at some step.</summary>
        public const string Active = "active";

        /// <summary><c>completed</c>: finished at least once and not active again.</summary>
        public const string Completed = "completed";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(NotStarted, Available, Active, Completed);
    }

    /// <summary>
    /// Why a quest start or abandon was refused, or why a met step's turn-in is held (T-602). In <c>QuestRefused</c> and on the wire as <c>rejected</c>
    /// (<c>intent</c> <c>dialogue_choice</c> or <c>abandon_quest</c>, with <c>quest</c>), and as a held step's <c>held</c>. VOCABULARY.md: "Quest reasons".
    /// </summary>
    public static class QuestReasons
    {
        /// <summary><c>unknown_quest</c>: no quest with this id.</summary>
        public const string UnknownQuest = "unknown_quest";

        /// <summary><c>already_active</c>: the quest is active.</summary>
        public const string AlreadyActive = "already_active";

        /// <summary><c>already_completed</c>: the quest was completed and is not repeatable.</summary>
        public const string AlreadyCompleted = "already_completed";

        /// <summary><c>base_level_too_low</c>: below the prerequisite <c>min_base_level</c>.</summary>
        public const string BaseLevelTooLow = "base_level_too_low";

        /// <summary><c>wrong_job</c>: the current job is not one of the prerequisite <c>job_ids</c>.</summary>
        public const string WrongJob = "wrong_job";

        /// <summary><c>quest_not_completed</c>: a prerequisite quest is not completed.</summary>
        public const string QuestNotCompleted = "quest_not_completed";

        /// <summary><c>too_many_active</c>: <c>quests.max_active</c> quests are active already.</summary>
        public const string TooManyActive = "too_many_active";

        /// <summary><c>not_active</c>: <c>abandon_quest</c> named a quest that is not active.</summary>
        public const string NotActive = "not_active";

        /// <summary><c>inventory_full</c>: the turn-in's items do not fit (no free entry); the step waits, nothing changed.</summary>
        public const string InventoryFull = "inventory_full";

        /// <summary><c>too_heavy</c>: the turn-in's items would pass the weight limit; the step waits, nothing changed.</summary>
        public const string TooHeavy = "too_heavy";

        /// <summary><c>missing_item</c>: an item the step consumes is no longer in the bag; the step waits, nothing changed.</summary>
        public const string MissingItem = "missing_item";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(UnknownQuest, AlreadyActive, AlreadyCompleted, BaseLevelTooLow, WrongJob, QuestNotCompleted,
            TooManyActive, NotActive, InventoryFull, TooHeavy, MissingItem);
    }

    /// <summary>What a <c>quest</c> message reports (T-602). VOCABULARY.md: "Quest changes".</summary>
    public static class QuestChanges
    {
        /// <summary><c>accepted</c>: the quest started, at its first step.</summary>
        public const string Accepted = "accepted";

        /// <summary><c>progress</c>: a counter of the current step moved, or its turn-in is held or no longer held.</summary>
        public const string Progress = "progress";

        /// <summary><c>step_done</c>: a step completed and the next one is current.</summary>
        public const string StepDone = "step_done";

        /// <summary><c>completed</c>: the last step completed and every reward was paid.</summary>
        public const string Completed = "completed";

        /// <summary><c>abandoned</c>: the player dropped the quest; its progress is gone.</summary>
        public const string Abandoned = "abandoned";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Accepted, Progress, StepDone, Completed, Abandoned);
    }

    /// <summary>The marker the client shows over an NPC for the player (T-602). VOCABULARY.md: "Quest markers".</summary>
    public static class QuestMarkers
    {
        /// <summary><c>offer</c>: the NPC gives a quest that is <c>available</c>.</summary>
        public const string Offer = "offer";

        /// <summary><c>turn_in</c>: talking to the NPC completes the current step of an active quest (every other objective of the step is met).</summary>
        public const string TurnIn = "turn_in";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Offer, TurnIn);
    }
}
