using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Combat;
using Game.Server.World.Monsters;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;
using ClientView = Game.Tests.Server.AoiCapTests.ClientView;

namespace Game.Tests.Server;

/// <summary>
/// T-217: the crowd cap shared out by kind. As in <see cref="AoiCapTests"/>, the window is much larger than the entities' spread, so
/// whatever is cut is cut by the cap. Players stand at distance 1-2 as the crowd; everything else is farther, so only its share can show it.
/// </summary>
public class AoiCapFairnessTests
{
    private static readonly GridCell Center = new(16, 16);

    private static GridCell At(int distance, int row = 0) => new(Center.X + distance, Center.Y + row);

    private static AoiRig Rig(int maxVisible, int hysteresis, CapReservations reservations) =>
        new(32, 1, 64, 64, maxVisible, hysteresis, reservations, "map.a");

    private static List<EntityId> Crowd(AoiRig rig, int count) =>
        Enumerable.Range(0, count).Select(i => rig.Add(At(1 + i % 2), kind: EntityKind.Player)).ToList();

    private static List<EntityId> Line(AoiRig rig, EntityKind kind, int count, int from) =>
        Enumerable.Range(0, count).Select(i => rig.Add(At(from + i), kind: kind)).ToList();

    private static EntityId AddItem(AoiRig rig, GridCell cell, EntityId? owner, long ownerUntilMs = 1_000_000) =>
        rig.World.Spawn(EntityKind.Item, "map.a", cell, Directions.S, e =>
        {
            e.DefinitionId = "item.test";
            e.GroundItem = new GroundItem("item.test", 1, new EntityId(9999), owner, 0, ownerUntilMs, 10_000_000);
        });

    // ---- reservations ----

    [Fact]
    public void Each_reservation_holds_in_a_crowd_with_the_nearest_of_its_kind()
    {
        var rig = Rig(20, 2, new CapReservations(Monsters: 5, Npcs: 2, Items: 3));
        var (me, sink) = rig.AddObserver(Center);
        Crowd(rig, 40);
        var monsters = Line(rig, EntityKind.Monster, 8, from: 5);
        var npcs = Line(rig, EntityKind.Npc, 4, from: 5);
        var items = Line(rig, EntityKind.Item, 6, from: 5);
        rig.Ticks();

        var shown = sink.Snapshots.Single().Entities;
        shown.Count.ShouldBe(18);   // the cutoff: a first look at a crowd lets nothing in past it
        shown.Select(e => e.Id).ShouldContain(me);
        shown.Where(e => e.Kind == EntityKind.Monster).Select(e => e.Id).ShouldBe(monsters.Take(5));
        shown.Where(e => e.Kind == EntityKind.Npc).Select(e => e.Id).ShouldBe(npcs.Take(2));
        shown.Where(e => e.Kind == EntityKind.Item).Select(e => e.Id).ShouldBe(items.Take(3));
        shown.Count(e => e.Kind == EntityKind.Player).ShouldBe(8);   // the observer and the 7 slots left
    }

    [Fact]
    public void Slots_a_kind_does_not_use_go_back_to_the_others_nearest_first()
    {
        var rig = Rig(20, 2, new CapReservations(Monsters: 5, Npcs: 2, Items: 3));
        var (_, sink) = rig.AddObserver(Center);
        var players = Enumerable.Range(0, 40).Select(i => rig.Add(At(1 + i / 8), kind: EntityKind.Player)).ToList();
        var npc = rig.Add(At(20), kind: EntityKind.Npc);   // one of the 2 NPC slots is used; no monsters or items
        rig.Ticks();

        var shown = sink.Snapshots.Single().Entities.Select(e => e.Id).ToList();
        shown.Count.ShouldBe(18);
        shown.ShouldContain(npc);
        shown.Where(id => id != npc).Skip(1).ShouldBe(players.Take(16));   // the observer, its NPC, then players only
    }

