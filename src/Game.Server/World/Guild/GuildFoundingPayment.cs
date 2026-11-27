using Game.Server.World.Saving;
using Game.Shared.Definitions;

namespace Game.Server.World.Guild;

/// <summary>
/// The founding cost of a guild (T-904), taken on the tick by the inventory system, which owns the bag and the currency. The guild service has reserved the guild in the database first; the
/// inventory system takes <see cref="Cost"/> and puts a <see cref="StaffNote.ForGuild"/> note on the founder's save, so the save that first lacks the cost also founds the guild: one
/// transaction (rule 10). Exactly one of the caller and the tick wins, as for staff intents: the tick runs it only after <see cref="TryStart"/>, and a caller that gave up first
/// (<see cref="TryAbandon"/>) is sure nothing was taken.
/// </summary>
public sealed class GuildFoundingPayment(EntityId entity, long guild, GuildCostDef? cost) : IIntent
{
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;   // 0 waiting, 1 started on the tick, 2 given up by the caller

    /// <inheritdoc />
    public EntityId Entity { get; } = entity;

    /// <summary>The reserved guild the save founds.</summary>
    public long Guild { get; } = guild;

    /// <summary>What is taken; null takes nothing (the note still founds the guild with the next save).</summary>
    public GuildCostDef? Cost { get; } = cost;

    /// <summary>Set on the tick when the cost was taken: it lands with the founder's next save, or never.</summary>
    public StaffNote? Note { get; private set; }

    /// <summary>Completes when the tick has run it: null when the cost was taken (see <see cref="Note"/>), else a "Guild reasons" term. Continuations never run on the tick thread.</summary>
    public Task<string?> Done => _done.Task;

    /// <summary>Tick thread: true when it may run now; false when the caller has given up (then it must change nothing).</summary>
    public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    /// <summary>The caller: true when it has not started and now never will.</summary>
    public bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

    /// <summary>Tick thread: the cost was taken and <paramref name="note"/> rides the next save.</summary>
    public void Paid(StaffNote note)
    {
        Note = note;
        _done.TrySetResult(null);
    }

    /// <summary>Tick thread: nothing was taken, for <paramref name="reason"/>.</summary>
    public void Refuse(string reason) => _done.TrySetResult(reason);
}
