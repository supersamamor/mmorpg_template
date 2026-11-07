using Game.Client.Core;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Client;

/// <summary>The view model applying snapshots and deltas, and the step timing (T-206, ARCHITECTURE §10).</summary>
public class ViewModelTests
{
    private const double Tick = 0.1;
    private const double Delay = 2 * Tick;
    private const double Tolerance = 1e-9;

    private static EntityRecord Standing(long id, int x, int y, string kind = EntityKinds.Monster) =>
        new(id, kind, "monster.a", new GridCell(x, y), Directions.N, null, 0);

    private static EntityRecord Walking(long id, int x, int y, int nextX, int nextY, double speed) =>
        new(id, EntityKinds.Monster, "monster.a", new GridCell(x, y), Directions.E, new GridCell(nextX, nextY), speed);

    private static DeltaMessage Delta(params ChangeRecord[] changes) => new(1, [], changes, []);

    private static ChangeRecord Stepped(long id, int x, int y, int nextX, int nextY) =>
        new(id, ChangedFields.Cell | ChangedFields.Next, new GridCell(x, y), null, new GridCell(nextX, nextY), 0, null);

    private static ChangeRecord Started(long id, int nextX, int nextY, double speed) =>
        new(id, ChangedFields.Next | ChangedFields.Speed, default, null, new GridCell(nextX, nextY), speed, null);

    private static ChangeRecord Stopped(long id, int x, int y) =>
        new(id, ChangedFields.Cell | ChangedFields.Next | ChangedFields.Speed, new GridCell(x, y), null, null, 0, null);

    private static WorldView NewWorld(params EntityRecord[] entities)
    {
        var world = new WorldView(new ClientConfig());
        world.ApplyWelcome(new WelcomeMessage(1, 1, "map.a", 10));
        world.ApplySnapshot(new SnapshotMessage(0, "map.a", 1, entities), 0);
        return world;
    }

    // ---- snapshots and deltas ----

    [Fact]
    public void A_snapshot_replaces_the_view()
    {
        var world = NewWorld(Standing(1, 1, 1, EntityKinds.Player), Standing(2, 2, 2), Standing(3, 3, 3));
        world.ApplySnapshot(new SnapshotMessage(9, "map.a", 1, [Standing(1, 5, 5, EntityKinds.Player), Standing(4, 4, 4)], new VitalsRecord(1, 2, 3, 4)), 1);

        world.Entities.Select(e => e.Id).OrderBy(i => i).ShouldBe([1L, 4L]);
        world.Removed.Select(e => e.Id).OrderBy(i => i).ShouldBe([2L, 3L]);
        world.Tick.ShouldBe(9);
        world.Own!.X.ShouldBe(5.5);
        world.Own!.Z.ShouldBe(5.5);
        world.Vitals.ShouldBe(new VitalsRecord(1, 2, 3, 4));
    }

    [Fact]
    public void A_cell_is_drawn_at_its_centre()
    {
        var world = NewWorld(Standing(2, 3, 7));
        var entity = world.Find(2)!;
        entity.X.ShouldBe(3.5);
        entity.Z.ShouldBe(7.5);
    }

    [Fact]
    public void A_snapshot_with_a_new_map_means_the_map_changed()
    {
        var world = NewWorld(Standing(1, 1, 1, EntityKinds.Player));
        world.ApplySnapshot(new SnapshotMessage(5, "map.b", 1, [Standing(1, 2, 7, EntityKinds.Player)]), 1);
        world.MapId.ShouldBe("map.b");
        world.Own!.Cell.X.ShouldBe(2);
    }

    [Fact]
    public void Welcome_on_a_resume_can_name_a_new_map_before_its_snapshot()
    {
        var world = NewWorld();
        world.ApplyWelcome(new WelcomeMessage(1, 1, "map.b", 10));
        world.MapId.ShouldBe("map.b");
    }

    [Fact]
    public void The_render_delay_comes_from_the_tick_rate()
    {
        var world = new WorldView(new ClientConfig());
        world.ApplyWelcome(new WelcomeMessage(1, 1, "map.a", 20));
        world.RenderDelay.ShouldBe(0.1, Tolerance);
    }

