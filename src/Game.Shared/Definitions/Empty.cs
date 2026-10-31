using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Game.Shared.Definitions
{
    /// <summary>Shared immutable empty collections used as property defaults.</summary>
    internal static class Empty
    {
        public static IReadOnlyList<T> List<T>() => Array.Empty<T>();

        public static IReadOnlyDictionary<TKey, TValue> Dictionary<TKey, TValue>() where TKey : notnull
            => EmptyDictionary<TKey, TValue>.Instance;

        private static class EmptyDictionary<TKey, TValue> where TKey : notnull
        {
            public static readonly IReadOnlyDictionary<TKey, TValue> Instance =
                new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>());
        }
    }
}
