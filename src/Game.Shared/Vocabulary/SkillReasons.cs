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

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            CasterDead,
            TargetDead,
            TargetGone,
            OutOfRange,
            NoLineOfSight,
            SafeZone,
            InvalidTarget,
            CannotUseSkills);
    }
}
