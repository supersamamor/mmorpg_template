using System.Collections.Generic;

namespace Game.Shared.Protocol
{
    /// <summary>
    /// The admin API's paths (ARCHITECTURE §12, docs/ADMIN_API.md), on the admin listener only, never on the game's. Every answer is JSON in snake_case; a refusal is
    /// <c>{"error":"&lt;Admin errors term&gt;","request_id":"..."}</c>.
    /// </summary>
    public static class AdminPaths
    {
        /// <summary>The prefix of them all.</summary>
        public const string Root = "/admin/v1";

        /// <summary><c>GET</c>: the server's state (<see cref="AdminStatusAnswer"/>).</summary>
        public const string Status = Root + "/status";

        /// <summary><c>GET</c> <c>?name=&amp;id=&amp;after=&amp;limit=</c>: accounts, by username prefix or id, a page at a time. <c>GET</c> <c>/{id}</c>: one account with its characters.</summary>
        public const string Accounts = Root + "/accounts";

        /// <summary><c>GET</c> <c>?name=&amp;id=&amp;account=&amp;after=&amp;limit=</c>: characters, by name prefix, id or account. <c>GET</c> <c>/{id}</c>: one character's saved state.</summary>
        public const string Characters = Root + "/characters";

        /// <summary><c>GET</c> <c>?target=&amp;staff=&amp;action=&amp;from=&amp;to=&amp;before=&amp;limit=</c>: the audit log, newest first.</summary>
        public const string Audit = Root + "/audit";

        /// <summary><c>GET</c>: every command and its lowest role. <c>POST</c> <c>/{action}</c> with the command's body: runs it (<see cref="AdminCommandAnswer"/>).</summary>
        public const string Commands = Root + "/commands";
    }

    /// <summary>The headers of an admin request.</summary>
    public static class AdminHeaders
    {
        /// <summary><c>Authorization: Bearer &lt;service credential&gt;</c>: proves the caller is the panel. Never log it.</summary>
        public const string CredentialScheme = "Bearer ";

        /// <summary>The staff member's stable id (the identity provider's subject): 1 to 128 of <c>A-Z a-z 0-9 . _ - @ : |</c>.</summary>
        public const string StaffId = "X-Staff-Id";

        /// <summary>The staff member's display name, 1 to 64 printable characters (UTF-8, percent-encoded).</summary>
        public const string StaffName = "X-Staff-Name";

        /// <summary>The staff member's role: a "Staff roles" term.</summary>
        public const string StaffRole = "X-Staff-Role";

        /// <summary>The request's id, 1 to 64 of <c>A-Z a-z 0-9 . _ -</c>; the server makes one when it is missing, and every answer carries it back.</summary>
        public const string RequestId = "X-Request-Id";
    }

    /// <summary>The body every command has: why (required, 1 to <see cref="MaxReasonLength"/> characters). Each command adds its own fields.</summary>
    public static class AdminCommandFields
    {
        /// <summary>The longest reason.</summary>
        public const int MaxReasonLength = 500;

        /// <summary>The field.</summary>
        public const string Reason = "reason";

        /// <summary>The account a command is for (a GUID).</summary>
        public const string AccountId = "account_id";
    }

    /// <summary>The body of <c>POST /admin/v1/commands/kick</c>.</summary>
    /// <param name="AccountId">Whose game ends.</param>
    /// <param name="Reason">Why (kept in the audit log; no personal data in it).</param>
    public sealed record KickRequest(string AccountId, string Reason);

    /// <summary>What came of a command: an "Admin outcomes" term, the audit row it wrote (none when refused), an "Admin errors" term when refused, and the command's own result.</summary>
    /// <param name="Outcome">The outcome.</param>
    /// <param name="RequestId">The request's id.</param>
    /// <param name="AuditId">The audit row, or null.</param>
    /// <param name="Error">Why it was refused, or null.</param>
    /// <param name="Result">The command's result fields, or null.</param>
    public sealed record AdminCommandAnswer(string Outcome, string RequestId, long? AuditId, string? Error, IReadOnlyDictionary<string, object?>? Result);

    /// <summary>The server's state.</summary>
    /// <param name="ContentHash">The loaded content's display-bundle hash (<c>ClientBundleExporter.Hash</c>).</param>
    /// <param name="StartedAt">When this server run started (UTC, ISO 8601).</param>
    /// <param name="PlayersOnline">Characters in the world (played, or in their grace period).</param>
    /// <param name="Sessions">Connections open.</param>
    /// <param name="Instances">Every map instance.</param>
    /// <param name="Saves">The saving figures, or null without a database.</param>
    /// <param name="Errors">Error and warning counts.</param>
    public sealed record AdminStatusAnswer(string ContentHash, string StartedAt, int PlayersOnline, int Sessions, IReadOnlyList<AdminInstanceStatus> Instances,
        AdminSaveStatus? Saves, AdminErrorCounts Errors);

    /// <summary>One map instance's figures; times in microseconds.</summary>
    public sealed record AdminInstanceStatus(string MapId, long Tick, long TickP50Micros, long TickP99Micros, long TickMaxMicros, long TickLifetimeMaxMicros,
        long SystemErrors, long HandlerErrors, long IntentsDropped);

    /// <summary>Saving since the start.</summary>
    public sealed record AdminSaveStatus(long Saves, long Transactions, long Failures, long Lost, int Held, int Waiting, int AtRisk);

    /// <summary>Errors and warnings logged since the start, and the gateway's <c>server_error</c> byes.</summary>
    public sealed record AdminErrorCounts(long LoggedErrors, long LoggedWarnings, long ServerErrorByes);
}
