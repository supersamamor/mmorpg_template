using Game.Server.Accounts;
using Game.Server.Admin;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Server.Persistence;

/// <summary>
/// The admin API's reads and its audited writes in PostgreSQL (T-804). Reads select what staff may see and never a password hash or a token hash. A write runs its change and its
/// audit row (with the staff member's row) in one transaction, so there is never a change without its row, or a row without its change. Audit rows are only ever inserted; the
/// table's triggers refuse anything else.
/// </summary>
public sealed class PostgresAdminStore(GameDatabase database) : IAdminStore
{
    /// <inheritdoc />
    public Task<AdminPage<AdminAccount>> FindAccountsAsync(AccountQuery query, CancellationToken cancellation) => RunAsync(async db =>
    {
        var rows = db.Accounts.AsNoTracking().AsQueryable();
        if (query.Id is { } id) rows = rows.Where(a => a.Id == id);
        if (query.NamePrefix is { } prefix) rows = rows.Where(a => a.Username.StartsWith(prefix));
        if (query.After is { } after) rows = rows.Where(a => string.Compare(a.Username, after) > 0);
        var page = await rows.OrderBy(a => a.Username).Take(query.Limit + 1)
            .Select(a => new AdminAccount(a.Id, a.Username, a.CreatedAt, a.PasswordChangedAt, a.EraseAfter,
                db.Characters.Count(c => c.AccountId == a.Id && c.DeletedAt == null), db.Sessions.Count(s => s.AccountId == a.Id)))
            .ToListAsync(cancellation).ConfigureAwait(false);
        return Page(page, query.Limit, a => a.Username);
    });

    /// <inheritdoc />
    public Task<(AdminAccount Account, IReadOnlyList<AdminCharacter> Characters)?> GetAccountAsync(Guid id, CancellationToken cancellation) =>
        RunAsync<(AdminAccount, IReadOnlyList<AdminCharacter>)?>(async db =>
    {
        var page = await FindAccountsAsync(new AccountQuery(null, id, null, 1), cancellation).ConfigureAwait(false);
        if (page.Items.Count == 0) return null;
        var characters = await Characters(db.Characters.Where(c => c.AccountId == id).OrderBy(c => c.Slot).ThenBy(c => c.CreatedAt)).ToListAsync(cancellation).ConfigureAwait(false);
        return (page.Items[0], characters);
    });

    /// <inheritdoc />
    public Task<AdminPage<AdminCharacter>> FindCharactersAsync(CharacterQuery query, CancellationToken cancellation) => RunAsync(async db =>
    {
        var rows = db.Characters.AsNoTracking().AsQueryable();
        if (query.Id is { } id) rows = rows.Where(c => c.Id == id);
        if (query.Account is { } account) rows = rows.Where(c => c.AccountId == account);
        if (query.NamePrefix is { } prefix) rows = rows.Where(c => c.NameKey.StartsWith(prefix));
        if (query.After is { } after) rows = rows.Where(c => string.Compare(c.NameKey, after) > 0);
        var page = await Characters(rows.OrderBy(c => c.NameKey).Take(query.Limit + 1)).ToListAsync(cancellation).ConfigureAwait(false);
        return Page(page, query.Limit, c => c.NameKey);
    });

    /// <inheritdoc />
    public Task<AdminCharacterDetail?> GetCharacterAsync(Guid id, CancellationToken cancellation) => RunAsync(async db =>
    {
        // One snapshot of the three tables: a save that lands meanwhile is seen whole or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellation).ConfigureAwait(false);
        var character = await Characters(db.Characters.Where(c => c.Id == id)).SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (character is null) return null;
        var s = await db.CharacterStates.AsNoTracking().SingleOrDefaultAsync(r => r.CharacterId == id, cancellation).ConfigureAwait(false);
        if (s is null) return new AdminCharacterDetail(character, null);
        var items = await db.Items.AsNoTracking().Where(i => i.CharacterId == id).OrderBy(i => i.Id)
            .Select(i => new AdminItem(i.Id, i.ItemId, i.Quantity, i.Slot, i.Cards, i.Refine, i.Aside)).ToListAsync(cancellation).ConfigureAwait(false);
        var outfits = await db.Outfits.AsNoTracking().Where(o => o.CharacterId == id).OrderBy(o => o.OutfitId).Select(o => o.OutfitId).ToListAsync(cancellation).ConfigureAwait(false);
        return new AdminCharacterDetail(character, new AdminSavedState(s.Version, s.SavedAt, s.Claim, s.HeldBy, s.MapId, s.CellX, s.CellY, s.Facing, s.Hp, s.Sp, s.SaveMapId, s.SaveCellX,
            s.SaveCellY, s.BaseLevel, s.BaseExp, s.JobId, s.JobLevel, s.JobExp, s.StatPoints, s.SkillPoints, s.Currency, s.Stats, s.Skills, s.Quests, s.Cooldowns, s.SetAside,
            s.OutfitSlots, s.OutfitsHidden, outfits, items));
    });

