using Game.Server.Events;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Effects;
using Game.Server.World.Inventory;
using Game.Server.World.Monsters;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Items;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>
/// Two small maps running movement, the dialogue system, combat and the inventory, with every event recorded (T-601). The test dialogue has a node per
/// feature: <c>dialogue.test</c> for conditions and graph walking, <c>dialogue.actions</c> for every action type. Players are new characters (base level 1,
/// job wanderer, 10 stat points) with nothing in the bag.
/// </summary>
public sealed class DialogueRig
{
    public const string TalkerNpc = "npc.talker";
    public const string ActionsNpc = "npc.actions";
    public const string MuteNpc = "npc.mute";
    public const string OtherMap = "map.other";
    public static readonly GridCell TalkerCell = new(10, 5);
    public static readonly GridCell ActionsCell = new(4, 5);
    public static readonly GridCell MuteCell = new(15, 5);
    public static readonly GridCell SavePointCell = new(3, 9);

    public readonly GameWorld World;
    public readonly ManualClock Clock = new();
    public readonly ListLog Log = new();
    public readonly InMemoryInventoryStore Store = new();
    public readonly InMemoryProgressionStore Progressions = new();
    public readonly InMemorySavePointStore SavePoints = new();
    public readonly List<(long Tick, IGameEvent Event)> Events = new();
    public readonly InventoryContent Items;

    public DialogueRig(bool refineGate = false, int maxEntries = 20, int talkRange = 2, NpcDef[]? extraNpcs = null, GridNpc[]? extraPlacements = null, string[]? walls = null)
    {
        var sample = TestGrids.SampleContent;
        var dialogues = Dialogues();
        var npcs = new List<NpcDef>
        {
            new() { Id = TalkerNpc, Name = "Talker", Title = "Chatty", DialogueId = "dialogue.test" },
            new() { Id = ActionsNpc, Name = "Actions", DialogueId = "dialogue.actions" },
            new() { Id = MuteNpc, Name = "Mute" },
        };
        npcs.AddRange(extraNpcs ?? []);
        var dialogueBalance = new DialogueBalanceDef { TalkRange = talkRange, RefineNeedsService = refineGate };
        var balance = TestCombat.Balance() with
        {
            Inventory = InventoryRulesTests.Balance(maxEntries),
            NewCharacter = TestCombat.Balance().NewCharacter with { StartItems = [] },
            Dialogue = dialogueBalance,
        };
        var items = InventoryRulesTests.Items();
        var effects = new EffectLibrary(sample.Skills.All, items.Values, sample.Statuses.All, dialogues);
        var catalog = new MonsterCatalog([TestMonsters.Passive], TestMonsters.Catalog().CreateStats, effects);
        var combat = TestCombat.Content(catalog, balance);
        var jobs = sample.Jobs.All.ToDictionary(j => j.Id, j => j.WeaponTypes, StringComparer.Ordinal);
        Items = new InventoryContent(items, balance, jobs, refine: CardRefineRulesTests.Table());
        Content = new DialogueContent(npcs, dialogues, dialogueBalance);

        var placements = new List<GridNpc>
        {
            new() { NpcId = TalkerNpc, Cell = TalkerCell, Facing = Directions.S },
            new() { NpcId = ActionsNpc, Cell = ActionsCell, Facing = Directions.E },
            new() { NpcId = MuteNpc, Cell = MuteCell, Facing = Directions.W },
        };
        placements.AddRange(extraPlacements ?? []);
        var pipeline = new SystemPipeline()
            .Add(() => new MovementSystem(TestGrids.Roomy))
            .Add(() => new MonsterSpawnerSystem(catalog, 0))
            .Add(() => new DialogueSystem(Content, SavePoints, Progressions, Store))
            .Add(() => new CombatSystem(combat, Progressions, SavePoints))
            .Add(() => new InventorySystem(Items, Store, Clock, Progressions));
        World = new GameWorld([Grid(TestCombat.MapId, 20, 12, placements.ToArray(), walls), Grid(OtherMap, 20, 12, [], null)], pipeline, 1, Clock, Log);
        foreach (var instance in World.Instances) instance.Events.SubscribeAll(e => Events.Add((instance.TickNumber, e)));
    }

    public DialogueContent Content { get; }

    public MapInstance Instance => World.GetInstance(TestCombat.MapId);

    public static MapGrid Grid(string mapId, int width, int height, GridNpc[] npcs, string[]? walls)
    {
        var cells = new byte[width * height];
        for (var i = 0; i < cells.Length; i++) cells[i] = CellFlagBits.Walkable;
        foreach (var wall in walls ?? [])
        {
            var parts = wall.Split(',');
            cells[int.Parse(parts[1]) * width + int.Parse(parts[0])] = CellFlagBits.BlocksLos;
        }
        return MapGrid.Create(new MapGridFile { MapId = mapId, Width = width, Height = height, Cells = Convert.ToBase64String(cells), Npcs = npcs });
    }

