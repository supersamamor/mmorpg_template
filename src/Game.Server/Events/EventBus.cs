namespace Game.Server.Events;

/// <summary>An event on the bus. <see cref="Name"/> is an <c>EventNames</c> term (VOCABULARY.md "Events").</summary>
public interface IGameEvent
{
    /// <summary>The event's vocabulary name.</summary>
    string Name { get; }
}

/// <summary>
/// In-process, per-instance typed publish/subscribe (hard rule 7: systems talk through events, not calls).
/// <see cref="Publish{T}"/> only queues; <see cref="Dispatch"/> (tick phase 4) delivers the queued events in raise
/// order, synchronously, on the instance's thread. An event published while dispatching (by a handler) is queued for
/// the next dispatch: there is no re-entrant dispatch. Handlers are matched on the event's exact runtime type.
/// Single-threaded like the instance that owns it: publish and dispatch only from the instance's tick; subscribing is
/// safe from any thread (copy-on-write) but is meant to happen at setup.
/// </summary>
public sealed class EventBus
{
    private readonly Action<IGameEvent, Exception> _onHandlerError;
    private readonly object _subscribeLock = new();
    private Dictionary<Type, Action<IGameEvent>[]> _handlers = new();
    private Action<IGameEvent>[] _allHandlers = [];
    private List<IGameEvent> _pending = new();
    private List<IGameEvent> _dispatching = new();

    /// <summary>Creates a bus. <paramref name="onHandlerError"/> is told about a throwing handler; dispatch continues.</summary>
    public EventBus(Action<IGameEvent, Exception> onHandlerError) => _onHandlerError = onHandlerError;

    /// <summary>Events queued for the next <see cref="Dispatch"/>.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Queues an event for the next dispatch.</summary>
    public void Publish<T>(T e) where T : IGameEvent
    {
        ArgumentNullException.ThrowIfNull(e);
        _pending.Add(e);
    }

    /// <summary>Subscribes to events of exactly type <typeparamref name="T"/>. Dispose the result to unsubscribe.</summary>
    public IDisposable Subscribe<T>(Action<T> handler) where T : IGameEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        Action<IGameEvent> wrapper = e => handler((T)e);
        lock (_subscribeLock)
        {
            var copy = new Dictionary<Type, Action<IGameEvent>[]>(_handlers);
            copy[typeof(T)] = copy.TryGetValue(typeof(T), out var list) ? [.. list, wrapper] : [wrapper];
            _handlers = copy;
        }
        return new Subscription(() =>
        {
            lock (_subscribeLock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list)) return;
                var copy = new Dictionary<Type, Action<IGameEvent>[]>(_handlers);
                var remaining = list.Where(h => h != wrapper).ToArray();
                if (remaining.Length == 0) copy.Remove(typeof(T));
                else copy[typeof(T)] = remaining;
                _handlers = copy;
            }
        });
    }

    /// <summary>
    /// Subscribes to every event, after the typed handlers of each event. Meant for cross-cutting consumers
    /// (AOI, the gateway, logs, tests), not for game rules.
    /// </summary>
    public IDisposable SubscribeAll(Action<IGameEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_subscribeLock) _allHandlers = [.. _allHandlers, handler];
        return new Subscription(() =>
        {
            lock (_subscribeLock) _allHandlers = _allHandlers.Where(h => h != handler).ToArray();
        });
    }

    /// <summary>
    /// Delivers every event queued before this call, in raise order. Returns how many were delivered. Events published
    /// by handlers stay queued for the next call.
    /// </summary>
    public int Dispatch()
    {
        (_dispatching, _pending) = (_pending, _dispatching);
        var handlers = _handlers;
        var all = _allHandlers;
        var count = _dispatching.Count;
        try
        {
            foreach (var e in _dispatching)
            {
                if (handlers.TryGetValue(e.GetType(), out var typed))
                    foreach (var h in typed) Invoke(h, e);
                foreach (var h in all) Invoke(h, e);
            }
        }
        finally
        {
            _dispatching.Clear();
        }
        return count;
    }

    private void Invoke(Action<IGameEvent> handler, IGameEvent e)
    {
        try
        {
            handler(e);
        }
        catch (Exception ex)
        {
            _onHandlerError(e, ex);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
