using Game.Server.Events;
using Game.Shared.Vocabulary;

namespace Game.Server.Admin;

/// <summary>
/// <c>AdminActionApplied</c>: staff changed something through the admin API, and it is audited (T-804). Ids only; the reason stays in the audit row.
/// </summary>
/// <param name="AuditId">The audit row.</param>
/// <param name="RequestId">The request.</param>
/// <param name="StaffId">Who.</param>
/// <param name="StaffRole">With which role.</param>
/// <param name="Action">An "Admin actions" term.</param>
/// <param name="TargetKind">An "Admin target kinds" term.</param>
/// <param name="TargetId">The target's id.</param>
public sealed record AdminActionApplied(long AuditId, string RequestId, string StaffId, string StaffRole, string Action, string TargetKind, string TargetId) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.AdminActionApplied;
}

/// <summary>
/// The admin API's event bus (rule 7). Admin commands do not run on one instance's tick, so they have a bus of their own, the same <see cref="EventBus"/> as an instance's:
/// a command publishes after its change is made and audited, and the bus is dispatched right away, on the request's thread, one command at a time. A handler that must touch
/// the world does what the gateway does from its handlers: it queues an intent. A handler that throws is logged and the others still run.
/// </summary>
public sealed class AdminEvents
{
    private readonly object _lock = new();

    /// <summary>Creates the bus; <paramref name="log"/> hears of a throwing handler.</summary>
    public AdminEvents(World.IServerLog log) => Bus = new EventBus((e, ex) => log.Error($"[admin] a handler of {e.Name} failed", ex));

    /// <summary>The bus, for subscribing (at setup).</summary>
    public EventBus Bus { get; }

    /// <summary>Publishes and dispatches at once. Thread-safe.</summary>
    public void Raise<T>(T e) where T : IGameEvent
    {
        lock (_lock)
        {
            Bus.Publish(e);
            Bus.Dispatch();
        }
    }
}
