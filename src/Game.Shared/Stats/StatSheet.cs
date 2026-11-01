using System;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Vocabulary;

namespace Game.Shared.Stats
{
    /// <summary>
    /// One entity's stats: a <see cref="StatBlock"/> (base values, levels) plus a <see cref="ModifierSet"/>, run through the
    /// ARCHITECTURE §6.1 pipeline to final primary and derived values. Results are cached and recomputed lazily on the
    /// next read after either input changes. Not thread-safe.
    /// </summary>
    /// <remarks>
    /// <para><b>Pipeline</b>, for every stat:</para>
    /// <code>
    /// value = (Base + ΣFlat) × (100 + ΣPercent) / 100      percent values: 10 means +10%
    /// value = last-added Override, if any                   replaces the line above
    /// </code>
    /// <list type="number">
    /// <item>Primary stats: Base is the <see cref="StatBlock"/> value. The result is floored to an integer at the end
    /// (after the override), with a 1e-9 tolerance so 12.999999999 from binary fractions floors to 13.</item>
    /// <item>Derived stats, in <see cref="DerivedStatFormulas.Order"/>: Base is the stat's formula evaluated with the final
    /// primary stats, <c>base_level</c>, <c>job_level</c> and already-final derived stats (0 if there is no formula).
    /// The result is <b>not</b> rounded: several derived stats are fractional by definition (<c>aspd</c>, <c>move_speed</c>,
    /// <c>crit</c>, <c>cast_time_mod</c>, regen). Formulas floor where they mean integers; callers that need an integer
    /// after percent modifiers (an HP pool from <c>max_hp</c>) use <see cref="GetInt(int)"/>.</item>
    /// </list>
    /// <para><b>Cost.</b> A recompute is one pass over the modifiers plus one formula evaluation per derived stat, using
    /// arrays and a <see cref="FormulaContext"/> owned by the sheet; it allocates nothing. Reads between changes are array reads.</para>
    /// </remarks>
    public sealed class StatSheet
    {
        private const double FloorTolerance = 1e-9;

