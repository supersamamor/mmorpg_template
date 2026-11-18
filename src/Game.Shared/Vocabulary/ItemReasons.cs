namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// Why the inventory refused an item intent (<c>pick_up</c>, <c>use_item</c>, <c>equip</c>, <c>unequip</c>, <c>drop_item</c>): the
    /// <c>ItemRefused</c> event and the <c>rejected</c> message's <c>reason</c>. Nothing changed. VOCABULARY.md: "Item reasons".
    /// </summary>
    public static class ItemReasons
    {
        /// <summary><c>item_gone</c>: the ground item is not there any more (taken, expired, never seen), or the inventory has no such entry.</summary>
        public const string ItemGone = "item_gone";

        /// <summary><c>not_yours</c>: another player still has loot priority on the ground item.</summary>
        public const string NotYours = "not_yours";

        /// <summary><c>unreachable</c>: no path leads into pick-up range of the ground item.</summary>
        public const string Unreachable = "unreachable";

        /// <summary><c>cancelled</c>: the walk to a ground item ended before it was in range (a new move order, a stop, death).</summary>
        public const string Cancelled = "cancelled";

        /// <summary><c>too_heavy</c>: taking it would carry the weight past 100% of <c>weight_limit</c>.</summary>
        public const string TooHeavy = "too_heavy";

        /// <summary><c>inventory_full</c>: it needs more entries than the inventory has free (<c>inventory.max_entries</c>).</summary>
        public const string InventoryFull = "inventory_full";

        /// <summary><c>not_usable</c>: the entry is not a consumable.</summary>
        public const string NotUsable = "not_usable";

        /// <summary><c>cooldown</c>: the item's <c>cooldown_ms</c> since its last use is not over.</summary>
        public const string Cooldown = "cooldown";


        /// <summary><c>not_equipment</c>: the entry is not an equipment item.</summary>
        public const string NotEquipment = "not_equipment";

        /// <summary><c>wrong_slot</c>: the item does not fit the slot asked for.</summary>
        public const string WrongSlot = "wrong_slot";

        /// <summary><c>level_too_low</c>: the character's base level is below the item's <c>min_base_level</c>.</summary>
        public const string LevelTooLow = "level_too_low";

        /// <summary><c>wrong_job</c>: the item's <c>job_ids</c> do not list the character's job, or the job cannot use the weapon's type.</summary>
        public const string WrongJob = "wrong_job";

        /// <summary><c>not_equipped</c>: the slot to unequip is empty (or not an equipment slot).</summary>
        public const string NotEquipped = "not_equipped";

        /// <summary><c>item_equipped</c>: the entry is worn; unequip it first (to use or drop it).</summary>
        public const string ItemEquipped = "item_equipped";

        /// <summary><c>invalid_quantity</c>: the quantity to drop is below 1 or more than the entry holds.</summary>
        public const string InvalidQuantity = "invalid_quantity";

        /// <summary><c>not_a_card</c>: the entry to socket is not a card.</summary>
        public const string NotACard = "not_a_card";

        /// <summary><c>card_does_not_fit</c>: the card's <c>slots</c> leave out every slot the equipment fits.</summary>
        public const string CardDoesNotFit = "card_does_not_fit";

        /// <summary><c>no_free_socket</c>: every one of the equipment's <c>card_slots</c> already holds a card (or it has none).</summary>
        public const string NoFreeSocket = "no_free_socket";

        /// <summary><c>not_refinable</c>: the equipment is not <c>refinable</c>.</summary>
        public const string NotRefinable = "not_refinable";

        /// <summary><c>at_max_refine</c>: the equipment is at <c>refine.yaml</c> <c>max_level</c>.</summary>
        public const string AtMaxRefine = "at_max_refine";

        /// <summary><c>missing_material</c>: the carried material for the next refine level is less than its quantity.</summary>
        public const string MissingMaterial = "missing_material";

        /// <summary><c>reset_in_combat</c> (T-503): a reset item is refused for <c>progression.reset.refuse_in_combat</c> while the character fought within the regeneration delay.</summary>
        public const string ResetInCombat = "reset_in_combat";

        /// <summary><c>reset_dead</c> (T-503): a reset item is refused while the character is dead.</summary>
        public const string ResetDead = "reset_dead";

        /// <summary><c>nothing_to_reset</c> (T-503): the reset item would change nothing (no stat raised, or no skill learned); it is not used up.</summary>
        public const string NothingToReset = "nothing_to_reset";

        /// <summary><c>service_required</c> (T-601): <c>refine</c> works only while an NPC's refine service is open and in reach (<c>dialogue.refine_needs_service</c>).</summary>
        public const string ServiceRequired = "service_required";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            ItemGone,
            NotYours,
            Unreachable,
            Cancelled,
            TooHeavy,
            InventoryFull,
            NotUsable,
            Cooldown,
            NotEquipment,
            WrongSlot,
            LevelTooLow,
            WrongJob,
            NotEquipped,
            ItemEquipped,
            InvalidQuantity,
            NotACard,
            CardDoesNotFit,
            NoFreeSocket,
            NotRefinable,
            AtMaxRefine,
            MissingMaterial,
            ResetInCombat,
            ResetDead,
            NothingToReset,
            ServiceRequired);
    }

    /// <summary>
    /// How heavily a character is loaded, from its carried weight as a share of <c>weight_limit</c> (ARCHITECTURE §6.8). The thresholds are
    /// <c>balance.yaml</c> <c>inventory.burdened_percent</c> and <c>overweight_percent</c>. VOCABULARY.md: "Weight states".
    /// </summary>
    public static class WeightStates
    {
        /// <summary><c>normal</c>: below the burdened threshold; nothing is limited.</summary>
        public const string Normal = "normal";

        /// <summary><c>burdened</c>: at or above <c>burdened_percent</c>: natural HP and SP regeneration stops.</summary>
        public const string Burdened = "burdened";

        /// <summary><c>overweight</c>: at or above <c>overweight_percent</c>: no regeneration, and the character cannot attack.</summary>
        public const string Overweight = "overweight";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Normal, Burdened, Overweight);
    }

    /// <summary>What changed a character's inventory, in <c>inventory_changed</c> <c>cause</c> (ARCHITECTURE §10). VOCABULARY.md: "Inventory changes".</summary>
    public static class InventoryChanges
    {
        /// <summary><c>picked_up</c>: a ground item went into the inventory.</summary>
        public const string PickedUp = "picked_up";

        /// <summary><c>used</c>: a consumable was used.</summary>
        public const string Used = "used";

        /// <summary><c>equipped</c>: an entry was put on (and anything it displaced taken off).</summary>
        public const string Equipped = "equipped";

        /// <summary><c>unequipped</c>: an entry was taken off.</summary>
        public const string Unequipped = "unequipped";

        /// <summary><c>dropped</c>: some or all of an entry was put on the ground.</summary>
        public const string Dropped = "dropped";

        /// <summary><c>weight</c>: only the weight limit or the weight state changed (stats or level changed).</summary>
        public const string Weight = "weight";

        /// <summary><c>card_inserted</c>: a card was socketed into an equipment entry (one card unit used up).</summary>
        public const string CardInserted = "card_inserted";

        /// <summary><c>refined</c>: a refine was tried: the material is used up and the entry went up, stayed, went down or broke (<c>refine_result</c> says which).</summary>
        public const string Refined = "refined";

        /// <summary><c>exchanged</c> (T-601): a dialogue's <c>take_item</c> / <c>give_item</c> actions changed the bag.</summary>
        public const string Exchanged = "exchanged";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(PickedUp, Used, Equipped, Unequipped, Dropped, Weight, CardInserted, Refined, Exchanged);
    }

    /// <summary>What a failed refine does to the equipment, per level of <c>refine.yaml</c> (<c>failure</c>; ARCHITECTURE §6.8). VOCABULARY.md: "Refine failures".</summary>
    public static class RefineFailures
    {
        /// <summary><c>keep</c>: the refine level stays as it was.</summary>
        public const string Keep = "keep";

        /// <summary><c>down</c>: the refine level goes down by one (never below 0).</summary>
        public const string Down = "down";

        /// <summary><c>break</c>: the equipment is destroyed, with its cards.</summary>
        public const string Break = "break";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Keep, Down, Break);
    }

    /// <summary>How a refine came out: the <c>ItemRefined</c> event and the <c>refine_result</c> message's <c>result</c>. VOCABULARY.md: "Refine results".</summary>
    public static class RefineResults
    {
        /// <summary><c>success</c>: the refine level went up by one.</summary>
        public const string Success = "success";

        /// <summary><c>keep</c>: it failed and the level stayed.</summary>
        public const string Keep = RefineFailures.Keep;

        /// <summary><c>down</c>: it failed and the level went down by one.</summary>
        public const string Down = RefineFailures.Down;

        /// <summary><c>break</c>: it failed and the equipment broke (it is gone, with its cards).</summary>
        public const string Break = RefineFailures.Break;

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Success, Keep, Down, Break);
    }
}
