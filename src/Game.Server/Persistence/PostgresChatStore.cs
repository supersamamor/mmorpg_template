using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Server.Chat;
using Game.Shared.Vocabulary;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Server.Persistence;

/// <summary>
/// Chat's data in PostgreSQL (T-901): blocks (<c>chat_blocks</c>) and players' reports of lines (<c>chat_reports</c>). A block takes the blocker's account row first, as everything that
/// writes for an account does, so it is never made for an account being erased; closing a report writes its audit row in the same transaction (rule 10). Both tables go with either account
/// when it is erased (<see cref="PostgresAccountStore.EraseAsync"/>).
/// </summary>
public sealed class PostgresChatStore(GameDatabase database) : IChatStore, IChatReportStore
{
    // ---- blocks ----------------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<IReadOnlyList<BlockedCharacter>> BlocksAsync(Guid account, CancellationToken cancellation) => RunAsync<IReadOnlyList<BlockedCharacter>>(async db =>
        await db.ChatBlocks.AsNoTracking().Where(b => b.AccountId == account).Join(db.Characters, b => b.BlockedCharacterId, c => c.Id, (b, c) => new BlockedCharacter(c.Id, c.Name))
            .ToListAsync(cancellation).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<ChatCharacter?> FindCharacterAsync(string nameKey, CancellationToken cancellation) => RunAsync(async db =>
        await db.Characters.AsNoTracking().Where(c => c.NameKey == nameKey).Select(c => new ChatCharacter(c.Id, c.AccountId, c.Name)).SingleOrDefaultAsync(cancellation).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<BlockOutcome> AddBlockAsync(Guid account, Guid character, int limit, DateTime now, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        // The account's row: an erasure takes it first too, so a block is never added to an account on its way out; and two blocks at once count right.
        var owner = await db.Accounts.FromSql($"SELECT * FROM accounts WHERE id = {account} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (owner is null || owner.EraseAfter is not null) return BlockOutcome.Gone;
        if (!await db.Characters.AnyAsync(c => c.Id == character, cancellation).ConfigureAwait(false)) return BlockOutcome.Gone;
        if (await db.ChatBlocks.AnyAsync(b => b.AccountId == account && b.BlockedCharacterId == character, cancellation).ConfigureAwait(false)) return BlockOutcome.AlreadyBlocked;
        if (await db.ChatBlocks.CountAsync(b => b.AccountId == account, cancellation).ConfigureAwait(false) >= limit) return BlockOutcome.Limit;
        db.ChatBlocks.Add(new ChatBlockRow { AccountId = account, BlockedCharacterId = character, CreatedAt = now });
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return BlockOutcome.Added;
    });

    /// <inheritdoc />
    public Task<bool> RemoveBlockAsync(Guid account, Guid character, CancellationToken cancellation) => RunAsync(async db =>
        await db.ChatBlocks.Where(b => b.AccountId == account && b.BlockedCharacterId == character).ExecuteDeleteAsync(cancellation).ConfigureAwait(false) > 0);

    // ---- reports ---------------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<long> AddReportAsync(ChatReportDraft report, CancellationToken cancellation) => RunAsync(async db =>
    {
        var row = new ChatReportRow
        {
            ReportedAt = report.ReportedAt, ReporterAccountId = report.ReporterAccountId, ReporterCharacterId = report.ReporterCharacterId, SenderAccountId = report.SenderAccountId,
            SenderCharacterId = report.SenderCharacterId, Channel = report.Channel, Text = report.Text, SaidAt = report.SaidAt, Status = ChatReportStatuses.Open,
        };
        db.ChatReports.Add(row);
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);   // the keys refuse a report naming an account or character an erasure took meanwhile
        return row.Id;
    });

    /// <inheritdoc />
    public Task<AdminPage<ChatReport>> FindReportsAsync(string? status, Guid? account, long? before, int limit, CancellationToken cancellation) => RunAsync(async db =>
    {
        var rows = db.ChatReports.AsNoTracking().AsQueryable();
        if (status is not null) rows = rows.Where(r => r.Status == status);
        if (account is { } id) rows = rows.Where(r => r.ReporterAccountId == id || r.SenderAccountId == id);
        if (before is { } b) rows = rows.Where(r => r.Id < b);
        var page = await rows.OrderByDescending(r => r.Id).Take(limit + 1).ToListAsync(cancellation).ConfigureAwait(false);
        var items = page.Take(limit).Select(AsReport).ToList();
        return new AdminPage<ChatReport>(items, page.Count > limit ? items[^1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    });

    /// <inheritdoc />
    public Task<ReportChange> CloseReportAsync(long report, DateTime now, Func<ChatReport, AuditDraft> audit, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var row = await db.ChatReports.FromSql($"SELECT * FROM chat_reports WHERE id = {report} FOR UPDATE").SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (row is null) return ReportChange.Refused(AdminErrors.UnknownTarget);
        if (row.Status != ChatReportStatuses.Open) return ReportChange.Refused(AdminErrors.ReportClosed);
        var auditId = await PostgresAdminStore.WriteAuditAsync(db, audit(AsReport(row)), cancellation).ConfigureAwait(false);
        row.Status = ChatReportStatuses.Closed;
        row.ClosedAt = now;
        row.ClosedAuditId = auditId;
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new ReportChange(null, AsReport(row), auditId);
    });

    /// <inheritdoc />
    public Task<int> PurgeClosedAsync(DateTime closedBefore, CancellationToken cancellation) => RunAsync(db =>
        db.ChatReports.Where(r => r.Status == ChatReportStatuses.Closed && r.ClosedAt < closedBefore).ExecuteDeleteAsync(cancellation));

    private static ChatReport AsReport(ChatReportRow r) => new(r.Id, r.ReportedAt, r.ReporterAccountId, r.ReporterCharacterId, r.SenderAccountId, r.SenderCharacterId, r.Channel, r.Text,
        r.SaidAt, r.Status, r.ClosedAt, r.ClosedAuditId);

    private async Task<T> RunAsync<T>(Func<GameDb, Task<T>> work)
    {
        try
        {
            await using var db = database.Open();
            return await work(db).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && GameDatabase.CauseOf(e) is { } cause)
        {
            throw new StoreUnavailableException(cause is PostgresException p ? $"database error {p.SqlState}" : $"database not answering ({cause.GetType().Name})");
        }
    }
}
