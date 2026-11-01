using System.Collections.Generic;

namespace Game.Shared.Combat
{
    /// <summary>Names of <see cref="DamageBreakdown"/> steps, in the order <see cref="CombatResolver"/> records them.</summary>
    public static class BreakdownSteps
    {
        /// <summary>Hit probability after the balance clamps (physical attacks only).</summary>
        public const string HitChance = "hit_chance";

        /// <summary>The [0, 1) roll compared with the hit chance; the attack lands when roll &lt; chance.</summary>
        public const string HitRoll = "hit_roll";

        /// <summary>Critical probability after the balance clamps (physical hits only).</summary>
        public const string CritChance = "crit_chance";

        /// <summary>The [0, 1) roll compared with the crit chance.</summary>
        public const string CritRoll = "crit_roll";

        /// <summary>The damage formula evaluated as if the target had no <c>def</c> / <c>mdef</c>.</summary>
        public const string Base = "base";

        /// <summary>The damage formula as written, i.e. after the target's defense.</summary>
        public const string Defense = "defense";

        /// <summary>The skill damage effect's multiplier (1 for auto-attacks).</summary>
        public const string Skill = "skill_multiplier";

        /// <summary>The element table multiplier.</summary>
        public const string Element = "element_multiplier";

        /// <summary>The critical multiplier (1 when not critical).</summary>
        public const string Crit = "crit_multiplier";

        /// <summary>Damage before clamping: <see cref="Defense"/> × every multiplier.</summary>
        public const string Raw = "raw";

        /// <summary>Final damage: floored and clamped (0 on a miss; negative heals).</summary>
        public const string Final = "final";
    }

    /// <summary>One named step of a damage calculation.</summary>
    public readonly struct BreakdownStep
    {
        /// <summary>Creates a step.</summary>
        /// <param name="name">Step name (<see cref="BreakdownSteps"/> or an <see cref="IDamageModifier.Name"/>).</param>
        /// <param name="value">The step's value: a chance, a roll, an amount of damage or a multiplier.</param>
        public BreakdownStep(string name, double value)
        {
            Name = name;
            Value = value;
        }

        /// <summary>Step name.</summary>
        public string Name { get; }

        /// <summary>Step value.</summary>
        public double Value { get; }

        /// <inheritdoc />
        public override string ToString() => Name + " = " + Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The named steps of one <see cref="CombatResolver.Resolve"/> call, for debugging balance and the <c>stats</c> report.
    /// Optional and reusable: set <see cref="AttackContext.Breakdown"/> to an instance to record, leave it null to skip
    /// recording (the normal server path). The resolver clears it at the start of every call, so one instance can be
    /// reused across hits; copy the steps out if you need to keep them.
    /// </summary>
    /// <remarks>
    /// Order for a physical hit: <c>hit_chance, hit_roll, crit_chance, crit_roll, base, defense, skill_multiplier,
    /// element_multiplier</c>, one step per <see cref="IDamageModifier"/>, then <c>crit_multiplier, raw, final</c>.
    /// Magic attacks skip the four chance/roll steps. A miss stops after <c>hit_roll</c> with <c>final = 0</c>.
    /// </remarks>
    public sealed class DamageBreakdown
    {
        private readonly List<BreakdownStep> _steps = new List<BreakdownStep>(16);

        /// <summary>Recorded steps in order.</summary>
        public IReadOnlyList<BreakdownStep> Steps => _steps;

        /// <summary>Number of recorded steps.</summary>
        public int Count => _steps.Count;

        /// <summary>Step by position.</summary>
        /// <param name="index">Position.</param>
        public BreakdownStep this[int index] => _steps[index];

        /// <summary>Value of the first step called <paramref name="name"/>.</summary>
        /// <param name="name">Step name.</param>
        /// <param name="value">The value, or 0.</param>
        /// <returns>True if the step was recorded.</returns>
        public bool TryGet(string name, out double value)
        {
            for (var i = 0; i < _steps.Count; i++)
            {
                if (_steps[i].Name != name) continue;
                value = _steps[i].Value;
                return true;
            }
            value = 0;
            return false;
        }

        /// <summary>Removes every step (keeps the capacity).</summary>
        public void Clear() => _steps.Clear();

        /// <summary>Appends a step.</summary>
        /// <param name="name">Step name.</param>
        /// <param name="value">Step value.</param>
        public void Add(string name, double value) => _steps.Add(new BreakdownStep(name, value));

        /// <inheritdoc />
        public override string ToString() => string.Join(", ", _steps);
    }
}
