using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.World;

public class GridDirectionTests
{
    [Fact]
    public void Index_order_is_the_vocabulary_order()
    {
        GridDirection.Count.ShouldBe(Directions.All.Count);
        for (var i = 0; i < GridDirection.Count; i++)
        {
            GridDirection.Name(i).ShouldBe(Directions.All.Ordered[i]);
            GridDirection.IndexOf(Directions.All.Ordered[i]).ShouldBe(i);
        }
        GridDirection.IndexOf("up").ShouldBe(-1);
    }

    [Theory]
    [InlineData("n", 0, -1)]
    [InlineData("ne", 1, -1)]
    [InlineData("e", 1, 0)]
    [InlineData("se", 1, 1)]
    [InlineData("s", 0, 1)]
    [InlineData("sw", -1, 1)]
    [InlineData("w", -1, 0)]
    [InlineData("nw", -1, -1)]
    public void Steps_follow_plus_x_east_and_plus_y_south(string name, int dx, int dy)
    {
        GridDirection.TryGetStep(name, out var x, out var y).ShouldBeTrue();
        (x, y).ShouldBe((dx, dy));
        GridDirection.FromStep(dx, dy).ShouldBe(name);
        GridDirection.FromStep(dx * 7, dy * 3).ShouldBe(name);
    }

    [Fact]
    public void Unknown_term_and_zero_step_have_no_direction()
    {
        GridDirection.TryGetStep("up", out _, out _).ShouldBeFalse();
        GridDirection.FromStep(0, 0).ShouldBeNull();
        GridDirection.Facing(new GridCell(3, 3), new GridCell(3, 3)).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 0, 1, 0, "e")]
    [InlineData(0, 0, 10, 1, "e")]      // far east, slightly south: nearest is e
    [InlineData(0, 0, 10, 4, "e")]      // slope 0.4 < tan 22.5 deg (0.414)
    [InlineData(0, 0, 10, 5, "se")]
    [InlineData(0, 0, 5, 10, "se")]
    [InlineData(0, 0, 4, 10, "s")]
    [InlineData(0, 0, 1, 10, "s")]
    [InlineData(5, 5, 5, 0, "n")]
    [InlineData(5, 5, 9, 1, "ne")]
    [InlineData(5, 5, 0, 0, "nw")]
    [InlineData(5, 5, 1, 5, "w")]
    [InlineData(5, 5, 2, 9, "sw")]
    public void Facing_picks_the_nearest_of_the_eight(int fx, int fy, int tx, int ty, string expected) =>
        GridDirection.Facing(new GridCell(fx, fy), new GridCell(tx, ty)).ShouldBe(expected);

    [Fact]
    public void Chebyshev_and_range()
    {
        GridDistance.Chebyshev(new GridCell(0, 0), new GridCell(3, -5)).ShouldBe(5);
        GridDistance.Chebyshev(new GridCell(2, 2), new GridCell(2, 2)).ShouldBe(0);
        GridDistance.Chebyshev(new GridCell(int.MinValue, 0), new GridCell(int.MaxValue, 0)).ShouldBe(int.MaxValue);
        GridDistance.InRange(new GridCell(0, 0), new GridCell(3, 3), 3).ShouldBeTrue();   // inclusive, diagonal counts as 1 per step
        GridDistance.InRange(new GridCell(0, 0), new GridCell(4, 1), 3).ShouldBeFalse();
        GridDistance.InRange(new GridCell(0, 0), new GridCell(0, 0), 0).ShouldBeTrue();
        GridDistance.InRange(new GridCell(0, 0), new GridCell(0, 0), -1).ShouldBeFalse();
    }
}
