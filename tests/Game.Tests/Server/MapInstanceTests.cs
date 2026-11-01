using Game.Server.Events;
using Game.Server.World;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Server;

public class MapInstanceTests
{
    private static readonly GridCell Origin = new(0, 0);

    [Fact]
    public void Phases_run_in_order_drain_systems_structural_events()
    {
        var trace = new List<string>();
        GameWorld world = null!;
        EntityId spawned = default;
        var pipeline = new SystemPipeline()
            .Add(() => new LambdaSystem("first", ctx =>
            {
                if (ctx.TickNumber != 1) return;
                trace.Add($"first intents={ctx.Intents.Count}");
                spawned = ctx.Spawn(EntityKind.Monster, new GridCell(1, 1), Directions.S);
                trace.Add($"first sees spawned={ctx.Instance.TryGetEntity(spawned, out _)}");
                // Enqueued mid-tick: must wait for the next tick's drain.
                world.TryEnqueue(new Poke(ctx.Intents.Cast<Poke>().Single().Entity, 2)).ShouldBe(EnqueueResult.Accepted);
            }, typeof(Poke)))
            .Add(() => new LambdaSystem("second", ctx =>
            {
                if (ctx.TickNumber != 1) return;
                trace.Add($"second intents={ctx.Intents.Count} sees spawned={ctx.Instance.TryGetEntity(spawned, out _)}");
                ctx.Publish(new Note("from second"));
            }));
        world = Worlds.Make(pipeline);
        var instance = world.GetInstance("map.a");
        var player = world.Spawn(EntityKind.Player, "map.a", Origin, Directions.S);
        instance.Tick();

        instance.Events.Subscribe<Note>(n => trace.Add($"event {n.Text}"));
        instance.Events.Subscribe<EntityEnteredMap>(e => trace.Add($"event entered present={instance.TryGetEntity(e.Entity, out _)}"));
        world.TryEnqueue(new Poke(player, 1));
        instance.Tick();

        trace.ShouldBe([
            "first intents=1",
            "first sees spawned=False",
            "second intents=0 sees spawned=False",
            "event from second", // raised in phase 2, before the phase-3 entered event
            "event entered present=True",
        ]);
        instance.Intents.Count.ShouldBe(1); // the mid-tick Poke waits for tick 2
        instance.TickNumber.ShouldBe(2);
    }

    [Fact]
    public void Despawn_is_deferred_to_phase_three()
    {
        var seenByLater = new List<bool>();
        EntityId target = default;
        var pipeline = new SystemPipeline()
            .Add(() => new LambdaSystem("killer", ctx => { if (ctx.TickNumber == 1) ctx.Despawn(target); }))
            .Add(() => new LambdaSystem("observer", ctx => seenByLater.Add(ctx.Instance.TryGetEntity(target, out _))));
        var world = Worlds.Make(pipeline);
        var instance = world.GetInstance("map.a");
        var left = new List<EntityLeftMap>();
        instance.Events.Subscribe<EntityLeftMap>(left.Add);
        target = world.Spawn(EntityKind.Monster, "map.a", Origin, Directions.N);

        instance.Tick(); // tick 0: added in phase 3, after the observer ran
        instance.Tick(); // tick 1: despawned in phase 3, after the observer ran
        instance.Tick();

        seenByLater.ShouldBe([false, true, false]);
        instance.Entities.ShouldBeEmpty();
        left.ShouldHaveSingleItem().Reason.ShouldBe(MapChangeReason.Spawn);
        world.LocationOf(target).ShouldBeNull();
    }

    [Fact]
    public void External_spawn_appears_on_next_tick_and_ids_are_never_reused()
    {
        var world = Worlds.Make();
        var instance = world.GetInstance("map.a");
        var first = world.Spawn(EntityKind.Npc, "map.a", Origin, Directions.E);
        instance.Entities.ShouldBeEmpty();
        world.LocationOf(first).ShouldBe("map.a");

        instance.Tick();
        var entity = instance.Entities.ShouldHaveSingleItem();
        (entity.Id, entity.Kind, entity.MapId, entity.Cell, entity.Facing).ShouldBe((first, EntityKind.Npc, "map.a", Origin, Directions.E));

        world.Despawn(first).ShouldBeTrue();
        instance.Tick();
        var second = world.Spawn(EntityKind.Npc, "map.a", Origin, Directions.E);
        second.Value.ShouldBeGreaterThan(first.Value);
        world.Despawn(first).ShouldBeFalse();
    }

    [Fact]
    public void Spawn_rejects_unwalkable_cell_unknown_map_and_bad_facing()
    {
        var world = Worlds.Make();
        Should.Throw<ArgumentException>(() => world.Spawn(EntityKind.Monster, "map.a", Grids.Wall, Directions.N));
        Should.Throw<ArgumentException>(() => world.Spawn(EntityKind.Monster, "map.a", new GridCell(9, 9), Directions.N));
        Should.Throw<ArgumentException>(() => world.Spawn(EntityKind.Monster, "map.nowhere", Origin, Directions.N));
        Should.Throw<ArgumentException>(() => world.Spawn(EntityKind.Monster, "map.a", Origin, "up"));
    }

