using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Combat;
using Game.Server.World.Monsters;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Server;

/// <summary>
/// T-215: the crowd cap. Every rig here has a window much larger than the entities' spread (32-cell buckets, radius 1, 64x64 map, unless
/// a test says otherwise), so what is cut is cut by the cap, never by the window.
/// </summary>
public class AoiCapTests
{
    private static readonly GridCell Center = new(16, 16);

    private static GridCell C(int x, int y) => new(x, y);

    /// <summary>A cell at this Chebyshev distance from <see cref="Center"/>, on the east side.</summary>
    private static GridCell At(int distance, int row = 0) => new(Center.X + distance, Center.Y + row);

    private static AoiRig Rig(int maxVisible, int hysteresis = 0) => new(32, 1, 64, 64, maxVisible, hysteresis, "map.a");

    /// <summary>What a client knows: its view rebuilt from the messages, and how often each entity came and went after the first message.</summary>
    internal sealed class ClientView(RecordingSink sink)
    {
        private int _snapshots;
        private int _deltas;

        public readonly Dictionary<EntityId, GridCell> Cells = new();
        public readonly Dictionary<EntityId, int> Appeared = new();
        public readonly Dictionary<EntityId, int> Disappeared = new();

        public void Update()
        {
            for (; _snapshots < sink.Snapshots.Count; _snapshots++)
            {
                Cells.Clear();
                foreach (var e in sink.Snapshots[_snapshots].Entities) Cells[e.Id] = e.Cell;
            }
            for (; _deltas < sink.Deltas.Count; _deltas++)
            {
                var delta = sink.Deltas[_deltas];
                foreach (var e in delta.Appeared)
                {
                    Cells.ContainsKey(e.Id).ShouldBeFalse($"{e.Id} appeared but was already shown");
                    Cells[e.Id] = e.Cell;
                    Appeared[e.Id] = Appeared.GetValueOrDefault(e.Id) + 1;
                }
                foreach (var c in delta.Changed)
                {
                    Cells.ContainsKey(c.View.Id).ShouldBeTrue($"a change for {c.View.Id}, which is not shown");
                    Cells[c.View.Id] = c.View.Cell;
                }
                foreach (var gone in delta.Disappeared)
                {
                    Cells.Remove(gone).ShouldBeTrue($"{gone} disappeared but was not shown");
                    Disappeared[gone] = Disappeared.GetValueOrDefault(gone) + 1;
                }
            }
        }

        public List<long> Ids => Cells.Keys.Select(k => k.Value).OrderBy(v => v).ToList();
    }

    // ---- the cap, and who wins ----

    [Fact]
    public void The_cap_holds_and_the_nearest_win()
    {
        var rig = Rig(maxVisible: 5);
        var (me, sink) = rig.AddObserver(Center);
        var farToNear = Enumerable.Range(0, 10).Select(i => rig.Add(At(10 - i))).ToList();   // ids grow as they get nearer
        rig.Ticks();

        var shown = sink.Snapshots.Single().Entities.Select(e => e.Id).ToList();
        shown.ShouldBe([me, farToNear[6], farToNear[7], farToNear[8], farToNear[9]]);   // itself and the 4 nearest, by id
    }

    [Fact]
    public void Ties_on_distance_go_to_the_lowest_id_whatever_the_side()
    {
        var rig = Rig(maxVisible: 4);
        var (me, sink) = rig.AddObserver(Center);
        var a = rig.Add(C(Center.X + 3, Center.Y));
        var b = rig.Add(C(Center.X - 3, Center.Y + 3));
        var c = rig.Add(C(Center.X, Center.Y - 3));
        rig.Add(C(Center.X + 3, Center.Y - 3));
        rig.Add(C(Center.X - 3, Center.Y));
        rig.Ticks();

        sink.Snapshots.Single().Entities.Select(e => e.Id).ShouldBe([me, a, b, c]);
    }

    [Fact]
    public void A_window_that_fits_is_not_cut_even_past_the_cutoff()
    {
        var rig = Rig(maxVisible: 6, hysteresis: 3);
        var (_, sink) = rig.AddObserver(Center);
        for (var i = 1; i <= 5; i++) rig.Add(At(i));
        rig.Ticks();

        sink.Snapshots.Single().Entities.Count.ShouldBe(6);
    }

