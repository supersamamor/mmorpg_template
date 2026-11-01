using System.Collections.Generic;

namespace Game.Shared.Progression
{
    /// <summary>Outcome of an EXP grant.</summary>
    /// <param name="LevelsGained">Number of levels reached (0 if none).</param>
    /// <param name="PointsAwarded">Stat points (base EXP) or skill points (job EXP) awarded in total.</param>
    /// <param name="ExpDiscarded">EXP thrown away because the level cap was reached; see <see cref="ProgressionRules"/>.</param>
    /// <param name="Events">One <see cref="LevelUp"/> / <see cref="JobLevelUp"/> per level reached, in order.</param>
    public sealed record ExpGainResult(int LevelsGained, int PointsAwarded, long ExpDiscarded, IReadOnlyList<IProgressionEvent> Events);

    /// <summary>Why a stat or skill point spend was rejected.</summary>
    public enum SpendError
    {
        /// <summary>Accepted.</summary>
        None,
        /// <summary>The amount was not positive.</summary>
        InvalidAmount,
        /// <summary>Not a primary stat id.</summary>
        UnknownStat,
        /// <summary>The raise would pass the max stat value.</summary>
        AtMaxStatValue,
        /// <summary>Not enough unspent points.</summary>
        NotEnoughPoints,
        /// <summary>Unknown skill id.</summary>
        UnknownSkill,
        /// <summary>The skill is not in the current job's (or an ancestor job's) skill tree.</summary>
        NotInSkillTree,
        /// <summary>The skill is already at its max level.</summary>
        AtMaxSkillLevel,
        /// <summary>A prerequisite skill is not learned to the required level.</summary>
        PrerequisiteNotMet,
    }

    /// <summary>Outcome of a stat or skill point spend.</summary>
    /// <param name="Error"><see cref="SpendError.None"/> on success.</param>
    /// <param name="PointsSpent">Points spent (0 on failure).</param>
    /// <param name="NewValue">Stat value or skill level after the spend (unchanged on failure).</param>
    /// <param name="Events">Events produced (a <see cref="SkillLearned"/> for skills).</param>
    public sealed record SpendResult(SpendError Error, int PointsSpent, int NewValue, IReadOnlyList<IProgressionEvent> Events)
    {
        /// <summary>True when <see cref="Error"/> is <see cref="SpendError.None"/>.</summary>
        public bool Success => Error == SpendError.None;
    }

    /// <summary>Why a job change is not allowed.</summary>
    public enum JobChangeFailureKind
    {
        /// <summary>The target job does not exist.</summary>
        UnknownJob,
        /// <summary>The character already has this job.</summary>
        SameJob,
        /// <summary>The target's parent job is not the current job.</summary>
        WrongParentJob,
        /// <summary>Base level is below the requirement.</summary>
        BaseLevelTooLow,
        /// <summary>Job level is below the requirement.</summary>
        JobLevelTooLow,
        /// <summary>A required quest is not completed (<see cref="JobChangeFailure.Detail"/> = quest id).</summary>
        QuestNotCompleted,
        /// <summary>The job needs an <c>unlock_job</c> reward first.</summary>
        NotUnlocked,
    }

    /// <summary>One reason a job change is blocked.</summary>
    /// <param name="Kind">The reason.</param>
    /// <param name="Detail">Extra data: the quest id, or "have/need" for levels; empty otherwise.</param>
    public sealed record JobChangeFailure(JobChangeFailureKind Kind, string Detail);

    /// <summary>Result of <see cref="ProgressionRules.CanChangeJob"/>: every failing reason, not just the first.</summary>
    /// <param name="Failures">Empty when the change is allowed.</param>
    public sealed record JobChangeCheck(IReadOnlyList<JobChangeFailure> Failures)
    {
        /// <summary>True when there are no failures.</summary>
        public bool Allowed => Failures.Count == 0;
    }
}
