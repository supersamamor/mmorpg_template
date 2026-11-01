using Game.Server.Events;
using Game.Shared.Definitions;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Aoi;

/// <summary>
/// A system that also runs in tick phase 5 (outbound), after structural changes and event dispatch, when the instance's state for the
/// tick is final. <see cref="MapInstance"/> calls <see cref="Outbound"/> on every system that implements this, in registration order.
/// </summary>
public interface IOutboundPhase
{
    /// <summary>Runs phase 5 for one tick. Entities are as phases 1–4 left them; publishing events here queues them for the next tick.</summary>
    void Outbound(TickContext context);
}

/// <summary>Asks for a full snapshot of what the observer sees (map entry, resume after a disconnect). Handled on the tick, sent in phase 5.</summary>
public sealed record RequestSnapshot(EntityId Entity) : IIntent;

/// <summary>
/// Sets (or, with a null sink, clears) the entity's <see cref="Entity.View"/> on the tick, from any thread. Setting a new sink sends it a
/// snapshot in the same tick's phase 5.
/// </summary>
public sealed record AttachView(EntityId Entity, IViewSink? Sink) : IIntent;

/// <summary>
/// Area of interest (ARCHITECTURE §3): tells every observer (an entity with a <see cref="Entity.View"/>) what it can see, as a snapshot
/// on demand and as a delta after every tick in which something in its view changed. Owns <see cref="RequestSnapshot"/> and
/// <see cref="AttachView"/>; its work is in phase 5 (<see cref="Outbound"/>).
/// </summary>
/// <remarks>
/// <para>Bucket upkeep is a direct hook in phase 5 rather than event-driven: one pass over the instance's entities compares each with
/// what observers were last told (cell, facing, next cell, speed, display key) and moves it between <see cref="AoiGrid"/> buckets when its cell
/// crosses a boundary. That catches every change, including a system that turns an entity without an event, costs O(entities) per tick,
/// and needs no knowledge of which system moved what. Events are used only for what the entity does not carry: whether it is walking
/// and at what speed (<c>MovementStarted</c>, <c>CellReached</c>, <c>MovementStopped</c>), and same-map warps (<c>EntityEnteredMap</c>),
/// which give the warper a fresh snapshot.</para>
/// <para>Per observer, the window's buckets are checked for a change this tick; only then is the visible set gathered, sorted by id and
/// merged with the last one, so the cost is O(observers × window) for quiet views and O(observers × visible) for busy ones, never
/// O(entities × observers). Allocation after warm-up: none per tick (entries are created when an entity joins, observer state when an
/// observer starts; the messages and lists are reused).</para>
/// </remarks>
public sealed class AoiSystem : ITickSystem, IOutboundPhase
{
    private static readonly int MoveSpeedStat = StatIndex.IndexOf(DerivedStats.MoveSpeed);
    private static readonly IReadOnlyCollection<Type> Owned = [typeof(RequestSnapshot), typeof(AttachView)];

    private readonly int _bucketSize;
    private readonly int _viewRadius;
    private readonly Dictionary<long, AoiEntry> _byId = new();
    private readonly List<AoiEntry> _entries = new();       // sorted by entity id
    private readonly List<EntityId> _snapshotIds = new();
    private readonly ViewSnapshot _snapshot = new();
    private readonly ViewDelta _delta = new();
    private AoiGrid? _grid;
    private EventBus? _subscribed;

    /// <summary>Creates the AOI with a bucket side and view radius (both above 0).</summary>
    public AoiSystem(int bucketSize, int viewRadius)
    {
        if (bucketSize < 1) throw new ArgumentOutOfRangeException(nameof(bucketSize), "Bucket size must be at least 1.");
        if (viewRadius < 1) throw new ArgumentOutOfRangeException(nameof(viewRadius), "View radius must be at least 1.");
        _bucketSize = bucketSize;
        _viewRadius = viewRadius;
    }

    /// <summary>Creates the AOI with the numbers of <c>balance.yaml</c> <c>aoi</c>.</summary>
    public static AoiSystem Create(BalanceDef balance) => new(balance.Aoi.BucketSize, balance.Aoi.ViewRadius);

    /// <inheritdoc />
    public string Name => "aoi";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>Entities with a sink, as of the last phase 5.</summary>
    public int ObserverCount { get; private set; }

    /// <summary>Snapshots handed to sinks.</summary>
    public long SnapshotsSent { get; private set; }

    /// <summary>Deltas handed to sinks.</summary>
    public long DeltasSent { get; private set; }

