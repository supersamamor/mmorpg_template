using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>A job (class): skill tree, job bonuses, allowed weapons and job change requirements.</summary>
    public sealed record JobDef : IDefinition
    {
        /// <summary>Id namespace for jobs.</summary>
        public const string IdPrefix = IdPrefixes.Job;

        /// <summary>Job id, e.g. <c>job.swordsman</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Display description.</summary>
        public string? Description { get; init; }

        /// <summary>Job this one is changed from (<c>job.*</c>). Null for a starting job.</summary>
        public string? ParentJobId { get; init; }

        /// <summary>Conditions for changing into this job from <see cref="ParentJobId"/>.</summary>
        public JobChangeRequirementsDef Requirements { get; init; } = new JobChangeRequirementsDef();

        /// <summary>EXP table for job levels (<c>exp.*</c>). Its length sets the max job level.</summary>
        public string JobExpTableId { get; init; } = string.Empty;

        /// <summary>Skills this job can learn, with per-job prerequisites.</summary>
        public IReadOnlyList<JobSkillDef> Skills { get; init; } = Empty.List<JobSkillDef>();

        /// <summary>Job bonus modifiers (ARCHITECTURE §6.1); values may scale with <c>job_level</c>.</summary>
        public IReadOnlyList<ModifierDef> StatBonuses { get; init; } = Empty.List<ModifierDef>();

        /// <summary>Weapon types this job may equip (<see cref="Vocabulary.WeaponTypes"/>). Empty means all.</summary>
        public IReadOnlyList<string> WeaponTypes { get; init; } = Empty.List<string>();

        /// <summary>Character body model (<c>model.*</c>). Rig and animations come from the theme manifest.</summary>
        public string? ModelAssetKey { get; init; }

        /// <summary>UI icon (<c>icon.*</c>).</summary>
        public string? IconAssetKey { get; init; }
    }

    /// <summary>Conditions for changing into a job. All listed conditions must hold.</summary>
    public sealed record JobChangeRequirementsDef
    {
        /// <summary>Minimum base level. 0 means no requirement.</summary>
        public int MinBaseLevel { get; init; }

        /// <summary>Minimum job level in the parent job. 0 means no requirement.</summary>
        public int MinJobLevel { get; init; }

        /// <summary>Quests (<c>quest.*</c>) that must be completed.</summary>
        public IReadOnlyList<string> QuestIds { get; init; } = Empty.List<string>();

        /// <summary>True if the job must first be unlocked by an <c>unlock_job</c> reward.</summary>
        public bool RequiresUnlock { get; init; }
    }

    /// <summary>One entry in a job's skill tree.</summary>
    public sealed record JobSkillDef
    {
        /// <summary>Skill id (<c>skill.*</c>).</summary>
        public string SkillId { get; init; } = string.Empty;

        /// <summary>Highest level learnable in this job. Null means the skill's <see cref="SkillDef.MaxLevel"/>.</summary>
        public int? MaxLevel { get; init; }

        /// <summary>Skills that must be learned first, at a minimum level.</summary>
        public IReadOnlyList<SkillRequirementDef> Requires { get; init; } = Empty.List<SkillRequirementDef>();
    }

    /// <summary>A skill at a minimum level.</summary>
    public sealed record SkillRequirementDef
    {
        /// <summary>Skill id (<c>skill.*</c>).</summary>
        public string SkillId { get; init; } = string.Empty;

        /// <summary>Minimum learned level. Default 1.</summary>
        public int Level { get; init; } = 1;
    }
}
