using Game.Server.Events;
using Game.Server.World;
using Game.Shared.Time;
using Game.Shared.World;

namespace Game.Tests.Server;

/// <summary>A clock tests move by hand.</summary>
public sealed class ManualClock : IClock
{
    public long NowMilliseconds { get; set; }

    public void Advance(long ms) => NowMilliseconds += ms;
}

/// <summary>Collects log lines.</summary>
public sealed class ListLog : IServerLog
{
    private readonly List<string> _lines = new();

    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) return _lines.ToList(); }
    }

    public void Info(string message) => Add("INFO " + message);

    public void Warn(string message) => Add("WARN " + message);

    public void Error(string message, Exception? exception = null) => Add("ERROR " + message + (exception is null ? "" : " :: " + exception.Message));

    private void Add(string line)
    {
        lock (_lines) _lines.Add(line);
    }
}

/// <summary>A system built from a delegate.</summary>
public sealed class LambdaSystem(string name, Action<TickContext> tick, params Type[] intentTypes) : ITickSystem
{
    public string Name => name;

    public IReadOnlyCollection<Type> IntentTypes => intentTypes;

    public void Tick(TickContext context) => tick(context);
}

public sealed record Poke(EntityId Entity, int N) : IIntent;

public sealed record Step(EntityId Entity) : IIntent;

public sealed record Unowned(EntityId Entity) : IIntent;

public sealed record Note(string Text) : IGameEvent
{
    public string Name => "TestNote";
}

public static class Grids
{
    /// <summary>A grid from rows of text: <c>.</c> walkable, <c>#</c> blocked.</summary>
    public static MapGrid Make(string mapId, params string[] rows)
    {
        var width = rows[0].Length;
        var cells = new byte[width * rows.Length];
        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < width; x++)
            cells[y * width + x] = rows[y][x] == '.' ? CellFlagBits.Walkable : (byte)0;
        return MapGrid.Create(new MapGridFile { MapId = mapId, Width = width, Height = rows.Length, Cells = Convert.ToBase64String(cells) });
    }

    /// <summary>5x5, walkable except a wall cell at (2, 2).</summary>
    public static MapGrid Room(string mapId) => Make(mapId, ".....", ".....", "..#..", ".....", ".....");

    public static readonly GridCell Wall = new(2, 2);
}

public static class Worlds
{
    public static GameWorld Make(SystemPipeline? pipeline = null, IServerLog? log = null, ulong seed = 1, int capacity = IntentQueue.DefaultCapacity,
        params string[] mapIds) =>
        new(mapIds.Length == 0 ? [Grids.Room("map.a")] : mapIds.Select(Grids.Room), pipeline ?? new SystemPipeline(), seed,
            new ManualClock(), log ?? new ListLog(), capacity);
}
