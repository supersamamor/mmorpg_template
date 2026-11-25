using Game.Server.Events;
using Game.Server.World;
using Game.Server.World.Autopilot;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Effects;
using Game.Server.World.Inventory;
using Game.Server.World.Quests;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Server;
using Game.Tests.World;
using static Game.Tests.Autopilot.AutopilotContentKit;

namespace Game.Tests.Autopilot;

/// <summary>The quest autopilot on the whole pipeline (T-604): objectives, travel, the queue, pausing, stopping and refusals.</summary>
public class AutopilotTests
{
    // ---- the whole path ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void First_hunt_is_played_from_accept_to_turn_in_with_no_input_after_the_start()
    {
        var rig = new AutopilotRig(TestGrids.SampleContent);
        var player = rig.Join(new GridCell(12, 12), boost: false);   // a new character with its Bone Dirk, in the safe zone
        rig.Accept(player, "quest.first_hunt");
        var draughts = rig.Count(player, "item.healing_draught");

        rig.Start(player, "quest.first_hunt");
        rig.RunsTo(player, AutopilotStops.Finished, 6000);

        rig.Completed(player, "quest.first_hunt").ShouldBeTrue();
        rig.Of<MonsterKilled>().Count(k => k.Credited.Contains(player) && k.MonsterId == "monster.gel_slime").ShouldBeGreaterThanOrEqualTo(5);
        rig.Of<MonsterKilled>().ShouldAllBe(k => k.MonsterId == "monster.gel_slime");
        rig.Bag(player).Currency.ShouldBe(150);
        rig.Count(player, "item.healing_draught").ShouldBe(draughts + 5);   // the reward; none drunk
        rig.Of<ItemUsed>().ShouldBeEmpty();
        rig.Of<ItemAcquired>().Select(a => a.ItemId).Distinct().ShouldBe(["item.gel_glob"]);   // only what the quest wants is picked up
        new[] { AutopilotActivities.Travelling, AutopilotActivities.Hunting, AutopilotActivities.Gathering, AutopilotActivities.Talking }.ShouldBeSubsetOf(rig.Activities(player).Select(a => a!));
        rig.Of<ServiceOpened>().ShouldBeEmpty();
        rig.Of<DialogueEnded>().Where(e => e.Player == player).ShouldHaveSingleItem().Reason.ShouldBe(DialogueEndReasons.Closed);   // it closed what it opened
        rig.Log.Lines.ShouldNotContain(l => l.StartsWith("ERROR"));
    }

    // ---- objectives -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Kill_hunts_the_nearest_and_waits_for_the_respawn()
    {
        var rig = new AutopilotRig(Build(grids: Grids(tame: 1)));
        var player = rig.Join();
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.RunsTo(player, AutopilotStops.Finished);

        rig.Completed(player, "quest.kill").ShouldBeTrue();
        rig.Of<MonsterKilled>().Count.ShouldBe(2);
        rig.Activities(player).ShouldContain(AutopilotActivities.Waiting);   // one placed, two wanted
        rig.Of<ItemAcquired>().ShouldBeEmpty();                              // a kill quest wants no items
    }

    [Fact]
    public void The_nearest_reachable_monster_is_the_one_with_the_shortest_walk_not_the_shortest_line()
    {
        // A wall between the player and the monster two cells away: the walk around it is longer than to the one six cells off.
        var walls = Enumerable.Range(0, 11).Select(y => new GridCell(8, y)).ToArray();
        var cells = new byte[30 * 12];
        Array.Fill(cells, CellFlagBits.Walkable);
        foreach (var w in walls) cells[w.Y * 30 + w.X] = CellFlagBits.BlocksLos;
        var grids = Grids().ToDictionary(p => p.Key, p => p.Value);
        grids[Alpha] = MapGrid.Create(new MapGridFile
        {
            MapId = Alpha, Width = 30, Height = 12, Cells = Convert.ToBase64String(cells),
            Spawns = [new GridSpawn { MonsterId = Tame, Area = new GridRect(9, 2, 1, 1), Count = 1, RespawnSeconds = 100 }, new GridSpawn { MonsterId = Tame, Area = new GridRect(1, 2, 1, 1), Count = 1, RespawnSeconds = 100 }],
        });
        var rig = new AutopilotRig(Build(grids: grids));
        var player = rig.Join(new GridCell(7, 2));
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.TickUntil(() => rig.Of<AttackStarted>().Count > 0, 50, "the first attack");
        rig.Get(rig.Of<AttackStarted>()[0].Target).Cell.X.ShouldBeLessThan(8);   // the farther one as the crow flies, on this side of the wall
        rig.RunsTo(player, AutopilotStops.Finished);
    }

    [Fact]
    public void A_monster_out_of_sight_is_walked_up_to_before_it_is_aimed_at()
    {
        var content = Build();
        var rig = new AutopilotRig(content);
        var player = rig.Join(new GridCell(1, 1));
        rig.World.Warp(player, Alpha, new GridCell(28, 11));   // 14 cells from the nearest, with 12 in sight
        rig.Ticks(2);
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        var sight = content.Balance.Aoi.ViewRadius * content.Balance.Aoi.BucketSize;
        rig.TickUntil(() => rig.Of<AttackStarted>().Count > 0, 100, "the first attack");
        var attack = rig.Events.First(e => e.Event is AttackStarted);
        GridDistance.Chebyshev(rig.Get(player).Cell, rig.Get(((AttackStarted)attack.Event).Target).Cell).ShouldBeLessThanOrEqualTo(sight);
        rig.Of<CellReached>().Count(e => e.Entity == player).ShouldBeGreaterThan(0);
        rig.RunsTo(player, AutopilotStops.Finished);
    }

