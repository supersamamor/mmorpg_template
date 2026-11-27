using Game.Server.Accounts;
using Game.Server.World;
using Game.Server.World.Guild;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Guild;

/// <summary>
/// A guild's shared storage (T-905, ARCHITECTURE §6.11), behind <see cref="GuildService"/> (its rate limit, its refusals). Opened from the guild panel: no storage NPC, because nothing the server
/// checks depends on where the character stands and a phone player reaches the panel anywhere.
/// <list type="bullet">
/// <item><b>Open</b>: the storage as the member may see it, the log only for <c>storage_log</c>.</item>
/// <item><b>A move</b>: (1) a hold in the database: the guild's row locked, the member's permission and the room checked, a withdrawal's entry or currency taken out of the storage;
/// (2) the bag's side on the tick (<see cref="GuildStorageTransfer"/>), checked again; (3) the character's save that has that side settles the hold in its transaction. A refusal on the tick,
/// a tick that never runs it, or a save that never lands undoes the hold; a crash leaves the hold to the character's next claim, which undoes it.</item>
/// </list>
/// Not thread-bound: every call runs in the caller's task.
/// </summary>
public sealed class GuildStorage(GuildStorageDef settings, IGuildStorageStore store, Func<long> newId, Func<IIntent, bool> enqueue, int waitMs, TimeProvider time, IServerLog log)
{
    /// <summary>The storage's settings.</summary>
    public GuildStorageDef Settings { get; } = settings;

    /// <summary>The storage of <paramref name="me"/>'s guild as it may see it, or a refusal.</summary>
    public async Task<(string? Refusal, GuildStorageMessage? Storage)> OpenAsync(Guid me, CancellationToken cancellation)
    {
        var view = await store.ReadAsync(me, Settings.LogShown, Now - TimeSpan.FromDays(Settings.LogDays), cancellation).ConfigureAwait(false);
        return view is null ? (GuildReasons.NotInGuild, null) : (null, Message(view));
    }

    /// <summary>A deposit by <paramref name="me"/> (playing as <paramref name="entity"/>): null when it landed, else a "Guild reasons" term (nothing moved then).</summary>
    public async Task<string?> DepositAsync(Guid me, EntityId entity, GuildDepositMessage message, CancellationToken cancellation)
    {
        if (!Valid(message.Entry, message.Quantity, message.Currency)) return GuildReasons.ItemGone;
        var hold = await store.HoldDepositAsync(me, message.Entry is not null, message.Currency, Settings.Size, Settings.MaxCurrency, Now, cancellation).ConfigureAwait(false);
        if (hold.Refusal is not null) return hold.Refusal;
        var transfer = new GuildStorageTransfer(entity, hold.Hold, hold.Guild, GuildStorageMoves.Deposit, message.Entry, message.Quantity, null, message.Currency);
        return await RunAsync(me, transfer).ConfigureAwait(false);
    }

    /// <summary>A withdrawal by <paramref name="me"/> (playing as <paramref name="entity"/>): null when it landed, else a "Guild reasons" term (nothing moved then).</summary>
    public async Task<string?> WithdrawAsync(Guid me, EntityId entity, GuildWithdrawMessage message, CancellationToken cancellation)
    {
        if (!Valid(message.Entry, message.Quantity, message.Currency)) return GuildReasons.ItemGone;
        var hold = await store.HoldWithdrawAsync(me, message.Entry, message.Quantity, message.Currency, newId, Now, cancellation).ConfigureAwait(false);
        if (hold.Refusal is not null) return hold.Refusal;
        var transfer = new GuildStorageTransfer(entity, hold.Hold, hold.Guild, GuildStorageMoves.Withdraw, null, 0, hold.Entry, message.Entry is null ? message.Currency : 0);
        return await RunAsync(me, transfer).ConfigureAwait(false);
    }

    /// <summary>The storage message of <paramref name="view"/>.</summary>
    public static GuildStorageMessage Message(StorageView view, int size) => new(size, view.Currency,
        view.Entries.Select(e => new InventoryEntryRecord(e.Id, e.ItemId, e.Quantity, null, e.Cards.Count == 0 ? null : e.Cards, e.Refine)).ToList(),
        view.Log?.Select(l => new GuildStorageLogRecord(new DateTimeOffset(DateTime.SpecifyKind(l.At, DateTimeKind.Utc)).ToUnixTimeSeconds(), l.Name, l.Move, l.ItemId, l.EntryId, l.Quantity,
            l.Currency)).ToList());

    private GuildStorageMessage Message(StorageView view) => Message(view, Settings.Size);

    /// <summary>Exactly one of an entry (with at least one unit) and currency.</summary>
    private static bool Valid(long? entry, int quantity, long currency) => entry is not null ? quantity >= 1 && currency == 0 : currency > 0;

    /// <summary>The bag's side on the tick, then the save; the hold is undone when either does not happen.</summary>
    private async Task<string?> RunAsync(Guid me, GuildStorageTransfer transfer)
    {
        string? reason;
        if (!enqueue(transfer)) reason = GuildReasons.Unavailable;
        else
        {
            var done = transfer.Done;
            if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromMilliseconds(waitMs), time)).ConfigureAwait(false) != done && transfer.TryAbandon()) reason = GuildReasons.Unavailable;
            else reason = await done.ConfigureAwait(false);
        }
        if (reason is null && !await transfer.Note!.Landed.ConfigureAwait(false)) reason = GuildReasons.Unavailable;   // the stay ended before the save: neither side moved
        if (reason is null)
        {
            log.Info($"[guild] storage {transfer.Move} by {me} in guild {transfer.Guild} landed (hold {transfer.Hold}).");
            return null;
        }
        try
        {
            await store.ReleaseAsync(transfer.Hold, CancellationToken.None).ConfigureAwait(false);
        }
        catch (StoreUnavailableException e)
        {
            log.Warn($"[guild] storage hold {transfer.Hold} of {me} not undone now ({e.Message}): the character's next claim undoes it.");
        }
        return reason;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;
}
