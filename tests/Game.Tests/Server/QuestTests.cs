using Game.Server.Events;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Quests;
using Game.Shared.Items;
using Game.Shared.Progression;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>
/// The real content on the real pipeline (movement, spawner, AI, dialogue, combat, inventory, quests), ticked by hand with every event recorded (T-602). Players
/// are new characters; a test may boost one's attack so kills take a tick or two.
/// </summary>
public sealed class QuestRig
{
    public const string Meadow = "map.starter_meadow";
    public static readonly GridCell Start = new(12, 12);        // the safe zone, within talk range of Odalys (11,10) and Captain Brannoc (14,10)
    public static readonly GridCell Field = new(20, 12);        // outside the safe zone: attacks work here

    public readonly GameWorld World;
    public readonly ManualClock Clock = new();
    public readonly ListLog Log = new();
    public readonly InMemoryInventoryStore Inventories = new();
    public readonly InMemoryProgressionStore Progressions = new();
    public readonly List<(long Tick, IGameEvent Event)> Events = new();
    private readonly NewCharacters _start = new(TestGrids.SampleContent);
    private readonly InventoryContent _items = InventoryContent.From(TestGrids.SampleContent);
    private readonly Game.Server.World.Monsters.MonsterCatalog _monsters = Game.Server.World.Monsters.MonsterCatalog.From(TestGrids.SampleContent);

    public QuestRig()
    {
        var content = TestGrids.SampleContent;
        World = new GameWorld(content.Grids.Values, ServerPipeline.Create(content, Progressions, Inventories, Clock), 602, Clock, Log);
        foreach (var instance in World.Instances) instance.Events.SubscribeAll(e => Events.Add((instance.TickNumber, e)));
    }

    public MapInstance Instance => World.GetInstance(Meadow);

    public EntityId Join(GridCell? at = null)
    {
        var id = World.Spawn(EntityKind.Player, Meadow, at ?? Start, Directions.S, e =>
        {
            e.Stats = _start.CreateStats();
            e.DefinitionId = _start.JobId;
        });
        Ticks(2);
        return id;
    }

    public Entity Get(EntityId id) => Instance.TryGetEntity(id, out var e) ? e : throw new InvalidOperationException($"{id} is gone");

    public ProgressionState Progress(EntityId id) => Progressions.TryGet(id, out var s) ? s : throw new InvalidOperationException("no progression");

    public InventoryState Bag(EntityId id) => Inventories.TryGet(id, out var s) ? s : throw new InvalidOperationException("no inventory");

    public int Count(EntityId id, string itemId) => Bag(id).Entries.Where(e => e.ItemId == itemId && e.Slot is null).Sum(e => e.Quantity);

    public long Put(EntityId id, string itemId, int quantity = 1)
    {
        var delta = new InventoryDelta();
        _items.Rules.TryAdd(Bag(id), itemId, quantity, int.MaxValue, Inventories.NextEntryId, delta).ShouldBeNull();
        return delta.Upserted.Last().Id;
    }

    /// <summary>Takes every carried unit of an item out of the bag (test setup: no event).</summary>
    public void Remove(EntityId id, string itemId)
    {
        var count = Count(id, itemId);
        if (count > 0) _items.Rules.TryExchange(Bag(id), [new(itemId, count)], [], int.MaxValue, Inventories.NextEntryId, new InventoryDelta()).ShouldBeNull();
    }

    public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

    public void Publish(IGameEvent e) => Instance.Events.Publish(e);

