using Game.Shared.Definitions;

namespace Game.Shared.Progression
{
    /// <summary>
    /// The definitions progression reads. <c>Game.Shared</c> cannot see <c>ContentDatabase</c> (it lives in
    /// <c>Game.Content</c>), so the host adapts its registries to this interface (a few one-line lookups).
    /// </summary>
    public interface IProgressionContent
    {
        /// <summary>Looks up a job (<c>job.*</c>).</summary>
        /// <param name="id">Job id.</param>
        /// <param name="job">The job, or null.</param>
        bool TryGetJob(string id, out JobDef job);

        /// <summary>Looks up a skill (<c>skill.*</c>).</summary>
        /// <param name="id">Skill id.</param>
        /// <param name="skill">The skill, or null.</param>
        bool TryGetSkill(string id, out SkillDef skill);

        /// <summary>Looks up an EXP table (<c>exp.*</c>).</summary>
        /// <param name="id">Table id.</param>
        /// <param name="table">The table, or null.</param>
        bool TryGetExpTable(string id, out ExpTableDef table);
    }
}