    public EntityId AddPlayer(GridCell cell, string mapId = TestCombat.MapId) =>
        World.Spawn(EntityKind.Player, mapId, cell, Directions.S, e =>
        {
            e.Stats = TestCombat.PlayerSheet();
            e.DefinitionId = "job.wanderer";
        });

    public Entity Get(EntityId id)
    {
        foreach (var instance in World.Instances)
            if (instance.TryGetEntity(id, out var e)) return e;
        throw new InvalidOperationException($"{id} is gone");
    }

    public bool Has(EntityId id) => World.Instances.Any(i => i.TryGetEntity(id, out _));

    public EntityId Npc(string npcId) => Instance.Entities.First(e => e.Kind == EntityKind.Npc && e.DefinitionId == npcId).Id;

    public InventoryState State(EntityId player) => Store.TryGet(player, out var s) ? s : throw new InvalidOperationException("no inventory");

    public long Give(EntityId player, string itemId, int quantity = 1)
    {
        var delta = new InventoryDelta();
        Items.Rules.TryAdd(State(player), itemId, quantity, int.MaxValue, Store.NextEntryId, delta).ShouldBeNull();
        return delta.Upserted.Last().Id;
    }

    public int Count(EntityId player, string itemId) => State(player).Entries.Where(e => e.ItemId == itemId && e.Slot is null).Sum(e => e.Quantity);

    public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++)
            foreach (var instance in World.Instances) instance.Tick();
    }

    public void TickUntil(Func<bool> done, int max = 200, string? what = null)
    {
        for (var i = 0; i < max && !done(); i++) Ticks();
        done().ShouldBeTrue(what ?? "the condition");
    }

    public List<T> Of<T>() where T : IGameEvent => Events.Select(e => e.Event).OfType<T>().ToList();

    /// <summary>The player's last <see cref="DialogueShown"/>.</summary>
    public DialogueShown Shown(EntityId player) => Of<DialogueShown>().Last(e => e.Player == player);

    /// <summary>Joins a player next to the talker NPC, talks, and ticks until the first node is shown.</summary>
    public EntityId Talking(string npc = TalkerNpc, GridCell? at = null)
    {
        var player = AddPlayer(at ?? new GridCell(9, 5));
        Ticks(2);
        Send(new TalkTo(player, Npc(npc)));
        Ticks(2);
        return player;
    }

    public static DialogueDef[] Dialogues() =>
    [
        new DialogueDef
        {
            Id = "dialogue.test", Name = "Test", Start = "start",
            Nodes = new Dictionary<string, DialogueNodeDef>
            {
                ["start"] = new()
                {
                    Text = "Hi.", SpeakerNpcId = TalkerNpc, PortraitAssetKey = "portrait.test",
                    Choices =
                    [
                        Choice("plain", "end"),
                        Choice("veteran", "end", new BaseLevelCondition { Min = 5 }),
                        Choice("warden", "end", new JobIsCondition { JobId = "job.warden" }),
                        Choice("not a warden", "end", new JobIsNotCondition { JobId = "job.warden" }),
                        Choice("has globs", "end", new HasItemCondition { ItemId = "item.gel_glob", Quantity = 2 }),
                        Choice("hasted", "end", new HasStatusCondition { StatusId = "status.swift" }),
                        Choice("novice", "end", new BaseLevelCondition { Max = 3 }, new JobLevelCondition { Min = 1 }),
                        Choice("go on", "more"),
                    ],
                },
                ["more"] = new() { Text = "More.", Next = "end" },
                ["end"] = new() { Text = "Bye." },
            },
        },
        new DialogueDef
        {
            Id = "dialogue.actions", Name = "Actions", Start = "menu",
            Nodes = new Dictionary<string, DialogueNodeDef>
            {
                ["menu"] = new()
                {
                    Text = "Menu.",
                    Choices =
                    [
                        Do("quest", new StartQuestAction { QuestId = "quest.first_hunt" }),                                          // 0
                        Do("save", new SetSavePointAction { X = SavePointCell.X, Y = SavePointCell.Y }),                              // 1
                        Do("home", new RunEffectsAction { Effects = [new TeleportEffect { ToSavePoint = true }] }),                    // 2
                        Do("reset", new RunEffectsAction { Effects = [new ResetStatsEffect()] }),                                      // 3
                        Do("give", new GiveItemAction { ItemId = "item.gel_glob", Quantity = 2 }),                                     // 4
                        Do("take", new TakeItemAction { ItemId = "item.gel_glob", Quantity = 1 }),                                     // 5
                        Do("swap", new TakeItemAction { ItemId = "item.gel_glob", Quantity = 1 }, new GiveItemAction { ItemId = "item.healing_draught" }),   // 6
                        Do("refine", new OpenServiceAction { Service = ServiceIds.Refine }),                                           // 7
                        Do("everything", new GiveItemAction { ItemId = "item.gel_glob" }, new SetSavePointAction { X = 5, Y = 6 }, new StartQuestAction { QuestId = "quest.gear_up" }),   // 8
                        Do("stay", next: "menu", new GiveItemAction { ItemId = "item.gel_glob" }),                                     // 9
                    ],
                },
            },
        },
    ];

    private static DialogueChoiceDef Choice(string text, string? next, params DialogueConditionDef[] conditions) =>
        new() { Text = text, Next = next, Conditions = conditions };

    private static DialogueChoiceDef Do(string text, params DialogueActionDef[] actions) => new() { Text = text, Actions = actions };

    private static DialogueChoiceDef Do(string text, string next, params DialogueActionDef[] actions) => new() { Text = text, Next = next, Actions = actions };
}

