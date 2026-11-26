namespace Game.Shared.Vocabulary
{
    /// <summary>Staff roles of the admin API (VOCABULARY.md "Staff roles", ARCHITECTURE §12.2), lowest first: each has what the one before it has, and more.</summary>
    public static class StaffRoles
    {
        /// <summary><c>support</c>: reads, kick (T-804); mute and the store-compliance queue later (T-806).</summary>
        public const string Support = "support";

        /// <summary><c>gm</c>: support, and bans, item and currency restores, announcements (T-806).</summary>
        public const string Gm = "gm";

        /// <summary><c>admin</c>: gm, and maintenance mode and role management.</summary>
        public const string Admin = "admin";

        /// <summary>Every role, lowest first.</summary>
        public static readonly VocabularySet All = new VocabularySet(Support, Gm, Admin);

        /// <summary>The role's rank (0 for <c>support</c>), or -1 when it is not a role. A role may do what its rank or a lower one may.</summary>
        public static int Rank(string? role)
        {
            if (role == null) return -1;
            var i = 0;
            foreach (var known in All.Ordered)
            {
                if (string.Equals(known, role, System.StringComparison.Ordinal)) return i;
                i++;
            }
            return -1;
        }

        /// <summary>True when <paramref name="role"/> is a role at or above <paramref name="lowest"/>. An unknown role is never enough.</summary>
        public static bool AtLeast(string? role, string lowest)
        {
            var rank = Rank(role);
            return rank >= 0 && rank >= Rank(lowest);
        }
    }

    /// <summary>Admin actions: what an admin command does, the <c>action</c> of its audit row (VOCABULARY.md "Admin actions"). T-806 adds the moderation and support ones.</summary>
    public static class AdminActions
    {
        /// <summary><c>kick</c>: ends the account's live game and every session of it; the player signs in again (T-804).</summary>
        public const string Kick = "kick";

        /// <summary>Every action.</summary>
        public static readonly VocabularySet All = new VocabularySet(Kick);
    }

    /// <summary>What an admin command or an audit row is about (VOCABULARY.md "Admin target kinds").</summary>
    public static class AdminTargetKinds
    {
        /// <summary><c>account</c>: the target id is an account id.</summary>
        public const string Account = "account";

        /// <summary><c>character</c>: the target id is a character id (permanent, not the runtime entity id).</summary>
        public const string Character = "character";

        /// <summary>Every kind.</summary>
        public static readonly VocabularySet All = new VocabularySet(Account, Character);
    }

    /// <summary>Why the admin API refused a request or a command: the <c>error</c> of its answer (VOCABULARY.md "Admin errors").</summary>
    public static class AdminErrors
    {
        /// <summary>The body or a query parameter is not what the endpoint expects, or a field is missing or too long.</summary>
        public const string InvalidRequest = "invalid_request";

        /// <summary>No service credential, or the wrong one.</summary>
        public const string Unauthorized = "unauthorized";

        /// <summary>The staff member is missing or malformed (id, name or role header).</summary>
        public const string InvalidStaff = "invalid_staff";

        /// <summary>The staff role is not a role.</summary>
        public const string UnknownRole = "unknown_role";

        /// <summary>The staff role is below the command's lowest role.</summary>
        public const string Forbidden = "forbidden";

        /// <summary>A write without its free-text reason.</summary>
        public const string ReasonRequired = "reason_required";

        /// <summary>No such endpoint or command.</summary>
        public const string NotFound = "not_found";

        /// <summary>The target (an account or character) does not exist.</summary>
        public const string UnknownTarget = "unknown_target";

        /// <summary>Too many requests with this credential (or failed ones from this address); the answer says when to retry.</summary>
        public const string RateLimited = "rate_limited";

        /// <summary>The world did not answer in time (the instance is stalled or stopping); nothing was changed.</summary>
        public const string Timeout = "timeout";

        /// <summary>The database could not answer now; nothing was changed.</summary>
        public const string Unavailable = "unavailable";

        /// <summary>Every admin error.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            InvalidRequest, Unauthorized, InvalidStaff, UnknownRole, Forbidden, ReasonRequired, NotFound, UnknownTarget, RateLimited, Timeout, Unavailable);
    }

    /// <summary>How an admin command ended: the <c>outcome</c> of its answer (VOCABULARY.md "Admin outcomes").</summary>
    public static class AdminOutcomes
    {
        /// <summary><c>done</c>: the change is made and audited; the answer has what came of it.</summary>
        public const string Done = "done";

        /// <summary><c>accepted</c>: the change is audited and queued; the world applies it on a coming tick (its result is not known yet).</summary>
        public const string Accepted = "accepted";

        /// <summary><c>refused</c>: nothing was changed; the answer has an "Admin errors" term.</summary>
        public const string Refused = "refused";

        /// <summary>Every outcome.</summary>
        public static readonly VocabularySet All = new VocabularySet(Done, Accepted, Refused);
    }
}
