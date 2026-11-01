namespace Game.Server.World;

/// <summary>
/// Something an entity's controller (a client session, an admin command, AI) asks the simulation to do. Implement as
/// an immutable <c>record</c>. Intents are requests, not results: the owning system validates them on the tick.
/// </summary>
public interface IIntent
{
    /// <summary>The entity the intent is for.</summary>
    EntityId Entity { get; }
}

/// <summary>
/// An intent that is still routed when its entity is <see cref="Entity.Dead"/> (every other intent of a dead entity is dropped and counted):
/// what a dead entity may still ask for, such as respawning.
/// </summary>
public interface IDeadEntityIntent : IIntent
{
}

/// <summary>Outcome of offering an intent to the world or an instance.</summary>
public enum EnqueueResult
{
    /// <summary>Queued for the instance's next tick.</summary>
    Accepted,

    /// <summary>The instance's queue was full; the intent was dropped. The caller may tell the client to slow down.</summary>
    QueueFull,

    /// <summary>The world does not know the entity (never existed or despawned); dropped.</summary>
    UnknownEntity,
}

/// <summary>
/// Bounded multi-producer, single-consumer queue of intents for one instance. Any thread may
/// <see cref="TryEnqueue"/>; only the instance's tick calls <see cref="DrainTo"/>, which takes everything enqueued so
/// far in arrival order. A short lock guards two swapped lists, so draining is O(1) and allocation-free; at 10 Hz and
/// thousands of intents per second the lock is uncontended in practice. When <see cref="Capacity"/> intents are
/// waiting, new ones are refused (<see cref="EnqueueResult.QueueFull"/>) and counted: the oldest work is kept, the
/// producer finds out at once, and memory stays bounded if a tick stalls.
/// </summary>
public sealed class IntentQueue
{
    /// <summary>Default per-instance bound: about 40 intents per player per tick for 100 players.</summary>
    public const int DefaultCapacity = 4096;

    private readonly object _lock = new();
    private List<IIntent> _incoming = new();
    private long _rejected;

    /// <summary>Creates a queue holding at most <paramref name="capacity"/> undrained intents.</summary>
    public IntentQueue(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>Maximum undrained intents.</summary>
    public int Capacity { get; }

    /// <summary>Intents refused because the queue was full, since creation.</summary>
    public long RejectedFull => Interlocked.Read(ref _rejected);

    /// <summary>Undrained intents right now.</summary>
    public int Count
    {
        get { lock (_lock) return _incoming.Count; }
    }

    /// <summary>Queues an intent unless the queue is full. Thread-safe.</summary>
    public EnqueueResult TryEnqueue(IIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        lock (_lock)
        {
            if (_incoming.Count >= Capacity)
            {
                _rejected++;
                return EnqueueResult.QueueFull;
            }
            _incoming.Add(intent);
            return EnqueueResult.Accepted;
        }
    }

    /// <summary>
    /// Moves every queued intent, in arrival order, into <paramref name="buffer"/> (which must be empty) and leaves the
    /// queue empty. Single reader only. The buffer becomes the queue's next incoming list, so the caller must be done
    /// with it (and clear it) before the next drain.
    /// </summary>
    internal List<IIntent> DrainTo(List<IIntent> buffer)
    {
        lock (_lock)
        {
            (buffer, _incoming) = (_incoming, buffer);
            return buffer;
        }
    }
}
