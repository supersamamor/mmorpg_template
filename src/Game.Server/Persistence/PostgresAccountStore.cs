using Game.Server.Accounts;
using Game.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Server.Persistence;

/// <summary>
/// The accounts, sessions and characters in PostgreSQL. Uniqueness is the database's: an insert is tried and a unique violation is the answer, so two requests
/// at once can never both win.
/// </summary>
public sealed class PostgresAccountStore(GameDatabase database) : IAccountStore
{
    /// <inheritdoc />
    public Task<AccountAuth?> FindAccountAsync(string username, CancellationToken cancellation) => RunAsync(db =>
        db.Accounts.Where(a => a.Username == username).Select(a => new AccountAuth(a.Id, a.PasswordHash)).SingleOrDefaultAsync(cancellation));

    /// <inheritdoc />
    public Task<AccountAuth?> FindAccountAsync(Guid id, CancellationToken cancellation) => RunAsync(db =>
        db.Accounts.Where(a => a.Id == id).Select(a => new AccountAuth(a.Id, a.PasswordHash)).SingleOrDefaultAsync(cancellation));

    /// <inheritdoc />
    public Task<bool> TryCreateAccountAsync(Guid id, string username, string passwordHash, DateTime now, CancellationToken cancellation) => RunAsync(async db =>
    {
        db.Accounts.Add(new AccountRow { Id = id, Username = username, PasswordHash = passwordHash, CreatedAt = now, PasswordChangedAt = now });
        return await TrySaveAsync(db, GameDb.UsernameIndex, cancellation).ConfigureAwait(false);
    });