    [Fact]
    public void An_entity_that_comes_nearer_than_the_shown_ones_replaces_the_farthest()
    {
        var rig = Rig(maxVisible: 3);
        var (_, sink) = rig.AddObserver(Center);
        var near = rig.Add(At(2));
        var far = rig.Add(At(4));
        var outside = rig.Add(At(6));
        rig.Ticks();
        sink.Snapshots.Single().Entities.Select(e => e.Id).ShouldNotContain(outside);

        rig.Get(outside).Cell = At(1);
        rig.Ticks();

        var delta = sink.Deltas.Single();
        delta.Appeared.Select(e => e.Id).ShouldBe([outside]);
        delta.Disappeared.ShouldBe([far]);
        near.Value.ShouldBeLessThan(far.Value);
    }

    [Fact]
    public void A_cut_entity_sends_nothing_until_it_is_shown_and_then_a_full_record()
    {
        var rig = Rig(maxVisible: 2);
        var (_, sink) = rig.AddObserver(Center);
        rig.Add(At(1));
        var cut = rig.Add(At(5), key: "monster.cut");
        rig.Ticks();
        sink.Count.ShouldBe(1);

        rig.Get(cut).Cell = At(6);
        rig.Ticks(3);
        sink.Count.ShouldBe(1);     // its bucket changed, but nothing the observer sees did

        rig.Get(cut).Cell = Center;
        rig.Ticks();
        sink.Deltas.Single().Appeared.Single().ShouldBe(new EntityView(cut, EntityKind.Monster, "monster.cut", Center, Directions.S, null, 0));
    }

    [Fact]
    public void A_snapshot_shows_the_same_set_as_the_deltas_built()
    {
        var rig = Rig(maxVisible: 6, hysteresis: 2);
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        for (var i = 1; i <= 12; i++) rig.Add(At(i));
        rig.Ticks();
        rig.Get(rig.A.Entities[^1].Id).Cell = At(0);   // comes close: the set moves on from the first snapshot
        rig.Ticks();
        view.Update();

        rig.Send(new RequestSnapshot(me));
        rig.Ticks();

        sink.Snapshots[^1].Entities.Select(e => e.Id.Value).ShouldBe(view.Ids);
    }

