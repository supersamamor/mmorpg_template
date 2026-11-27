namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// What a guild rank may do (T-904, VOCABULARY.md "Guild permissions"). The leader rank holds every one, and only the leader names and orders the ranks and hands the leader rank over.
    /// Every act on a member also needs that member's rank to be below the actor's.
    /// </summary>
    public static class GuildPermissions
    {
        /// <summary><c>invite</c>: invite characters into the guild.</summary>
        public const string Invite = "invite";

        /// <summary><c>kick</c>: take a member of a lower rank out.</summary>
        public const string Kick = "kick";

        /// <summary><c>edit_notice</c>: write the guild's notice.</summary>
        public const string EditNotice = "edit_notice";

        /// <summary><c>set_rank</c>: give a member of a lower rank another rank, also below one's own.</summary>
        public const string SetRank = "set_rank";

        /// <summary>Every permission, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(Invite, Kick, EditNotice, SetRank);
    }

    /// <summary>Why the server refused a guild intent (T-904, VOCABULARY.md "Guild reasons"): the <c>reason</c> of a <c>guild_refused</c>.</summary>
    public static class GuildReasons
    {
        /// <summary><c>not_online</c>: nobody of that name plays on this server now, or not near enough to be tapped; also what an invite to someone who blocks the inviter answers.</summary>
        public const string NotOnline = "not_online";

        /// <summary><c>invalid_target</c>: an invite, kick, rank change or hand-over of oneself, or of somebody who is not a member.</summary>
        public const string InvalidTarget = "invalid_target";

        /// <summary><c>invalid_name</c>: the guild name breaks the naming rules (<c>balance.yaml</c> <c>guild</c>).</summary>
        public const string InvalidName = "invalid_name";

        /// <summary><c>name_taken</c>: another guild has that name (letter case does not count).</summary>
        public const string NameTaken = "name_taken";

        /// <summary><c>invalid_emblem</c>: not one of the theme's emblems (<c>guild.emblems</c>).</summary>
        public const string InvalidEmblem = "invalid_emblem";

        /// <summary><c>already_in_guild</c>: the character (for a founding or an accept: this one) is in a guild already.</summary>
        public const string AlreadyInGuild = "already_in_guild";

        /// <summary><c>not_in_guild</c>: this character is in no guild.</summary>
        public const string NotInGuild = "not_in_guild";

        /// <summary><c>invite_pending</c>: the invited character has an invite it has not answered yet.</summary>
        public const string InvitePending = "invite_pending";

        /// <summary><c>no_invite</c>: there is no invite to answer: none came, it expired, or its inviter may no longer invite.</summary>
        public const string NoInvite = "no_invite";

        /// <summary><c>full</c>: the guild has <c>guild.max_members</c> members.</summary>
        public const string Full = "full";

        /// <summary><c>not_allowed</c>: this character's rank lacks the permission (or the act is the leader's).</summary>
        public const string NotAllowed = "not_allowed";

        /// <summary><c>rank_too_high</c>: the member's rank (or the rank asked for) is the actor's own or above it.</summary>
        public const string RankTooHigh = "rank_too_high";

        /// <summary><c>invalid_ranks</c>: a rank list the rules refuse: too few or too many, a name that is empty or too long, a permission that is not a term, a rank that does not exist.</summary>
        public const string InvalidRanks = "invalid_ranks";

        /// <summary><c>too_long</c>: the notice is longer than <c>--guild-notice-length</c>.</summary>
        public const string TooLong = "too_long";

        /// <summary><c>cannot_afford</c>: the character does not carry the founding cost (<c>guild.found_cost</c>).</summary>
        public const string CannotAfford = "cannot_afford";

        /// <summary><c>busy</c>: a founding of this character is under way.</summary>
        public const string Busy = "busy";

        /// <summary><c>rate_limited</c>: too many guild intents in a short time; <c>retry_after</c> says when the next may go.</summary>
        public const string RateLimited = "rate_limited";

        /// <summary><c>unavailable</c>: guilds need a stored character and a database that answers (a dev login has none).</summary>
        public const string Unavailable = "unavailable";

        /// <summary>Every reason, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(NotOnline, InvalidTarget, InvalidName, NameTaken, InvalidEmblem, AlreadyInGuild, NotInGuild, InvitePending, NoInvite,
            Full, NotAllowed, RankTooHigh, InvalidRanks, TooLong, CannotAfford, Busy, RateLimited, Unavailable);
    }

    /// <summary>What a <c>guild_news</c> tells (T-904, VOCABULARY.md "Guild news"): news the <c>guild</c> message alone does not carry.</summary>
    public static class GuildNews
    {
        /// <summary><c>invite_sent</c>: the invite went out (to the inviter).</summary>
        public const string InviteSent = "invite_sent";

        /// <summary><c>declined</c>: the invited character said no (to the inviter).</summary>
        public const string Declined = "declined";

        /// <summary><c>expired</c>: nobody answered in time (to the inviter and the invited).</summary>
        public const string Expired = "expired";

        /// <summary><c>founded</c>: the guild exists, its cost paid (to the founder).</summary>
        public const string Founded = "founded";

        /// <summary><c>joined</c>: a character joined (to the members).</summary>
        public const string Joined = "joined";

        /// <summary><c>left</c>: a member left (to the members who stay).</summary>
        public const string Left = "left";

        /// <summary><c>kicked</c>: a member was taken out (to the members, and to the one kicked).</summary>
        public const string Kicked = "kicked";

        /// <summary><c>rank</c>: a member has another rank (to the members).</summary>
        public const string Rank = "rank";

        /// <summary><c>leader</c>: a new leader (to the members).</summary>
        public const string Leader = "leader";

        /// <summary><c>ranks</c>: the leader renamed or reordered the ranks (to the members).</summary>
        public const string Ranks = "ranks";

        /// <summary><c>notice</c>: the notice changed (to the members).</summary>
        public const string Notice = "notice";

        /// <summary><c>disbanded</c>: the guild ended, its last member gone (to that member).</summary>
        public const string Disbanded = "disbanded";

        /// <summary>Every news term, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(InviteSent, Declined, Expired, Founded, Joined, Left, Kicked, Rank, Leader, Ranks, Notice, Disbanded);
    }
}