    /// <summary>EXP as a completed quest leaves it for combat to pay.</summary>
    public void Owe(EntityId player, long baseExp, long jobExp)
    {
        Progress(player).OwedRewards.Add(new OwedQuestRewards("test.setup", baseExp, jobExp, 0, 0, [], []));
        Publish(new QuestRewardsRequested(player, "test.setup"));
    }

    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++)
            foreach (var instance in World.Instances) instance.Tick();
    }

    public void TickUntil(Func<bool> done, int max = 300, string? what = null)
    {
        for (var i = 0; i < max && !done(); i++) Ticks();
        done().ShouldBeTrue(what ?? "the condition");
    }

    public List<T> Of<T>() where T : IGameEvent => Events.Select(e => e.Event).OfType<T>().ToList();

    public EntityId Npc(string npcId) => Instance.Entities.First(e => e.Kind == EntityKind.Npc && e.DefinitionId == npcId).Id;

    /// <summary>Talks to the NPC and waits for its first node.</summary>
    public DialogueShown Talk(EntityId player, string npcId)
    {
        var before = Of<DialogueShown>().Count(e => e.Player == player);
        Send(new TalkTo(player, Npc(npcId)));
        TickUntil(() => Of<DialogueShown>().Count(e => e.Player == player) > before, 50, "a dialogue node");
        return Of<DialogueShown>().Last(e => e.Player == player);
    }

    /// <summary>Picks the shown choice whose text contains <paramref name="text"/> and waits for the next node or the end.</summary>
    public void Choose(EntityId player, string text)
    {
        var node = Of<DialogueShown>().Last(e => e.Player == player);
        var choice = node.Choices.Single(c => c.Text.Contains(text, StringComparison.Ordinal));
        var events = Events.Count;
        Send(new DialogueChoose(player, choice.Index));
        TickUntil(() => Events.Skip(events).Any(e => e.Event is DialogueShown s && s.Player == player || e.Event is DialogueEnded d && d.Player == player), 20, "the choice's next node");
    }

    public void Close(EntityId player)
    {
        Send(new DialogueClose(player));
        Ticks(2);
    }

    /// <summary>Gives the player an attack no monster of the meadow survives.</summary>
    public void Boost(EntityId player)
    {
        var sheet = Get(player).Stats!;
        sheet.Modifiers.Add("test:boost", "atk", Game.Shared.Definitions.ModifierOp.Flat, 5000);
        sheet.Modifiers.Add("test:boost", "hit", Game.Shared.Definitions.ModifierOp.Flat, 5000);
        sheet.Modifiers.Add("test:boost", "max_hp", Game.Shared.Definitions.ModifierOp.Flat, 50000);
        Ticks();   // the vitals follow the sheet's new maximum; then fill them
        Get(player).Vitals!.Hp = Get(player).Vitals!.MaxHp;
    }

    /// <summary>Puts a monster next to the player (outside the safe zone) and returns it.</summary>
    public EntityId SpawnMonster(string monsterId, GridCell cell)
    {
        _monsters.TryGet(monsterId, out var def).ShouldBeTrue();
        var id = World.Spawn(EntityKind.Monster, Meadow, cell, Directions.W, e =>
        {
            e.DefinitionId = monsterId;
            e.Stats = _monsters.CreateStats(def);
        });
        Ticks();
        return id;
    }

    /// <summary>Kills a monster of <paramref name="monsterId"/> by auto-attack.</summary>
    public void Kill(EntityId player, string monsterId)
    {
        var cell = Get(player).Cell;
        var monster = SpawnMonster(monsterId, new GridCell(cell.X + 1, cell.Y));
        Send(new AttackTarget(player, monster));
        TickUntil(() => Of<MonsterKilled>().Any(k => k.Monster == monster), 100, "the kill");
    }

    /// <summary>Drops a ground item on the player's cell and picks it up through the inventory.</summary>
    public void PickUp(EntityId player, string itemId, int quantity)
    {
        var cell = Get(player).Cell;
        var item = new GroundItem(itemId, quantity, default, null, 0, 0, long.MaxValue);
        var ground = World.Spawn(EntityKind.Item, Meadow, cell, Directions.S, e =>
        {
            e.DefinitionId = itemId;
            e.GroundItem = item;
        });
        Ticks();
        Send(new PickUp(player, ground));
        TickUntil(() => Of<ItemAcquired>().Any(a => a.Player == player && a.Source == ground), 30, "the pick-up");
    }

    public QuestProgressed? LastProgress(EntityId player, string questId) => Of<QuestProgressed>().LastOrDefault(e => e.Player == player && e.QuestId == questId);
}

public class QuestTests
{
    // ---- First Hunt, end to end ---------------------------------------------------------------------------------------------------

