using Game.Shared.Vocabulary;

namespace Game.Shared.Progression
{
    /// <summary>A domain event produced by progression, returned as a value (no event bus yet). <see cref="Name"/> is an <see cref="EventNames"/> term.</summary>
    public interface IProgressionEvent
    {
        /// <summary>The event name (VOCABULARY.md "Events").</summary>
        string Name { get; }
    }

    /// <summary>A base level was reached. A multi-level gain yields one event per level, in order.</summary>
    /// <param name="NewLevel">The level just reached.</param>
    /// <param name="StatPointsAwarded">Stat points awarded for reaching it.</param>
    public sealed record LevelUp(int NewLevel, int StatPointsAwarded) : IProgressionEvent
    {
        /// <inheritdoc />
        public string Name => EventNames.LevelUp;
    }

    /// <summary>A job level was reached. A multi-level gain yields one event per level, in order.</summary>
    /// <param name="JobId">The job.</param>
    /// <param name="NewLevel">The job level just reached.</param>
    /// <param name="SkillPointsAwarded">Skill points awarded for reaching it.</param>
    public sealed record JobLevelUp(string JobId, int NewLevel, int SkillPointsAwarded) : IProgressionEvent
    {
        /// <inheritdoc />
        public string Name => EventNames.JobLevelUp;
    }

    /// <summary>The character changed job.</summary>
    /// <param name="FromJobId">Previous job.</param>
    /// <param name="ToJobId">New job.</param>
    public sealed record JobChanged(string FromJobId, string ToJobId) : IProgressionEvent
    {
        /// <inheritdoc />
        public string Name => EventNames.JobChanged;
    }

    /// <summary>A skill was learned or raised one level.</summary>
    /// <param name="SkillId">The skill.</param>
    /// <param name="NewLevel">Its level now.</param>
    public sealed record SkillLearned(string SkillId, int NewLevel) : IProgressionEvent
    {
        /// <inheritdoc />
        public string Name => EventNames.SkillLearned;
    }
}
