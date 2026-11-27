using Game.Shared.Items;

namespace Game.Server.Guild;

/// <summary>
/// One move between a character and its guild's storage (T-905), as the character's save carries it: the hold the store made (<see cref="Hold"/>), the guild, the "Guild storage moves"
/// term, the entry (for a deposit: as it leaves the bag, set on the tick; for a withdrawal: as it left the storage) and the currency. The save that first has the bag's side lands the
/// storage's side in its transaction (rule 10).
/// </summary>
public sealed record StorageMove(Guid Hold, long Guild, string Move, InventoryEntry? Entry, long Currency);

/// <summary>What a hold came to (T-905): a refusal ("Guild reasons"), or the hold's token and guild, and for a withdrawal of an item the entry taken out (with its id in the bag to be).</summary>
public sealed record StorageHold(string? Refusal, Guid Hold = default, long Guild = 0, InventoryEntry? Entry = null)
{
    /// <summary>A refusal.</summary>
    public static StorageHold Refused(string reason) => new(reason);
}

/// <summary>One landed move in the log (T-905): when, by whom (the character's name now; empty once it no longer exists), the move, the item, entry id, units, currency.</summary>
public sealed record StorageLogLine(DateTime At, string Name, string Move, string? ItemId, long? EntryId, int Quantity, long Currency);

/// <summary>A guild's storage as one member may see it (T-905): its entries (oldest first) and currency, and the log when the member's rank has <c>storage_log</c>.</summary>
public sealed record StorageView(long Guild, long Currency, IReadOnlyList<InventoryEntry> Entries, IReadOnlyList<StorageLogLine>? Log);

/// <summary>
/// Guild storage in the database (T-905). A move is two steps: a hold, made in one transaction that locks the guild's row (as every rank change does, so a rank change and a hold are run one
/// after the other on any server process) and checks the member's permission and the room; then the character's save that has the bag's side, which settles the hold in its own
/// transaction. A hold whose save never lands is undone (<see cref="ReleaseAsync"/>, and at the character's next claim). Every method throws
/// <see cref="Accounts.StoreUnavailableException"/> when the database does not answer.
/// </summary>
public interface IGuildStorageStore
{
    /// <summary>The storage of <paramref name="character"/>'s guild, with at most <paramref name="logShown"/> log lines newer than <paramref name="oldest"/> (older ones are deleted); null when it is in none.</summary>
    Task<StorageView?> ReadAsync(Guid character, int logShown, DateTime oldest, CancellationToken cancellation);

    /// <summary>
    /// A deposit's hold: a place when <paramref name="item"/>, and room for <paramref name="currency"/>. Refusals: <c>not_in_guild</c>, <c>not_allowed</c> (no <c>storage_deposit</c>),
    /// <c>storage_full</c> (<paramref name="size"/> entries, or more than <paramref name="maxCurrency"/>).
    /// </summary>
    Task<StorageHold> HoldDepositAsync(Guid character, bool item, long currency, int size, long maxCurrency, DateTime now, CancellationToken cancellation);

    /// <summary>
    /// A withdrawal's hold: the entry (all of it, or <paramref name="quantity"/> units of a stack as a new entry with an id from <paramref name="newId"/>) or <paramref name="currency"/> leave the
    /// storage now. Refusals: <c>not_in_guild</c>, <c>not_allowed</c> (no <c>storage_items</c> / <c>storage_currency</c>), <c>item_gone</c> (no such entry, too few units, too little currency).
    /// </summary>
    Task<StorageHold> HoldWithdrawAsync(Guid character, long? entry, int quantity, long currency, Func<long> newId, DateTime now, CancellationToken cancellation);

    /// <summary>Undoes a hold whose bag side never happened (or whose save will never land): a withdrawal's entry and currency go back. Nothing when it was settled already.</summary>
    Task ReleaseAsync(Guid hold, CancellationToken cancellation);
}
