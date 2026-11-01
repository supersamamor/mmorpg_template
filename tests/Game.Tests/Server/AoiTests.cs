using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>
/// T-204: the AOI phase. The rig uses 4-cell buckets and a view radius of 1 on open 24x16 maps, so an observer in bucket (0, 0)
/// (cells 0-3) sees cells 0-7 in both axes. Walkers move at 5 cells per second: one orthogonal cell every 2 ticks.
/// </summary>
public class AoiTests
{
    private static GridCell C(int x, int y) => new(x, y);

    [Fact]
    public void An_observer_gets_a_snapshot_of_itself_when_it_joins()
    {
        var rig = new AoiRig();
        var (o, sink) = rig.AddObserver(C(1, 1));
        rig.Ticks();

        sink.Lines.ShouldBe(["t0 snap map.a #1"]);
        var self = sink.Snapshots.Single().Entities.Single();
        self.ShouldBe(new EntityView(o, EntityKind.Player, null, C(1, 1), Directions.S, null, 0));
        sink.Snapshots[0].Observer.ShouldBe(o);
    }

    [Fact]
    public void An_idle_view_gets_no_messages_even_while_things_move_out_of_sight()
    {
        var rig = new AoiRig();
        var (_, sink) = rig.AddObserver(C(1, 1));
        rig.Add(C(2, 2));
        var far = rig.Add(C(20, 12));
        rig.Ticks();
        sink.Count.ShouldBe(1);

        rig.Send(new MoveTo(far, C(14, 9)));
        rig.Ticks(30);
        sink.Count.ShouldBe(1);
        rig.Aoi(rig.A).DeltasSent.ShouldBe(0);
    }

    [Fact]
    public void A_walker_appears_when_it_enters_the_window_changes_while_inside_and_disappears_when_it_leaves()
    {
        var rig = new AoiRig();
        var (_, sink) = rig.AddObserver(C(1, 1));
        var w = rig.Add(C(10, 1), key: "monster.test");
        rig.Ticks();
        sink.Bodies.ShouldBe(["snap map.a #1"]);

        rig.Send(new MoveTo(w, C(5, 1)));
        rig.Ticks(20);
        sink.Bodies.ShouldBe(["snap map.a #1", "+#2", "~#2[cell,next]", "~#2[cell,next,speed]"]);
        var appeared = sink.Deltas[0].Appeared.Single();
        appeared.ShouldBe(new EntityView(w, EntityKind.Monster, "monster.test", C(7, 1), Directions.W, C(6, 1), 5));   // x 7 is the first cell of bucket 1
        sink.Deltas[1].Changed.Single().View.Next.ShouldBe(C(5, 1));
        var arrived = sink.Deltas[2].Changed.Single().View;
        arrived.Cell.ShouldBe(C(5, 1));
        arrived.Next.ShouldBeNull();
        arrived.Speed.ShouldBe(0);

        rig.Send(new MoveTo(w, C(12, 1)));
        rig.Ticks(20);
        sink.Bodies.Skip(4).ShouldBe(["~#2[facing,next,speed]", "~#2[cell,next]", "~#2[cell,next]", "-#2"]);
        sink.Deltas.Last().Disappeared.ShouldBe([w]);
    }

    [Fact]
    public void Changed_lists_only_entities_and_fields_that_really_changed()
    {
        var rig = new AoiRig();
        var (_, sink) = rig.AddObserver(C(1, 1));
        rig.Add(C(2, 2));
        var w = rig.Add(C(3, 1));
        rig.Ticks();

        rig.Send(new MoveTo(w, C(3, 5)));   // straight south: it already faces south
        rig.Ticks(15);
        sink.Bodies.ShouldBe(["snap map.a #1 #2 #3", "~#3[next,speed]", "~#3[cell,next]", "~#3[cell,next]", "~#3[cell,next]", "~#3[cell,next,speed]"]);
        var start = sink.Deltas[0].Changed.Single();
        start.View.Next.ShouldBe(C(3, 2));
        start.View.Speed.ShouldBe(5);
        sink.Deltas.ShouldAllBe(d => d.Appeared.Count == 0 && d.Disappeared.Count == 0);
    }