    [Fact]
    public void A_kind_past_its_reservation_still_competes_for_the_remaining_slots()
    {
        var rig = Rig(6, 0, new CapReservations(Monsters: 2));
        var (me, sink) = rig.AddObserver(Center);
        var near = Line(rig, EntityKind.Monster, 4, from: 1);   // all nearer than any player
        var players = Enumerable.Range(0, 3).Select(i => rig.Add(At(10 + i), kind: EntityKind.Player)).ToList();
        rig.Ticks();

        sink.Snapshots.Single().Entities.Select(e => e.Id).ShouldBe(new[] { me }.Concat(near).Concat(players.Take(1)));
    }

    [Fact]
    public void What_is_pinned_of_a_kind_counts_toward_its_reservation()
    {
        var rig = Rig(8, 0, new CapReservations(Monsters: 3));
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        Crowd(rig, 10);
        var monsters = Line(rig, EntityKind.Monster, 6, from: 5);
        rig.Ticks();
        view.Update();
        view.Cells.Keys.Where(monsters.Contains).ShouldBe(monsters.Take(3), ignoreOrder: true);

        var far = monsters[5];
        rig.A.Events.Publish(new AttackStarted(me, far));
        rig.Ticks();
        view.Update();

        view.Cells.Keys.Where(monsters.Contains).ShouldBe([monsters[0], monsters[1], far], ignoreOrder: true);
    }

    [Fact]
    public void Pinned_entities_past_the_cutoff_leave_no_room_for_reservations_but_the_cap_still_holds()
    {
        var rig = Rig(4, 0, new CapReservations(Monsters: 1, Npcs: 1, Items: 1));
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        var attackers = Enumerable.Range(0, 3).Select(i => rig.Add(At(9 + i), kind: EntityKind.Player)).ToList();
        var npc = rig.Add(At(5), kind: EntityKind.Npc);
        rig.Add(At(6));
        rig.Add(At(7), kind: EntityKind.Item);
        rig.Ticks();

        foreach (var attacker in attackers) rig.A.Events.Publish(new DamageDealt(attacker, me, 1, HitResults.Hit, 10));
        rig.Ticks();
        view.Update();

        view.Cells.Keys.OrderBy(k => k.Value).ShouldBe(new[] { me }.Concat(attackers));
        view.Cells.Keys.ShouldNotContain(npc);
    }

    // ---- the viewer's own loot ----

    [Fact]
    public void Loot_this_observer_may_pick_up_ranks_ahead_of_other_ground_items_for_each_observer()
    {
        var rig = Rig(4, 0, new CapReservations(Items: 1));
        var (me, mine) = rig.AddObserver(Center);
        var (other, theirs) = rig.AddObserver(Center);
        rig.Ticks();

        var nearest = AddItem(rig, At(1), other);
        var myLoot = AddItem(rig, At(9), me);
        var free = AddItem(rig, At(12), null);
        rig.Add(At(2), kind: EntityKind.Player);
        rig.Ticks();

        var myView = new ClientView(mine);
        myView.Update();
        var theirView = new ClientView(theirs);
        theirView.Update();
        myView.Cells.Keys.ShouldContain(myLoot);
        myView.Cells.Keys.ShouldNotContain(nearest);   // nearest of all, but another player's
        theirView.Cells.Keys.ShouldContain(nearest);
        theirView.Cells.Keys.ShouldNotContain(myLoot);
        myView.Cells.Keys.ShouldNotContain(free);
    }

    [Fact]
    public void Another_players_loot_ranks_on_distance_once_its_priority_ends()
    {
        var rig = Rig(3, 0, new CapReservations(Items: 1));
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        rig.Ticks();
        var theirs = AddItem(rig, At(1), new EntityId(9998), ownerUntilMs: 1000);
        var free = AddItem(rig, At(9), null);
        rig.Add(At(2), kind: EntityKind.Player);
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(free);
        view.Cells.Keys.ShouldNotContain(theirs);
        me.Value.ShouldBeLessThan(free.Value);

        rig.Ticks(12);   // past 1000 ms: nothing moved, but the priority ending is news for the ranking
        view.Update();
        view.Cells.Keys.ShouldContain(theirs);
        view.Cells.Keys.ShouldNotContain(free);
    }

