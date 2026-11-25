using System;
using System.Collections.Generic;

namespace Game.Shared.Progression
{
    /// <summary>
    /// One character's progression: levels, EXP, unspent points, allocated stats and learned skills.
    /// A plain mutable data holder; <see cref="ProgressionRules"/> is the only code that should change it, and it
    /// changes it only when an operation succeeds (a rejected operation leaves the state untouched).
    /// Not thread-safe. Saved and loaded whole (T-702, <c>CharacterState</c>).
    /// </summary>
    public sealed class ProgressionState
    {
        /// <summary>Current base level (starts at 1).</summary>
        public int BaseLevel { get; set; } = 1;

        /// <summary>EXP gathered toward the next base level. Always 0 at the max level (see <see cref="ProgressionRules"/>).</summary>
        public long BaseExp { get; set; }

        /// <summary>Current job id (<c>job.*</c>).</summary>
        public string JobId { get; set; } = string.Empty;

        /// <summary>Current job level (starts at 1, resets on job change).</summary>
        public int JobLevel { get; set; } = 1;

        /// <summary>EXP gathered toward the next job level. Always 0 at the job's max level.</summary>
        public long JobExp { get; set; }

        /// <summary>Unspent stat points.</summary>
        public int StatPoints { get; set; }

        /// <summary>Unspent skill points.</summary>
        public int SkillPoints { get; set; }

        /// <summary>Primary stat id → current base value (allocated points included).</summary>
        public Dictionary<string, int> Stats { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Skill id → learned level (&gt; 0).</summary>
        public Dictionary<string, int> Skills { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Quest ids the character has completed; written by the quest system (T-602, <c>QuestRules</c>), read by job changes.</summary>
        public HashSet<string> CompletedQuests { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Active quests by id: <c>{questId, stepIndex, counters}</c> (ARCHITECTURE §6.4). Written only by <c>Game.Shared.Quests.QuestRules</c> (T-602); kept here so the quest
        /// log travels with the rest of the character.
        /// </summary>
        public Dictionary<string, Quests.QuestProgress> ActiveQuests { get; } = new Dictionary<string, Quests.QuestProgress>(StringComparer.Ordinal);

        /// <summary>The last quest turn-in number given out (<see cref="Quests.QuestProgress.Token"/>). Only goes up, so no number is ever used twice (T-702).</summary>
        public long TurnInSequence { get; set; }

        /// <summary>
        /// Rewards of completed quests the progression host has not paid yet (T-602, T-702): added in the step that completes the quest and removed in the step that pays, so
        /// neither a warp nor a save between the two loses or repeats them.
        /// </summary>
        public List<OwedQuestRewards> OwedRewards { get; } = new List<OwedQuestRewards>();

        /// <summary>Job ids unlocked by an <c>unlock_job</c> reward (T-602: applied by the progression host from a completed quest).</summary>
        public HashSet<string> UnlockedJobs { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Learned level of a skill, 0 if not learned.</summary>
        /// <param name="skillId">Skill id.</param>
        public int SkillLevel(string skillId) => Skills.TryGetValue(skillId, out var level) ? level : 0;

        /// <summary>Base value of a primary stat, 0 if not set.</summary>
        /// <param name="statId">Primary stat id.</param>
        public int StatValue(string statId) => Stats.TryGetValue(statId, out var value) ? value : 0;
    }

    /// <summary>The rewards of one completed quest that are not items. None of them can fail.</summary>
    /// <param name="QuestId">The quest.</param>
    /// <param name="BaseExp">Base EXP.</param>
    /// <param name="JobExp">Job EXP.</param>
    /// <param name="StatPoints">Stat points.</param>
    /// <param name="SkillPoints">Skill points.</param>
    /// <param name="UnlockJobs">Job ids unlocked.</param>
    /// <param name="StatusKeys">Compiled <c>apply_status</c> effects to run (<c>QuestRules.RewardKey</c>).</param>
    public sealed record OwedQuestRewards(string QuestId, long BaseExp, long JobExp, int StatPoints, int SkillPoints, IReadOnlyList<string> UnlockJobs,
        IReadOnlyList<string> StatusKeys);
}