    [Fact]
    public void Next_null_means_it_stopped()
    {
        var world = NewWorld(Walking(2, 1, 1, 2, 1, 2));
        world.ApplyDelta(Delta(Stopped(2, 2, 1)), 0.5);

        var entity = world.Find(2)!;
        entity.Next.ShouldBeNull();
        entity.Speed.ShouldBe(0);
        entity.Cell.X.ShouldBe(2);
        world.Advance(5);
        entity.IsWalking.ShouldBeFalse();
        entity.X.ShouldBe(2.5);
    }

    [Fact]
    public void Appearing_and_disappearing_in_one_delta()
    {
        var world = NewWorld(Standing(2, 1, 1));
        world.ApplyDelta(new DeltaMessage(1, [Standing(3, 2, 2)], [], [2]), 1);

        world.Entities.Select(e => e.Id).ShouldBe([3L]);
        world.Removed.Select(e => e.Id).ShouldBe([2L]);
    }

    [Fact]
    public void An_id_in_appeared_and_disappeared_ends_up_gone()
    {
        var world = NewWorld();
        world.ApplyDelta(new DeltaMessage(1, [Standing(3, 2, 2)], [], [3]), 1);
        world.Entities.ShouldBeEmpty();
    }

    [Fact]
    public void A_change_to_an_unknown_id_and_a_removal_of_one_are_ignored()
    {
        var world = NewWorld(Standing(2, 1, 1));
        world.ApplyDelta(new DeltaMessage(1, [], [Stopped(99, 1, 1)], [98]), 1);
        world.Entities.Count.ShouldBe(1);
        world.Removed.ShouldBeEmpty();
    }

    [Fact]
    public void Changed_fields_only_touch_what_they_name()
    {
        var world = NewWorld(new EntityRecord(2, EntityKinds.Monster, "monster.a", new GridCell(1, 1), Directions.N, null, 0, 1));
        world.ApplyDelta(Delta(new ChangeRecord(2, ChangedFields.Hp, default, null, null, 0, null, 0.37)), 1);
        var entity = world.Find(2)!;
        entity.Hp.ShouldBe(0.37);
        entity.Cell.X.ShouldBe(1);
        entity.DisplayKey.ShouldBe("monster.a");
        entity.LookDirty.ShouldBeTrue();

        entity.LookDirty = false;
        world.ApplyDelta(Delta(new ChangeRecord(2, ChangedFields.Dead | ChangedFields.Hp, default, null, null, 0, null, 0, true)), 1);
        entity.Dead.ShouldBeTrue();
        entity.LookDirty.ShouldBeTrue();
    }

    [Fact]
    public void A_ground_item_keeps_quantity_and_mine()
    {
        var world = NewWorld(new EntityRecord(9, EntityKinds.Item, "item.x", new GridCell(4, 5), Directions.S, null, 0, Quantity: 2, Mine: true));
        var item = world.Find(9)!;
        item.Quantity.ShouldBe(2);
        item.Mine.ShouldBeTrue();

        world.ApplyDelta(Delta(new ChangeRecord(9, ChangedFields.Mine, default, null, null, 0, null, Mine: false)), 1);
        item.Mine.ShouldBeFalse();
        item.Quantity.ShouldBe(2);
    }

    [Fact]
    public void Removed_entities_are_pooled()
    {
        var world = NewWorld(Standing(2, 1, 1));
        var first = world.Find(2);
        world.ApplyDelta(new DeltaMessage(1, [], [], [2]), 1);
        world.ClearRemoved();
        world.ApplyDelta(new DeltaMessage(2, [Standing(3, 2, 2)], [], []), 1);
        world.Find(3).ShouldBeSameAs(first);
        world.Find(3)!.X.ShouldBe(2.5);
    }

    // ---- interpolation ----