    [Fact]
    public void Loot_this_observer_may_not_pick_up_goes_behind_everything_for_the_remaining_slots_too()
    {
        var rig = Rig(3, 0, new CapReservations(Items: 1));
        var (_, sink) = rig.AddObserver(Center);
        rig.Ticks();
        AddItem(rig, At(1), null);
        var theirs = AddItem(rig, At(2), new EntityId(9998));
        var farPlayer = rig.Add(At(15), kind: EntityKind.Player);
        rig.Add(At(16), kind: EntityKind.Player);
        rig.Ticks();

        var view = new ClientView(sink);
        view.Update();
        view.Cells.Keys.ShouldContain(farPlayer);
        view.Cells.Keys.ShouldNotContain(theirs);
    }

    // ---- monsters that can reach you ----

    [Fact]
    public void A_monster_that_takes_the_observer_as_target_is_visible_until_it_gives_up_or_dies()
    {
        var rig = Rig(3, 0, new CapReservations(Chasers: 2));
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        Crowd(rig, 4);
        var chaser = rig.Add(At(20));
        var other = rig.Add(At(21));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldNotContain(chaser);

        rig.A.Events.Publish(new AggroGained(chaser, me, AggroReasons.Sighted));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(chaser);

        rig.Ticks(5);
        view.Update();
        view.Cells.Keys.ShouldContain(chaser);

        rig.A.Events.Publish(new AggroLost(chaser, me, AggroReasons.Leash));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldNotContain(chaser);

        rig.A.Events.Publish(new AggroGained(other, me, AggroReasons.Attacked));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(other);
        rig.Get(other).Dead = true;
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldNotContain(other);
    }

    [Fact]
    public void A_monster_chasing_someone_else_gets_no_special_treatment()
    {
        var rig = Rig(3, 0, new CapReservations(Chasers: 2));
        var (_, sink) = rig.AddObserver(Center);
        var crowd = Crowd(rig, 4);
        var chaser = rig.Add(At(20));
        rig.Ticks();

        rig.A.Events.Publish(new AggroGained(chaser, crowd[0], AggroReasons.Sighted));
        rig.Ticks(3);

        var view = new ClientView(sink);
        view.Update();
        view.Cells.Keys.ShouldNotContain(chaser);
    }

    /// <summary>A far aggressive monster walks up to a player in a crowd of nearer monsters. Returns the ticks it was shown before its first hit.</summary>
    private static int TicksShownBeforeTheFirstHit(CapReservations reservations)
    {
        var tank = TestCombat.Monster("monster.t_tank", maxHp: 100_000, atk: 1);
        var hunter = TestCombat.Monster("monster.t_hunter", maxHp: 100_000, atk: 1, aggressive: true, aggroRange: 20, speed: 2);
        var rig = new CombatRig(TestCombat.Open(40, 12, [TestMonsters.Spawn(hunter, 20, 5)]), [tank, hunter], bucketSize: 32, viewRadius: 1, ai: true, aoi: true,
            maxVisible: 4, reservations: reservations);
        var sink = new RecordingSink();
        var view = new ClientView(sink);
        var player = rig.AddPlayer(new GridCell(3, 5));
        for (var i = 0; i < 6; i++) rig.AddMonster(tank, new GridCell(1 + i, 6));   // nearer than the hunter, so the cap alone would hide it
        rig.Ticks();
        rig.Send(new AttachView(player, sink));

        var shown = 0;
        for (var ticks = 0; ticks < 400; ticks++)
        {
            rig.Ticks();
            view.Update();
            if (rig.Of<DamageDealt>().Any()) break;
            var far = rig.Instance.Entities.Single(e => e.DefinitionId == hunter.Id).Id;
            if (view.Cells.ContainsKey(far)) shown++;
            else if (reservations.Chasers > 0) rig.Of<AggroGained>().ShouldNotContain(a => a.Monster == far, "it has its target, so it must be shown");
        }
        rig.Of<DamageDealt>().ShouldNotBeEmpty("it should have reached the player");
        rig.Of<AggroGained>().Single().Target.ShouldBe(player);
        return shown;
    }

