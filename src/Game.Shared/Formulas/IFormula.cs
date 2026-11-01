using Game.Shared.Vocabulary;

namespace Game.Shared.Formulas
{
    /// <summary>
    /// A compiled, sandboxed formula (ARCHITECTURE §6.2). Compile once at load with
    /// <see cref="Formula.Parse(string?, FormulaSymbols?)"/>, then evaluate as often as needed.
    /// Evaluation is deterministic, never allocates and never throws.
    /// </summary>
    public interface IFormula
    {
        /// <summary>
        /// Evaluates the formula. Variables are read from <paramref name="context"/> by slot
        /// (see <see cref="FormulaSymbols"/>); the context must use the same symbols the formula was compiled with.
        /// </summary>
        /// <param name="context">Variable values. Constant formulas never read it.</param>
        double Evaluate(IFormulaContext context);

        /// <summary>
        /// Whitelisted variables the source text references, in order of first appearance.
        /// On .NET 5+ this is an <c>IReadOnlySet&lt;string&gt;</c>; <see cref="VocabularySet"/> keeps the
        /// same API on netstandard2.1, which has no such interface.
        /// </summary>
        VocabularySet Variables { get; }
    }

    /// <summary>
    /// Supplies variable values to <see cref="IFormula.Evaluate"/>. Slots come from the
    /// <see cref="FormulaSymbols"/> the formula was compiled with, so lookups are array reads, not string hashing.
    /// <see cref="FormulaContext"/> is the ready-made array-backed implementation.
    /// </summary>
    public interface IFormulaContext
    {
        /// <summary>Current value of the variable in <paramref name="slot"/>.</summary>
        /// <param name="slot">Slot from <see cref="FormulaSymbols.TryGetSlot"/>.</param>
        double GetVariable(int slot);
    }
}