    [Fact]
    public void First_hunt_is_accepted_in_dialogue_counted_from_kills_and_the_bag_and_turned_in_by_talking()
    {
        var rig = new QuestRig();
        var player = rig.Join();
        var draughts = rig.Count(player, "item.healing_draught");

        var greet = rig.Talk(player, "npc.guide");
        greet.Choices.Select(c => c.Text).ShouldContain(t => t.Contains("help with"));
        greet.Choices.Select(c => c.Text).ShouldNotContain(t => t.Contains("finished the hunt"));
        rig.Of<QuestMarkersChanged>().ShouldBeEmpty();   // known on arrival; nothing changed yet
        rig.Choose(player, "help with");
        rig.Choose(player, "hunt some slimes");
        rig.TickUntil(() => rig.Of<QuestStarted>().Any(), 10, "the start");
        rig.Progress(player).ActiveQuests.ShouldContainKey("quest.first_hunt");
        rig.Close(player);

        // Kills count for the player with kill credit; the step text and its three counters are in every message.
        rig.Send(new MoveTo(player, QuestRig.Field));
        rig.TickUntil(() => rig.Get(player).Cell.Equals(QuestRig.Field), 100, "the walk out");
        rig.Boost(player);
        for (var i = 0; i < 5; i++) rig.Kill(player, "monster.gel_slime");
        rig.Ticks(2);
        var kills = rig.LastProgress(player, "quest.first_hunt")!.Entry.Objectives[0];
        (kills.Type, kills.Target, kills.Have, kills.Need).ShouldBe((QuestObjectiveTypes.Kill, "monster.gel_slime", 5, 5));

        // The globs count from the bag (whatever the slimes dropped meanwhile); three more make sure.
        rig.PickUp(player, "item.gel_glob", 3);
        rig.Ticks(2);
        var globs = rig.Count(player, "item.gel_glob");
        rig.LastProgress(player, "quest.first_hunt")!.Entry.Objectives[1].Have.ShouldBe(3);
        rig.Of<QuestMarkersChanged>().Last(e => e.Player == player).TurnIn.ShouldBe(["npc.guide"]);
        rig.Progress(player).CompletedQuests.ShouldBeEmpty();   // not before the talk

        // The talk is the turn-in: the globs are taken and the rewards paid in one inventory change, then EXP, then the node shows the thanks.
        rig.Send(new MoveTo(player, QuestRig.Start));
        rig.TickUntil(() => rig.Get(player).Cell.Equals(QuestRig.Start), 100, "the walk back");
        var exp = rig.Progress(player).BaseExp + 0L;
        var level = rig.Progress(player).BaseLevel;
        rig.Talk(player, "npc.guide");
        rig.TickUntil(() => rig.Of<QuestCompleted>().Any(), 10, "the completion");
        rig.Ticks(2);

        rig.Progress(player).CompletedQuests.ShouldContain("quest.first_hunt");
        rig.Progress(player).ActiveQuests.ShouldBeEmpty();
        rig.Count(player, "item.gel_glob").ShouldBe(globs - 3);
        rig.Count(player, "item.healing_draught").ShouldBe(draughts + 5);
        rig.Bag(player).Currency.ShouldBe(150);
        var change = rig.Of<InventoryChanged>().Single(e => e.Player == player && e.Cause == InventoryChanges.Quest);
        change.Currency.ShouldBe(150);
        (rig.Progress(player).BaseLevel > level || rig.Progress(player).BaseExp > exp).ShouldBeTrue();
        rig.Of<QuestRewarded>().Single().BaseExp.ShouldBe(40);
        rig.Of<DialogueShown>().Last(e => e.Player == player).Choices.Select(c => c.Text).ShouldContain(t => t.Contains("finished the hunt"));
        rig.Of<QuestMarkersChanged>().Last(e => e.Player == player).TurnIn.ShouldBeEmpty();
    }

