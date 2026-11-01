using System;
using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Stats
{
    /// <summary>
    /// Dense integer indices for stat ids, so the stat pipeline works on arrays instead of string lookups.
    /// Index <c>i</c> is <c>Stats.All.Ordered[i]</c>: the primary stats come first (<c>0 .. PrimaryCount-1</c>),
    /// then the derived stats. Resolve ids to indices once (at load or construction), not per tick.
    /// </summary>
    public static class StatIndex
    {
        private static readonly Dictionary<string, int> Indices = Build();

        /// <summary>Total number of stats (primary + derived).</summary>
        public static int Count => Vocabulary.Stats.All.Count;

        /// <summary>Number of primary stats; primary stats occupy indices <c>0 .. PrimaryCount-1</c>.</summary>
        public static int PrimaryCount => PrimaryStats.All.Count;

        /// <summary>Looks up the index of a stat id. False if it is not a stat.</summary>
        /// <param name="stat">Stat id (ordinal, case-sensitive).</param>
        /// <param name="index">The index, or -1.</param>
        public static bool TryGetIndex(string stat, out int index)
        {
            if (stat != null && Indices.TryGetValue(stat, out index)) return true;
            index = -1;
            return false;
        }

        /// <summary>Index of a stat id; throws <see cref="ArgumentException"/> if it is not a stat.</summary>
        /// <param name="stat">Stat id.</param>
        public static int IndexOf(string stat)
        {
            if (TryGetIndex(stat, out var index)) return index;
            throw new ArgumentException("'" + stat + "' is not a stat id.", nameof(stat));
        }

        /// <summary>Stat id at <paramref name="index"/>.</summary>
        /// <param name="index">Stat index.</param>
        public static string NameOf(int index) => Vocabulary.Stats.All.Ordered[index];

        /// <summary>True if <paramref name="index"/> is a primary stat.</summary>
        /// <param name="index">Stat index.</param>
        public static bool IsPrimary(int index) => index >= 0 && index < PrimaryCount;

        private static Dictionary<string, int> Build()
        {
            var all = Vocabulary.Stats.All.Ordered;
            var map = new Dictionary<string, int>(all.Count, StringComparer.Ordinal);
            for (var i = 0; i < all.Count; i++) map.Add(all[i], i);
            return map;
        }
    }
}