    [Fact]
    public void A_step_is_drawn_at_speed_two_ticks_behind_its_arrival()
    {
        var world = NewWorld(Standing(2, 1, 1));
        var entity = world.Find(2)!;
        world.ApplyDelta(Delta(Started(2, 2, 1, 2)), 1); // 1 cell at 2 cells/s: 0.5 s, starting at arrival 1.0

        world.Advance(1 + Delay - 0.01);
        entity.X.ShouldBe(1.5, Tolerance);          // not started: 2 ticks behind
        world.Advance(1 + Delay + 0.25);
        entity.X.ShouldBe(2.0, Tolerance);          // halfway
        world.Advance(1 + Delay + 0.5);
        entity.X.ShouldBe(2.5, Tolerance);
        entity.IsWalking.ShouldBeFalse();
    }

    [Fact]
    public void A_diagonal_step_takes_root_two_as_long()
    {
        var world = NewWorld(Standing(2, 1, 1));
        var entity = world.Find(2)!;
        world.ApplyDelta(Delta(Started(2, 2, 2, 2)), 1);
        var duration = Math.Sqrt(2) / 2;

        world.Advance(1 + Delay + duration / 2);
        entity.X.ShouldBe(2.0, Tolerance);
        entity.Z.ShouldBe(2.0, Tolerance);
        world.Advance(1 + Delay + duration - 0.001);
        entity.IsWalking.ShouldBeTrue();
        world.Advance(1 + Delay + duration + 0.001);
        entity.X.ShouldBe(2.5, Tolerance);
        entity.IsWalking.ShouldBeFalse();
    }

    [Fact]
    public void A_walker_does_not_stop_at_each_cell()
    {
        var world = NewWorld(Standing(2, 0, 0));
        var entity = world.Find(2)!;
        world.ApplyDelta(Delta(Started(2, 1, 0, 2)), 0);

        // Deltas arrive at tick boundaries around the ideal 0.5 s step; the drawn speed must stay close to 2 cells/s throughout.
        var arrivals = new[] { 0.5, 1.0, 1.6, 2.0, 2.5 };
        var next = 0;
        var last = entity.X;
        const double dt = 1.0 / 30;
        for (var t = dt; t < 3; t += dt)
        {
            while (next < arrivals.Length && arrivals[next] <= t)
            {
                world.ApplyDelta(Delta(Stepped(2, next + 1, 0, next + 2, 0)), arrivals[next]);
                next++;
            }
            world.Advance(t);
            var moved = entity.X - last;
            last = entity.X;
            if (t > Delay + dt && t < 2.7) moved.ShouldBeGreaterThan(0, $"it stood still at t={t:0.00}, x={entity.X:0.00}");
            moved.ShouldBeLessThan(dt * 2 * 2.1, $"it jumped at t={t:0.00}");
        }
    }

    [Fact]
    public void A_late_delta_makes_it_wait_at_the_cell_and_never_jump()
    {
        var world = NewWorld(Standing(2, 0, 0));
        var entity = world.Find(2)!;
        world.ApplyDelta(Delta(Started(2, 1, 0, 2)), 0);

        world.Advance(0.5 + Delay);       // the step is done
        entity.X.ShouldBe(1.5, Tolerance);
        world.Advance(1.2);               // the next delta should have come at 0.5
        entity.X.ShouldBe(1.5, Tolerance);

        world.ApplyDelta(Delta(Stepped(2, 1, 0, 2, 0)), 1.2);
        world.Advance(1.2);
        entity.X.ShouldBe(1.5, Tolerance); // no jump the moment it arrives
        world.Advance(1.2 + 0.25);
        entity.X.ShouldBe(2.0, Tolerance); // and it starts at once from the render time: it is already behind
    }

    [Fact]
    public void A_late_delta_inside_the_render_delay_costs_nothing()
    {
        var world = NewWorld(Standing(2, 0, 0));
        var entity = world.Find(2)!;
        world.ApplyDelta(Delta(Started(2, 1, 0, 2)), 0);
        world.Advance(0.6);                                      // render time 0.4: still on the first step
        world.ApplyDelta(Delta(Stepped(2, 1, 0, 2, 0)), 0.6);    // 0.1 s late

        world.Advance(0.7);                                      // render time 0.5: the first step ends
        entity.X.ShouldBe(1.5, Tolerance);
        world.Advance(0.8);                                      // and the second follows on without a pause
        entity.X.ShouldBe(1.7, Tolerance);
    }

