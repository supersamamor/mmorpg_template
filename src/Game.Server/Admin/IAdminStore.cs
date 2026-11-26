namespace Game.Server.Admin;

/// <summary>
/// One audit row to write (ARCHITECTURE §12.3). <see cref="Before"/> and <see cref="After"/> are JSON objects of ids and numbers: never a username, a character name, a password
/// hash or a token, so erasing an account leaves nothing personal in its rows (docs/STORE_COMPLIANCE.md). <see cref="Reason"/> is the staff member's own words.
/// </summary>
public sealed record AuditDraft(StaffIdentity Staff, string RequestId, string Action, string TargetKind, string TargetId, string Before, string After, string Reason, DateTime At);

/// <summary>An audit row as stored.</summary>
public sealed record AuditRecord(long Id, DateTime At, string RequestId, string StaffId, string StaffName, string StaffRole, string Action, string TargetKind, string TargetId,
    string Before, string After, string Reason);

/// <summary>What the audit log is searched by; every part is optional. Newest first; <see cref="BeforeId"/> continues a page.</summary>
public sealed record AuditQuery(string? TargetId, string? StaffId, string? Action, DateTime? From, DateTime? To, long? BeforeId, int Limit);

/// <summary>A page of results and the cursor of the next one (null on the last page).</summary>
public sealed record AdminPage<T>(IReadOnlyList<T> Items, string? Next);

/// <summary>Accounts by username prefix (normalised) or id, in username order; <see cref="After"/> is the last username of the page before.</summary>
public sealed record AccountQuery(string? NamePrefix, Guid? Id, string? After, int Limit);

/// <summary>An account as staff see it: never its password hash or a session token.</summary>
public sealed record AdminAccount(Guid Id, string Username, DateTime CreatedAt, DateTime PasswordChangedAt, DateTime? EraseAfter, int Characters, int Sessions);

/// <summary>Characters by name prefix (its key), id or account, in name order; <see cref="After"/> is the last name key of the page before.</summary>
public sealed record CharacterQuery(string? NamePrefix, Guid? Id, Guid? Account, string? After, int Limit);

/// <summary>A character as staff see it (deleted ones too).</summary>
public sealed record AdminCharacter(Guid Id, Guid AccountId, int Slot, string Name, string NameKey, string JobId, DateTime CreatedAt, DateTime? DeletedAt);

/// <summary>One item of a saved character.</summary>
public sealed record AdminItem(long Id, string ItemId, int Quantity, string? Slot, IReadOnlyList<string> Cards, int Refine, string? Aside);

/// <summary>A character's saved state (T-702): null parts when it was never saved. The JSON columns are passed on as they are stored.</summary>
public sealed record AdminSavedState(long Version, DateTime? SavedAt, long Claim, long? HeldBy, string MapId, int CellX, int CellY, string Facing, int Hp, int Sp,
    string? SaveMapId, int? SaveCellX, int? SaveCellY, int BaseLevel, long BaseExp, string JobId, int JobLevel, long JobExp, int StatPoints, int SkillPoints, long Currency,
    string Stats, string Skills, string Quests, string Cooldowns, string SetAside, string OutfitSlots, bool OutfitsHidden, IReadOnlyList<string> Outfits, IReadOnlyList<AdminItem> Items);

/// <summary>A character and its saved state.</summary>
public sealed record AdminCharacterDetail(AdminCharacter Character, AdminSavedState? State);

/// <summary>A command's write that ended an account's sessions: their token hashes, and the audit row written in the same transaction.</summary>
public sealed record SessionsEnded(IReadOnlyList<byte[]> TokenHashes, long AuditId);

/// <summary>
/// What the admin API reads and writes (T-804). Reads only read; every write is one transaction with its audit row (rule 10). Implemented on PostgreSQL by
/// <c>PostgresAdminStore</c>. Throws <see cref="Accounts.StoreUnavailableException"/> when the database does not answer; must not be called on a tick thread.
/// </summary>
public interface IAdminStore
{
    /// <summary>A page of accounts.</summary>
    Task<AdminPage<AdminAccount>> FindAccountsAsync(AccountQuery query, CancellationToken cancellation);

    /// <summary>One account with its characters (deleted ones too), or null.</summary>
    Task<(AdminAccount Account, IReadOnlyList<AdminCharacter> Characters)?> GetAccountAsync(Guid id, CancellationToken cancellation);

    /// <summary>A page of characters.</summary>
    Task<AdminPage<AdminCharacter>> FindCharactersAsync(CharacterQuery query, CancellationToken cancellation);

    /// <summary>One character and its saved state, or null.</summary>
    Task<AdminCharacterDetail?> GetCharacterAsync(Guid id, CancellationToken cancellation);

    /// <summary>A page of the audit log, newest first.</summary>
    Task<AdminPage<AuditRecord>> SearchAuditAsync(AuditQuery query, CancellationToken cancellation);

    /// <summary>Writes an audit row on its own: for a change that is not in the database (written before the answer). Returns its id.</summary>
    Task<long> AppendAuditAsync(AuditDraft draft, CancellationToken cancellation);

    /// <summary>
    /// Ends every session of the account and writes the audit row <paramref name="audit"/> makes of how many ended, in one transaction; null (and nothing written) when there is
    /// no such account.
    /// </summary>
    Task<SessionsEnded?> EndSessionsAsync(Guid account, Func<int, AuditDraft> audit, CancellationToken cancellation);
}
