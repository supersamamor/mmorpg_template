using Game.Shared.World;

namespace Game.Server.World.Monsters;

/// <summary>
/// Where the players of one instance are, bucketed like the AOI (squares of <c>aoi.bucket_size</c> cells), rebuilt once per AI tick from
/// <see cref="MapInstance.Players"/>, so its cost is O(players) and never O(entities). Answers the AI's two questions: is any player close
/// enough to see this cell (the AOI window of <c>aoi.view_radius</c> buckets: the monster is awake), and which player is nearest within a
/// range. The AI's own copy: it does not reach into the AOI system, whose buckets are updated only in phase 5 and hold every entity.
/// No allocation after the first rebuild that sees a given bucket in use.
/// </summary>
internal sealed class PlayerGrid
{
    private readonly int _bucketSize;
    private readonly int _viewRadius;
    private readonly int _columns;
    private readonly int _rows;
    private readonly List<Entity>?[] _buckets;
    private readonly long[] _awake;      // == _stamp when a player's view window covers the bucket
    private readonly List<int> _used = new();
    private long _stamp;

    public PlayerGrid(MapGrid grid, int bucketSize, int viewRadius)
    {
        Grid = grid;
        _bucketSize = bucketSize;
        _viewRadius = viewRadius;
        _columns = (grid.Width + bucketSize - 1) / bucketSize;
        _rows = (grid.Height + bucketSize - 1) / bucketSize;
        _buckets = new List<Entity>?[_columns * _rows];
        _awake = new long[_columns * _rows];
    }

    public MapGrid Grid { get; }

    /// <summary>Players indexed by the last <see cref="Rebuild"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Re-buckets the living players at their current cells and marks the buckets they can see.</summary>
    public void Rebuild(IReadOnlyList<Entity> players)
    {
        foreach (var index in _used) _buckets[index]!.Clear();
        _used.Clear();
        _stamp++;
        Count = 0;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player.Dead) continue;
            var bx = Column(player.Cell.X);
            var by = Row(player.Cell.Y);
            var index = by * _columns + bx;
            var bucket = _buckets[index] ??= new List<Entity>();
            if (bucket.Count == 0) _used.Add(index);
            bucket.Add(player);
            Count++;

            for (var y = Math.Max(0, by - _viewRadius); y <= Math.Min(_rows - 1, by + _viewRadius); y++)
            for (var x = Math.Max(0, bx - _viewRadius); x <= Math.Min(_columns - 1, bx + _viewRadius); x++)
                _awake[y * _columns + x] = _stamp;
        }
    }

    /// <summary>True when some player's AOI window covers <paramref name="cell"/>'s bucket, i.e. a player could be seeing it.</summary>
    public bool IsAwake(GridCell cell) => _awake[Row(cell.Y) * _columns + Column(cell.X)] == _stamp;

    /// <summary>
    /// The living player nearest to <paramref name="from"/> (Chebyshev) within <paramref name="range"/> cells that <paramref name="from"/> has
    /// line of sight to; ties go to the lower entity id. Null when there is none. Looks only at the buckets the range touches.
    /// </summary>
    public Entity? Nearest(GridCell from, int range)
    {
        if (range < 0 || Count == 0) return null;
        Entity? best = null;
        var bestDistance = int.MaxValue;
        for (var by = Row(from.Y - range); by <= Row(from.Y + range); by++)
        for (var bx = Column(from.X - range); bx <= Column(from.X + range); bx++)
        {
            var bucket = _buckets[by * _columns + bx];
            if (bucket is null) continue;
            foreach (var player in bucket)
            {
                var distance = GridDistance.Chebyshev(from, player.Cell);
                if (distance > range || distance > bestDistance) continue;
                if (distance == bestDistance && player.Id.Value > best!.Id.Value) continue;
                if (!LineOfSight.HasLineOfSight(Grid, from, player.Cell)) continue;
                best = player;
                bestDistance = distance;
            }
        }
        return best;
    }

    private int Column(int x) => Math.Clamp(x, 0, Grid.Width - 1) / _bucketSize;

    private int Row(int y) => Math.Clamp(y, 0, Grid.Height - 1) / _bucketSize;
}