public class DialogueTests
{
    // ---- NPC entities ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_placement_becomes_an_npc_entity_on_its_cell_and_facing_with_no_stats()
    {
        var rig = new DialogueRig();
        rig.Ticks(2);

        var npcs = rig.Instance.Entities.Where(e => e.Kind == EntityKind.Npc).ToList();
        npcs.Count.ShouldBe(3);
        var talker = rig.Get(rig.Npc(DialogueRig.TalkerNpc));
        talker.Cell.ShouldBe(DialogueRig.TalkerCell);
        talker.Facing.ShouldBe(Directions.S);
        talker.DefinitionId.ShouldBe(DialogueRig.TalkerNpc);
        talker.Stats.ShouldBeNull();
        talker.Vitals.ShouldBeNull();
        rig.Get(rig.Npc(DialogueRig.ActionsNpc)).Facing.ShouldBe(Directions.E);
        rig.Instance.Systems.OfType<DialogueSystem>().Single().NpcCount.ShouldBe(3);
        rig.World.GetInstance(DialogueRig.OtherMap).Entities.ShouldBeEmpty();
    }

    [Fact]
    public void An_npc_never_moves_and_does_not_block_its_cell()
    {
        var rig = new DialogueRig();
        var player = rig.AddPlayer(new GridCell(8, 5));
        rig.Ticks(2);
        rig.Send(new MoveTo(rig.Npc(DialogueRig.MuteNpc), new GridCell(1, 1)));   // an NPC has no speed: the order is refused, and it is not even routed as a player's
        rig.Send(new MoveTo(player, DialogueRig.TalkerCell));
        rig.TickUntil(() => !rig.Get(player).Cell.Equals(new GridCell(8, 5)) && rig.Get(player).Cell.Equals(DialogueRig.TalkerCell), what: "the player walking onto the NPC's cell");

        rig.Get(rig.Npc(DialogueRig.MuteNpc)).Cell.ShouldBe(DialogueRig.MuteCell);
        rig.Get(rig.Npc(DialogueRig.TalkerNpc)).Cell.ShouldBe(DialogueRig.TalkerCell);
    }

    [Fact]
    public void An_npc_cannot_be_attacked_and_is_not_a_combatant()
    {
        var rig = new DialogueRig();
        var player = rig.AddPlayer(new GridCell(9, 5));
        rig.Ticks(2);

        rig.Send(new AttackTarget(player, rig.Npc(DialogueRig.TalkerNpc)));
        rig.Ticks(2);

        rig.Of<AttackRefused>().Single().Reason.ShouldBe(AttackReasons.InvalidTarget);
        rig.Instance.Systems.OfType<CombatSystem>().Single().CombatantCount.ShouldBe(1);   // the player only
    }

    [Fact]
    public void A_placement_of_an_unknown_npc_is_a_boot_error_not_a_missing_entity()
    {
        var rig = new DialogueRig(extraPlacements: [new GridNpc { NpcId = "npc.nope", Cell = new GridCell(1, 1), Facing = Directions.S }]);
        rig.Ticks(1);

        rig.Instance.Counters.SystemErrors.ShouldBe(1);
        rig.Log.Lines.Single(l => l.StartsWith("ERROR", StringComparison.Ordinal)).ShouldContain("npc.nope");
    }

