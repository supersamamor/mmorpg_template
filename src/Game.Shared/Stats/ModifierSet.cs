using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Formulas;

namespace Game.Shared.Stats
{
    /// <summary>One active modifier: an already-evaluated amount on one stat, tagged with the source that owns it.</summary>
    public readonly struct StatModifier
    {
        /// <summary>Creates a modifier.</summary>
        /// <param name="sourceId">Owner; see <see cref="SourceId"/>.</param>
        /// <param name="statIndex">Stat index (<see cref="Stats.StatIndex"/>).</param>
        /// <param name="op">Operation.</param>
        /// <param name="value">Amount; for <see cref="ModifierOp.Percent"/>, <c>10</c> means +10%.</param>
        public StatModifier(string sourceId, int statIndex, ModifierOp op, double value)
        {
            SourceId = sourceId;
            StatIndex = statIndex;
            Op = op;
            Value = value;
        }

        /// <summary>Who applied it, e.g. <c>equip:weapon</c>, <c>job:job.warden</c>, <c>status:status.swift</c>. Compared ordinally.</summary>
        public string SourceId { get; }

        /// <summary>Stat index (<see cref="Stats.StatIndex"/>).</summary>
        public int StatIndex { get; }

        /// <summary>Operation.</summary>
        public ModifierOp Op { get; }

        /// <summary>Evaluated amount.</summary>
        public double Value { get; }

        /// <inheritdoc />
        public override string ToString() => SourceId + ": " + Stats.StatIndex.NameOf(StatIndex) + " " + Op + " " + Value;
    }

    /// <summary>
    /// The modifiers currently applied to one entity, each tagged with a source id so a whole source
    /// (an unequipped item, an expired status) can be removed at once (ARCHITECTURE §6.1).
    /// </summary>
    /// <remarks>
    /// <para><b>Values are evaluated when added.</b> A modifier's value is a formula; <see cref="Add(string, CompiledModifier, IFormulaContext)"/>
    /// evaluates it once against the caller's context and stores the number. When an input of that formula changes
    /// (refine, skill level, job level), the owner removes and re-adds its source. This keeps recompute free of formula
    /// work for modifiers and makes the result independent of evaluation order.</para>
    /// <para>Insertion order is kept; it decides which <see cref="ModifierOp.Override"/> wins (the latest).</para>
    /// <para>Not thread-safe. Entries live in one list; add is amortized O(1), <see cref="RemoveBySource"/> is one O(n) pass.</para>
    /// </remarks>
    public sealed class ModifierSet
    {
        private readonly List<StatModifier> _items = new List<StatModifier>();

        /// <summary>Incremented on every change; compare against a saved value to detect changes.</summary>
        public long Version { get; private set; }

        /// <summary>Number of active modifiers.</summary>
        public int Count => _items.Count;

        /// <summary>Modifier at <paramref name="index"/>, in insertion order.</summary>
        /// <param name="index">0 .. Count-1.</param>
        public StatModifier this[int index] => _items[index];

        /// <summary>Adds an already-evaluated modifier.</summary>
        /// <param name="sourceId">Owner (non-empty).</param>
        /// <param name="statIndex">Stat index (<see cref="Stats.StatIndex"/>).</param>
        /// <param name="op">Operation.</param>
        /// <param name="value">Amount; non-finite values are stored as 0.</param>
        public void Add(string sourceId, int statIndex, ModifierOp op, double value)
        {
            if (string.IsNullOrEmpty(sourceId)) throw new ArgumentException("Source id must be non-empty.", nameof(sourceId));
            if (statIndex < 0 || statIndex >= Stats.StatIndex.Count) throw new ArgumentOutOfRangeException(nameof(statIndex));
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
            _items.Add(new StatModifier(sourceId, statIndex, op, value));
            Version++;
        }

        /// <summary>Adds an already-evaluated modifier by stat id.</summary>
        /// <param name="sourceId">Owner (non-empty).</param>
        /// <param name="stat">Stat id.</param>
        /// <param name="op">Operation.</param>
        /// <param name="value">Amount.</param>
        public void Add(string sourceId, string stat, ModifierOp op, double value) =>
            Add(sourceId, Stats.StatIndex.IndexOf(stat), op, value);

        /// <summary>Evaluates <paramref name="modifier"/> against <paramref name="context"/> and adds the result.</summary>
        /// <param name="sourceId">Owner (non-empty).</param>
        /// <param name="modifier">Compiled modifier.</param>
        /// <param name="context">The source's variables (see <see cref="CompiledModifier.Evaluate"/>).</param>
        public void Add(string sourceId, CompiledModifier modifier, IFormulaContext context)
        {
            if (modifier == null) throw new ArgumentNullException(nameof(modifier));
            Add(sourceId, modifier.StatIndex, modifier.Op, modifier.Evaluate(context));
        }

        /// <summary>Adds every modifier of one source.</summary>
        /// <param name="sourceId">Owner (non-empty).</param>
        /// <param name="modifiers">Compiled modifiers.</param>
        /// <param name="context">The source's variables.</param>
        public void AddRange(string sourceId, IReadOnlyList<CompiledModifier> modifiers, IFormulaContext context)
        {
            if (modifiers == null) throw new ArgumentNullException(nameof(modifiers));
            for (var i = 0; i < modifiers.Count; i++) Add(sourceId, modifiers[i], context);
        }

        /// <summary>Removes every modifier owned by <paramref name="sourceId"/> in one pass, keeping the order of the rest.</summary>
        /// <param name="sourceId">Owner.</param>
        /// <returns>Number removed. <see cref="Version"/> changes only if it is non-zero.</returns>
        public int RemoveBySource(string sourceId)
        {
            var write = 0;
            for (var read = 0; read < _items.Count; read++)
            {
                var item = _items[read];
                if (string.Equals(item.SourceId, sourceId, StringComparison.Ordinal)) continue;
                if (write != read) _items[write] = item;
                write++;
            }
            var removed = _items.Count - write;
            if (removed == 0) return 0;
            _items.RemoveRange(write, removed);
            Version++;
            return removed;
        }

        /// <summary>True if any modifier is owned by <paramref name="sourceId"/>.</summary>
        /// <param name="sourceId">Owner.</param>
        public bool HasSource(string sourceId)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].SourceId, sourceId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Removes every modifier.</summary>
        public void Clear()
        {
            if (_items.Count == 0) return;
            _items.Clear();
            Version++;
        }
    }
}
