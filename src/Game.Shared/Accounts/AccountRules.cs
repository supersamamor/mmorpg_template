using System;
using System.Text;

namespace Game.Shared.Accounts
{
    /// <summary>
    /// The rules of usernames and passwords (T-701): pure and the same everywhere. They are engine rules, not game content: what a character may be called is
    /// content (<see cref="CharacterNameRules"/>).
    /// </summary>
    public static class AccountRules
    {
        /// <summary>Shortest username.</summary>
        public const int MinUsernameLength = 3;

        /// <summary>Longest username.</summary>
        public const int MaxUsernameLength = 24;

        /// <summary>Shortest password. Length is the only rule: composition rules push people to predictable passwords.</summary>
        public const int MinPasswordLength = 10;

        /// <summary>Longest password, in UTF-16 units: room for a passphrase, and a bound on what is hashed.</summary>
        public const int MaxPasswordLength = 128;

        /// <summary>
        /// The normalised form of a username, or null when it is not a valid one: trimmed, NFKC, invariant lower case, then <c>a-z 0-9 . _ -</c> only, starting
        /// with a letter or digit. Only this form is stored and compared, so <c>Ada</c> and <c>ada</c> are one account, and look-alike letters of other
        /// scripts are not accepted at all.
        /// </summary>
        public static string? NormalizeUsername(string? username)
        {
            if (username is null || username.Length > MaxUsernameLength * 4) return null;
            if (!TryNormalize(username.Trim(), out var text)) return null;
            text = text.ToLowerInvariant();
            if (text.Length < MinUsernameLength || text.Length > MaxUsernameLength) return null;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                var letter = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (!letter && (i == 0 || (c != '.' && c != '_' && c != '-'))) return null;
            }
            return text;
        }

        /// <summary>
        /// The form of a password that is hashed (NFKC, so the same password typed on two keyboards hashes alike), or null when it is too short or too long.
        /// Nothing is trimmed and every character counts, spaces included.
        /// </summary>
        public static string? NormalizePassword(string? password)
        {
            if (password is null || password.Length > MaxPasswordLength) return null;
            if (!TryNormalize(password, out var text)) return null;
            return text.Length < MinPasswordLength ? null : text;
        }

        /// <summary>NFKC, or false for text that is not valid Unicode (a lone surrogate).</summary>
        internal static bool TryNormalize(string text, out string normalized)
        {
            try
            {
                normalized = text.Normalize(NormalizationForm.FormKC);
                return true;
            }
            catch (ArgumentException)
            {
                normalized = string.Empty;
                return false;
            }
        }
    }
}