    [Fact]
    public void Kill_credit_goes_to_every_player_with_a_damage_share_and_not_to_a_bystander()
    {
        var rig = new QuestRig();
        var weak = rig.Join(QuestRig.Field);
        var strong = rig.Join(new GridCell(QuestRig.Field.X, QuestRig.Field.Y + 2));
        var bystander = rig.Join(new GridCell(QuestRig.Field.X - 1, QuestRig.Field.Y));
        var monster = rig.SpawnMonster("monster.rock_beetle", new GridCell(QuestRig.Field.X + 1, QuestRig.Field.Y + 1));

        rig.Send(new AttackTarget(weak, monster));
        rig.TickUntil(() => rig.Of<DamageDealt>().Any(d => d.Source == weak && d.Target == monster && d.Amount > 0), 100, "a hit by the weak player");
        rig.Boost(strong);
        rig.Send(new AttackTarget(strong, monster));
        rig.TickUntil(() => rig.Of<MonsterKilled>().Any(k => k.Monster == monster), 100, "the kill");

        var killed = rig.Of<MonsterKilled>().Single(k => k.Monster == monster);
        killed.MonsterId.ShouldBe("monster.rock_beetle");
        killed.Credited.ShouldBe([weak, strong]);   // first-hit order
        killed.Credited.ShouldNotContain(bystander);
    }

    // ---- the full-bag rule ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_turn_in_whose_rewards_do_not_fit_waits_changes_nothing_and_completes_once_there_is_room()
    {
        var rig = new QuestRig();
        var player = rig.Join();
        var state = rig.Progress(player);
        // A hunt that only lacks its talk: kills set by hand, four globs (the stack stays after three are taken, so the bag stays full).
        rig.Talk(player, "npc.guide");
        rig.Choose(player, "help with");
        rig.Choose(player, "hunt some slimes");
        rig.Close(player);
        rig.Put(player, "item.gel_glob", 4);
        var slots = TestGrids.SampleContent.Balance.Inventory.MaxEntries;
        var bag = rig.Bag(player);
        // No room for the reward draughts: the bag's own draughts go, so the five need a new entry, and the bag is full.
        rig.Remove(player, "item.healing_draught");
        while (bag.Entries.Count < slots) rig.Put(player, "item.leather_cap");   // equipment: one entry each
        rig.Ticks();
        var p = state.ActiveQuests["quest.first_hunt"];
        p.Counters[0] = 5;   // the kills and the globs, as the events would have counted them
        p.Counters[1] = 3;
        var before = string.Join(",", bag.Entries.Select(e => e.Id + ":" + e.ItemId + "x" + e.Quantity));
        var currency = bag.Currency;

        rig.Talk(player, "npc.guide");
        rig.TickUntil(() => rig.LastProgress(player, "quest.first_hunt")?.Entry.Held is not null, 10, "the hold");

        var held = rig.LastProgress(player, "quest.first_hunt")!.Entry.Held;
        held.ShouldBeOneOf(QuestReasons.InventoryFull, QuestReasons.TooHeavy);
        string.Join(",", bag.Entries.Select(e => e.Id + ":" + e.ItemId + "x" + e.Quantity)).ShouldBe(before);   // nothing taken, nothing given
        bag.Currency.ShouldBe(currency);
        state.CompletedQuests.ShouldBeEmpty();
        rig.Of<QuestCompleted>().ShouldBeEmpty();

        // Making room (dropping the caps) is a bag change: the waiting turn-in is tried again, without talking again.
        foreach (var entry in bag.Entries.Where(e => e.ItemId == "item.leather_cap" && e.Slot is null).ToList()) rig.Send(new DropItem(player, entry.Id, entry.Quantity));
        rig.TickUntil(() => rig.Of<QuestCompleted>().Any(), 20, "the completion after making room");
        state.CompletedQuests.ShouldContain("quest.first_hunt");
        rig.Count(player, "item.gel_glob").ShouldBe(1);
        bag.Currency.ShouldBe(currency + 150);
    }

    // ---- abandoning, refusals, conditions -------------------------------------------------------------------------------------------

