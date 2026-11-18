namespace Game.Shared.Vocabulary
{
    /// <summary>Dialogue action types: the <c>type</c> discriminator of <see cref="Game.Shared.Definitions.DialogueActionDef"/>. VOCABULARY.md: "Dialogue action types".</summary>
    public static class DialogueActionTypes
    {
        /// <summary><c>start_quest</c>: ask for a quest to start (raises <c>QuestStartRequested</c>; quests are T-602).</summary>
        public const string StartQuest = "start_quest";

        /// <summary><c>set_save_point</c>: the character's save point becomes a cell of the NPC's map.</summary>
        public const string SetSavePoint = "set_save_point";

        /// <summary><c>run_effects</c>: run an effect list on the character through the effect runner.</summary>
        public const string RunEffects = "run_effects";

        /// <summary><c>take_item</c>: remove items from the bag.</summary>
        public const string TakeItem = "take_item";

        /// <summary><c>give_item</c>: put items in the bag.</summary>
        public const string GiveItem = "give_item";

        /// <summary><c>open_service</c>: open an NPC service (a <c>ServiceIds</c> term) for the character.</summary>
        public const string OpenService = "open_service";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(StartQuest, SetSavePoint, RunEffects, TakeItem, GiveItem, OpenService);
    }
}
