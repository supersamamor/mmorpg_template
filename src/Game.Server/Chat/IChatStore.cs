using Game.Server.Admin;

namespace Game.Server.Chat;

/// <summary>A character as chat finds it by name (T-901): its permanent id, its account, and its name as stored.</summary>
public sealed record ChatCharacter(Guid Id, Guid AccountId, string Name);

/// <summary>A character an account blocks, with its name as stored now.</summary>
public sealed record BlockedCharacter(Guid CharacterId, string Name);

/// <summary>What came of adding a block.</summary>
public enum BlockOutcome
{
    /// <summary>Stored.</summary>
    Added,

    /// <summary>It was there already.</summary>
    AlreadyBlocked,

    /// <summary>The account blocks as many as it may.</summary>
    Limit,

    /// <summary>The blocker's account or the blocked character is gone.</summary>
    Gone,
}

/// <summary>A report as chat makes it (T-901): who said what, when, on which channel, and who reported it when. Every id is permanent (accounts and characters), never an entity id.</summary>
public sealed record ChatReportDraft(Guid ReporterAccountId, Guid ReporterCharacterId, Guid SenderAccountId, Guid SenderCharacterId, string Channel, string Text, DateTime SaidAt,
    DateTime ReportedAt);

/// <summary>A report as stored.</summary>
public sealed record ChatReport(long Id, DateTime ReportedAt, Guid ReporterAccountId, Guid ReporterCharacterId, Guid SenderAccountId, Guid SenderCharacterId, string Channel, string Text,
    DateTime SaidAt, string Status, DateTime? ClosedAt, long? ClosedAuditId);

/// <summary>What came of closing a report: an "Admin errors" term (nothing written), or the report as it is now and its audit row.</summary>
public sealed record ReportChange(string? Error, ChatReport? Report, long AuditId)
{
    /// <summary>Refused.</summary>
    public static ReportChange Refused(string error) => new(error, null, 0);
}

/// <summary>
/// What chat keeps in the database (T-901): blocks (<c>chat_blocks</c>) and reports (<c>chat_reports</c>). Lines themselves are never stored, except the one in a report. Implemented on
/// PostgreSQL by <c>PostgresChatStore</c>; both tables go with either account when it is erased (<c>PostgresAccountStore.EraseAsync</c>). Throws
/// <see cref="Accounts.StoreUnavailableException"/> when the database does not answer; never on a tick thread.
/// </summary>
public interface IChatStore
{
    /// <summary>The characters the account blocks, by name.</summary>
    Task<IReadOnlyList<BlockedCharacter>> BlocksAsync(Guid account, CancellationToken cancellation);

    /// <summary>The character of that name key (<c>characters.name_key</c>; a deleted one keeps its name), or null.</summary>
    Task<ChatCharacter?> FindCharacterAsync(string nameKey, CancellationToken cancellation);

    /// <summary>Stores a block unless the account blocks <paramref name="limit"/> characters already.</summary>
    Task<BlockOutcome> AddBlockAsync(Guid account, Guid character, int limit, DateTime now, CancellationToken cancellation);

    /// <summary>Removes a block; false when there was none.</summary>
    Task<bool> RemoveBlockAsync(Guid account, Guid character, CancellationToken cancellation);

    /// <summary>Stores a report; its number. Throws <see cref="Accounts.StoreUnavailableException"/> also when an account or character in it is gone meanwhile.</summary>
    Task<long> AddReportAsync(ChatReportDraft report, CancellationToken cancellation);
}

/// <summary>What staff read and do with reports (T-901, through the admin API): the open and closed reports, and closing one with its audit row in one transaction (rule 10).</summary>
public interface IChatReportStore
{
    /// <summary>Reports newest first, by status and by account (as reporter or sender); <see cref="AdminPage{T}.Next"/> is the last number of the page (pass it as <paramref name="before"/>).</summary>
    Task<AdminPage<ChatReport>> FindReportsAsync(string? status, Guid? account, long? before, int limit, CancellationToken cancellation);

    /// <summary>Closes an open report with its audit row. Refused: <c>unknown_target</c>, <c>report_closed</c>.</summary>
    Task<ReportChange> CloseReportAsync(long report, DateTime now, Func<ChatReport, AuditDraft> audit, CancellationToken cancellation);

    /// <summary>Erases closed reports closed before <paramref name="closedBefore"/>; how many.</summary>
    Task<int> PurgeClosedAsync(DateTime closedBefore, CancellationToken cancellation);
}
