using Game.Shared.Formulas;
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
        [Formula]
        public IReadOnlyDictionary<string, string> Derived { get; init; } = Empty.Dictionary<string, string>();

        /// <summary>Base physical damage, used by auto-attacks and non-magic <c>damage</c> effects.</summary>
        [Formula]
        public string PhysicalDamage { get; init; } = string.Empty;

        /// <summary>Base magic damage, used by magic <c>damage</c> effects.</summary>
        [Formula]
        public string MagicDamage { get; init; } = string.Empty;

        /// <summary>Hit probability 0–1 for physical attacks.</summary>
        [Formula]
        public string HitChance { get; init; } = string.Empty;

        /// <summary>Critical probability 0–1 for physical attacks.</summary>
        [Formula]
        public string CritChance { get; init; } = string.Empty;

        /// <summary>
        /// The party EXP share (T-902): the EXP the members who share a kill split evenly, from the party's part of it. Variables: <c>exp</c>, <c>share_count</c>, <c>level_spread</c> only
        /// (<see cref="FormulaSymbols.PartyShare"/>). Applied to base and job EXP alike, floored, never below 0. Absent, it is <c>exp</c>: no bonus.
        /// </summary>
        [Formula]
        public string PartyExp { get; init; } = "exp";
    }
}
