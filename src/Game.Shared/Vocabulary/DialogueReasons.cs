namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// Why a <c>talk</c> or <c>dialogue_choice</c> was refused (<c>DialogueRefused</c>, and <c>rejected</c> with <c>intent</c> <c>talk</c> or <c>dialogue_choice</c>);
    /// nothing changed. VOCABULARY.md: "Dialogue reasons".
    /// </summary>
    public static class DialogueReasons
    {
        /// <summary><c>target_gone</c>: no such NPC in view.</summary>
        public const string TargetGone = "target_gone";

        /// <summary><c>invalid_target</c>: the target is not an NPC.</summary>
        public const string InvalidTarget = "invalid_target";

        /// <summary><c>dead</c>: the character is dead.</summary>
        public const string Dead = "dead";

        /// <summary><c>no_dialogue</c>: the NPC has nothing to say, or the character is not in a dialogue.</summary>
        public const string NoDialogue = "no_dialogue";

        /// <summary><c>unreachable</c>: no way to walk into talking range.</summary>
        public const string Unreachable = "unreachable";

        /// <summary><c>cancelled</c>: the character gave another order while walking to the NPC.</summary>
        public const string Cancelled = "cancelled";

        /// <summary><c>invalid_choice</c>: the choice does not exist, or its conditions do not hold.</summary>
        public const string InvalidChoice = "invalid_choice";

        /// <summary><c>busy</c>: the previous choice is still being applied.</summary>
        public const string Busy = "busy";

        /// <summary><c>missing_item</c>: a <c>take_item</c> found less in the bag than it asks for.</summary>
        public const string MissingItem = "missing_item";

        /// <summary><c>inventory_full</c>: a <c>give_item</c> has no free entry.</summary>
        public const string InventoryFull = "inventory_full";

        /// <summary><c>too_heavy</c>: a <c>give_item</c> would pass the weight limit.</summary>
        public const string TooHeavy = "too_heavy";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            TargetGone, InvalidTarget, Dead, NoDialogue, Unreachable, Cancelled, InvalidChoice, Busy, MissingItem, InventoryFull, TooHeavy);
    }

    /// <summary>Why a dialogue (and the services it opened) ended: <c>dialogue_ended</c> and <c>DialogueEnded</c>. VOCABULARY.md: "Dialogue end reasons".</summary>
    public static class DialogueEndReasons
    {
        /// <summary><c>finished</c>: the graph reached its end.</summary>
        public const string Finished = "finished";

        /// <summary><c>closed</c>: the player closed it.</summary>
        public const string Closed = "closed";

        /// <summary><c>moved_away</c>: the player is farther than <c>dialogue.talk_range</c> from the NPC.</summary>
        public const string MovedAway = "moved_away";

        /// <summary><c>warped</c>: the player left the map.</summary>
        public const string Warped = "warped";

        /// <summary><c>died</c>: the player died.</summary>
        public const string Died = "died";

        /// <summary><c>replaced</c>: the player started talking to another NPC.</summary>
        public const string Replaced = "replaced";

        /// <summary><c>npc_gone</c>: the NPC left the world.</summary>
        public const string NpcGone = "npc_gone";

        /// <summary><c>disconnected</c>: the player left the world.</summary>
        public const string Disconnected = "disconnected";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Finished, Closed, MovedAway, Warped, Died, Replaced, NpcGone, Disconnected);
    }
}
