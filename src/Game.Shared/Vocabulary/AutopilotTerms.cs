namespace Game.Shared.Vocabulary
{
    /// <summary>Whether the quest autopilot plays the character (T-604): the <c>mode</c> of an <c>autopilot</c> message. VOCABULARY.md: "Autopilot modes".</summary>
    public static class AutopilotModes
    {
        /// <summary><c>running</c>: it plays; <c>doing</c> says what.</summary>
        public const string Running = "running";

        /// <summary><c>paused</c>: it waits for the player; <c>reason</c> is an <see cref="AutopilotPauses"/> term. Goes on by itself when the quest moves.</summary>
        public const string Paused = "paused";

        /// <summary><c>off</c>: the run is over; <c>reason</c> is an <see cref="AutopilotStops"/> term.</summary>
        public const string Off = "off";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Running, Paused, Off);
    }

    /// <summary>What a running autopilot is doing (T-604): the <c>doing</c> of an <c>autopilot</c> message. VOCABULARY.md: "Autopilot activities".</summary>
    public static class AutopilotActivities
    {
        /// <summary><c>travelling</c>: on the way to a hunting ground, an NPC, a cell or a warp.</summary>
        public const string Travelling = "travelling";

        /// <summary><c>hunting</c>: after a monster, or fighting it.</summary>
        public const string Hunting = "hunting";

        /// <summary><c>gathering</c>: picking a wanted item up.</summary>
        public const string Gathering = "gathering";

        /// <summary><c>talking</c>: with the NPC, or turning the step in.</summary>
        public const string Talking = "talking";

        /// <summary><c>waiting</c>: at the hunting ground with nothing left to hunt, until it respawns.</summary>
        public const string Waiting = "waiting";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Travelling, Hunting, Gathering, Talking, Waiting);
    }

    /// <summary>
    /// Why a <c>start_autopilot</c> was refused (T-604), in <c>AutopilotRefused</c> and on the wire as <c>rejected</c> (with <c>quest</c> or <c>skill</c> naming the
    /// entry). Nothing changed: a run in progress goes on. VOCABULARY.md: "Autopilot reasons". Checked in this order.
    /// </summary>
    public static class AutopilotReasons
    {
        /// <summary><c>disabled</c>: <c>autopilot.enabled</c> is false.</summary>
        public const string Disabled = "disabled";

        /// <summary><c>dead</c>: the character is dead.</summary>
        public const string Dead = "dead";

        /// <summary><c>time_limit</c>: today's <c>autopilot.daily_limit_minutes</c> are used up.</summary>
        public const string TimeLimit = "time_limit";

        /// <summary><c>empty_queue</c>: no quest was ticked.</summary>
        public const string EmptyQueue = "empty_queue";

        /// <summary><c>too_many_quests</c>: more than <c>autopilot.max_quests</c>.</summary>
        public const string TooManyQuests = "too_many_quests";

        /// <summary><c>unknown_quest</c>: no quest with this id.</summary>
        public const string UnknownQuest = "unknown_quest";

        /// <summary><c>duplicate_quest</c>: the quest is in the list twice.</summary>
        public const string DuplicateQuest = "duplicate_quest";

        /// <summary><c>not_active</c>: the character does not have the quest.</summary>
        public const string NotActive = "not_active";

        /// <summary><c>too_many_skills</c>: more than <c>autopilot.max_skills</c>.</summary>
        public const string TooManySkills = "too_many_skills";

        /// <summary><c>unknown_skill</c>: no skill with this id.</summary>
        public const string UnknownSkill = "unknown_skill";

        /// <summary><c>not_learned</c>: the skill is not learned.</summary>
        public const string NotLearned = "not_learned";

        /// <summary><c>passive_skill</c>: a passive skill is not cast.</summary>
        public const string PassiveSkill = "passive_skill";

        /// <summary><c>level_too_high</c>: above the learned level.</summary>
        public const string LevelTooHigh = "level_too_high";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Disabled, Dead, TimeLimit, EmptyQueue, TooManyQuests, UnknownQuest, DuplicateQuest, NotActive,
            TooManySkills, UnknownSkill, NotLearned, PassiveSkill, LevelTooHigh);
    }

    /// <summary>Why a run is paused (T-604): the <c>reason</c> of an <c>autopilot</c> message with <c>mode</c> <c>paused</c>. VOCABULARY.md: "Autopilot pauses".</summary>
    public static class AutopilotPauses
    {
        /// <summary><c>needs_level</c>: the step wants a base level (nothing else of it is left to do).</summary>
        public const string NeedsLevel = "needs_level";

        /// <summary><c>needs_job</c>: the step wants a job change.</summary>
        public const string NeedsJob = "needs_job";

        /// <summary><c>needs_equip</c>: the step wants an item put on.</summary>
        public const string NeedsEquip = "needs_equip";

        /// <summary><c>needs_item_use</c>: the step wants an item used.</summary>
        public const string NeedsItemUse = "needs_item_use";

        /// <summary><c>no_source</c>: nothing placed on any map gives what the objective wants.</summary>
        public const string NoSource = "no_source";

        /// <summary><c>unreachable</c>: no way there from here, warps included.</summary>
        public const string Unreachable = "unreachable";

        /// <summary><c>no_progress</c>: nothing got done for <c>autopilot.no_progress_ms</c>.</summary>
        public const string NoProgress = "no_progress";

        /// <summary><c>no_safe_choice</c>: the NPC's dialogue offers nothing the autopilot may pick.</summary>
        public const string NoSafeChoice = "no_safe_choice";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(NeedsLevel, NeedsJob, NeedsEquip, NeedsItemUse, NoSource, Unreachable, NoProgress, NoSafeChoice);
    }

    /// <summary>Why a run ended (T-604): the <c>reason</c> of an <c>autopilot</c> message with <c>mode</c> <c>off</c>. VOCABULARY.md: "Autopilot stops".</summary>
    public static class AutopilotStops
    {
        /// <summary><c>finished</c>: every quest of the list is done.</summary>
        public const string Finished = "finished";

        /// <summary><c>stopped</c>: the player switched it off (<c>stop_autopilot</c>).</summary>
        public const string Stopped = "stopped";

        /// <summary><c>manual_input</c>: the player took over (a move, stop, attack, skill, item, dialogue, shop or respawn message).</summary>
        public const string ManualInput = "manual_input";

        /// <summary><c>died</c>: the character died.</summary>
        public const string Died = "died";

        /// <summary><c>logout</c>: the character left the world.</summary>
        public const string Logout = "logout";

        /// <summary><c>disconnected</c>: the connection ended, or another one took the character over. A resume does not restart it.</summary>
        public const string Disconnected = "disconnected";

        /// <summary><c>bag_full</c>: a wanted item or a turn-in does not fit the bag, or the load is too heavy to fight.</summary>
        public const string BagFull = "bag_full";

        /// <summary><c>time_limit</c>: today's <c>autopilot.daily_limit_minutes</c> are used up.</summary>
        public const string TimeLimit = "time_limit";

        /// <summary><c>quest_abandoned</c>: the player dropped the quest it was playing.</summary>
        public const string QuestAbandoned = "quest_abandoned";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Finished, Stopped, ManualInput, Died, Logout, Disconnected, BagFull, TimeLimit, QuestAbandoned);
    }
}