    // ---- talking --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Talking_in_range_starts_the_dialogue_and_raises_npc_talked()
    {
        var rig = new DialogueRig();
        var player = rig.Talking();

        var talked = rig.Of<NpcTalked>().Single();
        (talked.Player, talked.NpcId).ShouldBe((player, DialogueRig.TalkerNpc));
        var shown = rig.Shown(player);
        shown.NodeKey.ShouldBe("start");
        shown.Speaker.ShouldBe(DialogueRig.TalkerNpc);
        shown.Portrait.ShouldBe("portrait.test");
        shown.Text.ShouldBe("Hi.");
        shown.Npc.ShouldBe(rig.Npc(DialogueRig.TalkerNpc));
    }

    [Fact]
    public void Talking_from_afar_walks_the_player_into_range_first()
    {
        var rig = new DialogueRig();
        var player = rig.AddPlayer(new GridCell(1, 1));
        rig.Ticks(2);

        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.TalkerNpc)));
        rig.Ticks(2);
        rig.Of<DialogueShown>().ShouldBeEmpty();
        rig.TickUntil(() => rig.Of<DialogueShown>().Count > 0, what: "the dialogue after the walk");

        GridDistance.Chebyshev(rig.Get(player).Cell, DialogueRig.TalkerCell).ShouldBeLessThanOrEqualTo(2);
        rig.Of<NpcTalked>().Count.ShouldBe(1);
        rig.Instance.Systems.OfType<DialogueSystem>().Single().WalkingCount.ShouldBe(0);
    }

    [Fact]
    public void Every_talk_refusal_has_its_reason_and_changes_nothing()
    {
        var rig = new DialogueRig();
        var player = rig.AddPlayer(new GridCell(9, 5));
        var other = rig.AddPlayer(new GridCell(1, 1));
        rig.Ticks(2);

        rig.Send(new TalkTo(player, new EntityId(9999)));                       // gone
        rig.Send(new TalkTo(player, other));                                    // not an NPC
        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.MuteNpc)));             // nothing to say
        rig.Get(other).Dead = true;
        rig.Send(new TalkTo(other, rig.Npc(DialogueRig.TalkerNpc)));            // dead (routed although dead, to be told)
        rig.Ticks(2);

        rig.Of<DialogueRefused>().Select(r => (r.Player == player ? "p" : "o", r.Reason, r.Intent)).ShouldBe(
        [
            ("p", DialogueReasons.TargetGone, MessageTypes.Talk), ("p", DialogueReasons.InvalidTarget, MessageTypes.Talk),
            ("p", DialogueReasons.NoDialogue, MessageTypes.Talk), ("o", DialogueReasons.Dead, MessageTypes.Talk),
        ]);
        rig.Of<NpcTalked>().ShouldBeEmpty();
        rig.Instance.Systems.OfType<DialogueSystem>().Single().SessionCount.ShouldBe(0);
    }

    [Fact]
    public void A_new_order_while_walking_to_the_npc_cancels_the_talk()
    {
        var rig = new DialogueRig();
        var player = rig.AddPlayer(new GridCell(1, 1));
        rig.Ticks(2);
        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.TalkerNpc)));
        rig.Ticks(3);

        rig.Send(new MoveTo(player, new GridCell(1, 10)));
        rig.TickUntil(() => rig.Of<DialogueRefused>().Count > 0, what: "the refusal");

        rig.Of<DialogueRefused>().Single().Reason.ShouldBe(DialogueReasons.Cancelled);
        rig.Ticks(60);
        rig.Of<DialogueShown>().ShouldBeEmpty();
    }

    [Fact]
    public void An_npc_nobody_can_walk_to_is_unreachable()
    {
        var rig = new DialogueRig(walls: ["9,4", "10,4", "11,4", "9,5", "11,5", "9,6", "10,6", "11,6"]);   // the talker is walled in
        var player = rig.AddPlayer(new GridCell(1, 1));
        rig.Ticks(2);

        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.TalkerNpc)));
        rig.TickUntil(() => rig.Of<DialogueRefused>().Count > 0, what: "the refusal");

        rig.Of<DialogueRefused>().Single().Reason.ShouldBe(DialogueReasons.Unreachable);
    }

    // ---- the graph and its conditions --------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_server_shows_only_the_choices_whose_conditions_hold_with_their_original_indices()
    {
        var rig = new DialogueRig();
        var player = rig.Talking();

        // A new character: level 1, wanderer, no items, no status.
        rig.Shown(player).Choices.Select(c => (c.Index, c.Text)).ShouldBe([(0, "plain"), (3, "not a warden"), (6, "novice"), (7, "go on")]);

        rig.Give(player, "item.gel_glob", 2);
        rig.Progressions.TryGet(player, out var progression).ShouldBeTrue();
        progression!.BaseLevel = 6;
        progression.JobId = "job.warden";
        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.TalkerNpc)));
        rig.Ticks(2);

        rig.Shown(player).Choices.Select(c => c.Index).ShouldBe([0, 1, 2, 4, 7]);   // veteran, warden and has-globs now; not-a-warden and novice gone
    }

    [Fact]
    public void A_status_makes_its_choice_appear()
    {
        var rig = new DialogueRig();
        var player = rig.Talking();
        var swift = rig.Instance.Systems.OfType<CombatSystem>().Single().Runner!;
        swift.ShouldNotBeNull();
        var actor = new EffectActor(player.Value, rig.Get(player).Stats!, rig.Get(player).Cell, Directions.S, false, true);
        swift.Run(CompiledEffect.CompileList([new ApplyStatusEffect { StatusId = "status.swift", DurationMs = "60000" }]), actor, player.Value, null, 1);
        rig.Ticks(1);
        rig.Get(player).Statuses!.Find("status.swift").ShouldNotBeNull();

        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.TalkerNpc)));
        rig.Ticks(2);

        rig.Shown(player).Choices.Select(c => c.Index).ShouldContain(5);
    }

    [Fact]
    public void A_choice_moves_to_its_node_and_a_hidden_or_missing_one_is_refused()
    {
        var rig = new DialogueRig();
        var player = rig.Talking();

        rig.Send(new DialogueChoose(player, 1));    // veteran: hidden
        rig.Send(new DialogueChoose(player, 50));   // no such choice
        rig.Send(new DialogueChoose(player, -1));
        rig.Ticks(2);
        rig.Of<DialogueRefused>().Select(r => r.Reason).ShouldBe([DialogueReasons.InvalidChoice, DialogueReasons.InvalidChoice, DialogueReasons.InvalidChoice]);
        rig.Shown(player).NodeKey.ShouldBe("start");

        rig.Send(new DialogueChoose(player, 7));
        rig.Ticks(2);
        var more = rig.Shown(player);
        more.NodeKey.ShouldBe("more");
        more.Choices.ShouldBeEmpty();
        more.More.ShouldBeTrue();
    }

    [Fact]
    public void A_node_without_choices_continues_on_index_zero_and_a_null_next_ends_the_dialogue()
    {
        var rig = new DialogueRig();
        var player = rig.Talking();
        rig.Send(new DialogueChoose(player, 7));
        rig.Ticks(2);

        rig.Send(new DialogueChoose(player, 1));   // a "continue" has only index 0
        rig.Ticks(2);
        rig.Of<DialogueRefused>().Last().Reason.ShouldBe(DialogueReasons.InvalidChoice);
        rig.Shown(player).NodeKey.ShouldBe("more");

        rig.Send(new DialogueChoose(player, 0));
        rig.Ticks(2);
        var last = rig.Shown(player);
        last.NodeKey.ShouldBe("end");
        last.More.ShouldBeFalse();
        rig.Of<DialogueEnded>().ShouldBeEmpty();

        rig.Send(new DialogueChoose(player, 0));
        rig.Ticks(2);
        rig.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.Finished);
        rig.Instance.Systems.OfType<DialogueSystem>().Single().SessionCount.ShouldBe(0);
        rig.Send(new DialogueChoose(player, 0));
        rig.Ticks(2);
        rig.Of<DialogueRefused>().Last().Reason.ShouldBe(DialogueReasons.NoDialogue);
    }

    [Fact]
    public void Close_ends_the_dialogue_and_talking_to_another_npc_replaces_it()
    {
        var rig = new DialogueRig(extraNpcs: [new NpcDef { Id = "npc.twin", Name = "Twin", DialogueId = "dialogue.test" }],
            extraPlacements: [new GridNpc { NpcId = "npc.twin", Cell = new GridCell(11, 6), Facing = Directions.S }]);
        var player = rig.Talking(at: new GridCell(10, 6));
        rig.Send(new DialogueClose(player));
        rig.Ticks(2);
        rig.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.Closed);

        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.TalkerNpc)));
        rig.Ticks(2);
        rig.Send(new TalkTo(player, rig.Npc("npc.twin")));   // both in reach: the second replaces the first at once
        rig.Ticks(2);

        rig.Of<DialogueEnded>().Select(e => e.Reason).ShouldBe([DialogueEndReasons.Closed, DialogueEndReasons.Replaced]);
        rig.Of<NpcTalked>().Select(t => t.NpcId).ShouldBe([DialogueRig.TalkerNpc, DialogueRig.TalkerNpc, "npc.twin"]);
        rig.Instance.Systems.OfType<DialogueSystem>().Single().SessionCount.ShouldBe(1);
    }

    // ---- ending ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Walking_beyond_the_range_ends_the_dialogue()
    {
        var rig = new DialogueRig();
        var player = rig.Talking();

        rig.Send(new MoveTo(player, new GridCell(9, 11)));
        rig.TickUntil(() => rig.Of<DialogueEnded>().Count > 0, what: "the dialogue ending");

        rig.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.MovedAway);
        GridDistance.Chebyshev(rig.Get(player).Cell, DialogueRig.TalkerCell).ShouldBeGreaterThan(2);
    }

    [Fact]
    public void A_warp_to_another_map_a_death_and_a_disconnect_end_it()
    {
        var warp = new DialogueRig();
        var warper = warp.Talking();
        warp.World.Warp(warper, DialogueRig.OtherMap, new GridCell(2, 2));
        warp.Ticks(4);
        warp.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.Warped);
        warp.World.LocationOf(warper).ShouldBe(DialogueRig.OtherMap);

        var death = new DialogueRig();
        var dead = death.Talking();
        death.Get(dead).Dead = true;
        death.Ticks(2);
        death.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.Died);

        var leave = new DialogueRig();
        var leaver = leave.Talking();
        leave.World.Despawn(leaver).ShouldBeTrue();
        leave.Ticks(2);
        leave.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.Disconnected);
        leave.Instance.Systems.OfType<DialogueSystem>().Single().SessionCount.ShouldBe(0);
    }

    [Fact]
    public void A_same_map_teleport_beyond_the_range_ends_it_by_the_range_check()
    {
        var rig = new DialogueRig();
        var player = rig.Talking();

        rig.World.Warp(player, TestCombat.MapId, new GridCell(1, 11));
        rig.Ticks(3);

        rig.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.MovedAway);
    }

    // ---- actions --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Start_quest_raises_the_request_event_and_nothing_handles_it_yet()
    {
        var rig = new DialogueRig();
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));

        rig.Send(new DialogueChoose(player, 0));
        rig.Ticks(2);

        var request = rig.Of<QuestStartRequested>().Single();
        (request.Player, request.QuestId, request.NpcId).ShouldBe((player, "quest.first_hunt", DialogueRig.ActionsNpc));
        rig.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.Finished);
    }

    [Fact]
    public void The_save_point_is_set_and_used_by_the_teleport_effect_and_by_the_respawn()
    {
        var rig = new DialogueRig();
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));

        rig.Send(new DialogueChoose(player, 1));
        rig.Ticks(2);
        var set = rig.Of<SavePointSet>().Single();
        (set.MapId, set.Cell).ShouldBe((TestCombat.MapId, DialogueRig.SavePointCell));
        rig.SavePoints.TryGet(player, out var point).ShouldBeTrue();
        point.ShouldBe(new SavePoint(TestCombat.MapId, DialogueRig.SavePointCell));

        // teleport to_save_point, run as an effect by a choice
        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.ActionsNpc)));
        rig.Ticks(2);
        rig.Send(new DialogueChoose(player, 2));
        rig.TickUntil(() => rig.Of<Teleported>().Count > 0, what: "the teleport");
        rig.Ticks(2);
        rig.Get(player).Cell.ShouldBe(DialogueRig.SavePointCell);

        // death: the respawn goes to the save point too (the map's own respawn point is the start map's first warp / the new-character start)
        rig.Get(player).Dead = true;
        rig.Send(new MoveTo(player, new GridCell(1, 1)));   // dropped: dead entities cannot walk
        rig.Send(new Respawn(player));
        rig.Ticks(3);
        var respawned = rig.Of<PlayerRespawned>().Single();
        (respawned.MapId, respawned.Cell).ShouldBe((TestCombat.MapId, DialogueRig.SavePointCell));
    }

    [Fact]
    public void Without_a_save_point_the_respawn_uses_the_maps_respawn_point()
    {
        var rig = new DialogueRig();
        var player = rig.AddPlayer(new GridCell(5, 5));
        rig.Ticks(2);

        rig.Get(player).Dead = true;
        rig.Send(new Respawn(player));
        rig.Ticks(3);

        var respawned = rig.Of<PlayerRespawned>().Single();
        (respawned.MapId, respawned.Cell).ShouldBe((TestCombat.MapId, new GridCell(1, 1)));   // new_character start: the fallback
    }

    [Fact]
    public void A_save_point_is_forgotten_when_the_character_leaves_the_world()
    {
        var rig = new DialogueRig();
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));
        rig.Send(new DialogueChoose(player, 1));
        rig.Ticks(2);
        rig.SavePoints.Count.ShouldBe(1);

        rig.World.Despawn(player);
        rig.Ticks(3);

        rig.SavePoints.Count.ShouldBe(0);
    }

    [Fact]
    public void Run_effects_run_through_combats_effect_runner_here_a_stat_reset()
    {
        var rig = new DialogueRig();
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));
        rig.Send(new RaiseStat(player, "str", 1));
        rig.Ticks(2);
        rig.Progressions.TryGet(player, out var state).ShouldBeTrue();
        var raised = state!.StatValue("str");
        var points = state.StatPoints;
        raised.ShouldBeGreaterThan(1);

        rig.Send(new DialogueChoose(player, 3));
        rig.TickUntil(() => rig.Of<StatsReset>().Count > 0, what: "the reset");

        state.StatValue("str").ShouldBe(1);
        state.StatPoints.ShouldBeGreaterThan(points);
        rig.Of<DialogueEffectsRequested>().Single().Key.ShouldBe("dialogue.actions#menu#3#0");
    }

    [Fact]
    public void Give_and_take_items_change_the_bag_atomically_and_say_exchanged()
    {
        var rig = new DialogueRig();
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));

        rig.Send(new DialogueChoose(player, 4));   // give 2 globs
        rig.Ticks(4);
        rig.Count(player, "item.gel_glob").ShouldBe(2);
        var changed = rig.Of<InventoryChanged>().Single();
        changed.Cause.ShouldBe(InventoryChanges.Exchanged);
        rig.Of<DialogueItemsResolved>().Single().Reason.ShouldBeNull();
        rig.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.Finished);

        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.ActionsNpc)));
        rig.Ticks(2);
        rig.Send(new DialogueChoose(player, 6));   // swap one glob for a draught
        rig.Ticks(4);
        rig.Count(player, "item.gel_glob").ShouldBe(1);
        rig.Count(player, "item.healing_draught").ShouldBe(1);
    }

    [Fact]
    public void A_take_without_the_item_is_refused_cleanly_and_the_dialogue_stays()
    {
        var rig = new DialogueRig();
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));
        var before = rig.State(player).Revision;

        rig.Send(new DialogueChoose(player, 5));   // take a glob: the bag has none
        rig.Ticks(4);

        rig.Of<DialogueRefused>().Single().Reason.ShouldBe(DialogueReasons.MissingItem);
        rig.State(player).Revision.ShouldBe(before);
        rig.Of<InventoryChanged>().ShouldBeEmpty();
        rig.Of<DialogueEnded>().ShouldBeEmpty();
        rig.Shown(player).NodeKey.ShouldBe("menu");
    }

    [Fact]
    public void A_choice_that_takes_and_then_cannot_give_changes_nothing_and_runs_no_other_action()
    {
        var rig = new DialogueRig(maxEntries: 1);
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));
        rig.Give(player, "item.gel_glob", 3);   // the one entry the bag has room for

        rig.Send(new DialogueChoose(player, 6));   // take a glob, give a draught: no second entry
        rig.Ticks(4);

        rig.Of<DialogueRefused>().Single().Reason.ShouldBe(DialogueReasons.InventoryFull);
        rig.Count(player, "item.gel_glob").ShouldBe(3);   // the take was undone
        rig.Count(player, "item.healing_draught").ShouldBe(0);
        rig.Of<InventoryChanged>().ShouldBeEmpty();

        rig.Send(new DialogueChoose(player, 8));   // give a glob (fits the stack), set a save point, ask for a quest: all or none
        rig.Ticks(4);
        rig.Count(player, "item.gel_glob").ShouldBe(4);
        rig.SavePoints.TryGet(player, out var point).ShouldBeTrue();
        point.Cell.ShouldBe(new GridCell(5, 6));
        rig.Of<QuestStartRequested>().Single().QuestId.ShouldBe("quest.gear_up");
    }

    [Fact]
    public void A_give_past_the_weight_limit_is_refused_as_too_heavy()
    {
        var rig = new DialogueRig();
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));
        rig.State(player).WeightLimit.ShouldBeGreaterThan(0);
        rig.State(player).WeightLimit = 1;   // a test fixture: the limit is re-read from the sheet on the next tick, so give while it is this low

        rig.Items.Rules.TryExchange(rig.State(player), [], [new KeyValuePair<string, int>("item.gel_glob", 1000)], 1, rig.Store.NextEntryId, new InventoryDelta())
            .ShouldBe(DialogueReasons.TooHeavy);
        rig.State(player).Entries.ShouldBeEmpty();
    }

    [Fact]
    public void A_second_choice_while_the_items_are_pending_is_busy()
    {
        var rig = new DialogueRig();
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));

        rig.Send(new DialogueChoose(player, 9));   // give a glob and stay at the menu
        rig.Send(new DialogueChoose(player, 9));
        rig.Ticks(4);

        rig.Of<DialogueRefused>().Single().Reason.ShouldBe(DialogueReasons.Busy);
        rig.Count(player, "item.gel_glob").ShouldBe(1);
        rig.Shown(player).NodeKey.ShouldBe("menu");

        rig.Send(new DialogueChoose(player, 9));   // free again once the answer came
        rig.Ticks(4);
        rig.Count(player, "item.gel_glob").ShouldBe(2);
    }

    // ---- services -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_open_service_outlives_the_graph_and_ends_when_the_player_walks_away()
    {
        var rig = new DialogueRig();
        var player = rig.Talking(DialogueRig.ActionsNpc, new GridCell(5, 5));

        rig.Send(new DialogueChoose(player, 7));
        rig.Ticks(2);

        var opened = rig.Of<ServiceOpened>().Single();
        (opened.Player, opened.Service).ShouldBe((player, ServiceIds.Refine));
        rig.Get(player).OpenService.ShouldBe(ServiceIds.Refine);
        rig.Of<DialogueEnded>().Single().Reason.ShouldBe(DialogueEndReasons.Finished);
        rig.Instance.Systems.OfType<DialogueSystem>().Single().SessionCount.ShouldBe(1);   // the service is still held

        rig.Send(new MoveTo(player, new GridCell(5, 11)));
        rig.TickUntil(() => rig.Of<DialogueEnded>().Count == 2, what: "the service ending");
        rig.Of<DialogueEnded>().Last().Reason.ShouldBe(DialogueEndReasons.MovedAway);
        rig.Get(player).OpenService.ShouldBeNull();
    }

    [Fact]
    public void Refining_needs_an_open_refine_service_when_the_balance_says_so()
    {
        var rig = new DialogueRig(refineGate: true);
        var player = rig.AddPlayer(new GridCell(5, 5));
        rig.Ticks(2);
        var dirk = rig.Give(player, "item.bone_dirk");
        rig.Give(player, "item.temper_shard", 3);

        rig.Send(new RefineItem(player, dirk));
        rig.Ticks(2);
        var refused = rig.Of<ItemRefused>().Single();
        (refused.Intent, refused.Reason).ShouldBe((MessageTypes.Refine, ItemReasons.ServiceRequired));
        rig.Of<ItemRefined>().ShouldBeEmpty();

        rig.Send(new TalkTo(player, rig.Npc(DialogueRig.ActionsNpc)));
        rig.Ticks(2);
        rig.Send(new DialogueChoose(player, 7));
        rig.Ticks(2);
        rig.Send(new RefineItem(player, dirk));
        rig.Ticks(2);
        rig.Of<ItemRefined>().Count.ShouldBe(1);

        rig.Send(new MoveTo(player, new GridCell(5, 11)));
        rig.TickUntil(() => rig.Get(player).OpenService is null, what: "the service closing");
        rig.Send(new RefineItem(player, dirk));
        rig.Ticks(2);
        rig.Of<ItemRefused>().Last().Reason.ShouldBe(ItemReasons.ServiceRequired);
    }

    [Fact]
    public void Refining_works_anywhere_when_the_balance_does_not_ask_for_a_service()
    {
        var rig = new DialogueRig(refineGate: false);
        var player = rig.AddPlayer(new GridCell(5, 5));
        rig.Ticks(2);
        var dirk = rig.Give(player, "item.bone_dirk");
        rig.Give(player, "item.temper_shard", 3);

        rig.Send(new RefineItem(player, dirk));
        rig.Ticks(2);

        rig.Of<ItemRefined>().Count.ShouldBe(1);
    }

    // ---- content --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_talk_range_must_be_at_least_one()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new DialogueContent([], [], new DialogueBalanceDef { TalkRange = 0 }));
    }

    [Fact]
    public void The_real_content_has_guide_trainer_and_smith_placed_and_the_smith_opens_refining()
    {
        var db = TestGrids.SampleContent;
        var placed = db.Grids.Values.SelectMany(g => g.Npcs).Select(n => n.NpcId).ToList();

        placed.ShouldBe(["npc.guide", "npc.trainer", "npc.smith", "npc.merchant"], ignoreOrder: true);
        db.Balance.Dialogue.RefineNeedsService.ShouldBeTrue();
        var smith = db.Dialogue.Get("dialogue.smith_intro");
        smith.Nodes[smith.Start].Choices.SelectMany(c => c.Actions).OfType<OpenServiceAction>().Single().Service.ShouldBe(ServiceIds.Refine);
        var guide = db.Dialogue.Get("dialogue.guide_intro");
        guide.Nodes.Values.SelectMany(n => n.Choices).SelectMany(c => c.Actions).OfType<RunEffectsAction>().SelectMany(a => a.Effects).Select(e => e.Type)
            .ShouldBe([EffectTypes.ResetStats, EffectTypes.ResetSkills]);
    }
}
