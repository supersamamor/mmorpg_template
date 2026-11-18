namespace Game.Shared.Vocabulary
{
    /// <summary>Why a skill request was dropped before its effects ran (<c>SkillRefused</c>). VOCABULARY.md: "Skill reasons".</summary>
    public static class SkillReasons
    {
        /// <summary><c>caster_dead</c>: the caster is dead or no longer on the map.</summary>
        public const string CasterDead = "caster_dead";

        /// <summary><c>target_dead</c>: the target entity is dead.</summary>
        public const string TargetDead = "target_dead";

        /// <summary><c>target_gone</c>: the target entity is not on the caster's map.</summary>
        public const string TargetGone = "target_gone";

        /// <summary><c>out_of_range</c>: the target (entity or cell) is farther than the skill's <c>target.range</c> (Chebyshev).</summary>
        public const string OutOfRange = "out_of_range";

        /// <summary><c>no_line_of_sight</c>: a skill with range above 1 has no line of sight from the caster's cell to the target's.</summary>
        public const string NoLineOfSight = "no_line_of_sight";

        /// <summary><c>safe_zone</c>: a hostile skill aims at a player standing in a <c>safe</c> zone.</summary>
        public const string SafeZone = "safe_zone";

        /// <summary><c>invalid_target</c>: the target does not fit the skill (an enemy for a friendly skill, an ally for a hostile one, no cell for a ground skill).</summary>
        public const string InvalidTarget = "invalid_target";

        /// <summary><c>cannot_use_skills</c>: the caster has a status with <c>prevents_skills</c> (a stun).</summary>
        public const string CannotUseSkills = "cannot_use_skills";


        /// <summary><c>not_learned</c>: the player has not learned the skill (level 0).</summary>
        public const string NotLearned = "not_learned";

        /// <summary><c>level_too_high</c>: the requested level is above the learned level.</summary>
        public const string LevelTooHigh = "level_too_high";

        /// <summary><c>passive_skill</c>: a passive skill cannot be cast.</summary>
        public const string PassiveSkill = "passive_skill";

        /// <summary><c>not_enough_sp</c>: the caster has less SP than the skill costs at that level.</summary>
        public const string NotEnoughSp = "not_enough_sp";

        /// <summary><c>on_cooldown</c>: the skill's cooldown is not over.</summary>
        public const string OnCooldown = "on_cooldown";

        /// <summary><c>wrong_weapon</c>: the equipped weapon type is not in the skill's <c>weapon_types</c>.</summary>
        public const string WrongWeapon = "wrong_weapon";

        /// <summary><c>unknown_skill</c>: no such skill.</summary>
        public const string UnknownSkill = "unknown_skill";

        /// <summary><c>not_in_tree</c>: the skill is not in the player's job tree (or a parent job's).</summary>
        public const string NotInTree = "not_in_tree";

        /// <summary><c>at_max_level</c>: the skill is at the highest level the tree allows.</summary>
        public const string AtMaxLevel = "at_max_level";

        /// <summary><c>prerequisite_not_met</c>: a <c>requires</c> entry of the tree is not met.</summary>
        public const string PrerequisiteNotMet = "prerequisite_not_met";

        /// <summary><c>not_enough_points</c>: no skill point left.</summary>
        public const string NotEnoughPoints = "not_enough_points";

        /// <summary><c>unknown_job</c>: no such job.</summary>
        public const string UnknownJob = "unknown_job";

        /// <summary><c>same_job</c>: the player already has that job.</summary>
        public const string SameJob = "same_job";

        /// <summary><c>wrong_parent_job</c>: the job does not change from the player's current job.</summary>
        public const string WrongParentJob = "wrong_parent_job";

        /// <summary><c>base_level_too_low</c>: base level below the job's <c>min_base_level</c>.</summary>
        public const string BaseLevelTooLow = "base_level_too_low";

        /// <summary><c>job_level_too_low</c>: job level below the job's <c>min_job_level</c>.</summary>
        public const string JobLevelTooLow = "job_level_too_low";

        /// <summary><c>quest_not_completed</c>: a quest in the job's <c>quest_ids</c> is not completed (quests: T-602).</summary>
        public const string QuestNotCompleted = "quest_not_completed";

        /// <summary><c>not_unlocked</c>: the job needs <c>requires_unlock</c> and is not unlocked (unlocks: T-602).</summary>
        public const string NotUnlocked = "not_unlocked";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            CasterDead,
            TargetDead,
            TargetGone,
            OutOfRange,
            NoLineOfSight,
            SafeZone,
            InvalidTarget,
            CannotUseSkills,
            NotLearned,
            LevelTooHigh,
            PassiveSkill,
            NotEnoughSp,
            OnCooldown,
            WrongWeapon,
            UnknownSkill,
            NotInTree,
            AtMaxLevel,
            PrerequisiteNotMet,
            NotEnoughPoints,
            UnknownJob,
            SameJob,
            WrongParentJob,
            BaseLevelTooLow,
            JobLevelTooLow,
            QuestNotCompleted,
            NotUnlocked);
    }
}
