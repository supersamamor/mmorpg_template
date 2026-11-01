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

        /// <summary><c>MovementStarted</c>: an entity began walking a new path (also when it is re-pathed mid-walk).</summary>
        public const string MovementStarted = "MovementStarted";

        /// <summary><c>CellReached</c>: a walking entity fully reached a cell.</summary>
        public const string CellReached = "CellReached";

        /// <summary><c>MovementStopped</c>: an entity stopped walking, with a reason.</summary>
        public const string MovementStopped = "MovementStopped";

        /// <summary><c>MoveRejected</c>: a move request was refused, with a reason; the entity did not change course.</summary>
        public const string MoveRejected = "MoveRejected";

        /// <summary><c>AggroGained</c>: a monster took a target (reason: an <c>AggroReasons</c> term).</summary>
        public const string AggroGained = "AggroGained";

        /// <summary><c>AggroLost</c>: a monster dropped its target and heads home (reason: an <c>AggroReasons</c> term).</summary>
        public const string AggroLost = "AggroLost";

        /// <summary><c>MonsterReturned</c>: a monster that gave up arrived back on its home cell; combat resets its damage shares and may heal it.</summary>
        public const string MonsterReturned = "MonsterReturned";

        /// <summary><c>AttackRequested</c>: a monster decided to auto-attack its target; combat resolves it.</summary>
        public const string AttackRequested = "AttackRequested";

        /// <summary><c>AttackRefused</c>: an attack request or intent was dropped (reason: an <c>AttackReasons</c> term).</summary>
        public const string AttackRefused = "AttackRefused";

        /// <summary><c>AttackStopped</c>: a player's auto-attack ended (reason: an <c>AttackReasons</c> term).</summary>
        public const string AttackStopped = "AttackStopped";

        /// <summary><c>ExpGained</c>: a player earned base and job EXP from a kill.</summary>
        public const string ExpGained = "ExpGained";

        /// <summary><c>ExpLost</c>: a player lost base EXP on death.</summary>
        public const string ExpLost = "ExpLost";

        /// <summary><c>PlayerRespawned</c>: a dead player came back at the respawn point.</summary>
        public const string PlayerRespawned = "PlayerRespawned";

        /// <summary><c>ItemDropped</c>: a monster's drop became a ground item.</summary>
        public const string ItemDropped = "ItemDropped";

        /// <summary><c>ItemExpired</c>: a ground item disappeared after its lifetime.</summary>
        public const string ItemExpired = "ItemExpired";

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
            WarpRejected,
            MovementStarted,
            CellReached,
            MovementStopped,
            MoveRejected,
            AggroGained,
            AggroLost,
            MonsterReturned,
            AttackRequested,
            AttackRefused,
            AttackStopped,
            ExpGained,
            ExpLost,
            PlayerRespawned,
            ItemDropped,
            ItemExpired);
    }
}
