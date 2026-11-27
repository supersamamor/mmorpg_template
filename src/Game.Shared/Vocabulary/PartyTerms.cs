namespace Game.Shared.Vocabulary
{
    /// <summary>Why the server refused a party intent (T-902, VOCABULARY.md "Party reasons"): the <c>reason</c> of a <c>party_refused</c>.</summary>
    public static class PartyReasons
    {
        /// <summary><c>not_online</c>: nobody of that name plays now, or not near enough to be tapped; also what an invite to someone who blocks the inviter answers (a block does not show).</summary>
        public const string NotOnline = "not_online";

        /// <summary><c>invalid_target</c>: an invite, kick or hand-over of oneself.</summary>
        public const string InvalidTarget = "invalid_target";

        /// <summary><c>full</c>: the party has <c>party.max_members</c> members.</summary>
        public const string Full = "full";

        /// <summary><c>already_in_party</c>: the invited character is in a party already (for an accept: this character is).</summary>
        public const string AlreadyInParty = "already_in_party";

        /// <summary><c>invite_pending</c>: the invited character has an invite it has not answered yet.</summary>
        public const string InvitePending = "invite_pending";

        /// <summary><c>not_leader</c>: only the leader invites, kicks and hands the leader over.</summary>
        public const string NotLeader = "not_leader";

        /// <summary><c>not_in_party</c>: this character is in no party (leave), or the named one is not in this party (kick, hand-over).</summary>
        public const string NotInParty = "not_in_party";

        /// <summary><c>no_invite</c>: there is no invite to answer: none came, it expired, or its party is gone.</summary>
        public const string NoInvite = "no_invite";

        /// <summary><c>rate_limited</c>: too many invites in a short time; <c>retry_after</c> says when the next may go.</summary>
        public const string RateLimited = "rate_limited";

        /// <summary>Every reason, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(NotOnline, InvalidTarget, Full, AlreadyInParty, InvitePending, NotLeader, NotInParty, NoInvite, RateLimited);
    }

    /// <summary>What a <c>party_notice</c> tells (T-902, VOCABULARY.md "Party notices"): news the <c>party</c> message alone does not carry.</summary>
    public static class PartyNotices
    {
        /// <summary><c>invite_sent</c>: the invite went out (to the inviter).</summary>
        public const string InviteSent = "invite_sent";

        /// <summary><c>declined</c>: the invited character said no (to the inviter).</summary>
        public const string Declined = "declined";

        /// <summary><c>expired</c>: nobody answered in time (to the inviter and the invited).</summary>
        public const string Expired = "expired";

        /// <summary><c>joined</c>: a character joined (to every member).</summary>
        public const string Joined = "joined";

        /// <summary><c>left</c>: a member left, by itself or at the end of its grace period (to the members who stay).</summary>
        public const string Left = "left";

        /// <summary><c>kicked</c>: the leader took a member out (to the members, and to the one kicked).</summary>
        public const string Kicked = "kicked";

        /// <summary><c>leader</c>: a new leader (to every member).</summary>
        public const string Leader = "leader";

        /// <summary><c>disbanded</c>: the party ended, as one member was left (to that member).</summary>
        public const string Disbanded = "disbanded";

        /// <summary>Every notice, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(InviteSent, Declined, Expired, Joined, Left, Kicked, Leader, Disbanded);
    }
}