    [Fact]
    public void A_capped_view_stays_capped_until_its_window_is_back_at_the_cutoff()
    {
        var rig = Rig(maxVisible: 4, hysteresis: 2);   // the cutoff is 2: the observer and the nearest other entity
        var (_, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        var crowd = Enumerable.Range(1, 6).Select(i => rig.Add(At(i))).ToList();
        rig.Ticks();
        view.Update();
        view.Cells.Count.ShouldBe(2);

        // Down to 4 in the window: it fits the cap, but the view stays capped, so a crowd hovering at the cap cannot flicker its far end.
        rig.World.Despawn(crowd[5]);
        rig.World.Despawn(crowd[4]);
        rig.World.Despawn(crowd[3]);
        rig.Ticks();
        view.Update();
        view.Cells.Count.ShouldBe(2);

        // Down to the cutoff: the cap lets go, so the window can fill up to the cap again without a cut.
        rig.World.Despawn(crowd[2]);
        rig.World.Despawn(crowd[1]);
        rig.Ticks();
        view.Update();
        view.Cells.Count.ShouldBe(2);

        rig.Add(At(2));
        rig.Add(At(3));
        rig.Ticks();
        view.Update();
        view.Cells.Count.ShouldBe(4);
    }

    // ---- always visible ----

    [Fact]
    public void The_observer_itself_is_never_cut()
    {
        var rig = Rig(maxVisible: 1);
        var (me, sink) = rig.AddObserver(Center);
        rig.Add(Center);
        rig.Add(At(1));
        rig.Ticks();

        sink.Snapshots.Single().Entities.Select(e => e.Id).ShouldBe([me]);
    }

    [Fact]
    public void The_attack_target_stays_visible_from_the_start_of_the_attack_to_its_end_and_counts_toward_the_cap()
    {
        var rig = Rig(maxVisible: 3);
        var (me, sink) = rig.AddObserver(Center);
        var target = rig.Add(At(9));
        var near1 = rig.Add(At(1));
        var near2 = rig.Add(At(2));
        rig.Ticks();
        sink.Snapshots.Single().Entities.Select(e => e.Id).ShouldBe([me, near1, near2]);

        rig.A.Events.Publish(new AttackStarted(me, target));
        rig.Ticks();
        sink.Deltas.Single().Appeared.Select(e => e.Id).ShouldBe([target]);
        sink.Deltas.Single().Disappeared.ShouldBe([near2]);

        rig.Ticks(5);
        sink.Deltas.Count.ShouldBe(1);   // quiet: stays that way

        rig.A.Events.Publish(new AttackStopped(me, target, AttackReasons.Cancelled));
        rig.Ticks();
        sink.Deltas[1].Appeared.Select(e => e.Id).ShouldBe([near2]);
        sink.Deltas[1].Disappeared.ShouldBe([target]);
    }

    [Fact]
    public void Switching_target_frees_the_old_one_and_a_late_stop_for_it_changes_nothing()
    {
        var rig = Rig(maxVisible: 2);
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        rig.Add(At(1));
        var first = rig.Add(At(8));
        var second = rig.Add(At(9));
        rig.Ticks();

        rig.A.Events.Publish(new AttackStarted(me, first));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(first);

        rig.A.Events.Publish(new AttackStarted(me, second));
        rig.A.Events.Publish(new AttackStopped(me, first, AttackReasons.Cancelled));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(second);
        view.Cells.Keys.ShouldNotContain(first);
    }

    [Fact]
    public void An_attacker_is_shown_from_its_first_swing_until_it_gives_up_or_dies()
    {
        var rig = Rig(maxVisible: 3);
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        rig.Add(At(1));
        rig.Add(At(2));
        var attacker = rig.Add(At(9));
        var other = rig.Add(At(10));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldNotContain(attacker);

        rig.A.Events.Publish(new DamageDealt(attacker, me, 1, HitResults.Miss, 10));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(attacker);
        sink.Deltas[^1].Hits!.ShouldHaveSingleItem();

        rig.Ticks(5);
        view.Update();
        view.Cells.Keys.ShouldContain(attacker);    // it has not given up: no more hits needed

        rig.A.Events.Publish(new AggroLost(attacker, me, AggroReasons.Leash));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldNotContain(attacker);

        rig.A.Events.Publish(new DamageDealt(other, me, 1, HitResults.Miss, 10));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(other);
        rig.Get(other).Dead = true;
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldNotContain(other);    // a corpse does not attack
    }

    [Fact]
    public void An_attacker_of_someone_else_gets_no_special_treatment()
    {
        var rig = Rig(maxVisible: 2);
        var (_, sink) = rig.AddObserver(Center);
        var near = rig.Add(At(1));
        var attacker = rig.Add(At(9));
        rig.Ticks();

        rig.A.Events.Publish(new DamageDealt(attacker, near, 1, HitResults.Hit, 10));
        rig.Ticks();

        sink.Deltas.Single().Appeared.ShouldBeEmpty();   // it hit someone seen, so the hit is sent, but it stays cut
        sink.Deltas.Single().Hits!.ShouldHaveSingleItem();
    }

    [Fact]
    public void More_attackers_than_the_cap_are_all_kept_and_nobody_else_is()
    {
        var rig = Rig(maxVisible: 3);
        var (me, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        rig.Add(At(1));
        var attackers = Enumerable.Range(0, 4).Select(i => rig.Add(At(5 + i))).ToList();
        rig.Ticks();

        foreach (var attacker in attackers) rig.A.Events.Publish(new DamageDealt(attacker, me, 1, HitResults.Hit, 10));
        rig.Ticks();
        view.Update();

        view.Cells.Keys.OrderBy(k => k.Value).ShouldBe(new[] { me }.Concat(attackers));
    }

    [Fact]
    public void A_players_real_attack_keeps_its_target_visible_before_the_first_hit()
    {
        var tank = TestCombat.Monster("monster.t_tank", maxHp: 100_000, atk: 1);
        var rig = new CombatRig(TestCombat.Open(32, 12), [tank], bucketSize: 32, viewRadius: 1, aoi: true, maxVisible: 4);
        var sink = new RecordingSink();
        var view = new ClientView(sink);
        var player = rig.AddPlayer(new GridCell(2, 5));
        var target = rig.AddMonster(tank, new GridCell(20, 5));
        rig.Ticks();
        rig.Send(new AttachView(player, sink));
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(target);

        // The crowd arrives as the attack starts. The target is the farthest of 8, so only the attack can keep it.
        for (var i = 0; i < 6; i++) rig.AddMonster(tank, new GridCell(1 + i, 6));
        rig.Send(new AttackTarget(player, target));
        var ticksBeforeHit = 0;
        while (!rig.Of<DamageDealt>().Any() && ticksBeforeHit < 200)
        {
            rig.Ticks();
            ticksBeforeHit++;
            view.Update();
            view.Cells.Keys.ShouldContain(target, $"tick {ticksBeforeHit}");
            view.Cells.Count.ShouldBeLessThanOrEqualTo(4);
        }
        ticksBeforeHit.ShouldBeGreaterThan(20, "the walk should take a while");
        rig.Of<DamageDealt>().ShouldNotBeEmpty();
        rig.Of<AttackStarted>().ShouldBe([new AttackStarted(player, target)]);
        view.Disappeared.ShouldNotContainKey(target);
    }

    // ---- hysteresis ----

    /// <summary>
    /// X stands still, right at the cutoff rank. Y walks across it every tick, so X's rank swaps between the cutoff and the one after it.
    /// Returns how often X and Y came and went after the first snapshot.
    /// </summary>
    private static (int X, int Y) FlickerAtTheCutoff(int hysteresis, int ticks)
    {
        const int cap = 8;
        var rig = Rig(cap, hysteresis);
        var (_, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        var cutoff = cap - hysteresis;
        for (var i = 0; i < cutoff - 2; i++) rig.Add(At(1));   // ranks 1.. cutoff-2 (the observer itself takes one slot of the cutoff)
        var x = rig.Add(At(3));
        var y = rig.Add(At(4));
        for (var i = 0; i < 4; i++) rig.Add(At(8));            // keep the window over the cap
        rig.Ticks();

        for (var t = 0; t < ticks; t++)
        {
            rig.Get(y).Cell = At(t % 2 == 0 ? 2 : 4);
            rig.Ticks();
            view.Update();
        }
        return (view.Appeared.GetValueOrDefault(x) + view.Disappeared.GetValueOrDefault(x),
            view.Appeared.GetValueOrDefault(y) + view.Disappeared.GetValueOrDefault(y));
    }

    [Fact]
    public void An_entity_right_at_the_cutoff_does_not_flicker_for_a_hundred_ticks()
    {
        var (x, y) = FlickerAtTheCutoff(hysteresis: 2, ticks: 100);
        x.ShouldBe(0);   // shown from the start, it stays
        y.ShouldBe(1);   // came in the first time it ranked at the cutoff, then stayed
    }

    [Fact]
    public void Without_hysteresis_the_same_scene_flickers_every_tick()
    {
        var (x, y) = FlickerAtTheCutoff(hysteresis: 0, ticks: 100);
        x.ShouldBeGreaterThan(90);
        y.ShouldBeGreaterThan(90);
    }

    [Fact]
    public void A_shown_entity_leaves_past_the_cap_rank_and_comes_back_only_at_the_cutoff()
    {
        var rig = Rig(maxVisible: 5, hysteresis: 2);   // beside the observer: enter at rank 2 of the others, stay up to rank 4
        var (_, sink) = rig.AddObserver(Center);
        var view = new ClientView(sink);
        var a = rig.Add(At(2));
        var b = rig.Add(At(3));
        rig.Add(At(4));
        rig.Add(At(5));
        var e = rig.Add(At(6));
        rig.Ticks();
        rig.Get(e).Cell = At(1);     // e, a, b: b slips to rank 3 and stays
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(b);

        rig.Get(b).Cell = At(7);     // e, a, c, d, b: rank 5 is past the cap
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldNotContain(b);

        rig.Get(b).Cell = At(3);     // e, a, b: rank 3 would keep it, but it is not shown any more, and 3 is past the cutoff
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldNotContain(b);

        rig.Get(a).Cell = At(3);
        rig.Get(b).Cell = At(2);     // e, b, a: rank 2 is the cutoff, so it enters
        rig.Ticks();
        view.Update();
        view.Cells.Keys.ShouldContain(b);
    }

    // ---- hits ----

    [Fact]
    public void Hits_between_cut_entities_are_not_sent_and_hits_of_a_seen_one_are()
    {
        var rig = Rig(maxVisible: 3);
        var (me, sink) = rig.AddObserver(Center);
        var near1 = rig.Add(At(1));
        var near2 = rig.Add(At(2));
        var cut1 = rig.Add(At(8));
        var cut2 = rig.Add(At(9));
        rig.Ticks();
        var messages = sink.Count;

        rig.A.Events.Publish(new DamageDealt(cut1, cut2, 3, HitResults.Hit, 7));
        rig.Ticks();
        sink.Count.ShouldBe(messages);    // nothing it can show, so no message at all

        rig.A.Events.Publish(new DamageDealt(cut1, cut2, 3, HitResults.Hit, 4));
        rig.A.Events.Publish(new DamageDealt(cut1, near1, 2, HitResults.Hit, 5));
        rig.A.Events.Publish(new DamageDealt(near2, cut2, 1, HitResults.Crit, 3));
        rig.Ticks();
        sink.Deltas.Single().Hits!.ShouldBe([new HitView(cut1, near1, 2, HitResults.Hit), new HitView(near2, cut2, 1, HitResults.Crit)]);
        sink.Deltas.Single().Appeared.ShouldBeEmpty();
        me.Value.ShouldBeLessThan(near1.Value);
    }

    [Fact]
    public void A_hit_on_the_observer_arrives_with_its_attacker_in_the_same_delta()
    {
        var rig = Rig(maxVisible: 2);
        var (me, sink) = rig.AddObserver(Center);
        rig.Add(At(1));
        var attacker = rig.Add(At(9));
        rig.Ticks();

        rig.A.Events.Publish(new DamageDealt(attacker, me, 3, HitResults.Hit, 7));
        rig.Ticks();

        var delta = sink.Deltas.Single();
        delta.Appeared.Select(e => e.Id).ShouldBe([attacker]);
        delta.Hits!.ShouldBe([new HitView(attacker, me, 3, HitResults.Hit)]);
    }

    // ---- the crowd, and the cost ----

    [Theory]
    [InlineData(11)]
    [InlineData(2024)]
    public void Three_hundred_entities_converge_on_one_observer(int seed)
    {
        const int cap = 60;
        const int hysteresis = 5;
        const int bucket = 6;
        const int radius = 2;
        var rig = new AoiRig(bucket, radius, 96, 64, cap, hysteresis, "map.a");
        var random = new Random(seed);
        var center = new GridCell(48, 32);
        var (me, sink) = rig.AddObserver(center);
        var view = new ClientView(sink);
        var crowd = Enumerable.Range(0, 300).Select(_ => rig.Add(new GridCell(random.Next(96), random.Next(64)), agi: random.Next(250))).ToList();
        rig.Ticks();

        var peak = 0;
        var capped = false;
        for (var t = 0; t < 400; t++)
        {
            if (t % 60 == 0)
                foreach (var id in crowd)
                    rig.Send(new MoveTo(id, new GridCell(center.X + random.Next(-6, 7), center.Y + random.Next(-6, 7))));
            rig.Ticks();
            view.Update();

            var window = rig.A.Entities
                .Where(e => Math.Abs(e.Cell.X / bucket - center.X / bucket) <= radius && Math.Abs(e.Cell.Y / bucket - center.Y / bucket) <= radius)
                .ToList();
            peak = Math.Max(peak, window.Count);
            capped |= window.Count > cap;

            // Exactly what the rules say, recomputed by brute force from the entities' cells.
            view.Cells.Count.ShouldBeLessThanOrEqualTo(cap, $"tick {t}");
            view.Cells.Keys.ShouldContain(me);
            foreach (var (id, cell) in view.Cells) rig.Get(id).Cell.ShouldBe(cell, $"tick {t}: {id} is out of date");
            var ranked = window.Where(e => e.Id != me).OrderBy(e => GridDistance.Chebyshev(center, e.Cell)).ThenBy(e => e.Id.Value).ToList();
            if (!capped) view.Cells.Count.ShouldBe(window.Count, $"tick {t}");
            for (var i = 0; i < ranked.Count; i++)
            {
                var rank = i + 2;   // the observer holds the first place
                if (rank <= cap - hysteresis) view.Cells.Keys.ShouldContain(ranked[i].Id, $"tick {t}: rank {rank} must be shown");
                if (rank > cap) view.Cells.Keys.ShouldNotContain(ranked[i].Id, $"tick {t}: rank {rank} must not be shown");
            }
        }

        peak.ShouldBeGreaterThan(250, "the crowd should really have converged");
        view.Cells.Count.ShouldBeGreaterThanOrEqualTo(cap - hysteresis);
        rig.Log.Lines.Where(l => l.StartsWith("ERROR", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private static List<string> ScriptedCrowd()
    {
        var rig = new AoiRig(4, 2, 40, 40, 12, 2, "map.a");
        var random = new Random(77);
        var sinks = new List<RecordingSink>();
        var ids = new List<EntityId>();
        for (var i = 0; i < 4; i++)
        {
            var (id, sink) = rig.AddObserver(new GridCell(18 + i, 18));
            sinks.Add(sink);
            ids.Add(id);
        }
        for (var i = 0; i < 40; i++) ids.Add(rig.Add(new GridCell(random.Next(40), random.Next(40)), agi: random.Next(250)));
        rig.Ticks();
        for (var t = 0; t < 120; t++)
        {
            foreach (var id in ids)
                if (random.Next(10) == 0) rig.World.TryEnqueue(new MoveTo(id, new GridCell(random.Next(40), random.Next(40))));
            if (t % 7 == 0) rig.A.Events.Publish(new AttackStarted(ids[random.Next(4)], ids[4 + random.Next(40)]));
            rig.Ticks();
        }
        return sinks.SelectMany((s, i) => s.Detailed().Select(line => i + " " + line)).ToList();
    }

    [Fact]
    public void The_same_intents_give_identical_capped_views()
    {
        var first = ScriptedCrowd();
        first.Count.ShouldBeGreaterThan(100);
        ScriptedCrowd().ShouldBe(first);
    }

    [Fact]
    public void A_quiet_crowd_stays_on_the_fast_path()
    {
        var rig = Rig(maxVisible: 5, hysteresis: 1);
        var (_, sink) = rig.AddObserver(Center);
        for (var i = 1; i <= 20; i++) rig.Add(At(i % 10));
        rig.Ticks(2);
        var gathered = rig.Aoi(rig.A).WindowsGathered;
        var messages = sink.Count;

        rig.Ticks(20);

        rig.Aoi(rig.A).WindowsGathered.ShouldBe(gathered);
        sink.Count.ShouldBe(messages);
    }

    [Fact]
    public void Partial_selection_picks_the_same_items_as_a_full_sort()
    {
        var random = new Random(5);
        for (var run = 0; run < 200; run++)
        {
            var count = random.Next(1, 200);
            var items = Enumerable.Range(0, count).Select(i => new VisibleCap.Ranked(random.Next(8), new AoiEntry(TestEntity(i + 1)))).ToArray();
            var k = random.Next(0, count + 2);
            var expected = items.OrderBy(r => r.Distance).ThenBy(r => r.Id).Take(k).Select(r => r.Id).OrderBy(id => id).ToList();

            VisibleCap.SelectSmallest(items, count, k);

            items.Take(k).Select(r => r.Id).OrderBy(id => id).ShouldBe(expected);
        }
    }

    private static Entity TestEntity(long id) => new(new EntityId(id), EntityKind.Monster, "map.a", new GridCell(0, 0), Directions.S);

    [Fact]
    public void Bad_cap_numbers_fail_at_construction()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new AoiSystem(6, 2, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new AoiSystem(6, 2, 10, -1));
        Should.Throw<ArgumentOutOfRangeException>(() => new AoiSystem(6, 2, 10, 10));
    }
}