    [Fact]
    public void A_throwing_system_is_logged_and_the_tick_continues()
    {
        var log = new ListLog();
        var ran = new List<long>();
        var pipeline = new SystemPipeline()
            .Add(() => new LambdaSystem("boom", ctx => { if (ctx.TickNumber == 0) throw new InvalidOperationException("kaput"); }))
            .Add(() => new LambdaSystem("after", ctx => ran.Add(ctx.TickNumber)));
        var world = Worlds.Make(pipeline, log);
        var instance = world.GetInstance("map.a");
        var notes = new List<Note>();
        instance.Events.Subscribe<Note>(notes.Add);
        world.Spawn(EntityKind.Player, "map.a", Origin, Directions.S);

        instance.Tick();
        instance.Tick();

        ran.ShouldBe([0, 1]);
        instance.Entities.Count.ShouldBe(1); // phase 3 still ran on the failing tick
        instance.Counters.SystemErrors.ShouldBe(1);
        var error = log.Lines.ShouldHaveSingleItem();
        error.ShouldStartWith("ERROR");
        error.ShouldContain("map.a");
        error.ShouldContain("tick 0");
        error.ShouldContain("'boom'");
        error.ShouldContain("kaput");
    }

    [Fact]
    public void Intents_are_routed_to_their_owner_in_arrival_order_and_bad_ones_are_counted()
    {
        var pokes = new List<int>();
        var steps = 0;
        var pipeline = new SystemPipeline()
            .Add(() => new LambdaSystem("pokes", ctx => pokes.AddRange(ctx.Intents.Cast<Poke>().Select(p => p.N)), typeof(Poke)))
            .Add(() => new LambdaSystem("steps", ctx => steps += ctx.Intents.Count, typeof(Step)));
        var world = Worlds.Make(pipeline);
        var instance = world.GetInstance("map.a");
        var alive = world.Spawn(EntityKind.Player, "map.a", Origin, Directions.S);
        var dead = world.Spawn(EntityKind.Player, "map.a", new GridCell(1, 0), Directions.S);
        instance.Tick();
        instance.TryGetEntity(dead, out var deadEntity).ShouldBeTrue();
        deadEntity.Dead = true;

        world.TryEnqueue(new Poke(alive, 1));
        world.TryEnqueue(new Step(alive));
        world.TryEnqueue(new Poke(alive, 2));
        world.TryEnqueue(new Poke(dead, 3));
        world.TryEnqueue(new Unowned(alive));
        instance.TryEnqueue(new Poke(new EntityId(999), 4)).ShouldBe(EnqueueResult.Accepted);
        world.TryEnqueue(new Poke(new EntityId(999), 5)).ShouldBe(EnqueueResult.UnknownEntity);
        instance.Tick();

        pokes.ShouldBe([1, 2]);
        steps.ShouldBe(1);
        instance.Counters.IntentsRouted.ShouldBe(3);
        instance.Counters.IntentsDroppedDeadEntity.ShouldBe(1);
        instance.Counters.IntentsDroppedUnrouted.ShouldBe(1);
        instance.Counters.IntentsDroppedUnknownEntity.ShouldBe(1);
        world.IntentsUnknownEntity.ShouldBe(1);
    }

    [Fact]
    public void Intent_queue_is_bounded_and_refuses_when_full()
    {
        var seen = 0;
        var pipeline = new SystemPipeline().Add(() => new LambdaSystem("pokes", ctx => seen += ctx.Intents.Count, typeof(Poke)));
        var world = Worlds.Make(pipeline, capacity: 3);
        var instance = world.GetInstance("map.a");
        var player = world.Spawn(EntityKind.Player, "map.a", Origin, Directions.S);
        instance.Tick();

        Enumerable.Range(1, 4).Select(n => world.TryEnqueue(new Poke(player, n)))
            .ShouldBe([EnqueueResult.Accepted, EnqueueResult.Accepted, EnqueueResult.Accepted, EnqueueResult.QueueFull]);
        instance.Intents.RejectedFull.ShouldBe(1);
        instance.Tick();
        seen.ShouldBe(3);

        world.TryEnqueue(new Poke(player, 5)).ShouldBe(EnqueueResult.Accepted); // drained, room again
    }

    [Fact]
    public void Intent_queue_accepts_concurrent_producers_without_loss()
    {
        var queue = new IntentQueue(capacity: 100_000);
        Parallel.For(0, 8, p =>
        {
            for (var i = 0; i < 1000; i++) queue.TryEnqueue(new Poke(new EntityId(p), i)).ShouldBe(EnqueueResult.Accepted);
        });
        queue.Count.ShouldBe(8000);
    }

    [Fact]
    public void Pipeline_rejects_two_owners_for_one_intent_type()
    {
        var pipeline = new SystemPipeline()
            .Add(() => new LambdaSystem("a", _ => { }, typeof(Poke)))
            .Add(() => new LambdaSystem("b", _ => { }, typeof(Poke)));
        Should.Throw<InvalidOperationException>(() => pipeline.Build()).Message.ShouldContain("Poke");
        Should.Throw<InvalidOperationException>(() => new SystemPipeline().Add(() => new LambdaSystem("x", _ => { }, typeof(string))).Build());
    }

    [Fact]
    public void Each_instance_gets_its_own_system_objects()
    {
        var pipeline = new SystemPipeline().Add(() => new LambdaSystem("s", _ => { }));
        var world = Worlds.Make(pipeline, mapIds: ["map.a", "map.b"]);
        world.GetInstance("map.a").Systems[0].ShouldNotBeSameAs(world.GetInstance("map.b").Systems[0]);
        world.Instances.Select(i => i.MapId).ShouldBe(["map.a", "map.b"]);
    }

    [Fact]
    public void New_world_event_names_are_vocabulary_terms()
    {
        IGameEvent[] events =
        [
            new EntityEnteredMap(new EntityId(1), EntityKind.Player, "map.a", Origin, MapChangeReason.Spawn),
            new EntityLeftMap(new EntityId(1), EntityKind.Player, "map.a", Origin, MapChangeReason.Spawn),
            new WarpRejected(new EntityId(1), "map.a", "map.b", Origin, "x"),
        ];
        foreach (var e in events) EventNames.All.Contains(e.Name).ShouldBeTrue(e.Name);
    }
}
