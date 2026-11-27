namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// Chat channel kinds (T-901, VOCABULARY.md "Chat channels"): where a line goes. A channel whose members something else supplies (party, guild) is one more term and one
    /// <c>IChannelMembers</c> on the server; chat itself does not change.
    /// </summary>
    public static class ChatChannels
    {
        /// <summary><c>map</c>: everyone on the sender's map instance.</summary>
        public const string Map = "map";

        /// <summary><c>whisper</c>: one online character, named by the sender.</summary>
        public const string Whisper = "whisper";

        /// <summary><c>party</c> (T-902): the members of the sender's party; registered by the party service through the channel seam.</summary>
        public const string Party = "party";

        /// <summary><c>guild</c> (T-904): the members of the sender's guild who play on this server process; registered by the guild service through the channel seam.</summary>
        public const string Guild = "guild";

        /// <summary><c>system</c>: the server's own lines. No player sends on it; staff's announcements (T-806) are shown in the chat log under it.</summary>
        public const string System = "system";

        /// <summary>Every channel kind, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Map, Whisper, Party, Guild, System);
    }

    /// <summary>Why the server refused a chat line, block, unblock or report (T-901, VOCABULARY.md "Chat reasons"): the <c>reason</c> of a <c>chat_refused</c>.</summary>
    public static class ChatReasons
    {
        /// <summary><c>muted</c>: staff muted the account; <c>retry_after</c> says how long is left. The line went nowhere.</summary>
        public const string Muted = "muted";

        /// <summary><c>rate_limited</c>: too many lines in a short time; <c>retry_after</c> says when the next one may go.</summary>
        public const string RateLimited = "rate_limited";

        /// <summary><c>not_online</c>: nobody of that name plays now (also what a whisper to someone who blocks the sender answers: a block does not show).</summary>
        public const string NotOnline = "not_online";

        /// <summary><c>invalid_target</c>: a whisper to oneself, or a block of oneself.</summary>
        public const string InvalidTarget = "invalid_target";

        /// <summary><c>empty</c>: nothing is left of the line once it is cleaned and trimmed.</summary>
        public const string Empty = "empty";

        /// <summary><c>too_long</c>: the cleaned line is longer than the server takes (<c>chat_state</c> <c>max_length</c>).</summary>
        public const string TooLong = "too_long";

        /// <summary><c>invalid_channel</c>: not a channel a player may send on (an unknown kind, <c>system</c>, or one this character is not a member of).</summary>
        public const string InvalidChannel = "invalid_channel";

        /// <summary><c>unavailable</c>: the server cannot check or store it now (the database is away, or this login has no account); try again later.</summary>
        public const string Unavailable = "unavailable";

        /// <summary><c>unknown_character</c>: no character has that name.</summary>
        public const string UnknownCharacter = "unknown_character";

        /// <summary><c>already_blocked</c>: that character is blocked already.</summary>
        public const string AlreadyBlocked = "already_blocked";

        /// <summary><c>not_blocked</c>: that character is not blocked.</summary>
        public const string NotBlocked = "not_blocked";

        /// <summary><c>block_limit</c>: the account blocks as many characters as the server allows.</summary>
        public const string BlockLimit = "block_limit";

        /// <summary><c>unknown_line</c>: no line of that number reached this client recently enough to be reported (or it was its own).</summary>
        public const string UnknownLine = "unknown_line";

        /// <summary><c>already_reported</c>: this account reported that line already.</summary>
        public const string AlreadyReported = "already_reported";

        /// <summary>Every reason, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Muted, RateLimited, NotOnline, InvalidTarget, Empty, TooLong, InvalidChannel, Unavailable, UnknownCharacter,
            AlreadyBlocked, NotBlocked, BlockLimit, UnknownLine, AlreadyReported);
    }

    /// <summary>Where a chat report is (T-901, VOCABULARY.md "Chat report statuses", <c>chat_reports.status</c>).</summary>
    public static class ChatReportStatuses
    {
        /// <summary><c>open</c>: waiting for staff.</summary>
        public const string Open = "open";

        /// <summary><c>closed</c>: staff closed it (<c>close_report</c>, audited).</summary>
        public const string Closed = "closed";

        /// <summary>Every status, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Open, Closed);
    }
}