    /// <inheritdoc />
    public Task<AdminPage<AuditRecord>> SearchAuditAsync(AuditQuery query, CancellationToken cancellation) => RunAsync(async db =>
    {
        var rows = db.Audit.AsNoTracking().AsQueryable();
        if (query.TargetId is { } target) rows = rows.Where(a => a.TargetId == target);
        if (query.StaffId is { } staff) rows = rows.Where(a => a.StaffId == staff);
        if (query.Action is { } action) rows = rows.Where(a => a.Action == action);
        if (query.From is { } from) rows = rows.Where(a => a.At >= from);
        if (query.To is { } to) rows = rows.Where(a => a.At < to);
        if (query.BeforeId is { } before) rows = rows.Where(a => a.Id < before);
        var found = await rows.Join(db.Staff, a => a.StaffId, s => s.Id, (a, s) => new { Row = a, s.DisplayName })
            .OrderByDescending(x => x.Row.Id).Take(query.Limit + 1)
            .ToListAsync(cancellation).ConfigureAwait(false);
        var page = found.Select(x => new AuditRecord(x.Row.Id, x.Row.At, x.Row.RequestId, x.Row.StaffId, x.DisplayName, x.Row.StaffRole, x.Row.Action, x.Row.TargetKind,
            x.Row.TargetId, x.Row.Before, x.Row.After, x.Row.Reason)).ToList();
        return Page(page, query.Limit, a => a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    });

    /// <inheritdoc />
    public Task<long> AppendAuditAsync(AuditDraft draft, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var id = await WriteAuditAsync(db, draft, cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return id;
    });

    /// <inheritdoc />
    public Task<SessionsEnded?> EndSessionsAsync(Guid account, Func<int, AuditDraft> audit, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        // The account's row first, as a sign-in does (PostgresAccountStore.TryAddSessionAsync): no session is added while this runs, so none survives it.
        var row = await db.Accounts.FromSql($"SELECT * FROM accounts WHERE id = {account} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (row is null) return null;
        var ended = await db.Sessions.Where(s => s.AccountId == account).Select(s => s.TokenHash).ToListAsync(cancellation).ConfigureAwait(false);
        await db.Sessions.Where(s => s.AccountId == account).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        var id = await WriteAuditAsync(db, audit(ended.Count), cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new SessionsEnded(ended, id);
    });

    /// <summary>The staff member's row (made, or brought up to date) and the audit row, in the caller's transaction. Returns the audit row's id.</summary>
    internal static async Task<long> WriteAuditAsync(GameDb db, AuditDraft draft, CancellationToken cancellation)
    {
        var staff = draft.Staff;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO admin_staff (id, display_name, role, first_seen_at, last_seen_at) VALUES ({staff.Id}, {staff.Name}, {staff.Role}, {draft.At}, {draft.At})
            ON CONFLICT (id) DO UPDATE SET display_name = EXCLUDED.display_name, role = EXCLUDED.role, last_seen_at = GREATEST(admin_staff.last_seen_at, EXCLUDED.last_seen_at)
            """, cancellation).ConfigureAwait(false);
        var row = new AdminAuditRow
        {
            At = draft.At, RequestId = draft.RequestId, StaffId = staff.Id, StaffRole = staff.Role, Action = draft.Action, TargetKind = draft.TargetKind, TargetId = draft.TargetId,
            Before = draft.Before, After = draft.After, Reason = draft.Reason,
        };
        db.Audit.Add(row);
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        return row.Id;
    }

    private static IQueryable<AdminCharacter> Characters(IQueryable<CharacterRow> rows) =>
        rows.AsNoTracking().Select(c => new AdminCharacter(c.Id, c.AccountId, c.Slot, c.Name, c.NameKey, c.JobId, c.CreatedAt, c.DeletedAt));

    private static AdminPage<T> Page<T>(List<T> rows, int limit, Func<T, string> key) =>
        rows.Count > limit ? new AdminPage<T>(rows.Take(limit).ToList(), key(rows[limit - 1])) : new AdminPage<T>(rows, null);

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
