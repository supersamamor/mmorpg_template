using Game.Client.Core;
using Game.Client.Core.Animation;
using Game.Client.Core.Bundle;
using Game.Client.Core.World;
using Game.Content.ClientExport;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Client;

/// <summary>Level of detail, animation rate, the animation state machine, and the bundle-to-model lookup (T-207, ARCHITECTURE §9).</summary>
public class EntityVisualRulesTests
{
    // ---- level of detail and animation rate ----

    private static DetailPlanner Plan(int count, Func<int, double> distance, out int[] slots)
    {
        var planner = new DetailPlanner(8, 40);
        planner.Begin();
        slots = new int[count];
        for (var i = 0; i < count; i++) slots[i] = planner.Add(1000 + i, distance(i));
        planner.Plan();
        return planner;
    }

    [Fact]
    public void The_nearest_eight_characters_are_LOD0_and_all_the_others_LOD1()
    {
        // Entity i is i cells away, entered in reverse so that order of arrival means nothing.
        var planner = Plan(20, i => (19 - i) * (19 - i), out var slots);

        var lod0 = Enumerable.Range(0, 20).Where(i => planner.LodOf(slots[i]) == 0).ToList();
        lod0.Count.ShouldBe(8);
        lod0.ShouldBe(Enumerable.Range(12, 8).ToList());
        Enumerable.Range(0, 12).All(i => planner.LodOf(slots[i]) == 1).ShouldBeTrue();
    }

    [Fact]
    public void Fewer_than_eight_characters_are_all_LOD0()
    {
        var planner = Plan(5, i => i, out var slots);
        slots.All(s => planner.LodOf(s) == 0).ShouldBeTrue();
    }

    [Fact]
    public void Equal_distances_are_ranked_by_id_so_the_choice_never_flickers()
    {
        var planner = Plan(12, _ => 25, out var slots); // all the same distance; ids rise with the index

        Enumerable.Range(0, 12).Where(i => planner.LodOf(slots[i]) == 0).ShouldBe(Enumerable.Range(0, 8).ToList());
    }

    [Fact]
    public void The_nearest_forty_animate_every_frame_and_the_rest_at_the_reduced_rate()
    {
        var planner = Plan(55, i => i * i, out var slots);

        slots.Count(s => planner.FullRate(s)).ShouldBe(40);
        slots.Take(40).All(s => planner.FullRate(s)).ShouldBeTrue();
        slots.Skip(40).Any(s => planner.FullRate(s)).ShouldBeFalse();
    }

