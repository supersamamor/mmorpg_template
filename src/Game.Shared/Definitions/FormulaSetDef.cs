using System.Collections.Generic;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// The root of <c>content/formulas.yaml</c>: every balance expression the engine evaluates (ARCHITECTURE §6.2).
    /// A single set per game, so it has no id. Each value is an expression using only whitelisted
    /// variables and functions (<see cref="Vocabulary.FormulaVariables"/>, <see cref="Vocabulary.FormulaFunctions"/>).
    /// </summary>
    public sealed record FormulaSetDef
    {
        /// <summary>Optional editor-facing name.</summary>
        public string? Name { get; init; }

        /// <summary>Formula per derived stat id (<see cref="Vocabulary.DerivedStats"/>), e.g. <c>max_hp</c>.</summary>
        public IReadOnlyDictionary<string, string> Derived { get; init; } = Empty.Dictionary<string, string>();

        /// <summary>Base physical damage, used by auto-attacks and non-magic <c>damage</c> effects.</summary>
        public string PhysicalDamage { get; init; } = string.Empty;

        /// <summary>Base magic damage, used by magic <c>damage</c> effects.</summary>
        public string MagicDamage { get; init; } = string.Empty;

        /// <summary>Hit probability 0–1 for physical attacks.</summary>
        public string HitChance { get; init; } = string.Empty;

        /// <summary>Critical probability 0–1 for physical attacks.</summary>
        public string CritChance { get; init; } = string.Empty;
    }
}
