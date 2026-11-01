using Game.Shared.Random;
using Game.Shared.World;

namespace Game.Tests.World;

public class LineOfSightTests
{
    private static bool Los(MapGrid g, int x0, int y0, int x1, int y1) =>
        LineOfSight.HasLineOfSight(g, new GridCell(x0, y0), new GridCell(x1, y1));

    [Fact]
    public void Open_ground_has_line_of_sight_including_to_self()
    {
        var g = TestGrids.Parse(".....", ".....", ".....");
        Los(g, 0, 0, 4, 2).ShouldBeTrue();
        Los(g, 2, 1, 2, 1).ShouldBeTrue();
    }

    [Fact]
    public void A_wall_in_the_way_blocks_and_one_beside_does_not()
    {
        var g = TestGrids.Parse(
            ".....",
            "..#..",
            ".....");
        Los(g, 0, 1, 4, 1).ShouldBeFalse();
        Los(g, 0, 0, 4, 0).ShouldBeTrue();
        Los(g, 0, 2, 4, 2).ShouldBeTrue();
    }

    [Fact]
    public void Bush_cells_block_sight_but_can_be_stood_on()
    {
        var g = TestGrids.Parse("..b..");
        Los(g, 0, 0, 4, 0).ShouldBeFalse();
        Los(g, 0, 0, 1, 0).ShouldBeTrue();
        Los(g, 2, 0, 4, 0).ShouldBeTrue();   // starting inside the bush: the end cells never block
    }

    [Fact]
    public void End_cells_never_block()
    {
        var g = TestGrids.Parse("#...#");
        Los(g, 0, 0, 4, 0).ShouldBeTrue();
        Los(g, 4, 0, 0, 0).ShouldBeTrue();
    }

    [Fact]
    public void Outside_the_grid_blocks_between_cells_but_not_at_the_ends()
    {
        var g = TestGrids.Parse("...");
        Los(g, 0, 0, -3, 0).ShouldBeFalse();   // -1 and -2 lie between
        Los(g, 0, 0, -1, 0).ShouldBeTrue();    // adjacent: nothing between
    }

    [Fact]
    public void Line_through_an_exact_corner_is_blocked_only_when_both_sides_block()
    {
        // (0,0) -> (1,1) passes exactly through the shared corner of (1,0) and (0,1).
        Los(TestGrids.Parse(".#", "#."), 0, 0, 1, 1).ShouldBeFalse();
        Los(TestGrids.Parse(".#", ".."), 0, 0, 1, 1).ShouldBeTrue();
        Los(TestGrids.Parse("..", "#."), 0, 0, 1, 1).ShouldBeTrue();
        Los(TestGrids.Parse("..", ".."), 0, 0, 1, 1).ShouldBeTrue();
        // And the same line reversed.
        Los(TestGrids.Parse(".#", "#."), 1, 1, 0, 0).ShouldBeFalse();
        Los(TestGrids.Parse(".#", ".."), 1, 1, 0, 0).ShouldBeTrue();
    }

    [Fact]
    public void Diagonal_line_is_blocked_by_a_wall_on_the_diagonal()
    {
        var g = TestGrids.Parse("...", ".#.", "...");
        Los(g, 0, 0, 2, 2).ShouldBeFalse();
        Los(g, 2, 0, 0, 2).ShouldBeFalse();
    }

    [Fact]
    public void Knights_move_line_touches_both_cells_it_crosses()
    {
        // (0,0)->(2,1) runs through (1,0), then (1,1): a wall in either blocks it, a wall at (0,1) does not.
        Los(TestGrids.Parse("...", ".#."), 0, 0, 2, 1).ShouldBeFalse();
        Los(TestGrids.Parse(".#.", "..."), 0, 0, 2, 1).ShouldBeFalse();
        Los(TestGrids.Parse("...", "#.."), 0, 0, 2, 1).ShouldBeTrue();
    }

    [Fact]
    public void Result_is_symmetric_on_random_grids()
    {
        var rng = new Pcg32Random(2024, 7);
        for (var round = 0; round < 40; round++)
        {
            const int w = 12, h = 9;
            var bytes = new byte[w * h];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = rng.Next(100) < 25 ? CellFlagBits.BlocksLos : CellFlagBits.Walkable;
            var g = TestGrids.FromBytes(w, h, bytes);
            for (var n = 0; n < 200; n++)
            {
                var a = new GridCell(rng.Next(w), rng.Next(h));
                var b = new GridCell(rng.Next(w), rng.Next(h));
                LineOfSight.HasLineOfSight(g, a, b).ShouldBe(LineOfSight.HasLineOfSight(g, b, a), a + " <-> " + b);
            }
        }
    }
}