    [Fact]
    public void Abandon_drops_the_quest_and_a_refused_start_or_abandon_says_why()
    {
        var rig = new QuestRig();
        var player = rig.Join();

        rig.Send(new AbandonQuest(player, "quest.first_hunt"));
        rig.Ticks(2);
        rig.Of<QuestRefused>().Single().ShouldBe(new QuestRefused(player, "abandon_quest", "quest.first_hunt", QuestReasons.NotActive));

        rig.Publish(new QuestStartRequested(player, "quest.gear_up", "npc.guide"));   // as a start_quest action would
        rig.Ticks(2);
        rig.Of<QuestRefused>().Last().ShouldBe(new QuestRefused(player, "dialogue_choice", "quest.gear_up", QuestReasons.BaseLevelTooLow));

        rig.Publish(new QuestStartRequested(player, "quest.first_hunt", "npc.guide"));
        rig.Ticks(2);
        rig.Put(player, "item.gel_glob", 2);
        rig.Send(new AbandonQuest(player, "quest.first_hunt"));
        rig.Ticks(2);
        rig.Of<QuestAbandoned>().Single().QuestId.ShouldBe("quest.first_hunt");
        rig.Progress(player).ActiveQuests.ShouldBeEmpty();
        rig.Count(player, "item.gel_glob").ShouldBe(2);   // nothing taken, nothing given back
        rig.Of<QuestMarkersChanged>().Last(e => e.Player == player).Offer.ShouldContain("npc.guide");
    }

    [Fact]
    public void The_free_resets_are_hidden_in_combat()
    {
        var rig = new QuestRig();
        var player = rig.Join();
        rig.Talk(player, "npc.guide");
        rig.Choose(player, "something else");
        rig.Of<DialogueShown>().Last().Choices.Count.ShouldBe(4);
        rig.Close(player);

        rig.Get(player).InCombatUntilMs = rig.Instance.TickNumber * 100 + 60_000;   // as a hit dealt or taken would set it
        rig.Talk(player, "npc.guide");
        rig.Choose(player, "something else");

        rig.Of<DialogueShown>().Last().Choices.Select(c => c.Text).ShouldBe(["Make the meadow my home.", "Never mind."]);
    }

    // ---- Gear Up: equip, use, reach, talk and every reward type that is not in First Hunt ----------------------------------------------

    [Fact]
    public void Gear_up_counts_gear_a_draught_and_a_place_in_step_order_and_pays_points_and_a_status()
    {
        var rig = new QuestRig();
        var player = rig.Join();
        var state = rig.Progress(player);
        rig.Owe(player, 500, 0);   // base level 2 or more, as combat pays EXP
        state.CompletedQuests.Add("quest.first_hunt");
        rig.Ticks(3);
        state.BaseLevel.ShouldBeGreaterThanOrEqualTo(2);
        var stats = state.StatPoints;
        var skills = state.SkillPoints;

        rig.Talk(player, "npc.guide");
        rig.Choose(player, "Anything else");
        rig.Close(player);
        rig.TickUntil(() => state.ActiveQuests.ContainsKey("quest.gear_up"), 10, "the start");

        var draught = rig.Bag(player).Entries.First(e => e.ItemId == "item.healing_draught").Id;
        rig.Send(new UseItem(player, draught));   // too early: step 1 is the cap, so this use does not count
        rig.Ticks(3);
        state.ActiveQuests["quest.gear_up"].Step.ShouldBe(0);

        var cap = rig.Put(player, "item.leather_cap");
        rig.Send(new Equip(player, cap, null));
        rig.TickUntil(() => state.ActiveQuests["quest.gear_up"].Step == 1, 10, "the cap step");
        rig.Send(new UseItem(player, draught));
        rig.Clock.Advance(60_000);   // item cooldowns run on the server clock
        rig.TickUntil(() => state.ActiveQuests["quest.gear_up"].Step == 2, 10, "the draught step");

        rig.Boost(player);   // the walk passes monsters
        rig.Send(new MoveTo(player, new GridCell(40, 12)));
        rig.TickUntil(() => state.ActiveQuests["quest.gear_up"].Step == 3, 400, "the stone circle");
        rig.Send(new MoveTo(player, QuestRig.Start));
        rig.TickUntil(() => rig.Get(player).Cell.Equals(QuestRig.Start), 400, "the walk back");
        rig.Talk(player, "npc.guide");
        rig.TickUntil(() => rig.Of<QuestCompleted>().Any(c => c.QuestId == "quest.gear_up"), 10, "the completion");
        rig.Ticks(3);

        state.StatPoints.ShouldBe(stats + 3);
        state.SkillPoints.ShouldBe(skills + 1);
        rig.Count(player, "item.swift_elixir").ShouldBe(2);
        rig.Get(player).Statuses!.Find("status.swift").ShouldNotBeNull();
        rig.Of<QuestStepCompleted>().Where(e => e.QuestId == "quest.gear_up").Select(e => e.Step).ShouldBe([0, 1, 2]);
    }

