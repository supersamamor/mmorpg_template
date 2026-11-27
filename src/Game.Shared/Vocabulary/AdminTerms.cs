namespace Game.Shared.Vocabulary
{
    /// <summary>Staff roles of the admin API (VOCABULARY.md "Staff roles", ARCHITECTURE §12.2), lowest first: each has what the one before it has, and more.</summary>
    public static class StaffRoles
    {
        /// <summary><c>support</c>: reads, kick (T-804), mute and the store-compliance queue (T-806).</summary>
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

        /// <summary><c>ban</c> (T-806): the account may not sign in or play until the ban ends (or ever); its game and sessions end at once.</summary>
        public const string Ban = "ban";

        /// <summary><c>unban</c> (T-806): lifts the account's ban.</summary>
        public const string Unban = "unban";

        /// <summary><c>mute</c> (T-806): the account may not speak until the mute ends (the check chat calls, T-901).</summary>
        public const string Mute = "mute";

        /// <summary><c>unmute</c> (T-806): lifts the account's mute.</summary>
        public const string Unmute = "unmute";

        /// <summary><c>give_item</c> (T-806): new items for a character, with new instance ids and <c>source = admin</c>.</summary>
        public const string GiveItem = "give_item";

        /// <summary><c>remove_item</c> (T-806): takes units of one inventory entry (by its instance id) from a character.</summary>
        public const string RemoveItem = "remove_item";

        /// <summary><c>change_currency</c> (T-806): adds currency to a character or takes it away.</summary>
        public const string ChangeCurrency = "change_currency";

        /// <summary><c>unstick</c> (T-806): moves a character to its save point.</summary>
        public const string Unstick = "unstick";

        /// <summary><c>announce</c> (T-806): a server-wide announcement every playing client shows.</summary>
        public const string Announce = "announce";

        /// <summary><c>start_maintenance</c> (T-806): no new logins; those playing are warned, and their games end (saved) when the time is up.</summary>
        public const string StartMaintenance = "start_maintenance";

        /// <summary><c>end_maintenance</c> (T-806): logins are open again.</summary>
        public const string EndMaintenance = "end_maintenance";

        /// <summary><c>open_request</c> (T-806): a deletion or data-export request that came in through support goes into the queue.</summary>
        public const string OpenRequest = "open_request";

        /// <summary><c>complete_request</c> (T-806): handles a queued request: a deletion starts T-703's erasure wait, an export answers with the account's data.</summary>
        public const string CompleteRequest = "complete_request";

        /// <summary><c>cancel_request</c> (T-806): closes a queued request without handling it.</summary>
        public const string CancelRequest = "cancel_request";

        /// <summary><c>close_report</c> (T-901): staff handled a player's report of a chat line; it leaves the open list.</summary>
        public const string CloseReport = "close_report";

        /// <summary>Every action.</summary>
        public static readonly VocabularySet All = new VocabularySet(Kick, Ban, Unban, Mute, Unmute, GiveItem, RemoveItem, ChangeCurrency, Unstick, Announce, StartMaintenance,
            EndMaintenance, OpenRequest, CompleteRequest, CancelRequest, CloseReport);
    }

    /// <summary>What an admin command or an audit row is about (VOCABULARY.md "Admin target kinds").</summary>
    public static class AdminTargetKinds
    {
        /// <summary><c>account</c>: the target id is an account id.</summary>
        public const string Account = "account";

        /// <summary><c>character</c>: the target id is a character id (permanent, not the runtime entity id).</summary>
        public const string Character = "character";

        /// <summary><c>request</c> (T-806): the target id is a support request's number.</summary>
        public const string Request = "request";

        /// <summary><c>server</c> (T-806): the server process itself (announcements, maintenance); the target id is <c>server</c>.</summary>
        public const string Server = "server";

        /// <summary><c>chat_report</c> (T-901): the target id is a chat report's number.</summary>
        public const string ChatReport = "chat_report";

        /// <summary>Every kind.</summary>
        public static readonly VocabularySet All = new VocabularySet(Account, Character, Request, Server, ChatReport);
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

        /// <summary>The account has a ban already (T-806): lift it first to change it.</summary>
        public const string AlreadyBanned = "already_banned";

        /// <summary>The account has no ban to lift (T-806).</summary>
        public const string NotBanned = "not_banned";

        /// <summary>The account has a mute already (T-806).</summary>
        public const string AlreadyMuted = "already_muted";

        /// <summary>The account has no mute to lift (T-806).</summary>
        public const string NotMuted = "not_muted";

        /// <summary>A server holds the character but this one's world does not have it (another server plays it, or its last save has not landed): try again, or kick it first (T-806).</summary>
        public const string CharacterBusy = "character_busy";

        /// <summary>The character has never been saved, so there is nothing in the database to change yet (T-806).</summary>
        public const string NeverSaved = "never_saved";

        /// <summary>The item id is not in the content (T-806).</summary>
        public const string UnknownItem = "unknown_item";

        /// <summary>The character has no inventory entry with that instance id (T-806).</summary>
        public const string ItemGone = "item_gone";

        /// <summary>The entry holds fewer units than asked, or the character less currency (T-806).</summary>
        public const string NotEnough = "not_enough";

        /// <summary>The new entries do not fit <c>inventory.max_entries</c> (T-806).</summary>
        public const string InventoryFull = "inventory_full";

        /// <summary>The currency would pass the largest amount a character can hold (T-806).</summary>
        public const string CurrencyLimit = "currency_limit";

        /// <summary>Maintenance mode is on already (T-806).</summary>
        public const string MaintenanceOn = "maintenance_on";

        /// <summary>Maintenance mode is off (T-806).</summary>
        public const string MaintenanceOff = "maintenance_off";

        /// <summary>The support request is closed already (T-806).</summary>
        public const string RequestClosed = "request_closed";

        /// <summary>The account waits to be erased already (T-806): its deletion is under way.</summary>
        public const string AccountDeleting = "account_deleting";

        /// <summary>The chat report is closed already (T-901).</summary>
        public const string ReportClosed = "report_closed";

        /// <summary>Every admin error.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            InvalidRequest, Unauthorized, InvalidStaff, UnknownRole, Forbidden, ReasonRequired, NotFound, UnknownTarget, RateLimited, Timeout, Unavailable,
            AlreadyBanned, NotBanned, AlreadyMuted, NotMuted, CharacterBusy, NeverSaved, UnknownItem, ItemGone, NotEnough, InventoryFull, CurrencyLimit, MaintenanceOn, MaintenanceOff,
            RequestClosed, AccountDeleting, ReportClosed);
    }

    /// <summary>Kinds of support request (VOCABULARY.md "Support request kinds", T-806).</summary>
    public static class SupportRequestKinds
    {
        /// <summary><c>deletion</c>: the account's owner asked support to delete it; completing it starts T-703's erasure wait.</summary>
        public const string Deletion = "deletion";

        /// <summary><c>export</c>: the account's owner asked for a copy of their data; completing it answers with the data as JSON, for staff to send.</summary>
        public const string Export = "export";

        /// <summary>Every kind.</summary>
        public static readonly VocabularySet All = new VocabularySet(Deletion, Export);
    }

    /// <summary>Where a support request stands (VOCABULARY.md "Support request statuses", T-806).</summary>
    public static class SupportRequestStatuses
    {
        /// <summary><c>open</c>: in the queue.</summary>
        public const string Open = "open";

        /// <summary><c>done</c>: handled.</summary>
        public const string Done = "done";

        /// <summary><c>cancelled</c>: closed without being handled.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>Every status.</summary>
        public static readonly VocabularySet All = new VocabularySet(Open, Done, Cancelled);
    }

    /// <summary>What an account restriction is (VOCABULARY.md "Sanction kinds", T-806).</summary>
    public static class SanctionKinds
    {
        /// <summary><c>ban</c>: no sign-in and no game.</summary>
        public const string Ban = "ban";

        /// <summary><c>mute</c>: no speaking.</summary>
        public const string Mute = "mute";

        /// <summary>Every kind.</summary>
        public static readonly VocabularySet All = new VocabularySet(Ban, Mute);
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
