using System;
using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Formulas
{
    /// <summary>
    /// The variables a formula may use, each with a fixed integer slot (its index in <see cref="Variables"/>).
    /// Compiling resolves names to slots once, so evaluation reads variables by index.
    /// <see cref="Default"/> is the VOCABULARY.md whitelist; a narrower set can be built for fields
    /// where only some variables make sense.
    /// </summary>
    public sealed class FormulaSymbols
    {
        private readonly Dictionary<string, int> _slots;

        /// <summary>Every whitelisted variable (<see cref="FormulaVariables.All"/>).</summary>
        public static readonly FormulaSymbols Default = new FormulaSymbols(FormulaVariables.All);

        /// <summary>Only <c>base_level</c>: formulas awarded per base level.</summary>
        public static readonly FormulaSymbols BaseLevelOnly = new FormulaSymbols(new VocabularySet(FormulaVariables.BaseLevel));

        /// <summary>Only <c>job_level</c>: formulas awarded per job level.</summary>
        public static readonly FormulaSymbols JobLevelOnly = new FormulaSymbols(new VocabularySet(FormulaVariables.JobLevel));

        /// <summary>Only <c>stat_value</c>: the stat point cost formula.</summary>
        public static readonly FormulaSymbols StatValueOnly = new FormulaSymbols(new VocabularySet(FormulaVariables.StatValue));

        /// <summary>Creates a symbol table; slot <c>i</c> is <c>variables.Ordered[i]</c>.</summary>
        /// <param name="variables">Allowed variable names.</param>
        public FormulaSymbols(VocabularySet variables)
        {
            Variables = variables ?? throw new ArgumentNullException(nameof(variables));
            _slots = new Dictionary<string, int>(variables.Count, StringComparer.Ordinal);
            for (var i = 0; i < variables.Count; i++) _slots.Add(variables.Ordered[i], i);
        }

        /// <summary>Allowed variable names, in slot order.</summary>
        public VocabularySet Variables { get; }

        /// <summary>Number of slots.</summary>
        public int Count => Variables.Count;

        /// <summary>Looks up the slot of a variable. False if it is not whitelisted.</summary>
        /// <param name="name">Variable name (ordinal, case-sensitive).</param>
        /// <param name="slot">The slot, or -1.</param>
        public bool TryGetSlot(string name, out int slot)
        {
            if (name != null && _slots.TryGetValue(name, out slot)) return true;
            slot = -1;
            return false;
        }

        /// <summary>Slot of a whitelisted variable; throws <see cref="KeyNotFoundException"/> otherwise.</summary>
        /// <param name="name">Variable name.</param>
        public int SlotOf(string name)
        {
            if (TryGetSlot(name, out var slot)) return slot;
            throw new KeyNotFoundException("'" + name + "' is not a formula variable.");
        }

        /// <summary>Variable name in <paramref name="slot"/>.</summary>
        /// <param name="slot">Slot index.</param>
        public string NameOf(int slot) => Variables.Ordered[slot];
    }
}
