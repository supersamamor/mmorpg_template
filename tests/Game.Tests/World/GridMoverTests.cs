using Game.Shared.World;

namespace Game.Tests.World;

public class GridMoverTests
{
    private static List<GridCell> Cells(params (int x, int y)[] cells) => cells.Select(c => new GridCell(c.x, c.y)).ToList();

    [Fact]
    public void Orthogonal_step_takes_one_over_speed_seconds()
    {
        var m = new GridMover(new GridCell(0, 0));
        m.SetPath(Cells((1, 0), (2, 0)));

        m.Advance(2.0, 499).ShouldBe(0);
        m.Cell.X.ShouldBe(0);
        m.Progress.ShouldBe(0.998, 1e-9);
        m.Advance(2.0, 1).ShouldBe(1);
        m.Cell.X.ShouldBe(1);
        m.Progress.ShouldBe(0, 1e-9);
        m.IsMoving.ShouldBeTrue();
        m.Arrived.ShouldBeFalse();
    }

    [Fact]
    public void Diagonal_step_takes_root_two_times_as_long()
    {
        var m = new GridMover(new GridCell(0, 0));
        m.SetPath(Cells((1, 1)));

        m.Advance(1.0, 1414).ShouldBe(0);    // sqrt(2) s = 1414.2 ms
        m.Advance(1.0, 1).ShouldBe(1);
        m.Cell.ShouldBe(new GridCell(1, 1));
        m.Arrived.ShouldBeTrue();
        m.IsMoving.ShouldBeFalse();
    }

    [Fact]
    public void Leftover_time_carries_into_the_next_step()
    {
        var m = new GridMover(new GridCell(0, 0));
        m.SetPath(Cells((1, 0), (2, 0), (3, 0)));

        // 2.5 cells in one call: two steps done, half way through the third.
        m.Advance(1.0, 2500).ShouldBe(2);
        m.Cell.X.ShouldBe(2);
        m.Progress.ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void Ten_hertz_ticks_arrive_within_one_tick_of_one_big_step()
    {
        var path = Cells((1, 0), (2, 1), (3, 2), (4, 2), (5, 2), (5, 3), (6, 4), (7, 4));
        const double speed = 3.7;
        var distance = 0.0;
        var prev = new GridCell(0, 0);
        foreach (var c in path)
        {
            distance += c.X != prev.X && c.Y != prev.Y ? Math.Sqrt(2) : 1;
            prev = c;
        }
        var exactMs = distance / speed * 1000;

        var big = new GridMover(new GridCell(0, 0));
        big.SetPath(path);
        big.Advance(speed, (int)Math.Ceiling(exactMs));
        big.Arrived.ShouldBeTrue();
        big.Cell.ShouldBe(new GridCell(7, 4));

        var ticked = new GridMover(new GridCell(0, 0));
        ticked.SetPath(path);
        var ticks = 0;
        while (!ticked.Arrived && ticks < 1000)
        {
            ticked.Advance(speed, 100);
            ticks++;
        }
        ticked.Arrived.ShouldBeTrue();
        ticked.Cell.ShouldBe(new GridCell(7, 4));
        (ticks * 100.0).ShouldBeInRange(exactMs, exactMs + 100);
    }

    [Fact]
    public void Facing_follows_each_step_and_is_kept_when_arrived()
    {
        var m = new GridMover(new GridCell(0, 0));
        m.SetPath(Cells((1, 0), (1, 1), (0, 2)));
        m.Facing.ShouldBe("e");
        m.Advance(1.0, 1000);
        m.Facing.ShouldBe("s");
        m.Advance(1.0, 1000);
        m.Facing.ShouldBe("sw");
        m.Advance(1.0, 2000);
        m.Arrived.ShouldBeTrue();
        m.Facing.ShouldBe("sw");
    }

    [Fact]
    public void Empty_path_means_already_there()
    {
        var m = new GridMover(new GridCell(4, 4));
        m.SetPath(new List<GridCell>());
        m.Arrived.ShouldBeTrue();
        m.IsMoving.ShouldBeFalse();
        m.Advance(5.0, 1000).ShouldBe(0);
        m.Cell.ShouldBe(new GridCell(4, 4));
    }

    [Fact]
    public void Stop_drops_the_path_and_progress()
    {
        var m = new GridMover(new GridCell(0, 0));
        m.SetPath(Cells((1, 0), (2, 0)));
        m.Advance(1.0, 600);
        m.Stop();

        m.IsMoving.ShouldBeFalse();
        m.Arrived.ShouldBeFalse();
        m.Progress.ShouldBe(0);
        m.Advance(1.0, 5000).ShouldBe(0);
        m.Cell.ShouldBe(new GridCell(0, 0));
    }

    [Fact]
    public void Re_path_keeps_progress_when_the_next_cell_is_unchanged()
    {
        var m = new GridMover(new GridCell(0, 0));
        m.SetPath(Cells((1, 0), (2, 0), (3, 0)));
        m.Advance(1.0, 500);

        m.SetPath(Cells((1, 0), (1, 1)));   // same first cell, new route after it
        m.Cell.ShouldBe(new GridCell(0, 0));
        m.Progress.ShouldBe(0.5, 1e-9);
        m.Advance(1.0, 500).ShouldBe(1);
        m.Cell.ShouldBe(new GridCell(1, 0));
        m.NextCell.ShouldBe(new GridCell(1, 1));
    }

    [Fact]
    public void Re_path_to_a_different_first_cell_restarts_from_the_current_cell()
    {
        var m = new GridMover(new GridCell(0, 0));
        m.SetPath(Cells((1, 0), (2, 0)));
        m.Advance(1.0, 500);

        m.SetPath(Cells((0, 1), (0, 2)));
        m.Cell.ShouldBe(new GridCell(0, 0));
        m.Progress.ShouldBe(0, 1e-9);
        m.Facing.ShouldBe("s");
        m.Advance(1.0, 1000).ShouldBe(1);
        m.Cell.ShouldBe(new GridCell(0, 1));
    }

    [Theory]
    [InlineData(0.0, 1000)]
    [InlineData(-1.0, 1000)]
    [InlineData(2.0, 0)]
    [InlineData(2.0, -5)]
    [InlineData(double.NaN, 1000)]
    public void No_movement_for_non_positive_speed_or_time(double speed, int ms)
    {
        var m = new GridMover(new GridCell(0, 0));
        m.SetPath(Cells((1, 0)));
        m.Advance(speed, ms).ShouldBe(0);
        m.Cell.ShouldBe(new GridCell(0, 0));
    }
}
