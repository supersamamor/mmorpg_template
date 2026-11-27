using Game.Server.Guild;
using Game.Server.World.Saving;
using Game.Shared.Items;

namespace Game.Server.World.Guild;

/// <summary>
/// The bag's side of a guild storage move (T-905), run on the tick by the inventory system, which owns the bag. The guild service has made the storage's hold first (permission and room checked
/// in the database); the inventory system checks the bag again (<see cref="Game.Shared.Guild.GuildStorageRules"/>), moves its side and puts a <see cref="StaffNote.ForStorage"/> note on the
/// character's save, so the save that first has the bag's side lands the storage's side: one transaction (rule 10). Exactly one of the caller and the tick wins, as for
/// <see cref="GuildFoundingPayment"/>.
/// </summary>
/// <param name="entity">The character's entity.</param>
/// <param name="hold">The storage's hold for this move.</param>
/// <param name="guild">The guild.</param>
/// <param name="move">A "Guild storage moves" term.</param>
/// <param name="entryId">A deposit's bag entry, or null.</param>
/// <param name="quantity">A deposit's units of <paramref name="entryId"/>.</param>
/// <param name="entry">A withdrawal's entry, as it left the storage, or null.</param>
/// <param name="currency">Currency moving.</param>
public sealed class GuildStorageTransfer(EntityId entity, Guid hold, long guild, string move, long? entryId, int quantity, InventoryEntry? entry, long currency) : IIntent
{
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;   // 0 waiting, 1 started on the tick, 2 given up by the caller

    /// <inheritdoc />
    public EntityId Entity { get; } = entity;

    /// <summary>The storage's hold.</summary>
    public Guid Hold { get; } = hold;

    /// <summary>The guild.</summary>
    public long Guild { get; } = guild;

    /// <summary>A "Guild storage moves" term.</summary>
    public string Move { get; } = move;

    /// <summary>A deposit's bag entry, or null (currency only).</summary>
    public long? EntryId { get; } = entryId;

    /// <summary>A deposit's units.</summary>
    public int Quantity { get; } = quantity;

    /// <summary>A withdrawal's entry, or null (currency only).</summary>
    public InventoryEntry? Entry { get; } = entry;

    /// <summary>Currency moving.</summary>
    public long Currency { get; } = currency;

    /// <summary>Set on the tick when the bag's side moved: it lands with the character's next save, or never.</summary>
    public StaffNote? Note { get; private set; }

    /// <summary>Completes when the tick has run it: null when moved (see <see cref="Note"/>), else a "Guild reasons" term. Continuations never run on the tick thread.</summary>
    public Task<string?> Done => _done.Task;

    /// <summary>Tick thread: true when it may run now; false when the caller has given up (then it must change nothing).</summary>
    public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    /// <summary>The caller: true when it has not started and now never will.</summary>
    public bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

    /// <summary>Tick thread: the bag's side moved and <paramref name="note"/> rides the next save.</summary>
    public void Moved(StaffNote note)
    {
        Note = note;
        _done.TrySetResult(null);
    }

    /// <summary>Tick thread: nothing moved, for <paramref name="reason"/>.</summary>
    public void Refuse(string reason) => _done.TrySetResult(reason);

    /// <summary>The move as the save lands it, with the entry the storage gets or gave.</summary>
    internal StorageMove ToMove(InventoryEntry? entry) => new(Hold, Guild, Move, entry, Currency);
}
