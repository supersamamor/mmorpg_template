using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// An immutable, ordinal set of vocabulary terms that keeps declaration order.
    /// On .NET 5+ it implements <c>IReadOnlySet&lt;string&gt;</c>; on netstandard2.1 (which has no
    /// such interface) it offers the same <see cref="Contains"/> / <see cref="Count"/> / enumeration API.
    /// Enumeration follows <see cref="Ordered"/>, so generated schemas and reports are deterministic.
    /// </summary>
    public sealed class VocabularySet : IReadOnlyCollection<string>
#if NET5_0_OR_GREATER
        , IReadOnlySet<string>
#endif
    {
        private readonly ReadOnlyCollection<string> _ordered;
        private readonly HashSet<string> _set;

        /// <summary>Creates a set from the given terms. Duplicates are a programming error and throw.</summary>
        /// <param name="values">Terms in documentation order.</param>
        public VocabularySet(params string[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            _set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (string.IsNullOrEmpty(value)) throw new ArgumentException("Vocabulary terms must be non-empty.", nameof(values));
                if (!_set.Add(value)) throw new ArgumentException("Duplicate vocabulary term '" + value + "'.", nameof(values));
            }
            _ordered = Array.AsReadOnly((string[])values.Clone());
        }

        /// <summary>Concatenates several sets, in order. Overlapping terms throw.</summary>
        /// <param name="sets">Sets to combine.</param>
        public static VocabularySet Union(params VocabularySet[] sets)
        {
            var all = new List<string>();
            foreach (var set in sets) all.AddRange(set._ordered);
            return new VocabularySet(all.ToArray());
        }

        /// <summary>Terms in documentation order.</summary>
        public IReadOnlyList<string> Ordered => _ordered;

        /// <summary>Number of terms.</summary>
        public int Count => _ordered.Count;

        /// <summary>True if <paramref name="item"/> is a term of this set (ordinal, case-sensitive).</summary>
        /// <param name="item">Candidate term; null is never contained.</param>
        public bool Contains(string item) => item != null && _set.Contains(item);

        /// <summary>Enumerates terms in documentation order.</summary>
        public IEnumerator<string> GetEnumerator() => _ordered.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

#if NET5_0_OR_GREATER
        /// <inheritdoc />
        public bool IsProperSubsetOf(IEnumerable<string> other) => _set.IsProperSubsetOf(other);

        /// <inheritdoc />
        public bool IsProperSupersetOf(IEnumerable<string> other) => _set.IsProperSupersetOf(other);

        /// <inheritdoc />
        public bool IsSubsetOf(IEnumerable<string> other) => _set.IsSubsetOf(other);

        /// <inheritdoc />
        public bool IsSupersetOf(IEnumerable<string> other) => _set.IsSupersetOf(other);

        /// <inheritdoc />
        public bool Overlaps(IEnumerable<string> other) => _set.Overlaps(other);

        /// <inheritdoc />
        public bool SetEquals(IEnumerable<string> other) => _set.SetEquals(other);
#endif
    }
}