    [Fact]
    public void A_burst_of_deltas_catches_up_instead_of_falling_behind()
    {
        var world = NewWorld(Standing(2, 0, 0));
        var entity = world.Find(2)!;
        world.ApplyDelta(Delta(Started(2, 1, 0, 2)), 0);
        // After a stall, three steps arrive at once: they run at up to twice the speed.
        world.ApplyDelta(Delta(Stepped(2, 1, 0, 2, 0)), 0.1);
        world.ApplyDelta(Delta(Stepped(2, 2, 0, 3, 0)), 0.1);
        world.ApplyDelta(Delta(Stepped(2, 3, 0, 4, 0)), 0.1);

        world.Advance(2.0 + Delay);
        entity.X.ShouldBe(4.5, Tolerance);
    }

    [Fact]
    public void A_new_order_mid_step_turns_from_where_it_is_drawn()
    {
        var world = NewWorld(Standing(2, 0, 0));
        var entity = world.Find(2)!;
        world.ApplyDelta(Delta(Started(2, 1, 0, 2)), 0);
        world.Advance(Delay + 0.25);
        entity.X.ShouldBe(1.0, Tolerance);

        world.ApplyDelta(Delta(new ChangeRecord(2, ChangedFields.Next, default, null, new GridCell(0, 1), 0, null)), Delay + 0.25);
        world.Advance(Delay + 0.25);
        entity.X.ShouldBe(1.0, Tolerance); // no jump
        world.Advance(Delay + 5);
        entity.X.ShouldBe(0.5, Tolerance);
        entity.Z.ShouldBe(1.5, Tolerance);
    }

    [Fact]
    public void A_long_move_snaps_and_a_short_one_glides()
    {
        var world = NewWorld(Standing(2, 0, 0));
        var entity = world.Find(2)!;

        world.ApplyDelta(Delta(Stopped(2, 2, 0)), 1);       // 2 cells: glide
        entity.X.ShouldBe(0.5);
        world.Advance(10);
        entity.X.ShouldBe(2.5, Tolerance);

        world.ApplyDelta(Delta(Stopped(2, 20, 0)), 11);     // far (same-map warp): snap
        entity.X.ShouldBe(20.5);
    }

    [Fact]
    public void Facing_turns_smoothly_the_short_way()
    {
        var world = NewWorld(Standing(2, 0, 0));
        var entity = world.Find(2)!;
        entity.Yaw.ShouldBe(0);
        world.Advance(1);

        world.ApplyDelta(Delta(new ChangeRecord(2, ChangedFields.Facing, default, Directions.Nw, null, 0, null)), 1);
        entity.Yaw.ShouldBe(0); // not at once
        world.Advance(1 + 0.01);
        var arc = Math.IEEERemainder(entity.Yaw, 2 * Math.PI);
        arc.ShouldBeGreaterThan(0);                                 // north to north-west is 45° to the left, not 315° to the right
        arc.ShouldBeLessThan(Math.PI / 4);
        world.Advance(2);
        Math.IEEERemainder(entity.Yaw, 2 * Math.PI).ShouldBe(Math.PI / 4, Tolerance);
    }

    [Fact]
    public void A_still_world_reports_nothing_moved()
    {
        var world = NewWorld(Standing(2, 0, 0), Standing(3, 1, 1));
        foreach (var e in world.Entities) e.PoseDirty = false;
        world.Advance(1).ShouldBeFalse();
        world.Advance(2).ShouldBeFalse();
    }

    [Fact]
    public void Advancing_allocates_nothing()
    {
        var entities = Enumerable.Range(2, 40).Select(i => Walking(i, i, 0, i, 1, 2)).ToArray();
        var world = NewWorld(entities);
        var t = 0.0;
        AllocationProbe.MinAllocatedBytes(() =>
        {
            for (var i = 0; i < 100; i++) world.Advance(t += 0.01);
        }).ShouldBe(0);
    }
}
