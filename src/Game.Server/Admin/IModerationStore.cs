using System.Text.Json.Nodes;

namespace Game.Server.Admin;

/// <summary>A ban or mute (T-806) as stored: ids and times only; why is in the audit rows it names.</summary>
public sealed record Sanction(long Id, Guid AccountId, string Kind, DateTime CreatedAt, DateTime? Until, long AuditId, DateTime? LiftedAt, long? LiftedAuditId)
{
    /// <summary>True while it holds at <paramref name="now"/>: not lifted, and not over.</summary>
    public bool InForce(DateTime now) => LiftedAt is null && (Until is null || Until > now);
}

/// <summary>A support request (T-806) as stored.</summary>
public sealed record SupportRequest(long Id, Guid AccountId, string Kind, string Status, DateTime OpenedAt, long OpenedAuditId, DateTime? ClosedAt, long? ClosedAuditId);

/// <summary>What came of adding or lifting a sanction: an "Admin errors" term (nothing written), or the sanction, its audit row and the sessions a ban ended.</summary>
public sealed record SanctionChange(string? Error, Sanction? Sanction, long AuditId, IReadOnlyList<byte[]> EndedSessions)
{
    /// <summary>Refused.</summary>
    public static SanctionChange Refused(string error) => new(error, null, 0, []);
}

/// <summary>What came of opening or closing a support request: an "Admin errors" term (nothing written), or the request as it is now, its audit row, and the sessions a deletion ended.</summary>
public sealed record RequestChange(string? Error, SupportRequest? Request, long AuditId, IReadOnlyList<byte[]> EndedSessions)
{
    /// <summary>Refused.</summary>
    public static RequestChange Refused(string error) => new(error, null, 0, []);
}

/// <summary>A character's saved state as an offline change sees it (T-806): what support fixes touch.</summary>
public sealed record OfflineCharacter(Guid CharacterId, Guid AccountId, long Version, long Currency, IReadOnlyList<AdminItem> Items, string MapId, int CellX, int CellY,
    string? SaveMapId, int? SaveCellX, int? SaveCellY);

/// <summary>A new inventory entry an offline change writes.</summary>
public sealed record NewItemRow(long Id, string ItemId, int Quantity);

/// <summary>
/// What an offline change does to the character, all in one transaction with its audit row: the new currency (or null: unchanged), entries to add (and to mark <c>source = admin</c>), entries
/// to shrink (a quantity of 0 deletes the entry, worn or not), and a new place (or null).
/// </summary>
public sealed record OfflinePlan(long? Currency, IReadOnlyList<NewItemRow> Add, IReadOnlyList<(long Id, int Quantity)> Change, (string MapId, int CellX, int CellY)? Place, AuditDraft Audit,
    IReadOnlyDictionary<string, object?> Result)
{
    /// <summary>A plan's refusal: an "Admin errors" term; nothing is written.</summary>
    public string? Error { get; init; }

    /// <summary>Refused with <paramref name="error"/>.</summary>
    public static OfflinePlan Refuse(string error) => new(null, [], [], null, null!, new Dictionary<string, object?>()) { Error = error };
}

/// <summary>What came of an offline change: an "Admin errors" term (nothing written), or its audit row and the plan's result.</summary>
public sealed record OfflineResult(string? Error, long AuditId, IReadOnlyDictionary<string, object?>? Result);

/// <summary>
/// What T-806's moderation and support commands read and write (ARCHITECTURE §12.4). Every write is one transaction with its audit row (rule 10). Implemented on PostgreSQL by
/// <c>PostgresModerationStore</c>; the checks (<see cref="ActiveSanctionAsync"/>, <see cref="BannedAmongAsync"/>) run on every server process with a database, admin API or not, because
/// the database decides who is banned. Throws <see cref="Accounts.StoreUnavailableException"/> when the database does not answer; never on a tick thread.
/// </summary>
public interface IModerationStore
{
    /// <summary>The sanction of <paramref name="kind"/> in force on the account at <paramref name="now"/>, or null.</summary>
    Task<Sanction?> ActiveSanctionAsync(Guid account, string kind, DateTime now, CancellationToken cancellation);

