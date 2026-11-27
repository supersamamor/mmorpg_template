namespace Game.Shared.Vocabulary
{
    /// <summary>Why an account request was refused: the <c>error</c> of its answer (VOCABULARY.md "Account errors").</summary>
    public static class AccountErrors
    {
        /// <summary>The body is not the JSON object the endpoint expects, is too long, or lacks a field.</summary>
        public const string InvalidRequest = "invalid_request";

        /// <summary>The username does not follow the rules (length, characters).</summary>
        public const string InvalidUsername = "invalid_username";

        /// <summary>The password is too short or too long.</summary>
        public const string InvalidPassword = "invalid_password";

        /// <summary>Another account has this username.</summary>
        public const string UsernameTaken = "username_taken";

        /// <summary>A password change to the password the account has already (T-710).</summary>
        public const string SamePassword = "same_password";

        /// <summary>Unknown username or wrong password (at a password change: the current one is wrong): the same answer for both.</summary>
        public const string InvalidCredentials = "invalid_credentials";

        /// <summary>The account waits to be erased (T-703), told only to someone who has its password; the answer says how long is left. Its owner can take the request back.</summary>
        public const string AccountDeleting = "account_deleting";

        /// <summary>The session token is missing, unknown, expired or revoked: sign in again.</summary>
        public const string InvalidSession = "invalid_session";

        /// <summary>Too many requests or failures; the answer says when to retry.</summary>
        public const string RateLimited = "rate_limited";

        /// <summary>The character name does not follow the rules of <c>balance.yaml</c> <c>characters</c>.</summary>
        public const string InvalidName = "invalid_name";

        /// <summary>An appearance id is unknown, or a face or hairstyle is not one of the chosen gender's (T-705).</summary>
        public const string InvalidAppearance = "invalid_appearance";

        /// <summary>Another character has this name (whatever its letter case).</summary>
        public const string NameTaken = "name_taken";

        /// <summary>Every character slot of the account is in use.</summary>
        public const string NoFreeSlot = "no_free_slot";

        /// <summary>The account has no such character.</summary>
        public const string UnknownCharacter = "unknown_character";

        /// <summary>The character is in the world (played, or in its grace period): it cannot be deleted now.</summary>
        public const string CharacterInUse = "character_in_use";

        /// <summary>The server could not answer now (the database is away, or too many logins at once): try again later.</summary>
        public const string Unavailable = "unavailable";

        /// <summary>Staff banned the account (T-806): told only to someone who has its password, with <c>retry_after</c> (seconds until the ban ends; none when it never does).</summary>
        public const string AccountBanned = "account_banned";

        /// <summary>Every account error.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            InvalidRequest, InvalidUsername, InvalidPassword, SamePassword, UsernameTaken, InvalidCredentials, AccountDeleting, InvalidSession, RateLimited, InvalidName, InvalidAppearance, NameTaken,
            NoFreeSlot, UnknownCharacter, CharacterInUse, Unavailable, AccountBanned);
    }
}
