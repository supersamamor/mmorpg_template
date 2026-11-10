using Game.Client.Core;
using Game.Client.Core.Combat;
using Game.Client.Core.Theme;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Client;

/// <summary>Combat presentation (T-303, ARCHITECTURE §7.6): impact timing on the render clock, number styles, spreading and the cap, no replay, the HP counter and effect fallback.</summary>
public class CombatPresentationTests
{
    private static EntityRecord Standing(long id, string kind = EntityKinds.Monster, double? hp = 1) =>
        new(id, kind, "monster.a", new GridCell(1, 1), Directions.N, null, 0, hp);

    private static WorldView World(out List<HitImpact> impacts, ClientConfig? config = null, int others = 3)
    {
        var world = new WorldView(config ?? new ClientConfig());
        world.ApplyWelcome(new WelcomeMessage(1, 1, "map.a", 10)); // render delay: 2 ticks of 100 ms
        var records = new List<EntityRecord> { Standing(1, EntityKinds.Player) };
        for (var i = 2; i < 2 + others; i++) records.Add(Standing(i));
        world.ApplySnapshot(new SnapshotMessage(0, "map.a", 1, records), 0);
        var seen = new List<HitImpact>();
        world.Combat.Impact += (in HitImpact impact) => seen.Add(impact);
        impacts = seen;
        return world;
    }

    // ---- timing ----

    [Fact]
    public void A_hit_swings_at_render_time_and_lands_one_impact_delay_later()
    {
        var world = World(out var impacts);
        world.ApplyHits([new HitRecord(2, 1, 9, HitResults.Hit)], now: 10.0, tick: 5);

        world.Advance(10.19);
        world.Find(2)!.AttackCount.ShouldBe(0, "never before render time, so never before the attacker has reached range");
        world.Advance(10.21);
        world.Find(2)!.AttackCount.ShouldBe(1);
        impacts.ShouldBeEmpty();
        world.Find(1)!.HitCount.ShouldBe(0);

        world.Advance(10.39);
        impacts.ShouldBeEmpty();
        world.Advance(10.41);
        impacts.Count.ShouldBe(1);
        world.Find(1)!.HitCount.ShouldBe(1);
        impacts[0].Amount.ShouldBe(9);
        impacts[0].Side.ShouldBe(HitSide.Taken);

        world.Advance(12);
        impacts.Count.ShouldBe(1, "one hit lands once");
    }

    [Fact]
    public void The_impact_delay_comes_from_the_theme_per_attacker_and_is_clamped()
    {
        var world = World(out var impacts);
        world.Combat.ImpactDelay = e => e.Id == 2 ? 0.5 : 99;
        world.ApplyHits([new HitRecord(2, 1, 1, HitResults.Hit), new HitRecord(3, 1, 2, HitResults.Hit)], now: 0, tick: 1);

        world.Advance(0.69);
        impacts.ShouldBeEmpty();
        world.Advance(0.71);
        impacts.Select(i => i.Amount).ShouldBe(new[] { 1 });
        world.Advance(1.19);
        impacts.Count.ShouldBe(1);
        world.Advance(1.21);
        impacts.Select(i => i.Amount).ShouldBe(new[] { 1, 2 }, "99 s is clamped to the configured maximum (1 s)");
    }

    [Fact]
    public void A_miss_swings_and_lands_without_a_flinch()
    {
        var world = World(out var impacts);
        world.ApplyHits([new HitRecord(2, 3, 0, HitResults.Miss)], now: 0, tick: 1);
        world.Advance(0.45);
        world.Find(2)!.AttackCount.ShouldBe(1);
        world.Find(3)!.HitCount.ShouldBe(0);
        impacts.Single().Kind.ShouldBe(HitKind.Miss);
    }

    [Fact]
    public void A_late_batch_after_a_stall_is_dropped_not_replayed()
    {
        var world = World(out var impacts);
        world.ApplyHits([new HitRecord(2, 1, 9, HitResults.Hit)], now: 0, tick: 1);
        world.Advance(30); // the app was suspended for 30 s: the moment is long gone
        impacts.ShouldBeEmpty();
        world.Find(2)!.AttackCount.ShouldBe(0);
        world.Find(1)!.HitCount.ShouldBe(0);
        world.Combat.PendingCount.ShouldBe(0);
        world.Combat.Dropped.ShouldBe(1);
    }