    /// <summary>Which of <paramref name="accounts"/> are banned at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<Guid>> BannedAmongAsync(IReadOnlyCollection<Guid> accounts, DateTime now, CancellationToken cancellation);

    /// <summary>
    /// Adds a ban or a mute in force from <paramref name="now"/> until <paramref name="until"/> (null: a ban that never ends), with its audit row. A ban also ends every session of the
    /// account in the same transaction. Refused: <c>unknown_target</c> (no account), <c>already_banned</c> / <c>already_muted</c> (one is in force).
    /// </summary>
    Task<SanctionChange> AddSanctionAsync(Guid account, string kind, DateTime? until, DateTime now, AuditDraft audit, CancellationToken cancellation);

    /// <summary>Lifts the sanction of <paramref name="kind"/> in force, with its audit row. Refused: <c>unknown_target</c>, <c>not_banned</c> / <c>not_muted</c>.</summary>
    Task<SanctionChange> LiftSanctionAsync(Guid account, string kind, DateTime now, Func<Sanction, AuditDraft> audit, CancellationToken cancellation);

    /// <summary>Every ban and mute of the account, newest first.</summary>
    Task<IReadOnlyList<Sanction>> SanctionsAsync(Guid account, CancellationToken cancellation);

    /// <summary>Puts a request in the queue with its audit row. Refused: <c>unknown_target</c>.</summary>
    Task<RequestChange> OpenRequestAsync(Guid account, string kind, DateTime now, Func<long, AuditDraft> audit, CancellationToken cancellation);

    /// <summary>
    /// Closes an open request as <paramref name="status"/> with its audit row. Completing a deletion asks for the account's erasure in the same transaction, exactly as the in-app request does
    /// (T-703): erase from <paramref name="eraseAfter"/>, every session ended, the record in <c>account_deletions</c>. Refused: <c>unknown_target</c> (no such request), <c>request_closed</c>,
    /// <c>account_deleting</c> (a deletion of an account that waits to be erased already).
    /// </summary>
    Task<RequestChange> CloseRequestAsync(long request, string status, DateTime now, DateTime eraseAfter, Func<SupportRequest, AuditDraft> audit, CancellationToken cancellation);

    /// <summary>One request, or null.</summary>
    Task<SupportRequest?> GetRequestAsync(long request, CancellationToken cancellation);

    /// <summary>The queue, newest first, by status and account; <see cref="AdminPage{T}.Next"/> is the last number of the page (pass it as <paramref name="before"/>).</summary>
    Task<AdminPage<SupportRequest>> FindRequestsAsync(string? status, Guid? account, long? before, int limit, CancellationToken cancellation);

    /// <summary>
    /// Everything stored about the account (docs/STORE_COMPLIANCE.md §2), as one JSON object read in one snapshot, or null when there is no such account. Never a password hash or a token
    /// hash: that a hash exists, and when the password was set, is said instead.
    /// </summary>
    Task<JsonObject?> ExportAsync(Guid account, DateTime now, CancellationToken cancellation);

    /// <summary>
    /// Changes a character that no server holds, in the database, with the claim rules of T-702 (ARCHITECTURE §3): the character and its state are locked in a save's order, a character held by a
    /// live run is refused (<c>character_busy</c>), and the claim number goes up, so no save of an earlier stay can land after the change and undo it. <paramref name="plan"/> sees the state and
    /// says what to change (or refuses); the change and its audit row are one transaction. Refused also: <c>unknown_target</c> (no character, or deleted), <c>never_saved</c>.
    /// </summary>
    Task<OfflineResult> EditCharacterAsync(Guid character, Func<OfflineCharacter, OfflinePlan> plan, CancellationToken cancellation);
}
