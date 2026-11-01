using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Formulas;

namespace Game.Shared.Combat
{
    /// <summary>
    /// The combat content, compiled once at load and shared by every <see cref="CombatResolver"/>: the four combat formulas
    /// of <c>formulas.yaml</c>, the element table of <c>elements.yaml</c> as a dense matrix, and the clamps of
    /// <c>balance.yaml</c>. Immutable and thread-safe.
    /// </summary>
    public sealed class CombatRules
    {
        private readonly Dictionary<string, int> _elementIndex;
        private readonly double[,] _elementMatrix;

        private CombatRules(Formula physical, Formula magic, Formula hit, Formula crit, Formula critMultiplier,
            Dictionary<string, int> elementIndex, double[,] elementMatrix, CombatBalanceDef balance)
        {
            PhysicalDamage = physical;
            MagicDamage = magic;
            HitChance = hit;
            CritChance = crit;
            CritMultiplier = critMultiplier;
            _elementIndex = elementIndex;
            _elementMatrix = elementMatrix;
            Balance = balance;
        }

        /// <summary><c>physical_damage</c>.</summary>
        public Formula PhysicalDamage { get; }

        /// <summary><c>magic_damage</c>.</summary>
        public Formula MagicDamage { get; }

        /// <summary><c>hit_chance</c> (0–1).</summary>
        public Formula HitChance { get; }

        /// <summary><c>crit_chance</c> (0–1).</summary>
        public Formula CritChance { get; }

        /// <summary>Damage multiplier on a critical hit. 1 (no bonus) until content supplies one; see <see cref="Compile"/>.</summary>
        public Formula CritMultiplier { get; }

        /// <summary>Damage and chance clamps.</summary>
        public CombatBalanceDef Balance { get; }

        /// <summary>
        /// Compiles the combat content. Throws <see cref="FormulaException"/> for an invalid formula and
        /// <see cref="ArgumentException"/> when the element table is not square (some attack/defend pair missing):
        /// validated content has neither, so a throw here is a load error, never a runtime surprise.
        /// </summary>
        /// <param name="formulas">The formula set (<c>formulas.yaml</c>).</param>
        /// <param name="elements">The element table (<c>elements.yaml</c>).</param>
        /// <param name="balance">The combat clamps (<c>balance.yaml</c> <c>combat</c>).</param>
        /// <param name="critMultiplier">
        /// Overrides <see cref="CombatBalanceDef.CritMultiplier"/> (tests and what-if tools); null uses the content value.
        /// </param>
        public static CombatRules Compile(FormulaSetDef formulas, ElementTableDef elements, CombatBalanceDef balance,
            string? critMultiplier = null)
        {
            if (formulas == null) throw new ArgumentNullException(nameof(formulas));
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            if (balance == null) throw new ArgumentNullException(nameof(balance));

            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var attack in elements.Multipliers.Keys) index[attack] = index.Count;
            var matrix = new double[index.Count, index.Count];
            foreach (var row in elements.Multipliers)
            {
                foreach (var defend in index.Keys)
                {
                    if (!row.Value.TryGetValue(defend, out var value))
                        throw new ArgumentException("Element table has no multiplier for " + row.Key + " → " + defend + ".", nameof(elements));
                    matrix[index[row.Key], index[defend]] = value;
                }
                foreach (var defend in row.Value.Keys)
                {
                    if (!index.ContainsKey(defend))
                        throw new ArgumentException("Element table column " + defend + " has no row of its own.", nameof(elements));
                }
            }

            return new CombatRules(
                Formula.Compile(formulas.PhysicalDamage),
                Formula.Compile(formulas.MagicDamage),
                Formula.Compile(formulas.HitChance),
                Formula.Compile(formulas.CritChance),
                Formula.Compile(critMultiplier ?? balance.CritMultiplier),
                index, matrix, balance);
        }

        /// <summary>Element multiplier of <paramref name="attack"/> against <paramref name="defend"/>.</summary>
        /// <param name="attack">Attacking element id.</param>
        /// <param name="defend">Defending element id.</param>
        /// <exception cref="KeyNotFoundException">An id that is not in the element table.</exception>
        public double ElementMultiplier(string attack, string defend) =>
            _elementMatrix[IndexOf(attack), IndexOf(defend)];

        private int IndexOf(string element)
        {
            if (element != null && _elementIndex.TryGetValue(element, out var i)) return i;
            throw new KeyNotFoundException("Unknown element '" + element + "'.");
        }
    }
}
