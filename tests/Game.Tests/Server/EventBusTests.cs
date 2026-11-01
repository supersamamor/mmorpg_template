using Game.Server.Events;
using Game.Server.World;
using Game.Shared.World;

namespace Game.Tests.Server;

public class EventBusTests
{
    private readonly List<(IGameEvent Event, Exception Error)> _errors = new();
    private readonly EventBus _bus;

    public EventBusTests() => _bus = new EventBus((e, ex) => _errors.Add((e, ex)));

    private static EntityLeftMap Left(long id) => new(new EntityId(id), EntityKind.Monster, "map.a", new GridCell(0, 0), MapChangeReason.Spawn);

    [Fact]
    public void Publish_only_queues_and_dispatch_delivers_in_raise_order_across_types()
    {
        var seen = new List<string>();
        _bus.Subscribe<Note>(n => seen.Add("note " + n.Text));
        _bus.Subscribe<EntityLeftMap>(l => seen.Add("left " + l.Entity));

        _bus.Publish(new Note("1"));
        _bus.Publish(Left(7));
        _bus.Publish(new Note("2"));
        seen.ShouldBeEmpty();
        _bus.PendingCount.ShouldBe(3);

        _bus.Dispatch().ShouldBe(3);
        seen.ShouldBe(["note 1", "left #7", "note 2"]);
        _bus.Dispatch().ShouldBe(0);
    }

    [Fact]
    public void Events_published_by_handlers_wait_for_the_next_dispatch()
    {
        var seen = new List<string>();
        _bus.Subscribe<Note>(n =>
        {
            seen.Add(n.Text);
            if (n.Text == "outer") _bus.Publish(new Note("nested"));
        });
        _bus.Publish(new Note("outer"));
        _bus.Publish(new Note("sibling"));

        _bus.Dispatch();
        seen.ShouldBe(["outer", "sibling"]);
        _bus.PendingCount.ShouldBe(1);

        _bus.Dispatch();
        seen.ShouldBe(["outer", "sibling", "nested"]);
    }

    [Fact]
    public void Handlers_run_in_subscription_order_and_SubscribeAll_runs_after_typed()
    {
        var seen = new List<string>();
        _bus.SubscribeAll(_ => seen.Add("all"));
        _bus.Subscribe<Note>(_ => seen.Add("typed 1"));
        _bus.Subscribe<Note>(_ => seen.Add("typed 2"));
        _bus.Publish(new Note("x"));
        _bus.Dispatch();
        seen.ShouldBe(["typed 1", "typed 2", "all"]);
    }

    [Fact]
    public void A_throwing_handler_is_reported_and_the_others_still_run()
    {
        var seen = new List<string>();
        _bus.Subscribe<Note>(_ => throw new InvalidOperationException("bad handler"));
        _bus.Subscribe<Note>(n => seen.Add(n.Text));
        _bus.Publish(new Note("a"));
        _bus.Publish(new Note("b"));

        _bus.Dispatch();
        seen.ShouldBe(["a", "b"]);
        _errors.Count.ShouldBe(2);
        _errors[0].Error.Message.ShouldBe("bad handler");
    }

    [Fact]
    public void Disposing_a_subscription_unsubscribes()
    {
        var count = 0;
        var sub = _bus.Subscribe<Note>(_ => count++);
        var all = _bus.SubscribeAll(_ => count += 10);
        _bus.Publish(new Note("1"));
        _bus.Dispatch();
        sub.Dispose();
        all.Dispose();
        sub.Dispose();
        _bus.Publish(new Note("2"));
        _bus.Dispatch();
        count.ShouldBe(11);
    }

    [Fact]
    public void Instance_handler_errors_are_logged_with_map_and_tick()
    {
        var log = new ListLog();
        var world = Worlds.Make(log: log);
        var instance = world.GetInstance("map.a");
        instance.Events.Subscribe<EntityEnteredMap>(_ => throw new InvalidOperationException("nope"));
        world.Spawn(EntityKind.Npc, "map.a", new GridCell(0, 0), "n");

        instance.Tick();
        instance.Counters.HandlerErrors.ShouldBe(1);
        log.Lines.ShouldHaveSingleItem().ShouldContain("[map.a] tick 0");
    }
}