    [Fact]
    public void Each_step_is_one_change_not_one_per_tick()
    {
        var rig = new AoiRig();
        var (_, sink) = rig.AddObserver(C(1, 1));
        var w = rig.Add(C(0, 6));
        rig.Ticks();
        rig.Send(new MoveTo(w, C(7, 6)));
        rig.Ticks(14);   // 7 cells at 2 ticks each

        sink.Deltas.Count.ShouldBe(1 + 7);   // the start, then one per cell reached
        sink.Deltas.Select(d => d.Tick).Distinct().Count().ShouldBe(8);
    }

    [Fact]
    public void A_turn_made_by_any_system_is_seen_without_an_event()
    {
        var rig = new AoiRig();
        var (_, sink) = rig.AddObserver(C(1, 1));
        var s = rig.Add(C(2, 2), key: "monster.a");
        rig.Ticks();

        rig.Get(s).Facing = Directions.Ne;
        rig.Ticks();
        rig.Get(s).DefinitionId = "monster.b";
        rig.Ticks();
        sink.Bodies.Skip(1).ShouldBe(["~#2[facing]", "~#2[display_key]"]);
        sink.Deltas[1].Changed.Single().View.DisplayKey.ShouldBe("monster.b");
    }

    [Fact]
    public void Spawns_appear_and_despawns_disappear()
    {
        var rig = new AoiRig();
        var (_, sink) = rig.AddObserver(C(1, 1));
        rig.Ticks();
        var s = rig.Add(C(3, 3), key: "npc.test", kind: EntityKind.Npc);
        rig.Add(C(12, 12));   // out of view: nothing
        rig.Ticks();
        rig.World.Despawn(s);
        rig.Ticks();

        sink.Bodies.ShouldBe(["snap map.a #1", "+#2", "-#2"]);
        sink.Deltas[0].Appeared.Single().ShouldBe(new EntityView(s, EntityKind.Npc, "npc.test", C(3, 3), Directions.S, null, 0));
    }

    [Fact]
    public void Two_observers_with_overlapping_views_each_get_their_own_part()
    {
        var rig = new AoiRig();
        var (_, sink1) = rig.AddObserver(C(1, 1));   // bucket 0: sees cells 0-7
        var (_, sink2) = rig.AddObserver(C(9, 1));   // bucket 2: sees cells 4-15
        var w = rig.Add(C(5, 1));                    // bucket 1: both
        rig.Ticks();
        sink1.Bodies.ShouldBe(["snap map.a #1 #3"]);
        sink2.Bodies.ShouldBe(["snap map.a #2 #3"]);

        rig.Send(new MoveTo(w, C(2, 1)));
        rig.Ticks(10);
        sink1.Bodies.Skip(1).ShouldBe(["~#3[facing,next,speed]", "~#3[cell,next]", "~#3[cell,next]", "~#3[cell,next,speed]"]);
        sink2.Bodies.Skip(1).ShouldBe(["~#3[facing,next,speed]", "~#3[cell,next]", "-#3"]);
    }

    [Fact]
    public void Warping_out_disappears_for_old_neighbours_appears_for_new_ones_and_gives_the_warper_a_snapshot()
    {
        var rig = new AoiRig();
        var (_, near) = rig.AddObserver(C(1, 1));
        var (p, warper) = rig.AddObserver(C(2, 1));
        var (_, there) = rig.AddObserver(C(10, 10), "map.b");
        var stay = rig.Add(C(11, 11), "map.b");
        rig.Ticks();
        near.Bodies.ShouldBe(["snap map.a #1 #2"]);
        warper.Bodies.ShouldBe(["snap map.a #1 #2"]);
        there.Bodies.ShouldBe(["snap map.b #3 #4"]);

        rig.World.Warp(p, "map.b", C(9, 9)).ShouldBeTrue();
        rig.Ticks();
        near.Bodies.Skip(1).ShouldBe(["-#2"]);
        warper.Bodies.Skip(1).ShouldBe(["snap map.b #2 #3 #4"]);
        there.Bodies.Skip(1).ShouldBe(["+#2"]);
        warper.Snapshots[1].MapId.ShouldBe("map.b");
        stay.ShouldNotBe(p);

        // The warper now observes from map.b only.
        rig.World.Despawn(stay);
        rig.Ticks(5);
        warper.Bodies.Skip(2).ShouldBe(["-#4"]);
        rig.Aoi(rig.A).ObserverCount.ShouldBe(1);
        rig.Aoi(rig.B).ObserverCount.ShouldBe(2);
    }

