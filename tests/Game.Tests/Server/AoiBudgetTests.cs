using System.Diagnostics;
using Game.Server.Events;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;
using Xunit.Abstractions;

namespace Game.Tests.Server;

/// <summary>
/// T-204 budget checks (ARCHITECTURE §9): bytes per second for one observer with ~50 visible walkers, and the outbound phase's cost
/// with 500 entities and 100 observers. Both use the sample <c>balance.yaml</c> <c>aoi</c> numbers. They log; they do not assert wall time.
/// </summary>
public class AoiBudgetTests(ITestOutputHelper output)
{
    /// <summary>Wraps the AOI to time and count allocations of phase 5 alone.</summary>
    private sealed class TimedAoi(AoiSystem inner) : ITickSystem, IOutboundPhase
    {
        public readonly List<double> Micros = new();
        public readonly List<long> Bytes = new();
        public bool Measure;

        public AoiSystem Inner => inner;

        public string Name => inner.Name;

        public IReadOnlyCollection<Type> IntentTypes => inner.IntentTypes;

        public void Tick(TickContext context) => inner.Tick(context);

        public void Outbound(TickContext context)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            inner.Outbound(context);
            var elapsed = Stopwatch.GetElapsedTime(start);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (!Measure) return;
            Micros.Add(elapsed.TotalMilliseconds * 1000);
            Bytes.Add(bytes);
        }
    }

    /// <summary>One open map with movement + timed AOI; walkers pick a new random target in their own area whenever they stop.</summary>
    private sealed class Field
    {
        public readonly GameWorld World;
        public readonly MapInstance Map;
        public readonly TimedAoi Aoi;
        private readonly Random _random = new(99);
        private readonly List<EntityId> _idle = new();
        private readonly Dictionary<EntityId, GridRect> _areas = new();
        private readonly Dictionary<EntityId, (IViewSink? Sink, string? Key, int Agi)> _setup = new();

        public Field(int width, int height)
        {
            var balance = TestGrids.SampleContent.Balance;
            TimedAoi? timed = null;
            var pipeline = new SystemPipeline()
                .Add(() => MovementSystem.Create(balance))
                .Add(() => timed = new TimedAoi(AoiSystem.Create(balance)));
            World = new GameWorld([AoiRig.Open("map.field", width, height)], pipeline, 5, new ManualClock(), new ListLog());
            Map = World.Instances.Single();
            Aoi = timed!;
            Map.Events.Subscribe<EntityEnteredMap>(e =>
            {
                if (!_setup.Remove(e.Entity, out var s) || !Map.TryGetEntity(e.Entity, out var entity)) return;
                entity.View = s.Sink;
                entity.DefinitionId = s.Key;
                entity.Stats = AoiRig.Sheet(s.Agi);
                if (_areas.ContainsKey(e.Entity)) _idle.Add(e.Entity);   // its first move goes out once it is on the map
            });
            Map.Events.Subscribe<MovementStopped>(e => _idle.Add(e.Entity));
            Map.Events.Subscribe<MoveRejected>(e => _idle.Add(e.Entity));
        }

        /// <summary>Adds an entity in <paramref name="area"/>; a walker wanders inside it at 3.5-6 cells per second.</summary>
        public EntityId Add(GridRect area, bool walks, IViewSink? sink = null, EntityKind kind = EntityKind.Monster, string? key = "monster.sample")
        {
            var id = World.Spawn(kind, Map.MapId, Pick(area), Directions.S);
            _setup[id] = (sink, kind == EntityKind.Player ? null : key, _random.Next(250));
            if (!walks) return id;
            _areas[id] = area;
            return id;
        }

        public void Tick()
        {
            foreach (var id in _idle)
                if (_areas.TryGetValue(id, out var area)) World.TryEnqueue(new MoveTo(id, Pick(area)));
            _idle.Clear();
            Map.Tick();
        }

        /// <summary>A random <paramref name="side"/>-cell square inside the given rectangle.</summary>
        public GridRect Square(int x, int y, int w, int h, int side) =>
            new(x + _random.Next(w - side + 1), y + _random.Next(h - side + 1), side, side);

        private GridCell Pick(GridRect area) => new(area.X + _random.Next(area.W), area.Y + _random.Next(area.H));
    }

    /// <summary>Serializes every message with the stand-in JSON and keeps totals.</summary>
    private sealed class JsonSink : IViewSink
    {
        public long Bytes;
        public long ShortBytes;
        public long Messages;
        public long Snapshots;
        public int MaxVisible;
        public bool Counting;
        public string Sample = string.Empty;

        public void OnSnapshot(ViewSnapshot snapshot)
        {
            MaxVisible = Math.Max(MaxVisible, snapshot.Entities.Count);
            Count(ViewJson.Serialize(snapshot), ViewJson.Serialize(snapshot, shortKeys: true));
            if (Counting) Snapshots++;
        }

        public void OnDelta(ViewDelta delta)
        {
            var json = ViewJson.Serialize(delta);
            if (Sample.Length == 0 && delta.Changed.Count > 1) Sample = ViewJson.Text(json);
            Count(json, ViewJson.Serialize(delta, shortKeys: true));
        }

        private void Count(byte[] json, byte[] compact)
        {
            if (!Counting) return;
            Bytes += json.Length;
            ShortBytes += compact.Length;
            Messages++;
        }
    }

    [Fact]
    public void Bytes_per_second_for_one_observer_with_fifty_visible_walkers()
    {
        // 96x96 map, 6-cell buckets, radius 2: the observer at (48, 48) is in bucket (8, 8) and sees buckets 6-10, cells 36-65.
        var field = new Field(96, 96);
        var sink = new JsonSink();
        var observer = field.Add(new GridRect(48, 48, 1, 1), walks: false, sink: sink, kind: EntityKind.Player);
        var view = new GridRect(37, 37, 28, 28);
        for (var i = 0; i < 49; i++) field.Add(view, walks: true);
        for (var i = 0; i < 300; i++) field.Add(field.Square(0, 0, 30, 96, 30), walks: true);   // buckets 0-4: never visible
        field.Tick();
        for (var i = 0; i < 50; i++) field.Tick();   // warm-up

        sink.Counting = true;
        const int ticks = 600;   // 60 s of simulated time
        for (var i = 0; i < ticks; i++) field.Tick();
        field.World.TryEnqueue(new RequestSnapshot(observer));
        field.Tick();
        var seconds = (ticks + 1) / (double)TickRate.Hz;

        var perSecond = sink.Bytes / seconds;
        var shortPerSecond = sink.ShortBytes / seconds;
        output.WriteLine($"visible entities (incl. observer): {sink.MaxVisible}; others on the map: 300, all walking 3.5-6 cells/s, nonstop");
        output.WriteLine($"messages: {sink.Messages} in {seconds:F1} s ({sink.Messages / seconds:F1}/s), snapshots: {sink.Snapshots}");
        output.WriteLine($"stand-in JSON, full keys:   {perSecond:F0} B/s ({perSecond / 1024:F2} KB/s), {sink.Bytes / (double)sink.Messages:F0} B/message");
        output.WriteLine($"stand-in JSON, 1-char keys: {shortPerSecond:F0} B/s ({shortPerSecond / 1024:F2} KB/s)");
        output.WriteLine("budget (ARCHITECTURE §9): 10 KB/s per client with 50 visible entities; before WebSocket framing, TLS and compression");
        output.WriteLine("sample delta: " + sink.Sample);

        sink.MaxVisible.ShouldBe(50);
        sink.Snapshots.ShouldBe(1);
    }

    [Fact]
    public void Outbound_cost_with_five_hundred_entities_and_a_hundred_observers()
    {
        // 192x192 map (32x32 buckets): 100 observers and 400 monsters spread over the map, each wandering in its own 40x40 square
        // (paths stay under movement.max_path_length).
        var field = new Field(192, 192);
                var sinks = new List<CountingSink>();
        for (var i = 0; i < 100; i++)
        {
            var sink = new CountingSink();
            sinks.Add(sink);
            field.Add(field.Square(0, 0, 192, 192, 40), walks: true, sink: sink, kind: EntityKind.Player);
        }
        for (var i = 0; i < 400; i++) field.Add(field.Square(0, 0, 192, 192, 40), walks: true);
        for (var i = 0; i < 100; i++) field.Tick();   // warm-up: JIT, lists reach their working size

        field.Aoi.Measure = true;
        const int ticks = 300;
        for (var i = 0; i < ticks; i++) field.Tick();

        var micros = field.Aoi.Micros.OrderBy(m => m).ToList();
        var bytes = field.Aoi.Bytes;
        double P(double q) => micros[(int)Math.Min(micros.Count - 1, Math.Floor(q * micros.Count))];
        output.WriteLine($"entities: {field.Map.Entities.Count}, observers: {field.Aoi.Inner.ObserverCount}, ticks measured: {ticks}");
        output.WriteLine($"outbound phase: mean {micros.Average():F1} us, p50 {P(0.5):F1} us, p99 {P(0.99):F1} us, max {micros[^1]:F1} us per tick");
        output.WriteLine($"allocated in the outbound phase: mean {bytes.Average():F1} B/tick, max {bytes.Max()} B/tick (counting sinks)");
        output.WriteLine($"records handed to sinks: {sinks.Sum(s => s.Records) / (double)ticks:F0}/tick in {sinks.Sum(s => s.Messages) / (double)ticks:F0} messages/tick");

        field.Aoi.Inner.ObserverCount.ShouldBe(100);
    }
}