    [Fact]
    public void Planning_again_allocates_nothing_once_warm()
    {
        var planner = new DetailPlanner(8, 40);
        void Round()
        {
            planner.Begin();
            for (var i = 0; i < 60; i++) planner.Add(i, (i * 7) % 13);
            planner.Plan();
        }
        Round();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) Round();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }

    // ---- the animation state machine ----

    private static AnimClip? Next(AnimationDriver d, bool dead = false, bool walking = false, int attacks = 0, int hits = 0) =>
        d.Update(dead, walking, attacks, hits, out var clip) ? clip : null;

    [Fact]
    public void It_idles_then_walks_and_goes_back_to_idle()
    {
        var d = new AnimationDriver();
        Next(d).ShouldBe(AnimClip.Idle);
        Next(d).ShouldBeNull("nothing changed, so no restart");
        Next(d, walking: true).ShouldBe(AnimClip.Walk);
        Next(d, walking: true).ShouldBeNull();
        Next(d).ShouldBe(AnimClip.Idle);
    }

    [Fact]
    public void An_attack_plays_once_then_the_walk_or_idle_resumes()
    {
        var d = new AnimationDriver();
        Next(d, walking: true).ShouldBe(AnimClip.Walk);
        Next(d, walking: true, attacks: 1).ShouldBe(AnimClip.Attack);
        d.OneShotActive.ShouldBeTrue();
        Next(d, walking: false, attacks: 1).ShouldBeNull("the one-shot is not cut short by the entity standing still");
        d.OnFinished();
        Next(d, walking: false, attacks: 1).ShouldBe(AnimClip.Idle);
        Next(d, walking: false, attacks: 2).ShouldBe(AnimClip.Attack, "each new attack plays");
    }

    [Fact]
    public void A_hit_interrupts_an_attack_but_an_attack_waits_for_a_hit()
    {
        var d = new AnimationDriver();
        Next(d);
        Next(d, attacks: 1).ShouldBe(AnimClip.Attack);
        Next(d, attacks: 1, hits: 1).ShouldBe(AnimClip.Hit);
        Next(d, attacks: 2, hits: 1).ShouldBeNull("the hit that is playing is not cut off by a new attack");
        d.OnFinished();
        Next(d, attacks: 2, hits: 1).ShouldBe(AnimClip.Idle);
    }

    [Fact]
    public void Death_beats_everything_holds_and_ignores_later_hits_until_it_comes_back_to_life()
    {
        var d = new AnimationDriver();
        Next(d, attacks: 0);
        Next(d, dead: true, attacks: 1, hits: 1).ShouldBe(AnimClip.Die);
        Next(d, dead: true, attacks: 2, hits: 2).ShouldBeNull();
        d.OnFinished();
        d.Current.ShouldBe(AnimClip.Die, "dying holds its last pose");
        Next(d, dead: true, walking: true, attacks: 2, hits: 2).ShouldBeNull();
        Next(d, dead: false, attacks: 2, hits: 2).ShouldBe(AnimClip.Idle);
    }

    [Fact]
    public void A_visual_reused_for_another_entity_does_not_replay_what_that_entity_did_before()
    {
        var d = new AnimationDriver();
        Next(d, attacks: 3, hits: 2);
        d.Reset(attackCount: 7, hitCount: 5);
        Next(d, attacks: 7, hits: 5).ShouldBe(AnimClip.Idle);
        Next(d, attacks: 8, hits: 5).ShouldBe(AnimClip.Attack);
    }

    [Fact]
    public void Every_clip_has_a_vocabulary_animation_id_and_only_idle_and_walk_loop()
    {
        AnimClips.IdOf(AnimClip.Idle).ShouldBe(AnimationIds.Idle);
        AnimClips.IdOf(AnimClip.Walk).ShouldBe(AnimationIds.Walk);
        AnimClips.IdOf(AnimClip.Attack).ShouldBe(AnimationIds.Attack);
        AnimClips.IdOf(AnimClip.Hit).ShouldBe(AnimationIds.Hit);
        AnimClips.IdOf(AnimClip.Die).ShouldBe(AnimationIds.Die);
        Enum.GetValues<AnimClip>().Count(AnimClips.Loops).ShouldBe(2);
        AnimClips.Count.ShouldBe(Enum.GetValues<AnimClip>().Length);
    }

    // ---- hits reach the view model on the render clock ----

    private static EntityRecord Standing(long id, string kind = EntityKinds.Monster) => new(id, kind, "monster.a", new GridCell(1, 1), Directions.N, null, 0);

    private static WorldView World()
    {
        var world = new WorldView(new ClientConfig());
        world.ApplyWelcome(new WelcomeMessage(1, 1, "map.a", 10)); // render delay: 2 ticks of 100 ms
        world.ApplySnapshot(new SnapshotMessage(0, "map.a", 1, [Standing(1, EntityKinds.Player), Standing(2), Standing(3)]), 0);
        return world;
    }

    [Fact]
    public void A_hit_makes_the_attacker_attack_and_the_target_flinch_when_render_time_gets_there()
    {
        var world = World();
        world.ApplyHits([new HitRecord(1, 2, 13, HitResults.Hit)], now: 5.0);

        world.Advance(5.1);
        world.Find(1)!.AttackCount.ShouldBe(0, "the view is 200 ms behind, so it has not happened on screen yet");
        world.Find(2)!.HitCount.ShouldBe(0);

        world.Advance(5.25);
        world.Find(1)!.AttackCount.ShouldBe(1, "the swing starts at render time");
        world.Find(2)!.HitCount.ShouldBe(0, "the blow lands an impact delay (0.2 s by default) after the swing starts");

        world.Advance(5.45);
        world.Find(1)!.HitCount.ShouldBe(0);
        world.Find(2)!.HitCount.ShouldBe(1);
        world.Find(2)!.AttackCount.ShouldBe(0);

        world.Advance(6.0);
        world.Find(1)!.AttackCount.ShouldBe(1, "one hit is one animation");
    }

    [Fact]
    public void A_miss_is_an_attack_without_a_flinch_and_unknown_ids_are_ignored()
    {
        var world = World();
        world.ApplyHits([new HitRecord(2, 3, 0, HitResults.Miss), new HitRecord(99, 98, 5, HitResults.Crit)], now: 1.0);

        world.Advance(1.5);
        world.Find(2)!.AttackCount.ShouldBe(1);
        world.Find(3)!.HitCount.ShouldBe(0);
    }

    [Fact]
    public void A_snapshot_starts_every_entity_with_no_pending_animations()
    {
        var world = World();
        world.ApplyHits([new HitRecord(1, 2, 13, HitResults.Hit)], now: 1.0);
        world.ApplySnapshot(new SnapshotMessage(5, "map.a", 1, [Standing(1, EntityKinds.Player), Standing(2)]), 1.05);

        world.Advance(2.0);
        world.Find(1)!.AttackCount.ShouldBe(0);
        world.Find(2)!.HitCount.ShouldBe(0);
    }

    // ---- bundle to model key ----

    [Fact]
    public void A_display_key_maps_to_the_model_content_names_for_jobs_monsters_npcs_and_ground_items()
    {
        var bytes = ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes;
        DisplayBundle.TryRead(bytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);

        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        var expected = new Dictionary<string, string>();
        foreach (var (list, field) in new[] { ("jobs", "model"), ("monsters", "model"), ("npcs", "model"), ("items", "ground") })
            foreach (var entry in document.RootElement.GetProperty(list).EnumerateArray())
                if (entry.TryGetProperty(field, out var key)) expected[entry.GetProperty("id").GetString()!] = key.GetString()!;

        expected.Count.ShouldBeGreaterThan(10);
        foreach (var (id, key) in expected) bundle!.ModelKeyOf(id).ShouldBe(key, id);
        bundle!.ModelKeyOf("no.such.id").ShouldBeNull();
        bundle.ModelKeyOf(null).ShouldBeNull();
        bundle.ModelKeys.ShouldBe(expected.Values.Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList());
    }
}
