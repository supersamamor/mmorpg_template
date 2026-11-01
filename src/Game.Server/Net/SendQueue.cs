namespace Game.Server.Net;

/// <summary>
/// One session's bounded queue of serialized messages waiting to be sent (the back-pressure point between the tick threads and the
/// socket). Producers are tick threads (snapshots, deltas, rejections) and the session's receive loop (pongs); the single consumer is the
/// session's send loop. Posting never blocks.
/// </summary>
/// <remarks>
/// When the queue is full a new delta is not queued: every queued snapshot and delta is dropped (they would only be replaced), and the
/// session enters <em>resync</em>, in which further deltas are dropped too, since they are relative to a state the client will never
/// see. Once the send loop has drained the queue down to <c>resyncBelow</c> messages, <see cref="TryTake"/> tells it to ask the AOI for a
/// fresh snapshot (<c>RequestSnapshot</c>); the snapshot ends the resync. A snapshot always replaces queued views. Control messages
/// (welcome, rejected, pong) are never dropped for room; if they alone overflow twice the capacity the client is hopeless and the post
/// fails, and the session is closed (<c>too_slow</c>), as it is when it stays in resync too long (<see cref="BehindSinceMs"/>).
/// </remarks>
public sealed class SendQueue
{
    private readonly object _lock = new();
    private readonly LinkedList<Item> _items = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly int _capacity;
    private readonly int _resyncBelow;
    private bool _resyncing;
    private bool _resyncRequested;
    private long _behindSinceMs;
    private bool _completed;

    /// <summary>Creates a queue of <paramref name="capacity"/> messages that asks for a resync snapshot below <paramref name="resyncBelow"/>.</summary>
    public SendQueue(int capacity, int resyncBelow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(resyncBelow);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(resyncBelow, capacity);
        _capacity = capacity;
        _resyncBelow = resyncBelow;
    }

    private enum Kind
    {
        Control,
        Snapshot,
        Delta,
    }

    private readonly record struct Item(Kind Kind, byte[] Bytes);

    /// <summary>Messages waiting.</summary>
    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    /// <summary>True while deltas are being dropped until a fresh snapshot.</summary>
    public bool Resyncing
    {
        get { lock (_lock) return _resyncing; }
    }

    /// <summary>When the current resync began (gateway clock), or null when not resyncing.</summary>
    public long? BehindSinceMs
    {
        get { lock (_lock) return _resyncing ? _behindSinceMs : null; }
    }

    /// <summary>True once the final message was posted: nothing more is accepted.</summary>
    public bool Completed
    {
        get { lock (_lock) return _completed; }
    }

    /// <summary>Deltas dropped (queued ones thrown away on overflow, and ones refused while resyncing).</summary>
    public long DeltasDropped { get; private set; }

    /// <summary>Times the queue overflowed and started a resync.</summary>
    public long Resyncs { get; private set; }

    /// <summary>Queues a snapshot, replacing every queued snapshot and delta, and ends a resync. False only after <see cref="PostFinal"/>.</summary>
    public bool PostSnapshot(byte[] bytes)
    {
        lock (_lock)
        {
            if (_completed) return false;
            DropViews();
            _items.AddLast(new Item(Kind.Snapshot, bytes));
            _resyncing = false;
            _resyncRequested = false;
        }
        _signal.Release();
        return true;
    }

    /// <summary>
    /// Queues a delta, or drops it while resyncing; on overflow drops the queued views and starts a resync at <paramref name="nowMs"/>.
    /// Returns true when the delta was queued.
    /// </summary>
    public bool PostDelta(byte[] bytes, long nowMs)
    {
        var queued = false;
        lock (_lock)
        {
            if (_completed) return false;
            if (_resyncing)
            {
                DeltasDropped++;
                return false;
            }
            if (_items.Count >= _capacity)
            {
                DeltasDropped += DropViews() + 1;
                _resyncing = true;
                _resyncRequested = false;
                _behindSinceMs = nowMs;
                Resyncs++;
            }
            else
            {
                _items.AddLast(new Item(Kind.Delta, bytes));
                queued = true;
            }
        }
        _signal.Release(); // also on overflow, so an idle consumer notices the resync
        return queued;
    }

    /// <summary>Queues a message that must not be dropped. False when the queue is hopelessly full (the caller closes the session) or completed.</summary>
    public bool PostControl(byte[] bytes)
    {
        lock (_lock)
        {
            if (_completed || _items.Count >= _capacity * 2) return false;
            _items.AddLast(new Item(Kind.Control, bytes));
        }
        _signal.Release();
        return true;
    }

    /// <summary>Queues the last message (<c>bye</c>) after the control messages already waiting; views are dropped. Only the first call counts.</summary>
    public bool PostFinal(byte[] bytes)
    {
        lock (_lock)
        {
            if (_completed) return false;
            DropViews();
            _items.AddLast(new Item(Kind.Control, bytes));
            _completed = true;
        }
        _signal.Release();
        return true;
    }

    /// <summary>
    /// Takes the oldest message. <paramref name="requestSnapshot"/> is true once per resync, when the queue has drained enough that the
    /// caller should ask for the resync snapshot now.
    /// </summary>
    public bool TryTake(out byte[] bytes, out bool requestSnapshot)
    {
        lock (_lock)
        {
            requestSnapshot = false;
            if (_resyncing && !_resyncRequested && _items.Count <= _resyncBelow)
            {
                _resyncRequested = true;
                requestSnapshot = true;
            }
            if (_items.First is not { } first)
            {
                bytes = [];
                return false;
            }
            _items.RemoveFirst();
            bytes = first.Value.Bytes;
            return true;
        }
    }

    /// <summary>The resync snapshot could not be requested (the intent queue was full): ask again on the next take.</summary>
    public void ResyncRequestFailed()
    {
        lock (_lock) _resyncRequested = false;
    }

    /// <summary>Waits until something may have been posted (spurious wake-ups are possible: call <see cref="TryTake"/> in a loop).</summary>
    public Task WaitAsync(CancellationToken cancel) => _signal.WaitAsync(cancel);

    /// <summary>Wakes the consumer without posting (e.g. so it notices a resync that needs requesting).</summary>
    public void Wake() => _signal.Release();

    private int DropViews()
    {
        var deltas = 0;
        for (var node = _items.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.Kind != Kind.Control)
            {
                if (node.Value.Kind == Kind.Delta) deltas++;
                _items.Remove(node);
            }
            node = next;
        }
        return deltas;
    }
}
