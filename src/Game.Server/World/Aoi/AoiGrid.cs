using Game.Shared.World;

namespace Game.Server.World.Aoi;

/// <summary>
/// The AOI buckets of one map: square blocks of <c>bucket_size</c> cells, each holding the entities whose cell lies in it. An
/// observer's <em>window</em> is every bucket within <c>view_radius</c> buckets (Chebyshev) of its own. Each bucket remembers the last
/// tick in which anything in it changed (an entity came, left, or changed what observers see), so an observer whose window is quiet
/// costs a few array reads per tick. Membership changes are O(1) (swap-remove). Single-threaded, like its instance.
/// </summary>
internal sealed class AoiGrid
{
    private readonly int _bucketSize;
    private readonly int _viewRadius;
    private readonly List<AoiEntry>[] _members;
    private readonly long[] _touched;

    public AoiGrid(MapGrid grid, int bucketSize, int viewRadius)
    {
        Grid = grid;
        _bucketSize = bucketSize;
        _viewRadius = viewRadius;
        Columns = (grid.Width + bucketSize - 1) / bucketSize;
        Rows = (grid.Height + bucketSize - 1) / bucketSize;
        _members = new List<AoiEntry>[Columns * Rows];
        _touched = new long[Columns * Rows];
        for (var i = 0; i < _members.Length; i++)
        {
            _members[i] = new List<AoiEntry>();
            _touched[i] = -1;
        }
    }

    public MapGrid Grid { get; }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>The bucket holding a cell. Cells off the map (never expected) are clamped to the edge buckets.</summary>
    public int BucketOf(GridCell cell)
    {
        var bx = Math.Clamp(cell.X / _bucketSize, 0, Columns - 1);
        var by = Math.Clamp(cell.Y / _bucketSize, 0, Rows - 1);
        return by * Columns + bx;
    }

    public void Add(AoiEntry entry, int bucket, long tick)
    {
        var list = _members[bucket];
        entry.Bucket = bucket;
        entry.Slot = list.Count;
        list.Add(entry);
        _touched[bucket] = tick;
    }

    public void Remove(AoiEntry entry, long tick)
    {
        var list = _members[entry.Bucket];
        var last = list[^1];
        list[entry.Slot] = last;
        last.Slot = entry.Slot;
        list.RemoveAt(list.Count - 1);
        _touched[entry.Bucket] = tick;
        entry.Bucket = -1;
    }

    public void Touch(int bucket, long tick) => _touched[bucket] = tick;

    /// <summary>True when a bucket of the window around <paramref name="bucket"/> changed in <paramref name="tick"/>.</summary>
    public bool WindowTouched(int bucket, long tick)
    {
        Window(bucket, out var x0, out var y0, out var x1, out var y1);
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
            if (_touched[y * Columns + x] == tick) return true;
        return false;
    }

    /// <summary>Appends every entity in the window around <paramref name="bucket"/> to <paramref name="into"/> (in no particular order).</summary>
    public void Gather(int bucket, List<AoiEntry> into)
    {
        Window(bucket, out var x0, out var y0, out var x1, out var y1);
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
            into.AddRange(_members[y * Columns + x]);
    }

    private void Window(int bucket, out int x0, out int y0, out int x1, out int y1)
    {
        var bx = bucket % Columns;
        var by = bucket / Columns;
        x0 = Math.Max(0, bx - _viewRadius);
        y0 = Math.Max(0, by - _viewRadius);
        x1 = Math.Min(Columns - 1, bx + _viewRadius);
        y1 = Math.Min(Rows - 1, by + _viewRadius);
    }
}

/// <summary>The AOI's record of one entity of its instance: where it is bucketed and what observers were last told about it.</summary>
internal sealed class AoiEntry
{
    public AoiEntry(Entity entity)
    {
        Entity = entity;
        Id = entity.Id.Value;
    }

    public readonly Entity Entity;
    public readonly long Id;

    public int Bucket = -1;
    public int Slot;
    public long SeenTick;

    // What observers see (the last published state).
    public string? DisplayKey;
    public GridCell Cell;
    public string Facing = string.Empty;
    public GridCell? Next;
    public double Speed;

    // Fields that changed in ChangedTick.
    public long ChangedTick = -1;
    public ViewFields Changed;

    // Walking state, from the movement events: speed sampled where the movement system reads it (move start, cell reached).
    public bool Walking;
    public double WalkSpeed;

    public ObserverState? Observer;

    public EntityView ToView() => new(Entity.Id, Entity.Kind, DisplayKey, Cell, Facing, Next, Speed);
}

/// <summary>What one observer currently knows: the entities of its last snapshot or delta, by entity id.</summary>
internal sealed class ObserverState
{
    public ObserverState(IViewSink sink) => Sink = sink;

    public IViewSink Sink;
    public List<AoiEntry> Known = new();
    public List<AoiEntry> Scratch = new();
    public int WindowBucket = -1;
    public bool SnapshotPending = true;
}