    [Fact]
    public void A_real_aggressive_monster_is_visible_before_its_first_hit()
    {
        TicksShownBeforeTheFirstHit(new CapReservations(Chasers: 3)).ShouldBeGreaterThan(50);
        TicksShownBeforeTheFirstHit(default).ShouldBe(0);   // ranked by distance alone, it hit from out of view
    }

    [Fact]
    public void A_train_stays_bounded_and_nearest_first()
    {
        const int limit = 4;
        const int margin = 1;
        var rig = Rig(12, 0, new CapReservations(Margin: margin, Chasers: limit));
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        Crowd(rig, 30);
        var train = Line(rig, EntityKind.Monster, 30, from: 3);
        rig.Ticks();
        foreach (var monster in train) rig.A.Events.Publish(new AggroGained(monster, me, AggroReasons.Sighted));
        rig.Ticks();
        view.Update();

        view.Cells.Count.ShouldBeLessThanOrEqualTo(12);
        view.Cells.Keys.Where(train.Contains).ShouldBe(train.Take(limit), ignoreOrder: true);

        // The train walks past: whoever is nearest is shown, and never more of them than the limit and its margin.
        for (var t = 0; t < 30; t++)
        {
            rig.Get(train[29 - t]).Cell = At(2);
            rig.Ticks();
            view.Update();
            view.Cells.Count.ShouldBeLessThanOrEqualTo(12);
            view.Cells.Keys.Count(train.Contains).ShouldBeLessThanOrEqualTo(limit + margin);
            view.Cells.Keys.Count(train.Contains).ShouldBeGreaterThanOrEqualTo(limit);
        }
    }

    [Fact]
    public void A_chaser_past_the_limit_is_still_shown_with_its_first_hit()
    {
        var rig = Rig(4, 0, new CapReservations(Chasers: 1));
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        Crowd(rig, 4);
        var near = rig.Add(At(3));
        var past = rig.Add(At(4));
        rig.Ticks();
        rig.A.Events.Publish(new AggroGained(near, me, AggroReasons.Sighted));
        rig.A.Events.Publish(new AggroGained(past, me, AggroReasons.Sighted));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(near);
        view.Cells.Keys.ShouldNotContain(past);

        rig.A.Events.Publish(new DamageDealt(past, me, 1, HitResults.Hit, 10));
        rig.Ticks();

        var delta = sink.Deltas[^1];
        delta.Appeared.Select(e => e.Id).ShouldContain(past);
        delta.Hits!.ShouldHaveSingleItem();
    }

    // ---- no flicker ----

