using Game.Server.Events;
using Game.Server.World;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Server;

public class WarpTests
{
    private static readonly GridCell Start = new(0, 0);
    private static readonly GridCell Target = new(4, 4);

    private readonly GameWorld _world = Worlds.Make(mapIds: ["map.a", "map.b"]);
    private readonly MapInstance _a;
    private readonly MapInstance _b;
    private readonly List<string> _events = new();

    public WarpTests()
    {
        _a = _world.GetInstance("map.a");
        _b = _world.GetInstance("map.b");
        _a.Events.SubscribeAll(e => _events.Add("a:" + Describe(e)));
        _b.Events.SubscribeAll(e => _events.Add("b:" + Describe(e)));
    }

    private static string Describe(IGameEvent e) => e switch
    {
        EntityEnteredMap x => $"{x.Name} {x.Entity} {x.MapId} {x.Cell} {x.Reason}",
        EntityLeftMap x => $"{x.Name} {x.Entity} {x.MapId} {x.Cell} {x.Reason}",
        WarpRejected x => $"{x.Name} {x.Entity} {x.TargetMapId} {x.TargetCell} {x.Reason}",
        _ => e.Name,
    };

    private EntityId SpawnOnA()
    {
        var id = _world.Spawn(EntityKind.Player, "map.a", Start, Directions.S);
        _a.Tick();
        _events.Clear();
        return id;
    }

    [Fact]
    public void Warp_to_another_instance_leaves_at_end_of_tick_and_enters_on_targets_next_tick()
    {
        var id = SpawnOnA();
        _world.Warp(id, "map.b", Target).ShouldBeTrue();

        _a.Tick();
        _a.TryGetEntity(id, out _).ShouldBeFalse();
        _b.TryGetEntity(id, out _).ShouldBeFalse(); // in flight until b ticks
        _world.LocationOf(id).ShouldBe("map.b");
        _events.ShouldBe([$"a:{EventNames.EntityLeftMap} {id} map.a (0, 0) Warp"]);

        _b.Tick();
        _b.TryGetEntity(id, out var entity).ShouldBeTrue();
        (entity.MapId, entity.Cell).ShouldBe(("map.b", Target));
        _events.ShouldBe([
            $"a:{EventNames.EntityLeftMap} {id} map.a (0, 0) Warp",
            $"b:{EventNames.EntityEnteredMap} {id} map.b (4, 4) Warp",
        ]);
    }

    [Fact]
    public void Warp_requested_by_a_system_works_the_same()
    {
        var pipeline = new SystemPipeline().Add(() => new LambdaSystem("warper", ctx =>
        {
            foreach (var intent in ctx.Intents) ctx.Warp(intent.Entity, "map.b", Target);
        }, typeof(Step)));
        var world = Worlds.Make(pipeline, mapIds: ["map.a", "map.b"]);
        var id = world.Spawn(EntityKind.Player, "map.a", Start, Directions.S);
        world.GetInstance("map.a").Tick();

        world.TryEnqueue(new Step(id));
        world.GetInstance("map.a").Tick();
        world.GetInstance("map.b").Tick();

        world.GetInstance("map.b").Entities.ShouldHaveSingleItem().Id.ShouldBe(id);
        world.GetInstance("map.a").Entities.ShouldBeEmpty();
    }

    [Fact]
    public void Warp_to_an_unwalkable_cell_is_rejected_and_the_entity_stays()
    {
        var id = SpawnOnA();
        _world.Warp(id, "map.b", Grids.Wall);
        _a.Tick();
        _b.Tick();

        _a.TryGetEntity(id, out var entity).ShouldBeTrue();
        (entity.MapId, entity.Cell).ShouldBe(("map.a", Start));
        _b.Entities.ShouldBeEmpty();
        _world.LocationOf(id).ShouldBe("map.a");
        _events.ShouldBe([$"a:{EventNames.WarpRejected} {id} map.b (2, 2) target cell not walkable"]);
    }