    [Fact]
    public void A_snapshot_ending_a_catch_up_forgets_pending_hits_and_an_old_tick_is_not_applied_twice()
    {
        var world = World(out var impacts);
        world.ApplyHits([new HitRecord(2, 1, 9, HitResults.Hit)], now: 1, tick: 7);
        world.ApplySnapshot(new SnapshotMessage(20, "map.a", 1, [Standing(1, EntityKinds.Player), Standing(2)]), 1.05);
        world.Advance(3);
        impacts.ShouldBeEmpty();

        world.ApplyHits([new HitRecord(2, 1, 9, HitResults.Hit)], now: 3, tick: 20); // not newer than the snapshot's tick
        world.ApplyHits([new HitRecord(2, 1, 9, HitResults.Hit)], now: 3, tick: 19);
        world.Advance(4);
        impacts.ShouldBeEmpty();

        world.ApplyHits([new HitRecord(2, 1, 9, HitResults.Hit)], now: 4, tick: 21);
        world.Advance(4.45);
        impacts.Count.ShouldBe(1);
    }

    [Fact]
    public void Impacts_are_classified_as_dealt_taken_or_other()
    {
        var world = World(out var impacts);
        world.ApplyHits([new HitRecord(1, 2, 5, HitResults.Crit), new HitRecord(2, 1, 6, HitResults.Hit), new HitRecord(2, 3, 7, HitResults.Hit)], now: 0, tick: 1);
        world.Advance(0.45);
        impacts.Single(i => i.Amount == 5).Side.ShouldBe(HitSide.Dealt);
        impacts.Single(i => i.Amount == 5).Kind.ShouldBe(HitKind.Crit);
        impacts.Single(i => i.Amount == 6).Side.ShouldBe(HitSide.Taken);
        impacts.Single(i => i.Amount == 7).Side.ShouldBe(HitSide.Other);
    }

    [Fact]
    public void A_death_waits_for_the_killing_blow()
    {
        var world = World(out _);
        world.ApplyHits([new HitRecord(1, 2, 50, HitResults.Hit)], now: 0, tick: 1);
        world.ApplyDelta(new DeltaMessage(1, [], [new ChangeRecord(2, ChangedFields.Dead, default, null, null, 0, null, Dead: true)], []), 0);

        world.Advance(0.3);
        world.Find(2)!.Dead.ShouldBeTrue();
        world.Find(2)!.DeadShown.ShouldBeFalse("the hit has not landed yet");
        world.Advance(0.41);
        world.Find(2)!.DeadShown.ShouldBeTrue();
        world.Find(2)!.DeathFxPending.ShouldBeTrue();
    }

    [Fact]
    public void An_entity_that_appears_dead_is_dead_at_once_without_a_death_effect()
    {
        var world = World(out _);
        world.ApplyDelta(new DeltaMessage(1, [new EntityRecord(9, EntityKinds.Monster, "monster.a", new GridCell(2, 2), Directions.N, null, 0, 0, true)], [], []), 0);
        world.Find(9)!.DeadShown.ShouldBeTrue();
        world.Find(9)!.DeathFxPending.ShouldBeFalse();
    }

    // ---- numbers ----

    private static HitImpact Impact(HitSide side, HitKind kind, long target = 2, int amount = 10, double at = 0) =>
        new(1, target, amount, kind, side, 1, 1, at);

