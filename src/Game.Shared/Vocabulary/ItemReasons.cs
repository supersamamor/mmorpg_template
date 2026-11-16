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

        /// <summary><c>unsupported_effect</c>: the consumable has an effect type the item effects cannot run yet (until T-501); nothing is used up.</summary>
        public const string UnsupportedEffect = "unsupported_effect";

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
            UnsupportedEffect,
            NotEquipment,
            WrongSlot,
            LevelTooLow,
            WrongJob,
            NotEquipped,
            ItemEquipped,
            InvalidQuantity);
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

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(PickedUp, Used, Equipped, Unequipped, Dropped, Weight);
    }
}
