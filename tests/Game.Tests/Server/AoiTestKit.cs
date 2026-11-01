using System.Text;
using System.Text.Json;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>A sink that copies every message it gets, and also keeps it as a text line (<c>tN snap map #1 #2</c>, <c>tN +#3 ~#2[cell] -#4</c>).</summary>
public sealed class RecordingSink : IViewSink
{
    public readonly List<string> Lines = new();
    public readonly List<SnapshotCopy> Snapshots = new();
    public readonly List<DeltaCopy> Deltas = new();

    public sealed record SnapshotCopy(long Tick, string MapId, EntityId Observer, List<EntityView> Entities, OwnVitals? Vitals = null);

    public sealed record DeltaCopy(long Tick, string MapId, EntityId Observer, List<EntityView> Appeared, List<EntityChange> Changed, List<EntityId> Disappeared,
        List<HitView>? Hits = null, OwnVitals? Vitals = null);

    public int Count => Lines.Count;

    /// <summary>The lines without their tick prefix.</summary>
    public List<string> Bodies => Lines.Select(l => l[(l.IndexOf(' ') + 1)..]).ToList();

    public void OnSnapshot(ViewSnapshot snapshot)
    {
        Snapshots.Add(new SnapshotCopy(snapshot.Tick, snapshot.MapId, snapshot.Observer, snapshot.Entities.ToList(), snapshot.Vitals));
        Lines.Add($"t{snapshot.Tick} snap {snapshot.MapId} " + string.Join(" ", snapshot.Entities.Select(e => e.Id)));
    }

    public void OnDelta(ViewDelta delta)
    {
        Deltas.Add(new DeltaCopy(delta.Tick, delta.MapId, delta.Observer, delta.Appeared.ToList(), delta.Changed.ToList(), delta.Disappeared.ToList(),
            delta.Hits.ToList(), delta.Vitals));
        var parts = delta.Appeared.Select(e => "+" + e.Id)
            .Concat(delta.Changed.Select(c => $"~{c.View.Id}[{Fields(c.Fields)}]"))
            .Concat(delta.Disappeared.Select(id => "-" + id))
            .Concat(delta.Hits.Select(h => $"!{h.Attacker}>{h.Target}"))
            .Concat(delta.Vitals is { } v ? [$"hp{v.Hp}/{v.MaxHp}"] : []);
        Lines.Add($"t{delta.Tick} " + string.Join(" ", parts));
    }

    public static string Fields(ViewFields fields) =>
        string.Join(",", new[]
            {
                (ViewFields.Cell, "cell"), (ViewFields.Facing, "facing"), (ViewFields.Next, "next"), (ViewFields.Speed, "speed"), (ViewFields.DisplayKey, "display_key"),
                (ViewFields.Hp, "hp"), (ViewFields.Dead, "dead"), (ViewFields.Mine, "mine"),
            }
            .Where(f => fields.HasFlag(f.Item1)).Select(f => f.Item2));

    /// <summary>Every message in full, for determinism comparisons.</summary>
    public IEnumerable<string> Detailed()
    {
        foreach (var s in Snapshots) yield return $"S {s.Tick} {s.MapId} {s.Observer} " + string.Join(";", s.Entities.Select(Describe));
        foreach (var d in Deltas)
            yield return $"D {d.Tick} {d.MapId} {d.Observer} A:{string.Join(";", d.Appeared.Select(Describe))} " +
                         $"C:{string.Join(";", d.Changed.Select(c => Fields(c.Fields) + "=" + Describe(c.View)))} G:{string.Join(";", d.Disappeared)}";
    }

    public static string Describe(EntityView v) => $"{v.Id} {v.Kind} {v.DisplayKey} {v.Cell} {v.Facing} {v.Next} {v.Speed}";
}

/// <summary>A sink that only counts, so measurements see the AOI's own cost.</summary>
public sealed class CountingSink : IViewSink
{
    public long Messages;
    public long Records;

    public void OnSnapshot(ViewSnapshot snapshot)
    {
        Messages++;
        Records += snapshot.Entities.Count;
    }

    public void OnDelta(ViewDelta delta)
    {
        Messages++;
        Records += delta.Appeared.Count + delta.Changed.Count + delta.Disappeared.Count;
    }
}

/// <summary>
/// A stand-in serialization (System.Text.Json, not the T-205 wire format) used to size the messages: one JSON object per message,
/// changes carry only the id and their changed fields, nulls and zero speeds are left out, speeds are rounded to 0.01. <c>shortKeys</c> uses one-letter keys.
/// </summary>
public static class ViewJson
{
    public static byte[] Serialize(ViewSnapshot snapshot, bool shortKeys = false)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString(K("type", "y", shortKeys), "snapshot");
            w.WriteNumber(K("tick", "t", shortKeys), snapshot.Tick);
            w.WriteString(K("map", "m", shortKeys), snapshot.MapId);
            w.WriteNumber(K("you", "u", shortKeys), snapshot.Observer.Value);
            w.WriteStartArray(K("entities", "e", shortKeys));
            foreach (var view in snapshot.Entities) WriteView(w, view, All, true, shortKeys);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static byte[] Serialize(ViewDelta delta, bool shortKeys = false)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString(K("type", "y", shortKeys), "delta");
            w.WriteNumber(K("tick", "t", shortKeys), delta.Tick);
            if (delta.Appeared.Count > 0)
            {
                w.WriteStartArray(K("appeared", "a", shortKeys));
                foreach (var view in delta.Appeared) WriteView(w, view, All, true, shortKeys);
                w.WriteEndArray();
            }
            if (delta.Changed.Count > 0)
            {
                w.WriteStartArray(K("changed", "c", shortKeys));
                foreach (var change in delta.Changed) WriteView(w, change.View, change.Fields, false, shortKeys);
                w.WriteEndArray();
            }
            if (delta.Disappeared.Count > 0)
            {
                w.WriteStartArray(K("disappeared", "d", shortKeys));
                foreach (var id in delta.Disappeared) w.WriteNumberValue(id.Value);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private const ViewFields All = ViewFields.Cell | ViewFields.Facing | ViewFields.Next | ViewFields.Speed | ViewFields.DisplayKey;

    private static void WriteView(Utf8JsonWriter w, EntityView v, ViewFields fields, bool full, bool s)
    {
        w.WriteStartObject();
        w.WriteNumber(K("id", "i", s), v.Id.Value);
        if (full) w.WriteString(K("kind", "k", s), v.Kind.ToString().ToLowerInvariant());
        if (fields.HasFlag(ViewFields.DisplayKey) && (v.DisplayKey is not null || !full)) w.WriteString(K("key", "p", s), v.DisplayKey);
        if (fields.HasFlag(ViewFields.Cell)) Cell(w, K("cell", "x", s), v.Cell);
        if (fields.HasFlag(ViewFields.Facing)) w.WriteString(K("facing", "f", s), v.Facing);
        if (fields.HasFlag(ViewFields.Next))
        {
            if (v.Next is { } next) Cell(w, K("next", "n", s), next);
            else if (!full) w.WriteNull(K("next", "n", s));   // stopped
        }
        if (fields.HasFlag(ViewFields.Speed) && (v.Speed != 0 || !full))
            w.WriteNumber(K("speed", "v", s), Math.Round(v.Speed, 2));   // a serializer would quantize; raw doubles print as 4.0600000000000005
        w.WriteEndObject();
    }

    private static void Cell(Utf8JsonWriter w, string key, GridCell cell)
    {
        w.WriteStartArray(key);
        w.WriteNumberValue(cell.X);
        w.WriteNumberValue(cell.Y);
        w.WriteEndArray();
    }

    private static string K(string name, string shortName, bool useShort) => useShort ? shortName : name;

    public static string Text(byte[] json) => Encoding.UTF8.GetString(json);
}

/// <summary>
/// A world of open maps (<c>map.a</c>, <c>map.b</c>) running movement + AOI. Entities are set up (stats, display key, sink) in an
/// <c>EntityEnteredMap</c> handler, i.e. in phase 4 of the tick that adds them, so phase 5 already sees them complete.
/// </summary>
public sealed class AoiRig
{
    public const int Agi5 = 150;   // 5 cells per second with the sample formulas: half a cell per tick orthogonally

    public readonly GameWorld World;
    public readonly ListLog Log = new();
    private readonly Dictionary<EntityId, Action<Entity>> _setup = new();

    public AoiRig(int bucketSize = 4, int viewRadius = 1, int width = 24, int height = 16, params string[] maps)
    {
        var ids = maps.Length == 0 ? ["map.a", "map.b"] : maps;
        var pipeline = new SystemPipeline()
            .Add(() => new MovementSystem(TestGrids.Roomy))
            .Add(() => new AoiSystem(bucketSize, viewRadius));
        World = new GameWorld(ids.Select(id => Open(id, width, height)), pipeline, 7, new ManualClock(), Log);
        foreach (var instance in World.Instances)
        {
            var captured = instance;
            instance.Events.Subscribe<Game.Server.World.EntityEnteredMap>(e =>
            {
                if (_setup.Remove(e.Entity, out var setup) && captured.TryGetEntity(e.Entity, out var entity)) setup(entity);
            });
        }
    }

    public MapInstance A => World.GetInstance("map.a");

    public MapInstance B => World.GetInstance("map.b");

    public static MapGrid Open(string mapId, int width, int height)
    {
        var cells = new byte[width * height];
        Array.Fill(cells, CellFlagBits.Walkable);
        return MapGrid.Create(new MapGridFile { MapId = mapId, Width = width, Height = height, Cells = Convert.ToBase64String(cells) });
    }

    public static StatSheet Sheet(int agi)
    {
        var sheet = new StatSheet(DerivedStatFormulas.Compile(TestGrids.SampleContent.Formulas));
        sheet.Base.BaseLevel = 1;
        sheet.Base[PrimaryStats.Agi] = agi;
        return sheet;
    }

    /// <summary>Queues an entity; it joins on its instance's next tick.</summary>
    public EntityId Add(GridCell cell, string map = "map.a", EntityKind kind = EntityKind.Monster, string? key = null, IViewSink? sink = null,
        int? agi = Agi5, string facing = Directions.S)
    {
        var id = World.Spawn(kind, map, cell, facing);
        _setup[id] = e =>
        {
            e.DefinitionId = key;
            e.View = sink;
            if (agi is { } a) e.Stats = Sheet(a);
        };
        return id;
    }

    /// <summary>An observer (a player with a recording sink) at a cell; joins on the next tick.</summary>
    public (EntityId Id, RecordingSink Sink) AddObserver(GridCell cell, string map = "map.a")
    {
        var sink = new RecordingSink();
        return (Add(cell, map, EntityKind.Player, null, sink), sink);
    }

    public Entity Get(EntityId id) =>
        World.Instances.Select(i => i.TryGetEntity(id, out var e) ? e : null).FirstOrDefault(e => e is not null)
        ?? throw new InvalidOperationException($"{id} is in no instance");

    public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

    /// <summary>Ticks every instance once per step, in map id order.</summary>
    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++)
            foreach (var instance in World.Instances) instance.Tick();
    }

    public AoiSystem Aoi(MapInstance instance) => instance.Systems.OfType<AoiSystem>().Single();
}