    /// <summary>X and Y swap ranks every tick right at the edge of the monsters' reservation, far behind a crowd that fills the open slots.</summary>
    private static (int X, int Y) FlickerAtTheReservation(int margin, int ticks)
    {
        var rig = Rig(8, 0, new CapReservations(Monsters: 2, Margin: margin));
        var (_, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        Crowd(rig, 10);
        rig.Add(At(5));
        var x = rig.Add(At(7));
        var y = rig.Add(At(9));
        rig.Ticks();

        for (var t = 0; t < ticks; t++)
        {
            rig.Get(y).Cell = At(t % 2 == 0 ? 6 : 9);
            rig.Ticks();
            view.Update();
        }
        return (view.Appeared.GetValueOrDefault(x) + view.Disappeared.GetValueOrDefault(x),
            view.Appeared.GetValueOrDefault(y) + view.Disappeared.GetValueOrDefault(y));
    }

    [Fact]
    public void An_entity_at_the_edge_of_its_reservation_does_not_flicker_for_a_hundred_ticks()
    {
        var (x, y) = FlickerAtTheReservation(margin: 1, ticks: 100);
        x.ShouldBe(0);   // shown from the start, it stays
        y.ShouldBe(1);   // came in the first time it ranked inside the reservation, then stayed
    }

    [Fact]
    public void Without_a_margin_the_same_scene_flickers_every_tick()
    {
        var (x, y) = FlickerAtTheReservation(margin: 0, ticks: 100);
        x.ShouldBeGreaterThan(90);
        y.ShouldBeGreaterThan(90);
    }

    [Fact]
    public void A_reservation_changing_under_an_entity_does_not_flicker_it()
    {
        // An attacker comes and goes every tick, so the share left for other monsters changes every tick.
        var rig = Rig(8, 0, new CapReservations(Monsters: 3, Margin: 1));
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        Crowd(rig, 10);
        var monsters = Line(rig, EntityKind.Monster, 3, from: 5);
        var attacker = rig.Add(At(9));
        rig.Ticks();

        for (var t = 0; t < 100; t++)
        {
            if (t % 2 == 0) rig.A.Events.Publish(new DamageDealt(attacker, me, 1, HitResults.Miss, 10));
            else rig.A.Events.Publish(new AggroLost(attacker, me, AggroReasons.Leash));
            rig.Ticks();
            view.Update();
        }

        foreach (var monster in monsters)
        {
            view.Appeared.ShouldNotContainKey(monster);
            view.Disappeared.ShouldNotContainKey(monster);
        }
    }

    [Fact]
    public void A_chaser_at_the_edge_of_the_limit_does_not_flicker_for_a_hundred_ticks()
    {
        var rig = Rig(6, 0, new CapReservations(Margin: 1, Chasers: 2));
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        Crowd(rig, 10);
        rig.Ticks();
        var first = rig.Add(At(5));
        var x = rig.Add(At(7));
        var y = rig.Add(At(9));
        rig.Ticks();
        foreach (var chaser in new[] { first, x, y }) rig.A.Events.Publish(new AggroGained(chaser, me, AggroReasons.Sighted));
        rig.Ticks();

        for (var t = 0; t < 100; t++)
        {
            rig.Get(y).Cell = At(t % 2 == 0 ? 6 : 9);
            rig.Ticks();
            view.Update();
        }

        view.Disappeared.ShouldNotContainKey(x);
        view.Disappeared.ShouldNotContainKey(y);
    }

    [Fact]
    public void Moving_between_a_reservation_and_the_open_slots_does_not_flicker()
    {
        // One monster crosses the crowd, so it leaves the kind's top rank for the open slots and back, every tick; the one it swaps with is far.
        var rig = Rig(8, 2, new CapReservations(Monsters: 1, Margin: 1));
        var (_, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        Crowd(rig, 10);
        var far = rig.Add(At(20));
        var crossing = rig.Add(At(25));
        rig.Ticks();

        for (var t = 0; t < 100; t++)
        {
            rig.Get(crossing).Cell = At(t % 2 == 0 ? 1 : 25);
            rig.Ticks();
            view.Update();
        }

        view.Disappeared.GetValueOrDefault(far).ShouldBe(0);
        view.Disappeared.GetValueOrDefault(crossing).ShouldBe(0);
    }

    // ---- the crowd ----

    /// <summary>The acceptance scene, with the real <c>balance.yaml</c> numbers: 100 walking players around one NPC and a few monsters.</summary>
    [Fact]
    public void A_hundred_players_around_an_npc_and_monsters_leave_them_visible_to_everyone()
    {
        var balance = TestGrids.SampleContent.Balance.Aoi;
        var rig = new AoiRig(balance.BucketSize, balance.ViewRadius, 96, 64, balance.MaxVisible, balance.CapHysteresis, CapReservations.From(balance), "map.a");
        var random = new Random(113);
        var area = new GridRect(30, 30, 12, 12);   // all within 2 buckets of each other
        GridCell Pick() => new(area.X + random.Next(area.W), area.Y + random.Next(area.H));

        var players = Enumerable.Range(0, 100).Select(_ =>
        {
            var (id, sink) = rig.AddObserver(Pick());
            return (Id: id, View: new ClientView(sink));
        }).ToList();
        var npc = rig.Add(new GridCell(36, 36), kind: EntityKind.Npc, key: "npc.test");
        var monsters = Enumerable.Range(0, 8).Select(_ => rig.Add(Pick(), key: "monster.test")).ToList();
        rig.Ticks();

        for (var t = 0; t < 100; t++)
        {
            foreach (var (id, _) in players)
                if (random.Next(10) == 0) rig.Send(new MoveTo(id, Pick()));
            rig.Ticks();
            foreach (var (_, view) in players)
            {
                view.Update();
                view.Cells.Count.ShouldBeLessThanOrEqualTo(balance.MaxVisible);
                view.Cells.Keys.ShouldContain(npc, $"tick {t}");
                foreach (var monster in monsters) view.Cells.Keys.ShouldContain(monster, $"tick {t}");
            }
        }

        // The cap really was cutting: more entities than it allows, all within everyone's window.
        rig.A.Entities.Count.ShouldBeGreaterThan(balance.MaxVisible * 2);
        foreach (var (_, view) in players) view.Cells.Count(c => players.Any(p => p.Id == c.Key)).ShouldBeLessThan(100);
    }

    /// <summary>Random walkers of every kind; each tick the rules are recomputed by brute force from the entities' cells.</summary>
    [Theory]
    [InlineData(11)]
    [InlineData(2024)]
    public void Every_kinds_nearest_are_shown_while_the_crowd_moves(int seed)
    {
        const int cap = 30;
        var reservations = new CapReservations(Monsters: 6, Npcs: 3, Items: 4, Margin: 1);
        var rig = new AoiRig(32, 1, 64, 64, cap, 3, reservations, "map.a");
        var random = new Random(seed);
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        var entities = new List<EntityId>();
        foreach (var (kind, count) in new[] { (EntityKind.Player, 60), (EntityKind.Monster, 20), (EntityKind.Npc, 5), (EntityKind.Item, 10) })
            for (var i = 0; i < count; i++)
                entities.Add(rig.Add(new GridCell(random.Next(32), random.Next(32)), kind: kind, agi: random.Next(250)));
        rig.Ticks();

        for (var t = 0; t < 300; t++)
        {
            foreach (var id in entities)
                if (random.Next(20) == 0) rig.Send(new MoveTo(id, new GridCell(random.Next(32), random.Next(32))));
            rig.Ticks();
            view.Update();

            view.Cells.Count.ShouldBeLessThanOrEqualTo(cap, $"tick {t}");
            view.Cells.Keys.ShouldContain(me);
            foreach (var (kind, reserved) in new[] { (EntityKind.Monster, 6), (EntityKind.Npc, 3), (EntityKind.Item, 4) })
            {
                var nearest = rig.A.Entities.Where(e => e.Kind == kind)
                    .OrderBy(e => GridDistance.Chebyshev(rig.Get(me).Cell, e.Cell)).ThenBy(e => e.Id.Value).Take(reserved);
                foreach (var entity in nearest) view.Cells.Keys.ShouldContain(entity.Id, $"tick {t}: {kind}");
            }
        }
        view.Cells.Count.ShouldBeGreaterThanOrEqualTo(cap - 3);
    }

    // ---- determinism and the rest ----

    private static List<string> ScriptedCrowd()
    {
        var rig = new AoiRig(4, 2, 40, 40, 16, 2, new CapReservations(Monsters: 4, Npcs: 2, Items: 2, Margin: 1, Chasers: 3), "map.a");
        var random = new Random(78);
        var sinks = new List<RecordingSink>();
        var ids = new List<EntityId>();
        for (var i = 0; i < 4; i++)
        {
            var (id, sink) = rig.AddObserver(new GridCell(18 + i, 18));
            sinks.Add(sink);
            ids.Add(id);
        }
        var monsters = Enumerable.Range(0, 20).Select(_ => rig.Add(new GridCell(random.Next(40), random.Next(40)), agi: random.Next(250))).ToList();
        ids.AddRange(monsters);
        foreach (var (kind, count) in new[] { (EntityKind.Player, 20), (EntityKind.Npc, 4) })
            for (var i = 0; i < count; i++)
                ids.Add(rig.Add(new GridCell(random.Next(40), random.Next(40)), kind: kind, agi: random.Next(250)));
        for (var i = 0; i < 6; i++) AddItem(rig, new GridCell(random.Next(40), random.Next(40)), i % 2 == 0 ? ids[i % 4] : null, 5000);
        rig.Ticks();
        for (var t = 0; t < 120; t++)
        {
            foreach (var id in ids)
                if (random.Next(10) == 0) rig.World.TryEnqueue(new MoveTo(id, new GridCell(random.Next(40), random.Next(40))));
            if (t % 5 == 0) rig.A.Events.Publish(new AggroGained(monsters[random.Next(20)], ids[random.Next(4)], AggroReasons.Sighted));
            if (t % 7 == 0) rig.A.Events.Publish(new AttackStarted(ids[random.Next(4)], monsters[random.Next(20)]));
            rig.Ticks();
        }
        return sinks.SelectMany((s, i) => s.Detailed().Select(line => i + " " + line)).ToList();
    }

    [Fact]
    public void The_same_intents_give_identical_views_with_reservations()
    {
        var first = ScriptedCrowd();
        first.Count.ShouldBeGreaterThan(100);
        ScriptedCrowd().ShouldBe(first);
    }

    [Fact]
    public void A_quiet_crowd_with_chasers_stays_on_the_fast_path_and_a_new_chaser_wakes_it()
    {
        var rig = Rig(5, 1, new CapReservations(Monsters: 2, Chasers: 2));
        var (me, sink) = rig.AddObserver(Center);
        for (var i = 1; i <= 20; i++) rig.Add(At(i % 10));
        var chaser = rig.Add(At(20));
        rig.Ticks(2);
        var gathered = rig.Aoi(rig.A).WindowsGathered;
        var messages = sink.Count;

        rig.Ticks(20);
        rig.Aoi(rig.A).WindowsGathered.ShouldBe(gathered);
        sink.Count.ShouldBe(messages);

        rig.A.Events.Publish(new AggroGained(chaser, me, AggroReasons.Sighted));
        rig.Ticks();
        sink.Deltas[^1].Appeared.Select(e => e.Id).ShouldContain(chaser);
    }

    [Fact]
    public void Partial_selection_keeps_what_is_behind_after_everything_else()
    {
        var random = new Random(6);
        for (var run = 0; run < 200; run++)
        {
            var count = random.Next(1, 200);
            var items = Enumerable.Range(0, count)
                .Select(i => new VisibleCap.Ranked(random.Next(8), new AoiEntry(new Entity(new EntityId(i + 1), EntityKind.Item, "map.a", new GridCell(0, 0), Directions.S)), random.Next(4) == 0))
                .ToArray();
            var k = random.Next(0, count + 2);
            var expected = items.OrderBy(r => r.Behind).ThenBy(r => r.Distance).ThenBy(r => r.Id).Take(k).Select(r => r.Id).OrderBy(id => id).ToList();

            VisibleCap.SelectSmallest(items, count, k);

            items.Take(k).Select(r => r.Id).OrderBy(id => id).ShouldBe(expected);
        }
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, 0)]
    [InlineData(0, -1, 0, 0, 0)]
    [InlineData(0, 0, -1, 0, 0)]
    [InlineData(0, 0, 0, -1, 0)]
    [InlineData(0, 0, 0, 0, -1)]
    [InlineData(5, 3, 2, 0, 0)]   // 10 of a cutoff of 10 leaves no slot for the observer
    [InlineData(0, 0, 0, 5, 7)]   // 12 of a cap of 12
    public void Bad_reservations_fail_at_construction(int monsters, int npcs, int items, int margin, int chasers)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new AoiSystem(6, 2, 12, 2, new CapReservations(monsters, npcs, items, margin, chasers)));
    }
}