    [Fact]
    public void Warp_outside_the_grid_or_to_an_unknown_map_is_rejected()
    {
        var id = SpawnOnA();
        _world.Warp(id, "map.b", new GridCell(-1, 0));
        _world.Warp(id, "map.nowhere", Target);
        _a.Tick();

        _a.TryGetEntity(id, out _).ShouldBeTrue();
        _events.ShouldBe([
            $"a:{EventNames.WarpRejected} {id} map.b (-1, 0) target cell not walkable",
            $"a:{EventNames.WarpRejected} {id} map.nowhere (4, 4) unknown map",
        ]);
    }

    [Fact]
    public void Warp_within_the_same_map_moves_the_entity_in_place()
    {
        var id = SpawnOnA();
        _world.Warp(id, "map.a", Target);
        _a.Tick();

        _a.TryGetEntity(id, out var entity).ShouldBeTrue();
        entity.Cell.ShouldBe(Target);
        _events.ShouldBe([
            $"a:{EventNames.EntityLeftMap} {id} map.a (0, 0) Warp",
            $"a:{EventNames.EntityEnteredMap} {id} map.a (4, 4) Warp",
        ]);
    }

    [Fact]
    public void Intents_follow_the_entity_to_its_new_instance()
    {
        var id = SpawnOnA();
        _world.Warp(id, "map.b", Target);
        _a.Tick();
        _b.Tick();

        _world.TryEnqueue(new Step(id)).ShouldBe(EnqueueResult.Accepted);
        _b.Intents.Count.ShouldBe(1);
        _a.Intents.Count.ShouldBe(0);
    }

    // ---- a removal during a cross-map warp is never lost (T-214, ADR-002 #59) ----

    [Fact]
    public void A_despawn_queued_behind_a_warp_follows_the_entity_and_it_never_arrives()
    {
        var id = SpawnOnA();
        _world.Warp(id, "map.b", Target).ShouldBeTrue();
        _world.Despawn(id).ShouldBeTrue();   // the gateway saw a drop: a, which still holds it, gets the removal after the warp

        _a.Tick();                           // warp applied, so the removal finds nothing on a and follows the entity to b
        _b.Tick();                           // the add sees the pending removal: dropped
        _b.Tick();                           // the forwarded removal finds nothing and is done

        _b.TryGetEntity(id, out _).ShouldBeFalse();
        _a.TryGetEntity(id, out _).ShouldBeFalse();
        _world.LocationOf(id).ShouldBeNull();
        _world.RemovalPending(id).ShouldBeFalse();
        _events.ShouldBe([$"a:{EventNames.EntityLeftMap} {id} map.a (0, 0) Warp"]); // it entered nowhere
    }

    [Fact]
    public void A_despawn_while_the_warp_is_in_flight_drops_the_arrival()
    {
        var id = SpawnOnA();
        _world.Warp(id, "map.b", Target).ShouldBeTrue();
        _a.Tick();                           // in flight: located on b, not added yet
        _world.Despawn(id).ShouldBeTrue();
        _b.Tick();

        _b.TryGetEntity(id, out _).ShouldBeFalse();
        _world.LocationOf(id).ShouldBeNull();
        _world.RemovalPending(id).ShouldBeFalse();
        _b.Counters.StructuralIgnored.ShouldBe(1);   // the removal itself found nothing: the add had already been dropped
    }

    [Fact]
    public void A_despawn_before_the_spawn_is_applied_means_it_never_joins_and_its_initializer_never_runs()
    {
        var initialized = false;
        var id = _world.Spawn(EntityKind.Player, "map.a", Start, Directions.S, _ => initialized = true);
        _world.Despawn(id).ShouldBeTrue();
        _a.Tick();

        initialized.ShouldBeFalse();
        _a.Entities.ShouldBeEmpty();
        _world.LocationOf(id).ShouldBeNull();
        _events.ShouldBeEmpty();
    }

    [Fact]
    public void A_despawn_after_arrival_removes_it_normally()
    {
        var id = SpawnOnA();
        _world.Warp(id, "map.b", Target).ShouldBeTrue();
        _a.Tick();
        _b.Tick();
        _world.Despawn(id).ShouldBeTrue();
        _b.Tick();

        _b.TryGetEntity(id, out _).ShouldBeFalse();
        _world.LocationOf(id).ShouldBeNull();
        _world.RemovalPending(id).ShouldBeFalse();
        _events.ShouldContain($"b:{EventNames.EntityLeftMap} {id} map.b (4, 4) Spawn");
    }
}