    /// <summary>Sinks that threw and were detached.</summary>
    public long SinksDetached { get; private set; }

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        Subscribe(context.Instance);
        var intents = context.Intents;
        for (var i = 0; i < intents.Count; i++)
        {
            var intent = intents[i];
            if (intent is RequestSnapshot) _snapshotIds.Add(intent.Entity);
            else if (intent is AttachView attach && context.Instance.TryGetEntity(attach.Entity, out var entity)) entity.View = attach.Sink;
        }
    }

    /// <inheritdoc />
    public void Outbound(TickContext context)
    {
        var instance = context.Instance;
        var tick = context.TickNumber;
        Subscribe(instance);
        if (_grid is null || !ReferenceEquals(_grid.Grid, instance.Grid)) _grid = new AoiGrid(instance.Grid, _bucketSize, _viewRadius);

        Sweep(instance, tick);

        foreach (var id in _snapshotIds)
            if (_byId.TryGetValue(id.Value, out var entry) && entry.Observer is { } observer) observer.SnapshotPending = true;
        _snapshotIds.Clear();

        var observers = 0;
        foreach (var entry in _entries)
        {
            if (entry.Observer is null) continue;
            observers++;
            Send(context, entry, tick);
        }
        ObserverCount = observers;
    }

    private void Subscribe(MapInstance instance)
    {
        if (ReferenceEquals(_subscribed, instance.Events)) return;
        _subscribed = instance.Events;
        _subscribed.Subscribe<MovementStarted>(OnMovementStarted);
        _subscribed.Subscribe<CellReached>(OnCellReached);
        _subscribed.Subscribe<MovementStopped>(OnMovementStopped);
        _subscribed.Subscribe<EntityEnteredMap>(OnEnteredMap);
    }

    private void OnMovementStarted(MovementStarted e)
    {
        if (!_byId.TryGetValue(e.Entity.Value, out var entry)) return;   // moving entities joined in an earlier tick, so they are known
        entry.Walking = true;
        entry.WalkSpeed = SpeedOf(entry.Entity);
    }

    private void OnCellReached(CellReached e)
    {
        if (_byId.TryGetValue(e.Entity.Value, out var entry) && entry.Walking) entry.WalkSpeed = SpeedOf(entry.Entity);
    }

    private void OnMovementStopped(MovementStopped e)
    {
        if (_byId.TryGetValue(e.Entity.Value, out var entry)) entry.Walking = false;
    }

    private void OnEnteredMap(EntityEnteredMap e)
    {
        // A warp to another cell of this map leaves the entity known here; give the warper a fresh view. Arrivals from another map are new
        // entries, which get a snapshot anyway.
        if (e.Reason == MapChangeReason.Warp) _snapshotIds.Add(e.Entity);
    }

    private static double SpeedOf(Entity entity) => entity.Stats?.Get(MoveSpeedStat) ?? 0;

    /// <summary>Brings every entry up to date with its entity, buckets included; adds newcomers and drops entities that left.</summary>
    private void Sweep(MapInstance instance, long tick)
    {
        var grid = _grid!;
        var entities = instance.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (!_byId.TryGetValue(entity.Id.Value, out var entry))
            {
                entry = new AoiEntry(entity);
                Read(entry, out entry.DisplayKey, out entry.Cell, out entry.Facing, out entry.Next, out entry.Speed);
                _byId.Add(entry.Id, entry);
                InsertSorted(entry);
                grid.Add(entry, grid.BucketOf(entry.Cell), tick);
            }
            else
            {
                Update(grid, entry, tick);
            }
            entry.SeenTick = tick;
            SyncObserver(entry);
        }

        if (_entries.Count == entities.Count) return;
        var kept = 0;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.SeenTick == tick)
            {
                _entries[kept++] = entry;
                continue;
            }
            grid.Remove(entry, tick);
            _byId.Remove(entry.Id);
            entry.Observer = null;
        }
        _entries.RemoveRange(kept, _entries.Count - kept);
    }

    private static void Read(AoiEntry entry, out string? displayKey, out GridCell cell, out string facing, out GridCell? next, out double speed)
    {
        var entity = entry.Entity;
        displayKey = entity.DefinitionId;
        cell = entity.Cell;
        facing = entity.Facing;
        next = null;
        speed = 0;
        if (entry.Walking && GridDirection.TryGetStep(facing, out var dx, out var dy))
        {
            next = new GridCell(cell.X + dx, cell.Y + dy);
            speed = entry.WalkSpeed;
        }
    }

    private static void Update(AoiGrid grid, AoiEntry entry, long tick)
    {
        Read(entry, out var displayKey, out var cell, out var facing, out var next, out var speed);
        var changed = ViewFields.None;
        if (!string.Equals(displayKey, entry.DisplayKey, StringComparison.Ordinal)) changed |= ViewFields.DisplayKey;
        if (!Same(cell, entry.Cell)) changed |= ViewFields.Cell;
        if (!string.Equals(facing, entry.Facing, StringComparison.Ordinal)) changed |= ViewFields.Facing;
        if (next.HasValue != entry.Next.HasValue || (next.HasValue && !Same(next.Value, entry.Next!.Value))) changed |= ViewFields.Next;
        if (speed != entry.Speed) changed |= ViewFields.Speed;
        if (changed == ViewFields.None) return;

        entry.DisplayKey = displayKey;
        entry.Cell = cell;
        entry.Facing = facing;
        entry.Next = next;
        entry.Speed = speed;
        entry.Changed = changed;
        entry.ChangedTick = tick;

        var bucket = grid.BucketOf(cell);
        if (bucket == entry.Bucket)
        {
            grid.Touch(bucket, tick);
            return;
        }
        grid.Remove(entry, tick);
        grid.Add(entry, bucket, tick);
    }

    private static bool Same(GridCell a, GridCell b) => a.X == b.X && a.Y == b.Y;

    private static void SyncObserver(AoiEntry entry)
    {
        var sink = entry.Entity.View;
        if (sink is null) entry.Observer = null;
        else if (entry.Observer is null) entry.Observer = new ObserverState(sink);
        else if (!ReferenceEquals(entry.Observer.Sink, sink))
        {
            entry.Observer.Sink = sink;
            entry.Observer.SnapshotPending = true;
        }
    }

    private void Send(TickContext context, AoiEntry entry, long tick)
    {
        var grid = _grid!;
        var observer = entry.Observer!;
        if (!observer.SnapshotPending && entry.Bucket == observer.WindowBucket && !grid.WindowTouched(entry.Bucket, tick)) return;

        observer.WindowBucket = entry.Bucket;
        var visible = observer.Scratch;
        visible.Clear();
        grid.Gather(entry.Bucket, visible);
        visible.Sort(ById);   // a cached Comparison: Sort(IComparer) would allocate a delegate per call
        var known = observer.Known;
        observer.Known = visible;
        observer.Scratch = known;

        if (observer.SnapshotPending)
        {
            observer.SnapshotPending = false;
            var snapshot = _snapshot;
            snapshot.Tick = tick;
            snapshot.MapId = context.Instance.MapId;
            snapshot.Observer = entry.Entity.Id;
            snapshot.EntityList.Clear();
            foreach (var seen in visible) snapshot.EntityList.Add(seen.ToView());
            if (Deliver(context, entry, snapshot, null)) SnapshotsSent++;
            return;
        }

        var delta = _delta;
        delta.Clear();
        int i = 0, j = 0;
        while (i < known.Count || j < visible.Count)
        {
            var before = i < known.Count ? known[i] : null;
            var now = j < visible.Count ? visible[j] : null;
            if (now is null || (before is not null && before.Id < now.Id))
            {
                delta.DisappearedList.Add(before!.Entity.Id);
                i++;
            }
            else if (before is null || now.Id < before.Id)
            {
                delta.AppearedList.Add(now.ToView());
                j++;
            }
            else
            {
                if (now.ChangedTick == tick) delta.ChangedList.Add(new EntityChange(now.Changed, now.ToView()));
                i++;
                j++;
            }
        }
        if (delta.IsEmpty) return;
        delta.Tick = tick;
        delta.MapId = context.Instance.MapId;
        delta.Observer = entry.Entity.Id;
        if (Deliver(context, entry, null, delta)) DeltasSent++;
    }

    private bool Deliver(TickContext context, AoiEntry entry, ViewSnapshot? snapshot, ViewDelta? delta)
    {
        try
        {
            if (snapshot is not null) entry.Observer!.Sink.OnSnapshot(snapshot);
            else entry.Observer!.Sink.OnDelta(delta!);
            return true;
        }
        catch (Exception ex)
        {
            SinksDetached++;
            entry.Entity.View = null;
            entry.Observer = null;
            context.Log.Error($"[{context.Instance.MapId}] tick {context.TickNumber}: the view sink of {entry.Entity.Id} threw; detached it.", ex);
            return false;
        }
    }

    private void InsertSorted(AoiEntry entry)
    {
        var index = _entries.BinarySearch(entry, EntryOrder.Instance);
        _entries.Insert(index < 0 ? ~index : index, entry);
    }

    private static readonly Comparison<AoiEntry> ById = (x, y) => x.Id.CompareTo(y.Id);

    private sealed class EntryOrder : IComparer<AoiEntry>
    {
        public static readonly EntryOrder Instance = new();

        public int Compare(AoiEntry? x, AoiEntry? y) => x!.Id.CompareTo(y!.Id);
    }
}