    /// <inheritdoc />
    public Task<bool> TryAddSessionAsync(Guid account, string expectedHash, string? rehash, byte[] tokenHash, DateTime now, DateTime expires, int maxSessions,
        CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        // Locking the account makes this wait for a password change in progress, and a password change wait for this: it then ends this session too.
        var row = await LockAccountAsync(db, account, cancellation).ConfigureAwait(false);
        if (row is null || row.PasswordHash != expectedHash) return false;
        if (rehash is not null) row.PasswordHash = rehash;
        db.Sessions.Add(new SessionRow { TokenHash = tokenHash, AccountId = account, CreatedAt = now, ExpiresAt = expires });
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await db.Sessions.Where(s => s.AccountId == account && s.ExpiresAt <= now).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        var surplus = await db.Sessions.Where(s => s.AccountId == account && s.TokenHash != tokenHash)
            .OrderByDescending(s => s.CreatedAt).ThenBy(s => s.TokenHash).Skip(Math.Max(0, maxSessions - 1))
            .Select(s => s.TokenHash).ToListAsync(cancellation).ConfigureAwait(false);
        foreach (var hash in surplus) await db.Sessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return true;
    });

    /// <inheritdoc />
    public Task<Guid?> FindSessionAsync(byte[] tokenHash, DateTime now, CancellationToken cancellation) => RunAsync(db =>
        db.Sessions.Where(s => s.TokenHash == tokenHash && s.ExpiresAt > now).Select(s => (Guid?)s.AccountId).SingleOrDefaultAsync(cancellation));

    /// <inheritdoc />
    public Task<bool> DeleteSessionAsync(byte[] tokenHash, CancellationToken cancellation) => RunAsync(async db =>
        await db.Sessions.Where(s => s.TokenHash == tokenHash).ExecuteDeleteAsync(cancellation).ConfigureAwait(false) > 0);

    /// <inheritdoc />
    public Task<bool> TryChangePasswordAsync(Guid account, string expectedHash, string newHash, DateTime now, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var row = await LockAccountAsync(db, account, cancellation).ConfigureAwait(false);
        if (row is null || row.PasswordHash != expectedHash) return false;
        row.PasswordHash = newHash;
        row.PasswordChangedAt = now;
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await db.Sessions.Where(s => s.AccountId == account).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return true;
    });

    /// <inheritdoc />
    public Task<IReadOnlyList<CharacterEntry>> ListCharactersAsync(Guid account, CancellationToken cancellation) => RunAsync<IReadOnlyList<CharacterEntry>>(async db =>
        await db.Characters.Where(c => c.AccountId == account && c.DeletedAt == null).OrderBy(c => c.Slot)
            .Select(c => new CharacterEntry(c.Id, c.Slot, c.Name, c.JobId, new AppearanceChoice(c.Gender, c.SkinToneId, c.FaceId, c.HairstyleId, c.HairColorId)))
            .ToListAsync(cancellation).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<CharacterEntry?> FindCharacterAsync(Guid account, Guid character, CancellationToken cancellation) => RunAsync(db =>
        db.Characters.Where(c => c.Id == character && c.AccountId == account && c.DeletedAt == null)
            .Select(c => new CharacterEntry(c.Id, c.Slot, c.Name, c.JobId, new AppearanceChoice(c.Gender, c.SkinToneId, c.FaceId, c.HairstyleId, c.HairColorId)))
            .SingleOrDefaultAsync(cancellation));

    /// <inheritdoc />
    public Task<(CreateCharacterOutcome Outcome, CharacterEntry? Character)> CreateCharacterAsync(Guid account, Guid character, string name, string nameKey, string jobId, int maxSlots,
        AppearanceRecord? appearance, DateTime now, CancellationToken cancellation) => RunAsync<(CreateCharacterOutcome, CharacterEntry?)>(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        // One creation per account at a time, so two at once take two slots; the index on (account, slot) is the backstop.
        await LockAccountAsync(db, account, cancellation).ConfigureAwait(false);
        var used = await db.Characters.Where(c => c.AccountId == account && c.DeletedAt == null).Select(c => c.Slot).ToListAsync(cancellation).ConfigureAwait(false);
        var slot = 0;
        while (used.Contains(slot)) slot++;
        if (used.Count >= maxSlots || slot >= maxSlots) return (CreateCharacterOutcome.NoFreeSlot, null);
        db.Characters.Add(new CharacterRow { Id = character, AccountId = account, Slot = slot, Name = name, NameKey = nameKey, JobId = jobId, CreatedAt = now,
            Gender = appearance?.Gender, SkinToneId = appearance?.Skin, FaceId = appearance?.Face, HairstyleId = appearance?.Hair, HairColorId = appearance?.HairColor });
        if (!await TrySaveAsync(db, GameDb.CharacterNameIndex, cancellation).ConfigureAwait(false)) return (CreateCharacterOutcome.NameTaken, null);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return (CreateCharacterOutcome.Created, new CharacterEntry(character, slot, name, jobId,
            appearance is null ? null : new AppearanceChoice(appearance.Gender, appearance.Skin, appearance.Face, appearance.Hair, appearance.HairColor)));
    });

    /// <inheritdoc />
    public Task<bool> DeleteCharacterAsync(Guid account, Guid character, DateTime now, CancellationToken cancellation) => RunAsync(async db =>
    {
        // ADR-002 row 289: entering the world (PostgresCharacterStore.ClaimAsync) takes the same lock first, so either it sees the character deleted, or this sees it held.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var row = await db.Characters.FromSql($"SELECT * FROM characters WHERE id = {character} AND account_id = {account} AND deleted_at IS NULL FOR UPDATE")
            .SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (row is null) return false;
        var holder = await db.CharacterStates.Where(s => s.CharacterId == character).Select(s => s.HeldBy).SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (holder is { } run && await PostgresCharacterStore.IsAliveAsync(db, run, cancellation).ConfigureAwait(false)) return false;
        row.DeletedAt = now;
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return true;
    });

    private static Task<AccountRow?> LockAccountAsync(GameDb db, Guid account, CancellationToken cancellation) =>
        db.Accounts.FromSql($"SELECT * FROM accounts WHERE id = {account} FOR UPDATE").SingleOrDefaultAsync(cancellation);

    /// <summary>Saves; false when <paramref name="uniqueIndex"/> refused the row.</summary>
    private static async Task<bool> TrySaveAsync(GameDb db, string uniqueIndex, CancellationToken cancellation)
    {
        try
        {
            await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } p && p.ConstraintName == uniqueIndex)
        {
            return false;
        }
    }

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