    [Fact]
    public void Walking_onto_a_warp_cell_hands_the_view_over_to_the_target_map()
    {
        var file = new MapGridFile
        {
            MapId = "map.w", Width = 8, Height = 2, Cells = Convert.ToBase64String(Enumerable.Repeat(CellFlagBits.Walkable, 16).ToArray()),
            Warps = [new GridWarp { Cell = C(7, 0), TargetMapId = "map.v", TargetCell = C(1, 1) }],
        };
        var rig = new AoiWarpRig(MapGrid.Create(file), AoiRig.Open("map.v", 8, 8));
        rig.Run();
        rig.Sink.Bodies[0].ShouldBe("snap map.w #1");
        rig.Sink.Bodies.Last().ShouldBe("snap map.v #1");
        rig.Sink.Bodies.Count(b => b.StartsWith("snap", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public void A_warp_within_the_map_gives_the_warper_a_snapshot_and_neighbours_a_disappearance()
    {
        var rig = new AoiRig();
        var (_, near) = rig.AddObserver(C(1, 1));
        var (p, warper) = rig.AddObserver(C(2, 1));
        rig.Ticks();

        rig.World.Warp(p, "map.a", C(20, 12));
        rig.Ticks();
        near.Bodies.Skip(1).ShouldBe(["-#2"]);
        warper.Bodies.Skip(1).ShouldBe(["snap map.a #2"]);
    }

    [Fact]
    public void A_requested_snapshot_resets_the_baseline()
    {
        var rig = new AoiRig();
        var (o, sink) = rig.AddObserver(C(1, 1));
        var a = rig.Add(C(2, 2));
        rig.Ticks();

        rig.Send(new RequestSnapshot(o));
        rig.Send(new RequestSnapshot(a));   // not an observer: ignored
        rig.Add(C(3, 3));                   // joins in the same tick: in the snapshot, not in a delta
        rig.Ticks();
        sink.Bodies.ShouldBe(["snap map.a #1 #2", "snap map.a #1 #2 #3"]);

        rig.World.Despawn(a);
        rig.Ticks(5);
        sink.Bodies.Skip(2).ShouldBe(["-#2"]);
    }

    [Fact]
    public void A_view_can_be_attached_and_detached_by_intent()
    {
        var rig = new AoiRig();
        var p = rig.Add(C(1, 1), kind: EntityKind.Player);
        var w = rig.Add(C(3, 3));
        rig.Ticks();

        var sink = new RecordingSink();
        rig.Send(new AttachView(p, sink));
        rig.Ticks();
        sink.Bodies.ShouldBe(["snap map.a #1 #2"]);
        rig.Get(p).View.ShouldBeSameAs(sink);

        var other = new RecordingSink();
        rig.Send(new AttachView(p, other));   // a new sink (reconnect): it starts from a snapshot
        rig.Ticks();
        other.Bodies.ShouldBe(["snap map.a #1 #2"]);

        rig.Send(new AttachView(p, null));
        rig.Send(new MoveTo(w, C(4, 4)));
        rig.Ticks(5);
        other.Count.ShouldBe(1);
        sink.Count.ShouldBe(1);
        rig.Aoi(rig.A).ObserverCount.ShouldBe(0);
    }

    private sealed class ThrowingSink : IViewSink
    {
        public int Calls;

        public void OnSnapshot(ViewSnapshot snapshot) => Calls++;

        public void OnDelta(ViewDelta delta)
        {
            Calls++;
            throw new InvalidOperationException("socket buffer full");
        }
    }

    [Fact]
    public void A_sink_that_throws_is_logged_and_detached_and_the_tick_goes_on()
    {
        var rig = new AoiRig();
        var bad = new ThrowingSink();
        var o1 = rig.Add(C(1, 1), kind: EntityKind.Player, sink: bad);
        var (_, good) = rig.AddObserver(C(2, 1));
        rig.Ticks();
        bad.Calls.ShouldBe(1);

        rig.Add(C(3, 3));
        rig.Ticks();
        good.Bodies.Skip(1).ShouldBe(["+#3"]);
        bad.Calls.ShouldBe(2);
        rig.Get(o1).View.ShouldBeNull();
        rig.Aoi(rig.A).SinksDetached.ShouldBe(1);
        rig.A.Counters.SystemErrors.ShouldBe(0);
        rig.Log.Lines.ShouldContain(l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("view sink of #1") && l.Contains("socket buffer full"));

        rig.Add(C(4, 4));
        rig.Ticks(3);
        bad.Calls.ShouldBe(2);
        good.Bodies.Skip(2).ShouldBe(["+#4"]);
        rig.A.TickNumber.ShouldBe(5);
    }

    [Fact]
    public void Observers_at_a_map_edge_see_a_clipped_window()
    {
        var rig = new AoiRig(maps: ["map.a"]);
        var (_, sink) = rig.AddObserver(C(23, 15));   // last bucket (5, 3): sees buckets 4-5 x 2-3, cells 16-23 x 8-15
        rig.Add(C(16, 8));
        rig.Add(C(15, 15));
        rig.Ticks();
        sink.Bodies.ShouldBe(["snap map.a #1 #2"]);
    }

    // ---- determinism ----

    private static List<string> RunScripted()
    {
        var rig = new AoiRig(bucketSize: 4, viewRadius: 1, width: 32, height: 24);
        var random = new System.Random(1234);
        var sinks = new List<RecordingSink>();
        var walkers = new List<EntityId>();
        for (var i = 0; i < 4; i++)
        {
            var (id, sink) = rig.AddObserver(C(random.Next(32), random.Next(24)), i % 2 == 0 ? "map.a" : "map.b");
            sinks.Add(sink);
            walkers.Add(id);
        }
        for (var i = 0; i < 30; i++) walkers.Add(rig.Add(C(random.Next(32), random.Next(24)), i % 3 == 0 ? "map.b" : "map.a", agi: random.Next(250)));
        rig.Ticks();

        for (var t = 0; t < 120; t++)
        {
            foreach (var id in walkers)
                if (random.Next(10) == 0) rig.World.TryEnqueue(new MoveTo(id, C(random.Next(32), random.Next(24))));
            if (t % 25 == 10) rig.World.Warp(walkers[random.Next(walkers.Count)], random.Next(2) == 0 ? "map.a" : "map.b", C(random.Next(32), random.Next(24)));
            if (t % 40 == 20) rig.World.TryEnqueue(new RequestSnapshot(walkers[random.Next(4)]));
            rig.Ticks();
        }
        return sinks.SelectMany((s, i) => s.Detailed().Select(line => i + " " + line)).ToList();
    }

    [Fact]
    public void Same_intents_give_identical_message_sequences()
    {
        var first = RunScripted();
        var second = RunScripted();
        first.Count.ShouldBeGreaterThan(100);
        second.ShouldBe(first);
    }

    // ---- data ----

    [Fact]
    public void Numbers_come_from_balance_and_bad_ones_fail_at_construction()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new AoiSystem(0, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => new AoiSystem(6, 0));
        var balance = TestGrids.SampleContent.Balance;
        balance.Aoi.BucketSize.ShouldBeGreaterThan(0);
        balance.Aoi.ViewRadius.ShouldBeGreaterThan(0);
        var systems = Game.Server.Hosting.ServerPipeline.Create(TestGrids.SampleContent).Build();
        systems.OfType<AoiSystem>().Count().ShouldBe(1);
        systems[^1].ShouldBeOfType<AoiSystem>();
    }
}

/// <summary>One observer walking east along a 2-row map onto a warp at (7, 0) that leads to <c>map.v</c>.</summary>
internal sealed class AoiWarpRig
{
    private readonly GameWorld _world;
    private readonly EntityId _walker;

    public readonly RecordingSink Sink = new();

    public AoiWarpRig(MapGrid from, MapGrid to)
    {
        var pipeline = new SystemPipeline().Add(() => new MovementSystem(TestGrids.Roomy)).Add(() => new AoiSystem(4, 1));
        _world = new GameWorld([from, to], pipeline, 3, new ManualClock(), new ListLog());
        _walker = _world.Spawn(EntityKind.Player, from.MapId, new GridCell(0, 0), Directions.E);
        var source = _world.GetInstance(from.MapId);
        source.Events.Subscribe<EntityEnteredMap>(e =>
        {
            if (!source.TryGetEntity(e.Entity, out var entity)) return;
            entity.Stats = AoiRig.Sheet(AoiRig.Agi5);
            entity.View = Sink;
        });
    }

    public void Run()
    {
        Tick();
        _world.TryEnqueue(new MoveTo(_walker, new GridCell(7, 0)));
        for (var i = 0; i < 20; i++) Tick();
    }

    private void Tick()
    {
        foreach (var instance in _world.Instances) instance.Tick();
    }
}