    // ---- the Warden path ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_wanderer_becomes_a_warden_through_the_trial_the_unlock_the_job_change_and_the_oath()
    {
        var rig = new QuestRig();
        var player = rig.Join();
        var state = rig.Progress(player);

        // Below base level 5 the trial is not offered; Brannoc says what it takes.
        rig.Talk(player, "npc.trainer").Choices.Select(c => c.Text).ShouldContain(t => t.Contains("What does it take"));
        rig.Close(player);
        rig.Owe(player, 1400, 0);
        rig.TickUntil(() => state.BaseLevel >= 5, 10, "base level 5");
        state.BaseLevel.ShouldBeLessThan(10);

        // The trial.
        rig.Talk(player, "npc.trainer");
        rig.Choose(player, "ready for the trial");
        rig.Close(player);
        rig.TickUntil(() => state.ActiveQuests.ContainsKey("quest.warden_trial"), 10, "the trial");

        // Step 1: base level 10 (a level reached through EXP, as kills would bring it); job level 8 for the job change too.
        rig.Owe(player, 100_000, 100_000);
        rig.TickUntil(() => state.ActiveQuests["quest.warden_trial"].Step == 1, 10, "the level step");
        state.BaseLevel.ShouldBeGreaterThanOrEqualTo(10);
        state.JobLevel.ShouldBeGreaterThanOrEqualTo(8);

        // Step 2: three sting wasps, by real kills.
        rig.Send(new MoveTo(player, QuestRig.Field));
        rig.TickUntil(() => rig.Get(player).Cell.Equals(QuestRig.Field), 100, "the walk out");
        rig.Boost(player);
        for (var i = 0; i < 3; i++) rig.Kill(player, "monster.sting_wasp");
        rig.TickUntil(() => state.ActiveQuests["quest.warden_trial"].Step == 2, 10, "the wasp step");

        // Before the report the Warden is still locked.
        rig.Send(new ChangeJob(player, "job.warden"));
        rig.Ticks(2);
        rig.Of<Game.Server.World.Effects.JobChangeRefused>().Last().Reason.ShouldBe(SkillReasons.QuestNotCompleted);

        // Step 3: report to Brannoc; the talk completes the trial: the token and the unlock.
        rig.Send(new MoveTo(player, QuestRig.Start));
        rig.TickUntil(() => rig.Get(player).Cell.Equals(QuestRig.Start), 100, "the walk back");
        rig.Talk(player, "npc.trainer");
        rig.TickUntil(() => rig.Of<QuestCompleted>().Any(c => c.QuestId == "quest.warden_trial"), 10, "the trial's completion");
        rig.Ticks(3);
        state.CompletedQuests.ShouldContain("quest.warden_trial");
        state.UnlockedJobs.ShouldContain("job.warden");
        rig.Count(player, "item.warden_token").ShouldBe(1);
        // The node is shown again with the oath on offer.
        rig.Of<DialogueShown>().Last(e => e.Player == player).Choices.Select(c => c.Text).ShouldContain(t => t.Contains("Warden's oath"));

        // The job change is accepted now.
        rig.Send(new ChangeJob(player, "job.warden"));
        rig.TickUntil(() => state.JobId == "job.warden", 10, "the job change");

        // The oath: already a Warden, so it completes as soon as it is accepted, and pays.
        rig.Talk(player, "npc.trainer");
        rig.Choose(player, "Warden's oath");
        rig.TickUntil(() => rig.Of<QuestCompleted>().Any(c => c.QuestId == "quest.warden_oath"), 10, "the oath");
        rig.Ticks(3);
        state.CompletedQuests.ShouldContain("quest.warden_oath");
        rig.Count(player, "item.wooden_buckler").ShouldBe(1);
        rig.Of<QuestCompleted>().Select(c => c.QuestId).ShouldBe(["quest.warden_trial", "quest.warden_oath"]);
    }
}
