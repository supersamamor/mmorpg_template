using Game.Server.Accounts;
using Game.Server.Guild;
using Game.Shared.Guild;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Server.Persistence;

/// <summary>
/// Guild storage in PostgreSQL (T-905): <c>guild_storage_items</c>, <c>guild_storage_holds</c>, <c>guild_storage_log</c> and <c>guilds.storage_currency</c>. A hold takes the guild's row
/// (<c>FOR UPDATE</c>) like every guild change, then reads the member's rank and the storage: two members withdrawing the last unit on two server processes are run one after the other, and a
/// rank change racing a withdrawal is either before it (and refuses it) or after it. The character's save settles the hold in its own transaction (<see cref="LandAsync"/>), so the bag's side and
/// the storage's side land together or not at all; a hold whose save never lands is undone by <see cref="ReleaseAsync"/> or at the character's next claim (<see cref="ReleaseHoldsOfAsync"/>),
/// when no older stay of it can save any more.
/// </summary>
public sealed class PostgresGuildStorageStore(GameDatabase database) : IGuildStorageStore
{
    /// <inheritdoc />
    public Task<StorageView?> ReadAsync(Guid character, int logShown, DateTime oldest, CancellationToken cancellation) => RunAsync(async db =>
    {
        var member = await (from m in db.GuildMembers.AsNoTracking()
                            where m.CharacterId == character
                            join g in db.Guilds.Where(g => g.FoundedAt != null) on m.GuildId equals g.Id
                            select new { m.Rank, Guild = g }).SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (member is null) return null;
        var guild = member.Guild.Id;
        var entries = await db.GuildStorageItems.AsNoTracking().Where(i => i.GuildId == guild).OrderBy(i => i.Id).ToListAsync(cancellation).ConfigureAwait(false);
        IReadOnlyList<StorageLogLine>? log = null;
        if (GuildRules.Has(await RanksAsync(db, guild, cancellation).ConfigureAwait(false), member.Rank, GuildPermissions.StorageLog))
        {
            await db.GuildStorageLog.Where(l => l.GuildId == guild && l.At < oldest).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);   // kept guild.storage.log_days
            log = await (from l in db.GuildStorageLog.AsNoTracking()
                         where l.GuildId == guild
                         orderby l.At descending, l.Id descending
                         join c in db.Characters on l.CharacterId equals c.Id into who
                         from c in who.DefaultIfEmpty()
                         select new StorageLogLine(l.At, c == null ? string.Empty : c.Name, l.Move, l.ItemId, l.EntryId, l.Quantity, l.Currency))
                .Take(logShown).ToListAsync(cancellation).ConfigureAwait(false);
        }
        return new StorageView(guild, member.Guild.StorageCurrency, entries.Select(Entry).ToList(), log);
    });

    /// <inheritdoc />
    public Task<StorageHold> HoldDepositAsync(Guid character, bool item, long currency, int size, long maxCurrency, DateTime now, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        if (await LockAsync(db, character, cancellation).ConfigureAwait(false) is not { } me) return StorageHold.Refused(GuildReasons.NotInGuild);
        if (!GuildRules.Has(me.Ranks, me.Rank, GuildPermissions.StorageDeposit)) return StorageHold.Refused(GuildReasons.NotAllowed);
        var guild = me.Row.Id;
        if (item && await PlacesAsync(db, guild, cancellation).ConfigureAwait(false) >= size) return StorageHold.Refused(GuildReasons.StorageFull);
        if (currency > 0)
        {
            var coming = await db.GuildStorageHolds.Where(h => h.GuildId == guild && h.Move == GuildStorageMoves.Deposit).SumAsync(h => (long?)h.Currency, cancellation).ConfigureAwait(false) ?? 0;
            if (currency > maxCurrency - me.Row.StorageCurrency - coming) return StorageHold.Refused(GuildReasons.StorageFull);
        }
        var hold = new GuildStorageHoldRow
        {
            Id = Guid.NewGuid(), GuildId = guild, CharacterId = character, Move = GuildStorageMoves.Deposit, ItemId = item ? string.Empty : null, Currency = currency, CreatedAt = now,
        };
        db.GuildStorageHolds.Add(hold);
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new StorageHold(null, hold.Id, guild);
    });

    /// <inheritdoc />
    public Task<StorageHold> HoldWithdrawAsync(Guid character, long? entry, int quantity, long currency, Func<long> newId, DateTime now, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        if (await LockAsync(db, character, cancellation).ConfigureAwait(false) is not { } me) return StorageHold.Refused(GuildReasons.NotInGuild);
        var guild = me.Row.Id;
        var hold = new GuildStorageHoldRow { Id = Guid.NewGuid(), GuildId = guild, CharacterId = character, Move = GuildStorageMoves.Withdraw, CreatedAt = now };
        InventoryEntry? taken = null;
        if (entry is { } id)
        {
            if (!GuildRules.Has(me.Ranks, me.Rank, GuildPermissions.StorageItems)) return StorageHold.Refused(GuildReasons.NotAllowed);
            var row = await db.GuildStorageItems.SingleOrDefaultAsync(i => i.Id == id && i.GuildId == guild, cancellation).ConfigureAwait(false);
            if (row is null || quantity < 1 || quantity > row.Quantity) return StorageHold.Refused(GuildReasons.ItemGone);
            if (quantity == row.Quantity)
            {
                db.GuildStorageItems.Remove(row);
                taken = Entry(row);
            }
            else
            {
                row.Quantity -= quantity;   // part of a stack: a new entry with a new id
                taken = new InventoryEntry(newId(), row.ItemId, quantity);
            }
            (hold.EntryId, hold.ItemId, hold.Quantity, hold.Cards, hold.Refine) = (taken.Id, taken.ItemId, taken.Quantity, taken.Cards.ToArray(), taken.Refine);
        }
        else
        {
            if (!GuildRules.Has(me.Ranks, me.Rank, GuildPermissions.StorageCurrency)) return StorageHold.Refused(GuildReasons.NotAllowed);
            if (currency < 1 || currency > me.Row.StorageCurrency) return StorageHold.Refused(GuildReasons.ItemGone);
            me.Row.StorageCurrency -= currency;
            hold.Currency = currency;
        }
        db.GuildStorageHolds.Add(hold);
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new StorageHold(null, hold.Id, guild, taken);
    });

    /// <inheritdoc />
    public Task ReleaseAsync(Guid hold, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var row = await db.GuildStorageHolds.AsNoTracking().SingleOrDefaultAsync(h => h.Id == hold, cancellation).ConfigureAwait(false);
        if (row is not null)
        {
            await LockGuildAsync(db, row.GuildId, cancellation).ConfigureAwait(false);
            await UndoAsync(db, row.Id, cancellation).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return 0;
    });

    // ---- what other transactions call (in theirs) --------------------------------------------------------------------------------------------

    /// <summary>
    /// The character's save (T-905), in its transaction: the storage's side of <paramref name="move"/>, whose bag side this save is the first to have. A deposit puts the entry and currency in
    /// the storage; a withdrawal's hold is settled (the entry and currency left the storage when it was made). Either way the hold goes and a log line is written. A withdrawal whose hold is
    /// gone throws (the save rolls back): it would mean the storage has the entry back while the bag has it too, which the claim check before this makes impossible.
    /// </summary>
    internal static async Task LandAsync(GameDb db, StorageMove move, Guid character, DateTime at, CancellationToken cancellation)
    {
        var settled = await db.Database.ExecuteSqlAsync($"DELETE FROM guild_storage_holds WHERE id = {move.Hold} AND character_id = {character}", cancellation).ConfigureAwait(false);
        if (move.Move == GuildStorageMoves.Withdraw)
        {
            if (settled == 0) throw new InvalidOperationException($"guild storage hold {move.Hold} of {character} is gone before its save landed");
        }
        else
        {
            // The hold keeps the guild (its foreign key); only erasing its last member takes a guild with holds in it, and then what was on its way goes with the guild's storage.
            if (!await db.Guilds.AnyAsync(g => g.Id == move.Guild, cancellation).ConfigureAwait(false)) return;
            if (move.Entry is { } entry)
                db.GuildStorageItems.Add(new GuildStorageItemRow
                    { Id = entry.Id, GuildId = move.Guild, ItemId = entry.ItemId, Quantity = entry.Quantity, Cards = entry.Cards.ToArray(), Refine = entry.Refine });
            if (move.Currency > 0)
                await db.Database.ExecuteSqlAsync($"UPDATE guilds SET storage_currency = storage_currency + {move.Currency} WHERE id = {move.Guild}", cancellation).ConfigureAwait(false);
        }
        db.GuildStorageLog.Add(new GuildStorageLogRow
        {
            GuildId = move.Guild, CharacterId = character, Move = move.Move, ItemId = move.Entry?.ItemId, EntryId = move.Entry?.Id, Quantity = move.Entry?.Quantity ?? 0,
            Currency = move.Currency, At = at,
        });
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The character is claimed (a new stay, on any server process), in the claim's transaction: its holds belong to stays that can no longer save, so none of them will land; they are undone.
    /// </summary>
    internal static async Task<int> ReleaseHoldsOfAsync(GameDb db, Guid character, CancellationToken cancellation)
    {
        var holds = await db.GuildStorageHolds.AsNoTracking().Where(h => h.CharacterId == character).Select(h => new { h.Id, h.GuildId }).ToListAsync(cancellation).ConfigureAwait(false);
        foreach (var guild in holds.Select(h => h.GuildId).Distinct().Order()) await LockGuildAsync(db, guild, cancellation).ConfigureAwait(false);
        foreach (var hold in holds) await UndoAsync(db, hold.Id, cancellation).ConfigureAwait(false);
        return holds.Count;
    }

    /// <summary>Whether the guild's storage holds anything: entries, currency, or a move under way (T-905: the last member cannot leave then).</summary>
    internal static async Task<bool> HoldsAnythingAsync(GameDb db, GuildRow guild, CancellationToken cancellation) =>
        guild.StorageCurrency > 0
        || await db.GuildStorageItems.AnyAsync(i => i.GuildId == guild.Id, cancellation).ConfigureAwait(false)
        || await db.GuildStorageHolds.AnyAsync(h => h.GuildId == guild.Id, cancellation).ConfigureAwait(false);

    /// <summary>Deletes a guild's storage, holds and log with the guild (T-904's <c>DeleteGuildAsync</c>: a reservation, or a guild whose last member was erased or deleted).</summary>
    internal static async Task DeleteOfAsync(GameDb db, long guild, CancellationToken cancellation)
    {
        await db.GuildStorageHolds.Where(h => h.GuildId == guild).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        await db.GuildStorageItems.Where(i => i.GuildId == guild).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        await db.GuildStorageLog.Where(l => l.GuildId == guild).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Erasing accounts (T-703), in the erasure's transaction: the log keeps the moves for the guild's accounting but no longer says who made them (the character id becomes null), and the
    /// characters' holds are undone (no save of theirs will land).
    /// </summary>
    internal static async Task ForgetAsync(GameDb db, IReadOnlyCollection<Guid> characters, CancellationToken cancellation)
    {
        foreach (var character in characters) await ReleaseHoldsOfAsync(db, character, cancellation).ConfigureAwait(false);
        var ids = characters.ToArray();
        await db.GuildStorageLog.Where(l => l.CharacterId != null && ids.Contains(l.CharacterId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.CharacterId, (Guid?)null), cancellation).ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------------------

    private sealed record Member(GuildRow Row, int Rank, IReadOnlyList<GuildRankRecord> Ranks);

    /// <summary>The character's founded guild, its row locked, with the character's rank as read after the lock (a rank change commits before or after this, never in between).</summary>
    private static async Task<Member?> LockAsync(GameDb db, Guid character, CancellationToken cancellation)
    {
        var guildId = await db.GuildMembers.AsNoTracking().Where(m => m.CharacterId == character).Select(m => (long?)m.GuildId).SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (guildId is null) return null;
        var row = await db.Guilds.FromSql($"SELECT * FROM guilds WHERE id = {guildId.Value} AND founded_at IS NOT NULL FOR UPDATE").SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (row is null) return null;
        var rank = await db.GuildMembers.AsNoTracking().Where(m => m.CharacterId == character && m.GuildId == row.Id).Select(m => (int?)m.Rank).SingleOrDefaultAsync(cancellation)
            .ConfigureAwait(false);
        return rank is null ? null : new Member(row, rank.Value, await RanksAsync(db, row.Id, cancellation).ConfigureAwait(false));
    }

    private static Task<GuildRow?> LockGuildAsync(GameDb db, long guild, CancellationToken cancellation) =>
        db.Guilds.FromSql($"SELECT * FROM guilds WHERE id = {guild} FOR UPDATE").SingleOrDefaultAsync(cancellation);

    /// <summary>Places taken: entries, and holds that keep or hold one (a withdrawal under way may come back).</summary>
    private static async Task<int> PlacesAsync(GameDb db, long guild, CancellationToken cancellation) =>
        await db.GuildStorageItems.CountAsync(i => i.GuildId == guild, cancellation).ConfigureAwait(false)
        + await db.GuildStorageHolds.CountAsync(h => h.GuildId == guild && h.ItemId != null, cancellation).ConfigureAwait(false);

    /// <summary>Undoes one hold (the guild's row locked by the caller): a withdrawal's entry goes back (part of a stack onto a stack of the same item, if one is there) and its currency.</summary>
    private static async Task UndoAsync(GameDb db, Guid hold, CancellationToken cancellation)
    {
        var row = await db.GuildStorageHolds.SingleOrDefaultAsync(h => h.Id == hold, cancellation).ConfigureAwait(false);
        if (row is null) return;   // settled meanwhile
        if (row.Move == GuildStorageMoves.Withdraw)
        {
            if (row is { EntryId: { } id, ItemId: { } item })
            {
                var stack = row.Cards.Length == 0 && row.Refine == 0
                    ? await db.GuildStorageItems.FirstOrDefaultAsync(i => i.GuildId == row.GuildId && i.ItemId == item && i.Cards.Length == 0 && i.Refine == 0 && i.Id != id, cancellation)
                        .ConfigureAwait(false)
                    : null;
                if (stack is not null && (long)stack.Quantity + row.Quantity <= int.MaxValue) stack.Quantity += row.Quantity;
                else db.GuildStorageItems.Add(new GuildStorageItemRow { Id = id, GuildId = row.GuildId, ItemId = item, Quantity = row.Quantity, Cards = row.Cards, Refine = row.Refine });
            }
            if (row.Currency > 0)
                await db.Database.ExecuteSqlAsync($"UPDATE guilds SET storage_currency = storage_currency + {row.Currency} WHERE id = {row.GuildId}", cancellation).ConfigureAwait(false);
        }
        db.GuildStorageHolds.Remove(row);
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<GuildRankRecord>> RanksAsync(GameDb db, long guild, CancellationToken cancellation) =>
        (await db.GuildRanks.AsNoTracking().Where(r => r.GuildId == guild).OrderBy(r => r.Position).ToListAsync(cancellation).ConfigureAwait(false))
        .Select(r => new GuildRankRecord(r.Name, r.Permissions)).ToList();

    private static InventoryEntry Entry(GuildStorageItemRow i) => new(i.Id, i.ItemId, i.Quantity) { Cards = i.Cards, Refine = i.Refine };

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