    [Fact]
    public void Collect_hunts_what_drops_the_item_picks_that_item_up_only_and_counts_what_the_bag_holds()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Put(player, Wanted);
        rig.Accept(player, "quest.collect");
        rig.Progress(player).ActiveQuests["quest.collect"].Counters.ShouldBe([1]);

        rig.Start(player, "quest.collect");
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Of<MonsterKilled>().Count.ShouldBe(1);   // one of the two was there already
        rig.Of<ItemAcquired>().Select(a => a.ItemId).ShouldBe([Wanted]);
        rig.Count(player, Other).ShouldBe(0);
        rig.Count(player, Wanted).ShouldBe(0);       // consumed
    }

    [Fact]
    public void An_item_another_player_has_priority_on_is_left_until_the_priority_is_over()
    {
        var rig = new AutopilotRig(Build(grids: Grids(tame: 0)));
        var player = rig.Join();
        var other = rig.Join(new GridCell(13, 10));
        rig.Put(player, Wanted);
        rig.Accept(player, "quest.collect");
        var now = rig.World.GetInstance(Alpha).TickNumber * TickRate.IntervalMs;
        var item = new GroundItem(Wanted, 1, default, other, now, now + 5000, now + 60000);
        rig.World.Spawn(EntityKind.Item, Alpha, new GridCell(13, 6), Directions.S, e =>
        {
            e.DefinitionId = Wanted;
            e.GroundItem = item;
        });
        rig.Start(player, "quest.collect");
        rig.Ticks(40);
        rig.Of<ItemAcquired>().ShouldBeEmpty();
        rig.Of<ItemRefused>().ShouldBeEmpty();   // it does not even try
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Of<ItemAcquired>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Reach_walks_to_the_cell()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, "quest.reach");
        rig.Start(player, "quest.reach");
        rig.RunsTo(player, AutopilotStops.Finished);
        GridDistance.Chebyshev(rig.Get(player).Cell, new GridCell(20, 10)).ShouldBeLessThanOrEqualTo(1);
        rig.Activities(player).ShouldBe([AutopilotActivities.Travelling]);
    }

    [Fact]
    public void Talk_goes_to_the_npc_and_the_talk_is_the_turn_in()
    {
        var rig = new AutopilotRig();
        var player = rig.Join(new GridCell(20, 10));
        rig.Accept(player, "quest.talk");
        rig.Start(player, "quest.talk");
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Completed(player, "quest.talk").ShouldBeTrue();
        rig.Activities(player).ShouldBe([AutopilotActivities.Travelling, AutopilotActivities.Talking]);
        rig.Of<NpcTalked>().ShouldHaveSingleItem().NpcId.ShouldBe(Keeper);
    }

    [Theory]
    [InlineData("quest.level", AutopilotPauses.NeedsLevel)]
    [InlineData("quest.job", AutopilotPauses.NeedsJob)]
    [InlineData("quest.equip", AutopilotPauses.NeedsEquip)]
    [InlineData("quest.use", AutopilotPauses.NeedsItemUse)]
    public void What_only_the_player_may_do_pauses_the_run(string quest, string pause)
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Put(player, "item.leather_cap");
        var draughts = rig.Count(player, "item.healing_draught");
        rig.Accept(player, quest);
        rig.Start(player, quest);
        rig.PausesWith(player, pause);
        rig.Ticks(50);
        rig.Of<ItemEquipped>().ShouldBeEmpty();
        rig.Of<ItemUsed>().ShouldBeEmpty();
        rig.Count(player, "item.healing_draught").ShouldBe(draughts);
        rig.Progress(player).JobId.ShouldBe("job.wanderer");
        rig.Of<AutopilotChanged>().Count.ShouldBe(1);   // paused at once, and said so once
    }

    [Fact]
    public void A_step_with_something_to_play_is_played_before_it_pauses()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, "quest.level_and_kill");
        rig.Start(player, "quest.level_and_kill");
        rig.PausesWith(player, AutopilotPauses.NeedsLevel);
        rig.Progress(player).ActiveQuests["quest.level_and_kill"].Counters[1].ShouldBe(1);   // the kill, second in the list, came first
    }

    [Fact]
    public void A_paused_run_goes_on_by_itself_when_its_objective_is_met()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, "quest.job", "quest.talk");
        rig.Start(player, "quest.job", "quest.talk");
        rig.PausesWith(player, AutopilotPauses.NeedsJob);
        rig.Progress(player).UnlockedJobs.Add("job.warden");
        rig.Progress(player).CompletedQuests.Add("quest.warden_trial");
        rig.Progress(player).BaseLevel = 20;
        rig.Progress(player).JobLevel = 20;
        rig.Send(new ChangeJob(player, "job.warden"));   // not an order to the character: it does not end the run
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Completed(player, "quest.job").ShouldBeTrue();
        rig.Completed(player, "quest.talk").ShouldBeTrue();
    }

    [Fact]
    public void Steps_follow_one_another_in_one_run()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, "quest.steps");
        rig.Start(player, "quest.steps");
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Completed(player, "quest.steps").ShouldBeTrue();
        rig.Of<QuestStepCompleted>().ShouldHaveSingleItem().Step.ShouldBe(0);
    }

    // ---- travel ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("quest.kill_near", Beta, 1)]
    [InlineData("quest.kill_far", Gamma, 2)]
    [InlineData("quest.reach_far", Gamma, 2)]
    [InlineData("quest.talk_far", Gamma, 2)]
    public void It_travels_over_warps_and_its_run_follows(string quest, string map, int warps)
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, quest);
        rig.Start(player, quest);
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Completed(player, quest).ShouldBeTrue();
        rig.Get(player).MapId.ShouldBe(map);
        rig.Of<EntityEnteredMap>().Count(e => e.Entity == player && e.Reason == MapChangeReason.Warp).ShouldBe(warps);   // no warp taken twice
        rig.Of<Teleported>().ShouldBeEmpty();
        rig.Log.Lines.ShouldNotContain(l => l.StartsWith("ERROR"));
    }

    [Fact]
    public void A_way_longer_than_one_path_search_allows_is_walked_in_pieces()
    {
        var rig = new AutopilotRig(Build(a => a with { LegCells = 5 }, balance: b => b with { Movement = b.Movement with { MaxPathLength = 5, MaxNodesExpanded = 60 } }));
        var player = rig.Join();
        rig.Accept(player, "quest.reach_far");
        rig.Start(player, "quest.reach_far");
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Of<MovementStarted>().Where(e => e.Entity == player).ShouldAllBe(e => e.PathLength <= 5);
    }

    [Theory]
    [InlineData("quest.kill_walled")]
    [InlineData("quest.reach_wall")]
    [InlineData("quest.kill_nowhere")]
    public void What_cannot_be_reached_pauses(string quest)
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, quest);
        rig.Start(player, quest);
        rig.PausesWith(player, quest == "quest.kill_nowhere" ? AutopilotPauses.NoSource : AutopilotPauses.Unreachable);
    }

    [Fact]
    public void Another_map_is_unreachable_without_warps()
    {
        var rig = new AutopilotRig(Build(grids: Grids(warps: false)));
        var player = rig.Join();
        rig.Accept(player, "quest.kill_far");
        rig.Start(player, "quest.kill_far");
        rig.PausesWith(player, AutopilotPauses.Unreachable);

        rig.World.Warp(player, Gamma, new GridCell(3, 3));   // something else takes it there: the run goes on
        rig.RunsTo(player, AutopilotStops.Finished);
    }

    [Fact]
    public void A_warp_by_something_else_in_the_middle_of_a_journey_is_planned_around()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, "quest.kill_near");
        rig.Start(player, "quest.kill_near");
        rig.Ticks(15);
        rig.World.Warp(player, Gamma, new GridCell(10, 10));
        rig.TickUntil(() => rig.Get(player).MapId == Gamma, 5);
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Get(player).MapId.ShouldBe(Beta);

        var second = rig.Join();   // and one within the map
        rig.Accept(second, "quest.reach");
        rig.Start(second, "quest.reach");
        rig.Ticks(10);
        rig.World.Warp(second, Alpha, new GridCell(1, 1));
        rig.RunsTo(second, AutopilotStops.Finished);
        rig.Completed(second, "quest.reach").ShouldBeTrue();
    }

    [Fact]
    public void Warps_that_are_not_on_the_way_are_walked_around()
    {
        // A column of warps back to the start across the straight way east, with a gap far from it.
        var warps = Enumerable.Range(1, 11).Select(y => new GridWarp { Cell = new GridCell(13, y), TargetMapId = Alpha, TargetCell = new GridCell(2, 6) }).ToArray();
        var grids = Grids().ToDictionary(p => p.Key, p => p.Value);
        var cells = new byte[30 * 12];
        Array.Fill(cells, CellFlagBits.Walkable);
        grids[Alpha] = MapGrid.Create(new MapGridFile { MapId = Alpha, Width = 30, Height = 12, Cells = Convert.ToBase64String(cells), Warps = warps });
        var rig = new AutopilotRig(Build(grids: grids));
        var player = rig.Join();
        rig.Accept(player, "quest.reach");
        rig.Start(player, "quest.reach");
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Of<EntityEnteredMap>().Count(e => e.Entity == player && e.Reason == MapChangeReason.Warp).ShouldBe(0);
    }

    // ---- the queue ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Quests_are_played_in_the_order_they_were_ticked()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        string[] order = ["quest.reach", "quest.kill", "quest.talk", "quest.collect"];
        rig.Accept(player, order);
        rig.Start(player, order);
        rig.RunsTo(player, AutopilotStops.Finished, 2500);
        rig.Of<QuestCompleted>().Select(e => e.QuestId).ShouldBe(order);
        rig.Of<AutopilotChanged>().Select(e => e.Status.QuestId).Where(q => q is not null).Distinct().ShouldBe(order);
    }

    [Fact]
    public void A_quest_that_was_done_or_dropped_meanwhile_is_skipped_and_dropping_the_current_one_ends_the_run()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, "quest.reach", "quest.kill", "quest.talk");
        rig.Start(player, "quest.reach", "quest.kill", "quest.talk");
        rig.Ticks(3);
        rig.Send(new AbandonQuest(player, "quest.kill"));
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Of<QuestCompleted>().Select(e => e.QuestId).ShouldBe(["quest.reach", "quest.talk"]);

        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.Ticks(3);
        rig.Send(new AbandonQuest(player, "quest.kill"));
        rig.RunsTo(player, AutopilotStops.QuestAbandoned, 10);
    }

    [Fact]
    public void A_new_start_replaces_the_list()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, "quest.level", "quest.talk");
        rig.Start(player, "quest.level");
        rig.PausesWith(player, AutopilotPauses.NeedsLevel);
        rig.Start(player, "quest.talk");
        rig.RunsTo(player, AutopilotStops.Finished);
    }

    // ---- fighting ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void It_steps_out_of_a_safe_zone_to_fight()
    {
        var rig = new AutopilotRig(Build(grids: Grids(safe: new GridRect(0, 0, 11, 12))));
        var player = rig.Join();
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Of<AttackRefused>().Where(e => e.Attacker == player).ShouldBeEmpty();
    }

    [Fact]
    public void Shortcut_skills_are_cast_in_slot_order_when_ready_else_it_auto_attacks()
    {
        var rig = new AutopilotRig(Build(tameHp: 100_000));
        var player = rig.Join(boost: false);
        rig.Boost(player, atk: 50);
        rig.Get(player).Vitals!.Sp = 10_000;
        rig.Get(player).Stats!.Modifiers.Add("test:sp", "max_sp", ModifierOp.Flat, 10_000);
        rig.Ticks();
        rig.Get(player).Vitals!.Sp = 10_000;
        foreach (var skill in new[] { "skill.ember_bolt", "skill.twin_strike", "skill.second_wind", "skill.return_home", "skill.mana_font", "skill.call_seedling", "skill.shockwave" }) rig.Learn(player, skill);
        rig.Accept(player, "quest.kill");
        rig.StartWith(player, ["quest.kill"], "skill.return_home", "skill.call_seedling", "skill.mana_font", "skill.shockwave", "skill.second_wind", "skill.ember_bolt", "skill.twin_strike");
        rig.Ticks(200);

        var casts = rig.Of<SkillRequested>().Where(e => e.Caster == player).Select(e => e.SkillId).ToList();
        casts[0].ShouldBe("skill.ember_bolt");          // the first slot that may be cast on a monster with a dagger in hand
        casts.ShouldContain("skill.twin_strike");       // while the bolt cools down
        casts.ShouldNotContain("skill.second_wind");    // full HP: nothing to do
        casts.ShouldNotContain("skill.shockwave");      // not with a dagger
        casts.Distinct().ShouldBe(["skill.ember_bolt", "skill.twin_strike"], ignoreOrder: true);   // never a teleport, a summon or a friendly ground skill
        rig.Of<SkillRefused>().ShouldBeEmpty();         // the server's own rules are checked first: nothing is sent to be refused
        // Every cast kept its cooldown.
        var bolts = rig.Events.Where(e => e.Event is SkillRequested { SkillId: "skill.twin_strike" }).Select(e => e.Tick).ToList();
        for (var i = 1; i < bolts.Count; i++) (bolts[i] - bolts[i - 1]).ShouldBeGreaterThanOrEqualTo(15);
    }

    [Fact]
    public void Without_sp_it_auto_attacks()
    {
        var rig = new AutopilotRig(Build(tameHp: 100_000));
        var player = rig.Join(boost: false);
        rig.Boost(player, atk: 50);
        rig.Get(player).Vitals!.Sp = 0;
        rig.Learn(player, "skill.twin_strike");
        rig.Accept(player, "quest.kill");
        rig.StartWith(player, ["quest.kill"], "skill.twin_strike");
        rig.Ticks(60);
        rig.Of<DamageDealt>().Count(e => e.Source == player).ShouldBeGreaterThan(3);
        rig.Of<SkillRequested>().ShouldBeEmpty();
    }

    [Fact]
    public void A_supportive_skill_is_cast_on_the_player_when_it_helps_and_no_potion_is_drunk()
    {
        var rig = new AutopilotRig(Build(tameHp: 100_000));
        var player = rig.Join(boost: false);
        rig.Boost(player, atk: 50);
        rig.Learn(player, "skill.second_wind");
        rig.Accept(player, "quest.kill");
        rig.Get(player).Vitals!.Hp = rig.Get(player).Vitals!.MaxHp / 4;
        rig.StartWith(player, ["quest.kill"], "skill.second_wind");
        rig.TickUntil(() => rig.Of<SkillRequested>().Any(e => e.SkillId == "skill.second_wind"), 200, "the heal");
        rig.Of<SkillRequested>()[0].Target.ShouldBeNull();   // on itself
        rig.TickUntil(() => rig.Of<HpRestored>().Any(e => e.Target == player), 10);
        rig.Of<ItemUsed>().ShouldBeEmpty();
    }

    [Fact]
    public void On_a_journey_it_walks_on_when_attacked_and_at_the_hunting_ground_it_fights_back()
    {
        var rig = new AutopilotRig(Build(grids: Grids(tame: 0, fierce: 2)));
        var player = rig.Join(new GridCell(14, 6));
        rig.Accept(player, "quest.kill_near", "quest.kill");
        rig.Start(player, "quest.kill_near");
        rig.TickUntil(() => rig.Get(player).MapId == Beta, 300, "the journey past the fierce ones");
        rig.Of<DamageDealt>().ShouldContain(e => e.Target == player);          // they did hit it
        rig.Of<AttackStarted>().ShouldBeEmpty();                               // and it walked on
        rig.Send(new StopAutopilot(player, AutopilotStops.Stopped));
        rig.Ticks(2);

        rig.World.Warp(player, Alpha, new GridCell(13, 6));   // waiting at the (empty) ground of the tame ones
        rig.Ticks(2);
        rig.Start(player, "quest.kill");
        rig.TickUntil(() => rig.Of<MonsterKilled>().Any(k => k.MonsterId == Fierce), 400, "fighting back");
        rig.Get(player).Autopilot!.Active.ShouldBeTrue();
    }

    [Fact]
    public void A_kill_stolen_by_another_player_is_hunted_again()
    {
        var rig = new AutopilotRig(Build(grids: Grids(tame: 1), tameHp: 60));
        var player = rig.Join(boost: false);
        rig.Boost(player, atk: 1);
        var thief = rig.Join(new GridCell(13, 7));
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.TickUntil(() => rig.Of<AttackStarted>().Any(e => e.Attacker == player), 100, "the hunt");
        var prey = rig.Of<AttackStarted>().Last(e => e.Attacker == player).Target;
        rig.Send(new AttackTarget(thief, prey));
        rig.TickUntil(() => rig.Of<MonsterKilled>().Any(k => k.Monster == prey), 60, "the theft");
        rig.Of<MonsterKilled>().Single(k => k.Monster == prey).Credited.ShouldContain(thief);
        rig.Boost(player);
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Completed(player, "quest.kill").ShouldBeTrue();
    }

    [Fact]
    public void Two_players_on_the_same_monster_share_the_kill()
    {
        var rig = new AutopilotRig(Build(grids: Grids(tame: 1), tameHp: 4000));
        var a = rig.Join(new GridCell(11, 6), boost: false);
        var b = rig.Join(new GridCell(16, 6), boost: false);
        rig.Boost(a, 300);
        rig.Boost(b, 300);
        rig.Accept(a, "quest.kill");
        rig.Accept(b, "quest.kill");
        rig.Start(a, "quest.kill");
        rig.Start(b, "quest.kill");
        rig.RunsTo(a, AutopilotStops.Finished, 2500);
        rig.RunsTo(b, AutopilotStops.Finished, 2500);
        rig.Of<MonsterKilled>().ShouldContain(k => k.Credited.Contains(a) && k.Credited.Contains(b));   // credit by damage share, as for anyone
    }

    // ---- the same rewards -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Rewards_are_those_of_manual_play()
    {
        (long Exp, int Level, long Currency) Play(bool autopilot)
        {
            var rig = new AutopilotRig(Build(grids: Grids(tame: 2), globChance: 0));
            var player = rig.Join();
            rig.Accept(player, "quest.kill", "quest.talk");
            if (autopilot)
            {
                rig.Start(player, "quest.kill", "quest.talk");
                rig.RunsTo(player, AutopilotStops.Finished);
            }
            else
            {
                while (!rig.Completed(player, "quest.kill"))
                {
                    var prey = rig.World.GetInstance(Alpha).Entities.First(e => e.DefinitionId == Tame && !e.Dead).Id;
                    rig.Send(new AttackTarget(player, prey));
                    rig.TickUntil(() => rig.Of<MonsterKilled>().Any(k => k.Monster == prey), 200);
                    rig.Ticks(3);
                }
                rig.Send(new TalkTo(player, rig.World.GetInstance(Alpha).Entities.First(e => e.DefinitionId == Keeper).Id));
                rig.TickUntil(() => rig.Completed(player, "quest.talk"), 200);
            }
            rig.Ticks(5);
            var state = rig.Progress(player);
            rig.Of<MonsterKilled>().Count.ShouldBe(2);
            return (state.BaseExp, state.BaseLevel, rig.Bag(player).Currency);
        }

        Play(autopilot: true).ShouldBe(Play(autopilot: false));
    }

    // ---- dialogue -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void At_an_npc_that_offers_everything_it_only_talks()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        var stats = rig.Progress(player).Stats.ToDictionary(p => p.Key, p => p.Value);
        rig.Accept(player, "quest.talk");
        rig.Start(player, "quest.talk");
        rig.RunsTo(player, AutopilotStops.Finished);

        rig.Of<DialogueShown>().ShouldNotBeEmpty();
        Untouched(rig, player);
        rig.Progress(player).Stats.ShouldBe(stats);
        rig.Progress(player).ActiveQuests.ShouldBeEmpty();   // quest.other was on offer and not ticked
        rig.Of<DialogueEnded>().ShouldHaveSingleItem().Reason.ShouldBe(DialogueEndReasons.Closed);
    }

    [Fact]
    public void Without_a_safe_choice_it_closes_the_dialogue_and_pauses()
    {
        var rig = new AutopilotRig(questSystem: false);   // the talk does not count: only its dialogue could turn anything in
        var player = rig.Join();
        rig.PutInLog(player, "quest.talk");
        rig.Start(player, "quest.talk");
        rig.PausesWith(player, AutopilotPauses.NoSafeChoice, 200);
        rig.Ticks(5);
        rig.Of<DialogueShown>().ShouldHaveSingleItem().NodeKey.ShouldBe("greet");
        rig.Of<DialogueEnded>().ShouldHaveSingleItem().Reason.ShouldBe(DialogueEndReasons.Closed);
        Untouched(rig, player);

        var mute = new AutopilotRig();   // an NPC with nothing to say at all
        var other = mute.Join();
        mute.Accept(other, "quest.talk_mute");
        mute.Start(other, "quest.talk_mute");
        mute.PausesWith(other, AutopilotPauses.NoSafeChoice, 200);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void It_continues_toward_a_choice_of_a_ticked_quest_and_no_further(bool ticked)
    {
        var rig = new AutopilotRig(questSystem: false);
        var player = rig.Join();
        rig.PutInLog(player, "quest.talk_trader");
        rig.PutInLog(player, "quest.kill");   // the step choice is this one's
        if (ticked) rig.Start(player, "quest.talk_trader", "quest.kill");
        else rig.Start(player, "quest.talk_trader");
        rig.PausesWith(player, AutopilotPauses.NoSafeChoice, 300);
        rig.Ticks(5);
        rig.Of<DialogueShown>().Select(d => d.NodeKey).ShouldBe(ticked ? ["one", "two", "three"] : ["one"]);
        Untouched(rig, player);
    }

    private static void Untouched(AutopilotRig rig, EntityId player)
    {
        rig.Of<ServiceOpened>().ShouldBeEmpty();
        rig.Of<SavePointSet>().ShouldBeEmpty();
        rig.Of<DialogueEffectsRequested>().ShouldBeEmpty();
        rig.Of<DialogueItemsRequested>().ShouldBeEmpty();
        rig.Of<StatsReset>().ShouldBeEmpty();
        rig.Of<QuestStartRequested>().Where(e => e.QuestId == "quest.other").ShouldBeEmpty();
        rig.SavePoints.TryGet(player, out _).ShouldBeFalse();
        rig.Get(player).OpenService.ShouldBeNull();
    }

    // ---- stopping ------------------------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> ManualInputs => new()
    {
        "move", "stop", "attack", "stop_attack", "skill", "pick_up", "use_item", "equip", "unequip", "drop_item", "talk", "dialogue_choice", "dialogue_close", "shop_buy", "shop_sell", "respawn",
    };

    [Theory]
    [MemberData(nameof(ManualInputs))]
    public void The_players_own_input_ends_the_run_and_is_carried_out(string input)
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        var draught = rig.Bag(player).Entries.First(e => e.ItemId == "item.healing_draught").Id;
        rig.Accept(player, "quest.reach_far");
        rig.Start(player, "quest.reach_far");
        rig.Ticks(8);
        rig.Status(player).Mode.ShouldBe(AutopilotModes.Running);

        var keeper = rig.World.GetInstance(Alpha).Entities.First(e => e.DefinitionId == Keeper).Id;
        IIntent order = input switch
        {
            "move" => new MoveTo(player, new GridCell(2, 2)),
            "stop" => new StopMove(player),
            "attack" => new AttackTarget(player, rig.World.GetInstance(Alpha).Entities.First(e => e.DefinitionId == Tame).Id),
            "stop_attack" => new StopAttack(player),
            "skill" => new UseSkill(player, "skill.second_wind", 0, null, null),
            "pick_up" => new PickUp(player, new EntityId(99999)),
            "use_item" => new UseItem(player, draught),
            "equip" => new Equip(player, draught, null),
            "unequip" => new Unequip(player, EquipSlots.Weapon),
            "drop_item" => new DropItem(player, draught, 1),
            "talk" => new TalkTo(player, keeper),
            "dialogue_choice" => new DialogueChoose(player, 0),
            "dialogue_close" => new DialogueClose(player),
            "shop_buy" => new ShopBuy(player, "item.healing_draught", 1),
            "shop_sell" => new ShopSell(player, draught, 1),
            _ => new Respawn(player),
        };
        var isOrder = input is "move" or "stop" or "attack" or "stop_attack" or "skill" or "pick_up" or "talk" or "respawn";
        rig.Send(new ManualInput(player, isOrder));
        rig.Send(order);
        rig.RunsTo(player, AutopilotStops.ManualInput, 3);

        rig.Ticks(60);
        rig.Get(player).Autopilot!.Active.ShouldBeFalse();   // nothing restarts it
        switch (input)
        {
            case "move": rig.Get(player).Cell.ShouldBe(new GridCell(2, 2)); break;
            case "attack": rig.Of<AttackStarted>().ShouldContain(e => e.Attacker == player); break;
            case "talk": rig.Of<NpcTalked>().ShouldContain(e => e.Player == player); break;
            case "use_item": rig.Of<ItemUsed>().ShouldContain(e => e.Player == player); break;
        }
        if (!isOrder) rig.Of<MovementStopped>().Last(e => e.Entity == player).Reason.ShouldBe(MovementReasons.Cancelled);   // its journey ends with it
    }

    [Fact]
    public void An_order_the_autopilot_queued_a_moment_ago_never_overrules_the_player()
    {
        // Whatever tick the manual move lands on (the autopilot thinks in some of them), it is the one that counts.
        for (var wait = 1; wait <= 12; wait++)
        {
            var rig = new AutopilotRig();
            var player = rig.Join();
            rig.Accept(player, "quest.reach_far");
            rig.Start(player, "quest.reach_far");
            rig.Ticks(wait);
            var manual = new GridCell(3, 10);
            // The player's input first, the autopilot's order behind it in the same queue: the worst case.
            rig.Send(new ManualInput(player, true));
            rig.Send(new MoveTo(player, manual));
            rig.Send(new Automated(new MoveTo(player, new GridCell(20, 2))));
            rig.Ticks(60);
            rig.Get(player).Cell.ShouldBe(manual, $"after {wait} ticks");
        }
        var counted = new AutopilotRig();
        var p = counted.Join();
        counted.Send(new ManualInput(p, true));
        counted.Send(new Automated(new StopMove(p)));
        counted.Ticks();
        counted.World.GetInstance(Alpha).Counters.IntentsDroppedCancelled.ShouldBe(1);
    }

    [Fact]
    public void Input_that_arrives_while_the_character_is_between_maps_still_ends_the_run()
    {
        var rig = new AutopilotRig();
        var player = rig.Join(new GridCell(27, 6));
        rig.Accept(player, "quest.kill_near");
        rig.Start(player, "quest.kill_near");
        var alpha = rig.World.GetInstance(Alpha);
        var beta = rig.World.GetInstance(Beta);
        for (var i = 0; i < 100 && rig.World.LocationOf(player) == Alpha; i++) alpha.Tick();   // only alpha ticks: it is in flight now
        rig.World.LocationOf(player).ShouldBe(Beta);
        beta.TryGetEntity(player, out _).ShouldBeFalse();
        rig.Send(new ManualInput(player, true));
        rig.RunsTo(player, AutopilotStops.ManualInput, 5);
    }

    [Theory]
    [InlineData(AutopilotStops.Stopped)]
    [InlineData(AutopilotStops.Disconnected)]
    public void A_stop_ends_it_and_the_character_stands_still(string reason)
    {
        var rig = new AutopilotRig(Build(tameHp: 100_000));
        var player = rig.Join(boost: false);
        rig.Boost(player, 10);
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.TickUntil(() => rig.Of<DamageDealt>().Any(e => e.Source == player), 100, "the fight");
        rig.Send(new StopAutopilot(player, reason));
        rig.RunsTo(player, reason, 3);
        rig.Ticks(3);
        var hits = rig.Of<DamageDealt>().Count(e => e.Source == player);
        rig.Ticks(40);
        rig.Of<DamageDealt>().Count(e => e.Source == player).ShouldBe(hits);
        rig.Send(new StopAutopilot(player, reason));   // without a run: nothing
        rig.Ticks(2);
        rig.Of<AutopilotChanged>().Count(e => e.Status.Mode == AutopilotModes.Off).ShouldBe(1);
    }

    [Fact]
    public void Death_ends_it_and_a_respawn_does_not_restart_it()
    {
        var rig = new AutopilotRig(Build(grids: Grids(fierce: 3)));
        var player = rig.Join(new GridCell(18, 6), boost: false);
        rig.Accept(player, "quest.kill_fierce");
        rig.Get(player).Vitals!.Hp = 1;
        rig.Start(player, "quest.kill_fierce");
        rig.RunsTo(player, AutopilotStops.Died);
        rig.Send(new Respawn(player));
        rig.Ticks(20);
        rig.Get(player).Dead.ShouldBeFalse();
        rig.Get(player).Autopilot!.Active.ShouldBeFalse();
    }

    [Fact]
    public void Leaving_the_world_ends_it()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, "quest.reach_far");
        rig.Start(player, "quest.reach_far");
        rig.Ticks(5);
        rig.World.Despawn(player);
        rig.Ticks(2);
        rig.Status(player).ShouldBe(new AutopilotStatus(AutopilotModes.Off, null, null, AutopilotStops.Logout));
        rig.World.GetInstance(Alpha).Systems.OfType<AutopilotSystem>().Single().RunCount.ShouldBe(0);
    }

    [Fact]
    public void A_bag_with_no_room_for_a_wanted_item_ends_it()
    {
        var rig = new AutopilotRig(Build(balance: b => b with { Inventory = b.Inventory with { MaxEntries = 4 } }));   // the starting items fill it
        var player = rig.Join();
        rig.Accept(player, "quest.collect");
        rig.Start(player, "quest.collect");
        rig.RunsTo(player, AutopilotStops.BagFull);
        rig.Count(player, Wanted).ShouldBe(0);
    }

    [Fact]
    public void A_turn_in_whose_rewards_do_not_fit_ends_it()
    {
        var rig = new AutopilotRig(Build(balance: b => b with { Inventory = b.Inventory with { MaxEntries = 5 } }));
        var player = rig.Join();
        rig.Put(player, Wanted);
        rig.Accept(player, "quest.full");
        rig.Start(player, "quest.full");
        rig.RunsTo(player, AutopilotStops.BagFull);
        rig.Progress(player).ActiveQuests["quest.full"].Held.ShouldBe(QuestReasons.InventoryFull);   // the quest waits, as always
        rig.Get(player).OpenService.ShouldBeNull();
    }

    [Fact]
    public void A_load_too_heavy_to_fight_ends_it()
    {
        var rig = new AutopilotRig(Build(balance: b => b with { Inventory = b.Inventory with { BurdenedPercent = 1, OverweightPercent = 1 } }));
        var player = rig.Join();
        rig.Get(player).Load.ShouldBe(WeightStates.Overweight);
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.RunsTo(player, AutopilotStops.BagFull);
    }

    [Fact]
    public void The_daily_limit_ends_it_refuses_another_and_is_over_the_next_day()
    {
        var rig = new AutopilotRig(Build(a => a with { DailyLimitMinutes = 1, NoProgressMs = 10_000_000 }, Grids(tame: 0)));
        var player = rig.Join();
        rig.Accept(player, "quest.kill", "quest.level");
        rig.Start(player, "quest.kill");
        rig.Ticks(300);
        rig.Start(player, "quest.level");          // paused: the time stands still
        rig.PausesWith(player, AutopilotPauses.NeedsLevel);
        rig.Ticks(600);
        rig.Status(player).Mode.ShouldBe(AutopilotModes.Paused);
        rig.Start(player, "quest.kill");
        rig.Ticks(280);
        rig.Status(player).Mode.ShouldBe(AutopilotModes.Running);
        rig.RunsTo(player, AutopilotStops.TimeLimit, 30);

        rig.Start(player, "quest.kill");
        rig.Ticks(2);
        rig.Of<AutopilotRefused>().ShouldHaveSingleItem().Reason.ShouldBe(AutopilotReasons.TimeLimit);
        rig.Clock.Advance(24L * 60 * 60 * 1000);
        rig.Start(player, "quest.kill");
        rig.Ticks(2);
        rig.Status(player).Mode.ShouldBe(AutopilotModes.Running);
    }

    [Fact]
    public void No_progress_pauses_it()
    {
        var rig = new AutopilotRig(Build(a => a with { NoProgressMs = 5000 }, Grids(tame: 0)));
        var player = rig.Join();
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.PausesWith(player, AutopilotPauses.NoProgress, 200);
        // A kill it gets credit for moves the quest: it goes on.
        rig.World.GetInstance(Alpha).Events.Publish(new MonsterKilled(new EntityId(1), Tame, default, [player]));
        rig.Ticks(4);
        rig.Status(player).Mode.ShouldBe(AutopilotModes.Running);
    }

    [Fact]
    public void A_long_hunt_without_the_drop_is_progress_as_long_as_it_kills()
    {
        var rig = new AutopilotRig(Build(a => a with { NoProgressMs = 8000 }, globChance: 0.0001));
        var player = rig.Join();
        rig.Accept(player, "quest.collect");
        rig.Start(player, "quest.collect");
        rig.Ticks(400);
        rig.Status(player).Mode.ShouldBe(AutopilotModes.Running);
        rig.Of<MonsterKilled>().Count.ShouldBeGreaterThan(5);
    }

    // ---- refusals -------------------------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> Refusals => [.. AutopilotReasons.All];

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_bad_start_is_refused_with_its_reason_and_changes_nothing(string reason)
    {
        var rig = new AutopilotRig(Build(a => a with
        {
            Enabled = reason != AutopilotReasons.Disabled, MaxQuests = 3, MaxSkills = 2, DailyLimitMinutes = reason == AutopilotReasons.TimeLimit ? 1 : 0,
        }));
        var player = rig.Join();
        rig.Accept(player, "quest.kill", "quest.reach");
        rig.Learn(player, "skill.second_wind");
        rig.Learn(player, "skill.tough_body");
        string[] quests = ["quest.kill"];
        SkillLevelRecord[] skills = [];
        string? aboutQuest = null, aboutSkill = null;
        switch (reason)
        {
            case AutopilotReasons.Dead: rig.Get(player).Dead = true; break;
            case AutopilotReasons.TimeLimit:
                rig.Accept(player, "quest.kill_walled", "quest.talk");
                rig.Get(player).Autopilot = null;
                rig.Start(player, "quest.reach");
                rig.Ticks(2);
                for (var i = 0; i < 600; i++) rig.Get(player).Autopilot!.Usage.Add(TickRate.IntervalMs);
                rig.RunsTo(player, AutopilotStops.TimeLimit, 5);
                break;
            case AutopilotReasons.EmptyQueue: quests = []; break;
            case AutopilotReasons.TooManyQuests: quests = ["quest.kill", "quest.reach", "quest.talk", "quest.job"]; break;
            case AutopilotReasons.UnknownQuest: quests = ["quest.kill", aboutQuest = "quest.none"]; break;
            case AutopilotReasons.DuplicateQuest: quests = ["quest.kill", "quest.reach", aboutQuest = "quest.kill"]; break;
            case AutopilotReasons.NotActive: quests = ["quest.kill", aboutQuest = "quest.talk"]; break;
            case AutopilotReasons.TooManySkills: skills = [new("skill.second_wind", 1), new("skill.second_wind", 1), new("skill.second_wind", 1)]; break;
            case AutopilotReasons.UnknownSkill: skills = [new(aboutSkill = "skill.none", 1)]; break;
            case AutopilotReasons.NotLearned: skills = [new(aboutSkill = "skill.ember_bolt", 1)]; break;
            case AutopilotReasons.PassiveSkill: skills = [new(aboutSkill = "skill.tough_body", 1)]; break;
            case AutopilotReasons.LevelTooHigh: skills = [new(aboutSkill = "skill.second_wind", 2)]; break;
        }
        var before = rig.Of<AutopilotChanged>().Count;
        rig.Send(new StartAutopilot(player, quests, skills));
        rig.Ticks(2);
        rig.Of<AutopilotRefused>().ShouldHaveSingleItem().ShouldBe(new AutopilotRefused(player, reason, aboutQuest, aboutSkill));
        rig.Of<AutopilotChanged>().Count.ShouldBe(before);
        (rig.Get(player).Autopilot?.Active ?? false).ShouldBeFalse();
    }

    [Fact]
    public void A_refused_start_leaves_a_run_in_progress_alone()
    {
        var rig = new AutopilotRig();
        var player = rig.Join();
        rig.Accept(player, "quest.reach");
        rig.Start(player, "quest.reach");
        rig.Ticks(3);
        rig.Start(player, "quest.none");
        rig.RunsTo(player, AutopilotStops.Finished);
        rig.Of<AutopilotRefused>().ShouldHaveSingleItem();
    }

    // ---- what its owner hears ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Its_owner_is_told_on_change_only()
    {
        var rig = new AutopilotRig(Build(grids: Grids(tame: 0), autopilot: a => a with { NoProgressMs = 10_000_000 }));
        var player = rig.Join();
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.Ticks(1000);
        rig.Of<AutopilotChanged>().Select(e => e.Status.Doing).ShouldBe([AutopilotActivities.Travelling, AutopilotActivities.Waiting]);
        var told = rig.Of<AutopilotChanged>();
        for (var i = 1; i < told.Count; i++) told[i].ShouldNotBe(told[i - 1]);
    }

    // ---- determinism --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_seed_and_orders_give_the_same_run()
    {
        List<string> Play()
        {
            var rig = new AutopilotRig(TestGrids.SampleContent, seed: 99);
            var player = rig.Join(new GridCell(12, 12), boost: false);
            rig.Accept(player, "quest.first_hunt");
            rig.Start(player, "quest.first_hunt");
            rig.Ticks(1500);
            return rig.Events.Select(e => $"{e.Map}@{e.Tick} {Describe(e.Event)}").ToList();
        }

        var first = Play();
        first.Count.ShouldBeGreaterThan(500);
        Play().ShouldBe(first);
    }

    private static string Describe(IGameEvent e) => e switch
    {
        InventoryChanged i => $"{i.Name} {i.Cause} {i.ItemId} {i.Weight}",
        MonsterKilled k => $"{k.Name} {k.Monster} {string.Join(",", k.Credited)}",
        DialogueShown d => $"{d.Name} {d.NodeKey}",
        QuestStarted or QuestProgressed or QuestStepCompleted or QuestItemsRequested or QuestRewardsRequested or QuestMarkersChanged => e.Name,
        _ => e.ToString()!,
    };
}
