using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.World;

public class MapGridTests
{
    // 3x2:  y=0: walkable, wall (blocks LOS), water (nothing)   y=1: walkable+LOS, nothing, walkable
    private static MapGridFile File(params byte[] cells) => new()
    {
        MapId = "map.test", Width = 3, Height = 2, Cells = Convert.ToBase64String(cells),
    };

    private static MapGrid Grid() => MapGrid.Create(File(0b01, 0b10, 0b00, 0b11, 0b00, 0b01));

    [Fact]
    public void Flag_bits_follow_the_vocabulary_order()
    {
        CellFlags.All.Ordered.ShouldBe(new[] { "walkable", "blocks_los" });
        CellFlagBits.Walkable.ShouldBe((byte)1);
        CellFlagBits.BlocksLos.ShouldBe((byte)2);
        CellFlagBits.All.ShouldBe((byte)3);
    }

    [Fact]
    public void Cells_are_row_major_from_the_north_west_corner()
    {
        var g = Grid();

        g.IsWalkable(0, 0).ShouldBeTrue();
        g.IsWalkable(1, 0).ShouldBeFalse();
        g.IsWalkable(2, 0).ShouldBeFalse();
        g.IsWalkable(0, 1).ShouldBeTrue();   // index 3 = y * width + x
        g.IsWalkable(1, 1).ShouldBeFalse();
        g.IsWalkable(2, 1).ShouldBeTrue();
        g.BlocksLos(1, 0).ShouldBeTrue();
        g.BlocksLos(2, 0).ShouldBeFalse();
        g.BlocksLos(0, 1).ShouldBeTrue();
        g.BlocksLos(0, 0).ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(3, 0)]
    [InlineData(0, 2)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void Out_of_bounds_is_not_walkable_and_blocks_los(int x, int y)
    {
        var g = Grid();

        g.InBounds(x, y).ShouldBeFalse();
        g.IsWalkable(x, y).ShouldBeFalse();
        g.BlocksLos(x, y).ShouldBeTrue();
    }

    [Fact]
    public void Object_lists_are_copied_and_read_only()
    {
        var spawns = new List<GridSpawn> { new() { MonsterId = "monster.a", Count = 1 } };
        var g = MapGrid.Create(File(1, 1, 1, 1, 1, 1) with { Spawns = spawns });
        spawns.Clear();

        g.Width.ShouldBe(3);
        g.Height.ShouldBe(2);
        g.MapId.ShouldBe("map.test");
        g.Spawns.Count.ShouldBe(1);
        g.Warps.ShouldBeEmpty();
        (g.Spawns is GridSpawn[]).ShouldBeFalse("the list must not be castable back to a mutable array");
    }

    [Fact]
    public void Unknown_flag_bits_are_counted_and_ignored_by_queries()
    {
        var g = MapGrid.Create(File(0b01, 0, 0, 0, 0b101, 0b100));

        g.UnknownFlagCells.ShouldBe(2);
        g.FirstUnknownFlagCell.X.ShouldBe(1);
        g.FirstUnknownFlagCell.Y.ShouldBe(1);
        g.IsWalkable(1, 1).ShouldBeTrue();
        g.BlocksLos(1, 1).ShouldBeFalse();
    }

    [Theory]
    [InlineData("not base64!", "base64")]
    [InlineData("AAAA", "expected width * height = 6")]
    public void Bad_cells_are_rejected(string cells, string fragment)
    {
        MapGrid.TryCreate(File() with { Cells = cells }, out var grid, out var error).ShouldBeFalse();

        grid.ShouldBeNull();
        error.ShouldContain(fragment);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(3, 0)]
    [InlineData(MapGridFile.MaxSize + 1, 1)]
    public void Bad_sizes_are_rejected(int w, int h)
    {
        MapGrid.TryCreate(File(1) with { Width = w, Height = h }, out _, out var error).ShouldBeFalse();

        error.ShouldContain("width and height");
    }

    [Fact]
    public void Unknown_format_version_is_rejected()
    {
        MapGrid.TryCreate(File(1, 1, 1, 1, 1, 1) with { FormatVersion = 2 }, out _, out var error).ShouldBeFalse();

        error.ShouldContain("format_version");
    }

    [Fact]
    public void Object_list_names_match_the_vocabulary()
    {
        var keys = typeof(MapGridFile).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
        foreach (var term in MapObjectTypes.All) keys.ShouldContain(term + "s");
    }
}
