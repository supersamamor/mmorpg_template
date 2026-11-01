using System;
using Game.Shared.Definitions;
using Game.Shared.Formulas;

namespace Game.Shared.Stats
{
    /// <summary>
    /// A <see cref="ModifierDef"/> with its stat resolved to an index and its value compiled to a <see cref="Formula"/>.
    /// Compile once at load (per item, job bonus, passive, status) and reuse; adding it to a <see cref="ModifierSet"/>
    /// only evaluates the formula.
    /// </summary>
    public sealed class CompiledModifier
    {
        private CompiledModifier(string stat, int statIndex, ModifierOp op, Formula value)
        {
            Stat = stat;
            StatIndex = statIndex;
            Op = op;
            Value = value;
        }

        /// <summary>Stat id.</summary>
        public string Stat { get; }

        /// <summary>Stat index (<see cref="Stats.StatIndex"/>).</summary>
        public int StatIndex { get; }

        /// <summary>How the value combines with the stat.</summary>
        public ModifierOp Op { get; }

        /// <summary>The compiled value formula.</summary>
        public Formula Value { get; }

        /// <summary>
        /// Compiles a modifier definition. Throws <see cref="ArgumentException"/> for an unknown stat and
        /// <see cref="FormulaException"/> for an invalid value; both are load errors the validator reports first.
        /// </summary>
        /// <param name="def">The definition.</param>
        public static CompiledModifier Compile(ModifierDef def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            return new CompiledModifier(def.Stat, Stats.StatIndex.IndexOf(def.Stat), def.Op, Formula.Compile(def.Value));
        }

        /// <summary>
        /// Evaluates the value. Constant values skip the context. The caller supplies the source's own variables:
        /// <c>refine</c> for equipment, <c>level</c> (skill level) for passives and statuses, <c>job_level</c> /
        /// <c>base_level</c> for job bonuses. Unset variables read as 0.
        /// </summary>
        /// <param name="context">Variables for the source; <see cref="FormulaContext.None"/> is fine for constants.</param>
        public double Evaluate(IFormulaContext context) =>
            Value.TryGetConstant(out var constant) ? constant : Value.Evaluate(context ?? FormulaContext.None);

        /// <inheritdoc />
        public override string ToString() => Stat + " " + Op + " " + Value.Source;
    }
}
