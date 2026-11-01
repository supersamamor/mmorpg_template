using Game.Server.Events;
using Game.Server.World;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Server;

/// <summary>
/// Same seed + same intents at the same tick numbers ⇒ same state and same events (T-202 requirement 2). Two worlds run
/// side by side in lockstep with systems that use the instance RNG, spawn, despawn, warp and publish.
/// </summary>
public class SimulationDeterminismTests
{
    private sealed record Moved(EntityId Entity, GridCell Cell, string Facing) : IGameEvent
    {
        public string Name => "TestMoved";
    }

    private static readonly string[] Facings = Directions.All.Ordered.ToArray();

    private static SystemPipeline Pipeline() => new SystemPipeline()
        .Add(() => new LambdaSystem("wander", ctx =>
        {
            foreach (var intent in ctx.Intents)
            {
                if (!ctx.Instance.TryGetEntity(intent.Entity, out var e)) continue;
                var cell = new GridCell(e.Cell.X + ctx.Random.Next(3) - 1, e.Cell.Y + ctx.Random.Next(3) - 1);
                e.Facing = Facings[ctx.Random.Next(Facings.Length)];
                if (ctx.Instance.Grid.IsWalkable(cell.X, cell.Y)) e.Cell = cell;
                ctx.Publish(new Moved(e.Id, e.Cell, e.Facing));
            }
        }, typeof(Step)))
        .Add(() => new LambdaSystem("churn", ctx =>
        {
            if (ctx.TickNumber % 4 == 3)
                ctx.Spawn(EntityKind.Monster, new GridCell(ctx.Random.Next(2), ctx.Random.Next(5)), Directions.S);
            if (ctx.TickNumber % 7 == 6 && ctx.Instance.Entities.Count > 2)
                ctx.Despawn(ctx.Instance.Entities[ctx.Random.Next(ctx.Instance.Entities.Count)].Id);
            if (ctx.TickNumber % 9 == 8 && ctx.Instance.MapId == "map.a" && ctx.Instance.Entities.Count > 0)
                ctx.Warp(ctx.Instance.Entities[0].Id, "map.b", new GridCell(ctx.Random.Next(5), 0));
        }));

    private static (string State, List<string> Events) Run(ulong seed)
    {
        var world = Worlds.Make(Pipeline(), seed: seed, mapIds: ["map.a", "map.b"]);
        var events = new List<string>();
        foreach (var instance in world.Instances)
            instance.Events.SubscribeAll(e => events.Add($"{instance.MapId}@{instance.TickNumber}: {e}"));

        var players = new[]
        {
            world.Spawn(EntityKind.Player, "map.a", new GridCell(0, 0), Directions.S),
            world.Spawn(EntityKind.Player, "map.a", new GridCell(4, 4), Directions.N),
        };
        for (var tick = 0; tick < 60; tick++)
        {
            // Scripted input: which intents arrive before which tick.
            if (tick % 2 == 0) world.TryEnqueue(new Step(players[0]));
            if (tick % 3 == 0) world.TryEnqueue(new Step(players[1]));
            if (tick == 20) world.Warp(players[1], "map.b", new GridCell(1, 1));
            foreach (var instance in world.Instances) instance.Tick();
        }

        var state = string.Join("\n", world.Instances.SelectMany(i =>
            i.Entities.Select(e => $"{i.MapId} {e.Id} {e.Kind} {e.Cell} {e.Facing} dead={e.Dead}")));
        return (state, events);
    }

    [Fact]
    public void Same_seed_and_inputs_give_identical_state_and_events()
    {
        var first = Run(seed: 12345);
        var second = Run(seed: 12345);

        first.Events.Count.ShouldBeGreaterThan(50); // the script actually exercises the systems
        first.Events.ShouldContain(e => e.Contains(EventNames.EntityLeftMap) && e.Contains("Warp"));
        second.State.ShouldBe(first.State);
        second.Events.ShouldBe(first.Events);
    }

    [Fact]
    public void A_different_seed_diverges()
    {
        Run(seed: 1).Events.ShouldNotBe(Run(seed: 2).Events);
    }
}
