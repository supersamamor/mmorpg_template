using System;
using Game.Shared.Definitions;
using Game.Shared.Formulas;

namespace Game.Shared.Combat
{
    /// <summary>
    /// A <see cref="DamageEffect"/> with its multiplier formula compiled. Compile once at load (per skill effect) and keep it;
    /// <see cref="AttackContext.Effect"/> points at it for skill hits.
    /// </summary>
    public sealed class CompiledDamageEffect
    {
        private CompiledDamageEffect(DamageEffect source, Formula multiplier)
        {
            Source = source;
            Multiplier = multiplier;
        }

        /// <summary>The content record.</summary>
        public DamageEffect Source { get; }

        /// <summary>True to use the magic damage formula.</summary>
        public bool Magic => Source.Magic;

        /// <summary>Element override, or null to use the attacker's element.</summary>
        public string? Element => Source.Element;

        /// <summary>Multiplier formula; variable <c>level</c> is the skill level.</summary>
        public Formula Multiplier { get; }

        /// <summary>Compiles <paramref name="effect"/>. Throws <see cref="FormulaException"/> for an invalid multiplier (validated content never has one).</summary>
        /// <param name="effect">The damage effect.</param>
        public static CompiledDamageEffect Compile(DamageEffect effect)
        {
            if (effect == null) throw new ArgumentNullException(nameof(effect));
            return new CompiledDamageEffect(effect, Formula.Compile(effect.Multiplier));
        }
    }
}
