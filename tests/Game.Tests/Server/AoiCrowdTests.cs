using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Shared.World;

namespace Game.Tests.Server;

/// <summary>
/// T-213: a crowd. Many observers and walkers cross bucket boundaries at once, and after every tick each observer's view (rebuilt from
/// the snapshot and deltas it was sent) equals a brute-force "every entity inside the window" set computed from the entities' cells.
/// </summary>
public class AoiCrowdTests
{
    private const int BucketSize = 4;
    private const int ViewRadius = 1;
    private const int Width = 48;
    private const int Height = 32;

    [Theory]
    [InlineData(11)]
    [InlineData(2024)]
    public void Every_observer_sees_exactly_the_entities_inside_its_window_after_every_tick(int seed)
    {
        var rig = new AoiRig(BucketSize, ViewRadius, Width, Height, "map.a");
        var random = new Random(seed);
        GridCell Random() => new(random.Next(Width), random.Next(Height));

        var observers = Enumerable.Range(0, 12).Select(_ => rig.AddObserver(Random(), "map.a")).ToList();
        var walkers = Enumerable.Range(0, 60).Select(_ => rig.Add(Random(), "map.a")).ToList();
        var views = observers.ToDictionary(o => o.Id, _ => new Dictionary<EntityId, GridCell>());
        var seen = observers.ToDictionary(o => o.Id, _ => (Snapshots: 0, Deltas: 0));
        var bucketCrossings = 0;
        var lastBucket = new Dictionary<EntityId, (int, int)>();

        for (var tick = 0; tick < 400; tick++)
        {
            if (tick % 12 == 0)
                foreach (var id in observers.Select(o => o.Id).Concat(walkers))
                    rig.Send(new MoveTo(id, Random()));   // long walks: the crowd keeps crossing boundaries
            rig.Ticks();

            foreach (var (id, sink) in observers)
            {
                Apply(views[id], sink, seen, id);
                var observer = rig.Get(id);
                var bucket = (observer.Cell.X / BucketSize, observer.Cell.Y / BucketSize);
                if (lastBucket.TryGetValue(id, out var before) && before != bucket) bucketCrossings++;
                lastBucket[id] = bucket;

                var expected = rig.A.Entities
                    .Where(e => InWindow(observer.Cell, e.Cell))
                    .ToDictionary(e => e.Id, e => e.Cell);
                views[id].OrderBy(p => p.Key.Value).ShouldBe(expected.OrderBy(p => p.Key.Value), $"tick {tick}, observer {id}");
            }
        }

        bucketCrossings.ShouldBeGreaterThan(40, "the test should make observers cross many bucket boundaries");
        rig.Log.Lines.Where(l => l.StartsWith("ERROR", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private static bool InWindow(GridCell observer, GridCell cell)
    {
        // The window is the observer's bucket and its neighbours within ViewRadius buckets, clamped to the map by construction.
        var bx = observer.X / BucketSize;
        var by = observer.Y / BucketSize;
        return Math.Abs(cell.X / BucketSize - bx) <= ViewRadius && Math.Abs(cell.Y / BucketSize - by) <= ViewRadius;
    }

    /// <summary>Applies the messages the sink got since the last call to the observer's view of the world.</summary>
    private static void Apply(Dictionary<EntityId, GridCell> view, RecordingSink sink, Dictionary<EntityId, (int Snapshots, int Deltas)> seen, EntityId id)
    {
        var (snapshots, deltas) = seen[id];
        for (; snapshots < sink.Snapshots.Count; snapshots++)
        {
            view.Clear();
            foreach (var e in sink.Snapshots[snapshots].Entities) view[e.Id] = e.Cell;
        }
        for (; deltas < sink.Deltas.Count; deltas++)
        {
            var delta = sink.Deltas[deltas];
            foreach (var e in delta.Appeared) view[e.Id] = e.Cell;
            foreach (var c in delta.Changed) view[c.View.Id] = c.View.Cell;
            foreach (var gone in delta.Disappeared) view.Remove(gone);
        }
        seen[id] = (snapshots, deltas);
    }
}
