using System;
using System.Collections.Generic;
using Game.Shared.Definitions;

namespace Game.Shared.Accounts
{
    /// <summary>
    /// What a character may be called, from <c>balance.yaml</c> <c>characters</c> (T-701). Pure; the server checks every name with it, and the database decides
    /// whether a name is free (a unique index on <see cref="Key"/>).
    /// </summary>
    public sealed class CharacterNameRules
    {
        /// <summary>The longest <c>name_max_length</c> content may ask for (the width of the database column).</summary>
        public const int LongestAllowed = 32;

        private readonly List<(char From, char To)> _letters = new List<(char, char)>();
        private readonly HashSet<char> _separators = new HashSet<char>();

        /// <summary>Reads validated content; throws <see cref="ArgumentException"/> on an entry the validator refuses.</summary>
        public CharacterNameRules(CharactersBalanceDef def)
        {
            MinLength = def.NameMinLength;
            MaxLength = def.NameMaxLength;
            foreach (var entry in def.NameLetters)
                _letters.Add(TryParseRange(entry, out var range) ? range : throw new ArgumentException("characters.name_letters: not a character or a range like 'a-z': " + entry));
            foreach (var entry in def.NameSeparators)
                _separators.Add(entry.Length == 1 ? entry[0] : throw new ArgumentException("characters.name_separators: not one character: " + entry));
        }

        /// <summary>Shortest name.</summary>
        public int MinLength { get; }

        /// <summary>Longest name.</summary>
        public int MaxLength { get; }

        /// <summary>
        /// The name as it is stored, or null when it breaks a rule: trimmed and NFKC-normalised, of the allowed length, made of allowed letters with separators
        /// only between two letters (never first, last, or two in a row).
        /// </summary>
        public string? Normalize(string? name)
        {
            if (name is null || name.Length > LongestAllowed * 4) return null;
            if (!AccountRules.TryNormalize(name.Trim(), out var text)) return null;
            if (text.Length < MinLength || text.Length > MaxLength) return null;
            var separated = true;   // at the start: a separator may not come first
            foreach (var c in text)
            {
                if (_separators.Contains(c))
                {
                    if (separated) return null;
                    separated = true;
                }
                else if (IsLetter(c)) separated = false;
                else return null;
            }
            return separated ? null : text;
        }

        /// <summary>The key two names are compared by: they clash when their keys are equal, so letter case does not make a new name.</summary>
        public static string Key(string normalizedName) => normalizedName.ToLowerInvariant();

        /// <summary>Parses one <c>name_letters</c> entry: a single character, or a range <c>x-y</c> with x not above y.</summary>
        public static bool TryParseRange(string? entry, out (char From, char To) range)
        {
            range = default;
            if (entry is null) return false;
            if (entry.Length == 1) range = (entry[0], entry[0]);
            else if (entry.Length == 3 && entry[1] == '-' && entry[0] <= entry[2]) range = (entry[0], entry[2]);
            else return false;
            return !char.IsSurrogate(range.From) && !char.IsSurrogate(range.To) && !char.IsControl(range.From) && !char.IsControl(range.To);
        }

        private bool IsLetter(char c)
        {
            foreach (var (from, to) in _letters)
                if (c >= from && c <= to) return true;
            return false;
        }
    }
}
