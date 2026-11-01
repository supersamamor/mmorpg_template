using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Vocabulary;

namespace Game.Shared.Stats
{
    /// <summary>
    /// The derived-stat formulas of a <see cref="FormulaSetDef"/>, compiled once at load and put in evaluation order.
    /// Immutable and shareable by every <see cref="StatSheet"/>.
    /// </summary>
    /// <remarks>
    /// <para>A derived formula may read primary stats, <c>base_level</c>, <c>job_level</c> and other derived stats.
    /// Derived stats are evaluated so that each one's dependencies come first (stats without a formula first of all,
    /// then documentation order where there is a choice); a formula sees the <i>final</i> value (after modifiers) of every
    /// stat it reads. A cycle is a load error.</para>
    /// <para>Any other variable (<c>weapon_atk</c>, <c>refine</c>, <c>level</c>, <c>t_*</c>) is not part of a stat sheet and is a
    /// load error here rather than a silent 0.</para>
    /// </remarks>
    public sealed class DerivedStatFormulas
    {
        private static readonly VocabularySet Supplied = VocabularySet.Union(
            Vocabulary.Stats.All,
            new VocabularySet(FormulaVariables.BaseLevel, FormulaVariables.JobLevel));

        private readonly Formula?[] _byStat;
        private readonly int[] _order;

        private DerivedStatFormulas(Formula?[] byStat, int[] order)
        {
            _byStat = byStat;
            _order = order;
        }

        /// <summary>Number of derived stats that have a formula.</summary>
        public int FormulaCount
        {
            get
            {
                var count = 0;
                foreach (var f in _byStat) if (f != null) count++;
                return count;
            }
        }

        /// <summary>Every derived stat index (<see cref="StatIndex"/>), in evaluation order.</summary>
        public IReadOnlyList<int> Order => _order;

        /// <summary>The formula for a stat index, or null (primary stats and derived stats without one).</summary>
        /// <param name="statIndex">Stat index.</param>
        public Formula? FormulaOf(int statIndex) => _byStat[statIndex];

        /// <summary>
        /// Compiles <see cref="FormulaSetDef.Derived"/>. Throws <see cref="ArgumentException"/> for a key that is not a
        /// derived stat, an unsupported variable or a dependency cycle, and <see cref="FormulaException"/> for invalid syntax.
        /// </summary>
        /// <param name="formulas">The formula set (<c>content/formulas.yaml</c>).</param>
        public static DerivedStatFormulas Compile(FormulaSetDef formulas)
        {
            if (formulas == null) throw new ArgumentNullException(nameof(formulas));
            var byStat = new Formula?[StatIndex.Count];
            foreach (var pair in formulas.Derived)
            {
                if (!StatIndex.TryGetIndex(pair.Key, out var index) || StatIndex.IsPrimary(index))
                    throw new ArgumentException("Derived formula key '" + pair.Key + "' is not a derived stat.", nameof(formulas));
                var formula = Formula.Compile(pair.Value);
                foreach (var variable in formula.Variables)
                {
                    if (!Supplied.Contains(variable))
                        throw new ArgumentException("Derived formula for '" + pair.Key + "' uses '" + variable +
                            "', which a stat sheet does not supply (only stats, base_level and job_level).", nameof(formulas));
                }
                byStat[index] = formula;
            }
            return new DerivedStatFormulas(byStat, Sort(byStat));
        }

        private static int[] Sort(Formula?[] byStat)
        {
            var order = new List<int>(StatIndex.Count - StatIndex.PrimaryCount);
            var state = new byte[StatIndex.Count]; // 0 = new, 1 = visiting, 2 = done
            for (var i = StatIndex.PrimaryCount; i < StatIndex.Count; i++)
            {
                if (byStat[i] != null) continue;
                state[i] = 2;
                order.Add(i);
            }
            for (var i = StatIndex.PrimaryCount; i < StatIndex.Count; i++) Visit(i, byStat, state, order);
            return order.ToArray();
        }

        private static void Visit(int index, Formula?[] byStat, byte[] state, List<int> order)
        {
            if (state[index] == 2) return;
            if (state[index] == 1)
                throw new ArgumentException("Derived formulas have a dependency cycle through '" + StatIndex.NameOf(index) + "'.");
            state[index] = 1;
            foreach (var variable in byStat[index]!.Variables)
            {
                if (StatIndex.TryGetIndex(variable, out var dependency) && !StatIndex.IsPrimary(dependency))
                    Visit(dependency, byStat, state, order);
            }
            state[index] = 2;
            order.Add(index);
        }
    }
}