        private static readonly int[] CasterSlots = BuildSlots(FormulaSymbols.Default, string.Empty);
        private static readonly int[] TargetSlots = BuildSlots(FormulaSymbols.Default, FormulaVariables.TargetPrefix);
        private static readonly int BaseLevelSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.BaseLevel);
        private static readonly int JobLevelSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.JobLevel);

        private readonly DerivedStatFormulas _formulas;
        private readonly FormulaContext _context = new FormulaContext(FormulaSymbols.Default);
        private readonly double[] _values = new double[StatIndex.Count];
        private readonly double[] _flat = new double[StatIndex.Count];
        private readonly double[] _percent = new double[StatIndex.Count];
        private readonly double[] _override = new double[StatIndex.Count];
        private readonly bool[] _hasOverride = new bool[StatIndex.Count];
        private long _computedVersion = -1;

        /// <summary>Creates an empty sheet (all base stats and levels 0, no modifiers).</summary>
        /// <param name="formulas">Compiled derived formulas, shared across sheets.</param>
        public StatSheet(DerivedStatFormulas formulas)
        {
            _formulas = formulas ?? throw new ArgumentNullException(nameof(formulas));
        }

        /// <summary>Base primary stats and levels. Changing them marks the sheet stale.</summary>
        public StatBlock Base { get; } = new StatBlock();

        /// <summary>Active modifiers. Changing them marks the sheet stale.</summary>
        public ModifierSet Modifiers { get; } = new ModifierSet();

        /// <summary>Changes whenever <see cref="Base"/> or <see cref="Modifiers"/> changes (their versions summed).
        /// Save it and compare to know when to resend stats to the client or refresh a display.</summary>
        public long Version => Base.Version + Modifiers.Version;

        /// <summary>Final value of a stat by index (<see cref="StatIndex"/>).</summary>
        /// <param name="statIndex">Stat index.</param>
        public double Get(int statIndex)
        {
            Refresh();
            return _values[statIndex];
        }

        /// <summary>Final value of a stat by id.</summary>
        /// <param name="stat">Stat id.</param>
        public double Get(string stat) => Get(StatIndex.IndexOf(stat));

        /// <summary>Final value floored to an integer (same tolerance as primary stats).</summary>
        /// <param name="statIndex">Stat index.</param>
        public int GetInt(int statIndex) => (int)Math.Floor(Get(statIndex) + FloorTolerance);

        /// <summary>Final value floored to an integer.</summary>
        /// <param name="stat">Stat id.</param>
        public int GetInt(string stat) => GetInt(StatIndex.IndexOf(stat));

        /// <summary>
        /// Writes the levels and every final stat into <paramref name="context"/> for combat or skill formulas:
        /// caster variables (<c>str</c>, <c>atk</c>, <c>base_level</c>, ...) or, with <paramref name="asTarget"/>, the
        /// <c>t_</c> variables (<c>t_def</c>, <c>t_base_level</c>, and <c>t_level</c> = base level). Variables the context's
        /// symbols lack are skipped. Other slots (<c>weapon_atk</c>, <c>refine</c>, <c>level</c>) are left to the caller.
        /// Allocation-free for contexts on <see cref="FormulaSymbols.Default"/>.
        /// </summary>
        /// <param name="context">Destination context.</param>
        /// <param name="asTarget">Write the <c>t_</c> variables instead of the caster ones.</param>
        public void WriteTo(FormulaContext context, bool asTarget = false)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            Refresh();
            var prefix = asTarget ? FormulaVariables.TargetPrefix : string.Empty;
            var symbols = context.Symbols;
            if (ReferenceEquals(symbols, FormulaSymbols.Default))
            {
                var slots = asTarget ? TargetSlots : CasterSlots;
                for (var i = 0; i < _values.Length; i++) context[slots[i]] = _values[i];
            }
            else
            {
                for (var i = 0; i < _values.Length; i++) Set(context, prefix + StatIndex.NameOf(i), _values[i]);
            }
            Set(context, prefix + FormulaVariables.BaseLevel, Base.BaseLevel);
            Set(context, prefix + FormulaVariables.JobLevel, Base.JobLevel);
            if (asTarget) Set(context, FormulaVariables.TargetLevel, Base.BaseLevel);
        }

        /// <summary>Recomputes now if an input changed since the last computation. Reads call this themselves.</summary>
        public void Refresh()
        {
            var version = Version;
            if (version == _computedVersion) return;
            Recompute();
            _computedVersion = version;
        }

        private void Recompute()
        {
            Array.Clear(_flat, 0, _flat.Length);
            Array.Clear(_percent, 0, _percent.Length);
            Array.Clear(_hasOverride, 0, _hasOverride.Length);
            var modifiers = Modifiers;
            for (var i = 0; i < modifiers.Count; i++)
            {
                var m = modifiers[i];
                switch (m.Op)
                {
                    case ModifierOp.Flat:
                        _flat[m.StatIndex] += m.Value;
                        break;
                    case ModifierOp.Percent:
                        _percent[m.StatIndex] += m.Value;
                        break;
                    case ModifierOp.Override:
                        _override[m.StatIndex] = m.Value; // later entries overwrite earlier ones: latest wins
                        _hasOverride[m.StatIndex] = true;
                        break;
                }
            }

            _context.Clear();
            for (var i = 0; i < StatIndex.PrimaryCount; i++)
            {
                var value = Math.Floor(Combine(i, Base[i]) + FloorTolerance);
                _values[i] = value;
                _context[CasterSlots[i]] = value;
            }
            _context[BaseLevelSlot] = Base.BaseLevel;
            _context[JobLevelSlot] = Base.JobLevel;

            var order = _formulas.Order;
            for (var k = 0; k < order.Count; k++)
            {
                var i = order[k];
                var formula = _formulas.FormulaOf(i);
                var value = Combine(i, formula == null ? 0 : formula.Evaluate(_context));
                _values[i] = value;
                _context[CasterSlots[i]] = value;
            }
        }

        private double Combine(int index, double baseValue)
        {
            if (_hasOverride[index]) return _override[index];
            var value = (baseValue + _flat[index]) * (100 + _percent[index]) / 100;
            return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
        }

        private static void Set(FormulaContext context, string name, double value)
        {
            if (context.Symbols.TryGetSlot(name, out var slot)) context[slot] = value;
        }

        private static int[] BuildSlots(FormulaSymbols symbols, string prefix)
        {
            var slots = new int[StatIndex.Count];
            for (var i = 0; i < slots.Length; i++) slots[i] = symbols.SlotOf(prefix + StatIndex.NameOf(i));
            return slots;
        }
    }
}