    [Theory]
    [InlineData(HitSide.Dealt, HitKind.Hit, NumberStyle.Dealt)]
    [InlineData(HitSide.Dealt, HitKind.Crit, NumberStyle.DealtCrit)]
    [InlineData(HitSide.Dealt, HitKind.Miss, NumberStyle.DealtMiss)]
    [InlineData(HitSide.Taken, HitKind.Hit, NumberStyle.Taken)]
    [InlineData(HitSide.Taken, HitKind.Crit, NumberStyle.TakenCrit)]
    [InlineData(HitSide.Taken, HitKind.Miss, NumberStyle.TakenMiss)]
    [InlineData(HitSide.Other, HitKind.Hit, NumberStyle.Other)]
    [InlineData(HitSide.Other, HitKind.Crit, NumberStyle.OtherCrit)]
    [InlineData(HitSide.Other, HitKind.Miss, NumberStyle.OtherMiss)]
    public void Hit_crit_miss_and_who_it_concerns_pick_the_style(HitSide side, HitKind kind, NumberStyle expected)
    {
        NumberStyles.Of(side, kind).ShouldBe(expected);
        NumberStyles.IsCrit(expected).ShouldBe(kind == HitKind.Crit);
        NumberStyles.IsMiss(expected).ShouldBe(kind == HitKind.Miss);
        NumberStyles.IsTaken(expected).ShouldBe(side == HitSide.Taken);
        NumberStyles.IsOther(expected).ShouldBe(side == HitSide.Other);
    }

    [Fact]
    public void A_miss_shows_the_word_from_UiText_and_a_hit_shows_the_amount()
    {
        var pool = new DamageNumberPool(8, 1, 0.5);
        pool.OnImpact(Impact(HitSide.Dealt, HitKind.Miss));
        pool.OnImpact(Impact(HitSide.Dealt, HitKind.Crit, amount: 123, target: 3));
        pool.Slots.Where(s => s.Active).Select(s => s.Text).ShouldBe(new[] { UiText.MissWord, "123" });
    }

    [Fact]
    public void Numbers_on_one_target_spread_into_lanes_and_reuse_a_lane_when_the_window_has_passed()
    {
        var pool = new DamageNumberPool(16, 1, 0.5);
        for (var i = 0; i < 5; i++) pool.OnImpact(Impact(HitSide.Other, HitKind.Hit, target: 2, at: 0.01 * i));
        pool.Slots.Where(s => s.Active).Select(s => s.Lane).ShouldBe(new[] { 0, 1, -1, 2, -2 });

        pool.OnImpact(Impact(HitSide.Other, HitKind.Hit, target: 3, at: 0.05));
        pool.Slots.Where(s => s.Active && s.Target == 3).Single().Lane.ShouldBe(0, "another target has its own lanes");

        pool.OnImpact(Impact(HitSide.Other, HitKind.Hit, target: 2, at: 0.6));
        pool.Slots.Where(s => s.Active && s.Start == 0.6).Single().Lane.ShouldBe(0, "the earlier ones are past the window");
    }

    [Fact]
    public void The_cap_drops_the_oldest_number_and_counts_it()
    {
        var pool = new DamageNumberPool(3, 10, 0.5);
        for (var i = 0; i < 5; i++) pool.OnImpact(Impact(HitSide.Dealt, HitKind.Hit, target: 10 + i, amount: i, at: i));
        pool.ActiveCount.ShouldBe(3);
        pool.DroppedForCap.ShouldBe(2);
        pool.Slots.Where(s => s.Active).Select(s => s.Text).OrderBy(t => t).ShouldBe(new[] { "2", "3", "4" });
    }

    [Fact]
    public void Numbers_expire_and_a_quiet_pool_reports_it()
    {
        var pool = new DamageNumberPool(4, 1, 0.5);
        pool.Update(0).ShouldBeFalse();
        pool.OnImpact(Impact(HitSide.Dealt, HitKind.Hit));
        pool.Update(0.5).ShouldBeTrue();
        pool.Update(1.0).ShouldBeFalse();
        pool.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public void Showing_a_number_allocates_nothing_once_the_digits_are_cached()
    {
        var pool = new DamageNumberPool(8, 1, 0.5);
        for (var i = 0; i < 20; i++) pool.OnImpact(Impact(HitSide.Dealt, HitKind.Hit, amount: 42, at: i));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) pool.OnImpact(Impact(HitSide.Dealt, HitKind.Hit, amount: 42, at: 100 + i));
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }

    // ---- HP counter ----

    [Fact]
    public void The_hp_counter_goes_up_only_when_the_fraction_changes()
    {
        var world = World(out _);
        var entity = world.Find(2)!;
        var start = entity.HpVersion;

        world.ApplyDelta(new DeltaMessage(1, [], [new ChangeRecord(2, ChangedFields.Hp, default, null, null, 0, null, Hp: 0.5)], []), 0);
        entity.HpVersion.ShouldBe(start + 1);
        entity.Hp.ShouldBe(0.5);

        world.ApplyDelta(new DeltaMessage(2, [], [new ChangeRecord(2, ChangedFields.Hp, default, null, null, 0, null, Hp: 0.5)], []), 0.1);
        entity.HpVersion.ShouldBe(start + 1, "the same fraction again changes nothing");

        world.ApplyDelta(new DeltaMessage(3, [], [new ChangeRecord(2, ChangedFields.Facing, default, Directions.E, null, 0, null)], []), 0.2);
        entity.HpVersion.ShouldBe(start + 1, "other changes do not touch it");
    }

    // ---- effect fallback and impact delay data ----

    private static ThemeCatalog Catalog(string manifestJson, Func<string, bool> exists)
    {
        ThemeManifest.TryParse("default", manifestJson, out var manifest, out var error).ShouldBeTrue(error);
        return new ThemeCatalog(new[] { manifest! }, exists, _ => { });
    }

    [Fact]
    public void A_missing_crit_effect_falls_back_to_the_normal_one_and_a_missing_chain_shows_nothing()
    {
        var catalog = Catalog("""{"vfx.hit.normal":{"file":"vfx/n.tscn","effect":"effect.hit"},"vfx.hit.crit":{"file":"vfx/c.tscn","effect":"effect.crit"},"vfx.death.generic":{"file":"vfx/d.tscn","effect":"effect.death"}}""", p => !p.EndsWith("c.tscn") && !p.EndsWith("d.tscn"));
        catalog.ResolveEffect(EffectNames.Crit, out var crit).ShouldBeTrue();
        crit.ShouldBe("themes/default/vfx/n.tscn");
        catalog.ResolveEffect(EffectNames.Hit, out var normal).ShouldBeTrue();
        normal.ShouldBe("themes/default/vfx/n.tscn");
        catalog.ResolveEffect(EffectNames.LevelUp, out var level).ShouldBeTrue("level up falls to crit, then normal");
        level.ShouldBe("themes/default/vfx/n.tscn");

        var none = Catalog("{}", _ => false);
        none.ResolveEffect(EffectNames.Hit, out _).ShouldBeFalse();
        none.ResolveEffect(EffectNames.Death, out _).ShouldBeFalse();
        none.WarningCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_manifest_entry_names_the_effect_it_plays_and_any_key_can_do_it()
    {
        var catalog = Catalog("""{"vfx.x":{"file":"vfx/x.tscn","effect":"effect.hit"}}""", p => p.EndsWith("x.tscn"));
        catalog.ResolveEffect(EffectNames.Hit, out var path).ShouldBeTrue();
        path.ShouldBe("themes/default/vfx/x.tscn");
    }

    [Fact]
    public void Impact_delay_is_theme_data_per_model_then_per_rig_then_the_default()
    {
        var json = """
            {"anim.h":{"file":"a.glb","impact_delay":0.4},
             "model.a":{"file":"a.glb","anims":"anim.h","impact_delay":0.1},
             "model.b":{"file":"b.glb","anims":"anim.h"},
             "model.c":{"file":"c.glb"}}
            """;
        var catalog = Catalog(json, _ => true);
        catalog.ImpactDelayOf(catalog.ResolveModel("model.a", EntityKinds.Monster), 0.2).ShouldBe(0.1);
        catalog.ImpactDelayOf(catalog.ResolveModel("model.b", EntityKinds.Monster), 0.2).ShouldBe(0.4);
        catalog.ImpactDelayOf(catalog.ResolveModel("model.c", EntityKinds.Monster), 0.2).ShouldBe(0.2);
    }
}
