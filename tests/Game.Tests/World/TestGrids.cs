using System.Text;
using Game.Content.Loading;
using Game.Shared.World;

namespace Game.Tests.World;

/// <summary>Small grids from text for the movement tests, and the sample content's grids.</summary>
internal static class TestGrids
{
    /// <summary>The limits of the sample <c>balance.yaml</c>, so tests run under the real numbers.</summary>
    public static readonly PathLimits Roomy = new(1000, 100_000);

    /// <summary>
    /// Rows of text: <c>.</c> walkable, <c>#</c> wall (not walkable, blocks line of sight), <c>~</c> water (neither),
    /// <c>b</c> bush (walkable and blocks line of sight).
    /// </summary>
    public static MapGrid Parse(params string[] rows)
    {
        var width = rows[0].Length;
        var bytes = new byte[width * rows.Length];
        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < width; x++)
        {
            bytes[y * width + x] = rows[y][x] switch
            {
                '.' => CellFlagBits.Walkable,
                '#' => CellFlagBits.BlocksLos,
                '~' => (byte)0,
                'b' => (byte)(CellFlagBits.Walkable | CellFlagBits.BlocksLos),
                _ => throw new ArgumentException("bad map char " + rows[y][x]),
            };
        }
        return FromBytes(width, rows.Length, bytes);
    }

    public static MapGrid FromBytes(int width, int height, byte[] cells) =>
        MapGrid.Create(new MapGridFile { MapId = "map.test", Width = width, Height = height, Cells = Convert.ToBase64String(cells) });

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private static readonly Lazy<Game.Content.Registry.ContentDatabase> Content = new(() =>
    {
        var load = ContentLoader.Load(Path.Combine(RepoRoot(), "content"));
        load.Errors.ShouldBeEmpty(string.Join("\n", load.Errors));
        return load.Database!;
    });

    public static Game.Content.Registry.ContentDatabase SampleContent => Content.Value;

    public static MapGrid SampleGrid(string mapId) => SampleContent.GetGrid(mapId);

    public static PathLimits SampleLimits
    {
        get
        {
            var m = SampleContent.Balance.Movement;
            return new PathLimits(m.MaxPathLength, m.MaxNodesExpanded);
        }
    }

    public static string Draw(MapGrid grid, IEnumerable<GridCell>? path = null, GridCell? start = null)
    {
        var onPath = new HashSet<(int, int)>((path ?? Array.Empty<GridCell>()).Select(c => (c.X, c.Y)));
        var sb = new StringBuilder();
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                char c;
                if (start is { } s && s.X == x && s.Y == y) c = 'S';
                else if (onPath.Contains((x, y))) c = '*';
                else if (grid.IsWalkable(x, y)) c = grid.BlocksLos(x, y) ? 'b' : '.';
                else c = grid.BlocksLos(x, y) ? '#' : '~';
                sb.Append(c);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
