using System.Collections.Generic;

namespace Game.Shared.Vocabulary
{
    /// <summary>Variables formulas may read (whitelist). VOCABULARY.md: "Formula variables (whitelist)".</summary>
    public static class FormulaVariables
    {
        /// <summary><c>base_level</c>: caster base level.</summary>
        public const string BaseLevel = "base_level";

        /// <summary><c>job_level</c>: caster job level.</summary>
        public const string JobLevel = "job_level";

        /// <summary><c>weapon_atk</c>: attack of the caster's equipped weapon.</summary>
        public const string WeaponAtk = "weapon_atk";

        /// <summary><c>refine</c>: refine level of the relevant equipment.</summary>
        public const string Refine = "refine";

        /// <summary><c>level</c>: skill level.</summary>
        public const string SkillLevel = "level";

        /// <summary><c>t_</c>: prefix that turns a caster variable into the target's (<c>t_def</c>, <c>t_vit</c>).</summary>
        public const string TargetPrefix = "t_";

        /// <summary><c>t_level</c>: target base level (documented example of a target variable).</summary>
        public const string TargetLevel = "t_level";

        /// <summary>Caster variables: levels, every stat id, <c>weapon_atk</c>, <c>refine</c>.</summary>
        public static readonly VocabularySet Caster = VocabularySet.Union(
            new VocabularySet(BaseLevel, JobLevel),
            Stats.All,
            new VocabularySet(WeaponAtk, Refine));

        /// <summary>Target variables: <see cref="TargetPrefix"/> + each caster variable, plus <c>t_level</c>.</summary>
        public static readonly VocabularySet Target = BuildTarget();

        /// <summary>Skill variables.</summary>
        public static readonly VocabularySet Skill = new VocabularySet(SkillLevel);

        /// <summary>Every variable a formula may use: caster, target, then skill.</summary>
        public static readonly VocabularySet All = VocabularySet.Union(Caster, Target, Skill);

        private static VocabularySet BuildTarget()
        {
            var names = new List<string>();
            foreach (var name in Caster) names.Add(TargetPrefix + name);
            names.Add(TargetLevel);
            return new VocabularySet(names.ToArray());
        }
    }
}
