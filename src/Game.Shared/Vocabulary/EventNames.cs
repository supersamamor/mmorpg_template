namespace Game.Shared.Vocabulary
{
    /// <summary>Event bus event names. VOCABULARY.md: "Events (event bus)".</summary>
    public static class EventNames
    {
        /// <summary><c>EntitySpawned</c>: EntitySpawned event.</summary>
        public const string EntitySpawned = "EntitySpawned";

        /// <summary><c>EntityDied</c>: EntityDied event.</summary>
        public const string EntityDied = "EntityDied";

        /// <summary><c>MonsterKilled</c>: MonsterKilled event.</summary>
        public const string MonsterKilled = "MonsterKilled";

        /// <summary><c>DamageDealt</c>: DamageDealt event.</summary>
        public const string DamageDealt = "DamageDealt";

        /// <summary><c>ItemAcquired</c>: ItemAcquired event.</summary>
        public const string ItemAcquired = "ItemAcquired";

        /// <summary><c>ItemUsed</c>: ItemUsed event.</summary>
        public const string ItemUsed = "ItemUsed";

        /// <summary><c>ItemEquipped</c>: ItemEquipped event.</summary>
        public const string ItemEquipped = "ItemEquipped";

        /// <summary><c>NpcTalked</c>: NpcTalked event.</summary>
        public const string NpcTalked = "NpcTalked";

        /// <summary><c>MapEntered</c>: MapEntered event.</summary>
        public const string MapEntered = "MapEntered";

        /// <summary><c>ZoneEntered</c>: ZoneEntered event.</summary>
        public const string ZoneEntered = "ZoneEntered";

        /// <summary><c>LevelUp</c>: LevelUp event.</summary>
        public const string LevelUp = "LevelUp";

        /// <summary><c>JobLevelUp</c>: JobLevelUp event.</summary>
        public const string JobLevelUp = "JobLevelUp";

        /// <summary><c>JobChanged</c>: JobChanged event.</summary>
        public const string JobChanged = "JobChanged";

        /// <summary><c>SkillLearned</c>: SkillLearned event.</summary>
        public const string SkillLearned = "SkillLearned";

        /// <summary><c>QuestStarted</c>: QuestStarted event.</summary>
        public const string QuestStarted = "QuestStarted";

        /// <summary><c>QuestStepCompleted</c>: QuestStepCompleted event.</summary>
        public const string QuestStepCompleted = "QuestStepCompleted";

        /// <summary><c>QuestCompleted</c>: QuestCompleted event.</summary>
        public const string QuestCompleted = "QuestCompleted";

        /// <summary><c>StatusApplied</c>: StatusApplied event.</summary>
        public const string StatusApplied = "StatusApplied";

        /// <summary><c>StatusExpired</c>: StatusExpired event.</summary>
        public const string StatusExpired = "StatusExpired";

        /// <summary><c>EntityEnteredMap</c>: an entity was added to a map instance (spawn or warp arrival).</summary>
        public const string EntityEnteredMap = "EntityEnteredMap";

        /// <summary><c>EntityLeftMap</c>: an entity was removed from a map instance (despawn or warp departure).</summary>
        public const string EntityLeftMap = "EntityLeftMap";

        /// <summary><c>WarpRejected</c>: a warp was refused (unknown map or unwalkable target cell); the entity stayed.</summary>
        public const string WarpRejected = "WarpRejected";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            EntitySpawned,
            EntityDied,
            MonsterKilled,
            DamageDealt,
            ItemAcquired,
            ItemUsed,
            ItemEquipped,
            NpcTalked,
            MapEntered,
            ZoneEntered,
            LevelUp,
            JobLevelUp,
            JobChanged,
            SkillLearned,
            QuestStarted,
            QuestStepCompleted,
            QuestCompleted,
            StatusApplied,
            StatusExpired,
            EntityEnteredMap,
            EntityLeftMap,
            WarpRejected);
    }
}
