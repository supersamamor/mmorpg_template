using System;

namespace Game.Shared.Formulas
{
    /// <summary>
    /// Array-backed <see cref="IFormulaContext"/>: one <c>double</c> per slot of its <see cref="FormulaSymbols"/>.
    /// Create one per caller and reuse it (set values, evaluate, repeat); it never allocates after construction.
    /// Unset variables read as 0. Not thread-safe.
    /// </summary>
    public sealed class FormulaContext : IFormulaContext
    {
        private readonly double[] _values;

        /// <summary>A context in which every variable is 0. Handy for constant formulas.</summary>
        public static IFormulaContext None { get; } = new ZeroContext();

        /// <summary>Creates a context for the default whitelist.</summary>
        public FormulaContext()
            : this(FormulaSymbols.Default)
        {
        }

        /// <summary>Creates a context for <paramref name="symbols"/>.</summary>
        /// <param name="symbols">Must be the symbols the evaluated formulas were compiled with.</param>
        public FormulaContext(FormulaSymbols symbols)
        {
            Symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
            _values = new double[symbols.Count];
        }

        /// <summary>The symbol table that defines the slots.</summary>
        public FormulaSymbols Symbols { get; }

        /// <summary>Value by slot.</summary>
        /// <param name="slot">Slot index.</param>
        public double this[int slot]
        {
            get => _values[slot];
            set => _values[slot] = value;
        }

        /// <summary>Value by variable name; unknown names throw <see cref="System.Collections.Generic.KeyNotFoundException"/>.</summary>
        /// <param name="name">Whitelisted variable name.</param>
        public double this[string name]
        {
            get => _values[Symbols.SlotOf(name)];
            set => _values[Symbols.SlotOf(name)] = value;
        }

        /// <summary>Resets every variable to 0.</summary>
        public void Clear() => Array.Clear(_values, 0, _values.Length);

        /// <inheritdoc />
        public double GetVariable(int slot) => _values[slot];

        private sealed class ZeroContext : IFormulaContext
        {
            public double GetVariable(int slot) => 0;
        }
    }
}
