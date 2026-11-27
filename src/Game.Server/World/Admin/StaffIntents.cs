using Game.Server.Admin;
using Game.Server.World.Saving;

namespace Game.Server.World.Admin;

/// <summary>
/// An admin command's change on the tick (T-806) that belongs to a game system, not to <see cref="AdminSystem"/>: the system that owns what it changes (the bag, the place) runs it, with that
/// system's own rules, and answers through <see cref="Done"/>. It is routed like any intent, also for a dead entity. Exactly one of the caller and the tick wins: the tick runs it only after
/// <see cref="TryStart"/>, and a caller that gave up first (<see cref="TryAbandon"/>) is sure it never runs, so a command that answered <c>timeout</c> changed nothing.
/// </summary>
public abstract class StaffIntent(EntityId entity, string action) : IDeadEntityIntent
{
    private readonly TaskCompletionSource<AdminStepResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;   // 0 waiting, 1 started on the tick, 2 given up by the caller

    /// <inheritdoc />
    public EntityId Entity { get; } = entity;

    /// <summary>The "Admin actions" term of the command (for the log).</summary>
    public string Action { get; } = action;

    /// <summary>Completes when the tick has run it (continuations never run on the tick thread).</summary>
    public Task<AdminStepResult> Done => _done.Task;

    /// <summary>Tick thread: true when it may run now; false when the caller has given up (then it must change nothing).</summary>
    public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    /// <summary>The caller: true when it has not started and now never will.</summary>
    public bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

    /// <summary>Tick thread: what came of it.</summary>
    public void Complete(AdminStepResult result) => _done.TrySetResult(result);

    /// <summary>Tick thread: it threw.</summary>
    public void Fail(Exception e) => _done.TrySetException(e);
}

/// <summary>
/// A support fix to a character's bag (T-806), run by the inventory system on the tick of the instance that holds it: give <see cref="GiveItemId"/> (new entries with new ids, never into a
/// stack it has), take units of entry <see cref="RemoveEntryId"/> (worn or not), or change the currency by <see cref="CurrencyDelta"/>. Weight is not checked: staff may load a character past
/// what it can carry, which the game then treats as any heavy bag. For a saved character the change's audit row (<see cref="Audit"/>, made from its before and after) rides its next save
/// (<see cref="StaffNote"/>), so the two land together.
/// </summary>
public sealed class StaffItemChange(EntityId entity, string action, Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>, AuditDraft> audit)
    : StaffIntent(entity, action)
{
    /// <summary>The item to give, or null.</summary>
    public string? GiveItemId { get; init; }

    /// <summary>The entry to take from, or null.</summary>
    public long? RemoveEntryId { get; init; }

    /// <summary>Units to give or take.</summary>
    public int Quantity { get; init; }

    /// <summary>Currency to add (above 0) or take (below 0).</summary>
    public long CurrencyDelta { get; init; }

    /// <summary>Makes the audit row from what the change saw and did.</summary>
    public Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>, AuditDraft> Audit { get; } = audit;

    /// <summary>Set on the tick for a saved character: the note that lands with its next save. Null for a character that is not saved (a dev login).</summary>
    public StaffNote? Note { get; set; }
}

/// <summary>A stuck character goes to its save point (T-806), run by the combat system, which knows where that is (as when it respawns); a dead one respawns there.</summary>
public sealed class StaffUnstick(EntityId entity, string action) : StaffIntent(entity, action);
