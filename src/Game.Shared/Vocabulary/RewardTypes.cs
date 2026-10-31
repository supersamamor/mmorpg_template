namespace Game.Shared.Vocabulary
{
    /// <summary>Quest reward types: the <c>type</c> discriminator of <see cref="Game.Shared.Definitions.RewardDef"/>. VOCABULARY.md: "Reward types".</summary>
    public static class RewardTypes
    {
        /// <summary><c>exp</c>: Base and/or job EXP.</summary>
        public const string Exp = "exp";

        /// <summary><c>item</c>: Items.</summary>
        public const string Item = "item";

        /// <summary><c>zeny</c>: Currency (engine id <c>currency</c>).</summary>
        public const string Zeny = "zeny";

        /// <summary><c>skill_point</c>: Skill points.</summary>
        public const string SkillPoint = "skill_point";

        /// <summary><c>stat_point</c>: Stat points.</summary>
        public const string StatPoint = "stat_point";

        /// <summary><c>unlock_job</c>: Unlock a job change.</summary>
        public const string UnlockJob = "unlock_job";

        /// <summary><c>status</c>: Apply a status.</summary>
        public const string Status = "status";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Exp,
            Item,
            Zeny,
            SkillPoint,
            StatPoint,
            UnlockJob,
            Status);
    }
}
