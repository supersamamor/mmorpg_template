using Game.Server.World;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Server;

namespace Game.Tests.Network;

/// <summary>The thread-safe join entry point the gateway uses: <c>GameWorld.Spawn(..., initialize)</c>.</summary>
public class SpawnInitializerTests
{
    [Fact]
    public void Initializer_runs_on_the_tick_before_EntityEnteredMap_and_the_aoi()
    {
        var rig = new AoiRig();
        var order = new List<string>();
        rig.A.Events.Subscribe<EntityEnteredMap>(e => order.Add("entered:" + (rig.A.TryGetEntity(e.Entity, out var x) ? x.DefinitionId : "?")));
        var sink = new RecordingSink();

        var id = rig.World.Spawn(EntityKind.Player, "map.a", new GridCell(1, 1), Directions.S, entity =>
        {
            order.Add("init");
            entity.DefinitionId = "job.novice";
            entity.Stats = AoiRig.Sheet(1);
            entity.View = sink;
        });
        order.ShouldBeEmpty();      // nothing happens off the tick
        rig.Ticks();

        order.ShouldBe(["init", "entered:job.novice"]);
        sink.Snapshots.Single().Entities.Single().Id.ShouldBe(id);
        sink.Snapshots[0].Entities[0].DisplayKey.ShouldBe("job.novice");
    }

    [Fact]
    public void A_throwing_initializer_is_logged_and_the_entity_still_joins()
    {
        var rig = new AoiRig();
        var id = rig.World.Spawn(EntityKind.Player, "map.a", new GridCell(1, 1), Directions.S, _ => throw new InvalidOperationException("boom"));
        rig.Ticks();
        rig.A.TryGetEntity(id, out _).ShouldBeTrue();
        rig.A.Counters.HandlerErrors.ShouldBe(1);
        rig.Log.Lines.ShouldContain(l => l.Contains("initializing") && l.Contains("boom"));
    }
}
