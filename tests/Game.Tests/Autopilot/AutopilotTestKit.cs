using Game.Content.Registry;
using Game.Server.Events;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Autopilot;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Monsters;
using Game.Server.World.Quests;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Server;
using Game.Tests.World;

namespace Game.Tests.Autopilot;

/// <summary>
/// Content for the autopilot tests (T-604): three maps in a row with warps between them (alpha, beta, gamma), tame and fierce monsters, a talker whose dialogue offers a quest, a
/// service, a save point, a reset and an item trade, and one quest per objective type. Everything else (jobs, skills, items, formulas) is the sample's.
/// </summary>
public static class AutopilotContentKit
{
    public const string Alpha = "map.alpha";
    public const string Beta = "map.beta";
    public const string Gamma = "map.gamma";
    public const string Tame = "monster.tame";        // alpha: passive, one hit point less than a blow
    public const string Far = "monster.far";          // gamma only
    public const string Near = "monster.near";        // beta only
    public const string Fierce = "monster.fierce";    // alpha, on the way east: aggressive
    public const string Walled = "monster.walled";    // alpha, behind walls
    public const string Nowhere = "monster.nowhere";  // placed on no map
    public const string Wanted = "item.gel_glob";
    public const string Other = "item.mossy_fiber";
    public const string Keeper = "npc.keeper";
    public const string FarKeeper = "npc.far_keeper";
    public const string Mute = "npc.mute_keeper";
    public const string Trader = "npc.trader";

    public static readonly GridCell Start = new(6, 6);
    public static readonly GridCell KeeperCell = new(4, 4);
    public static readonly GridCell TraderCell = new(4, 9);

    public static QuestDef Quest(string id, params QuestObjectiveDef[] objectives) => new()
    {
        Id = id, Name = id, Steps = [new QuestStepDef { Description = id, Objectives = objectives }],
        Rewards = [new ExpReward { Base = 5, Job = 5 }, new CurrencyReward { Amount = 10 }],
    };

    public static QuestDef Steps(string id, params QuestObjectiveDef[][] steps) => new()
    {
        Id = id, Name = id, Steps = steps.Select(s => new QuestStepDef { Description = id, Objectives = s }).ToArray(), Rewards = [new CurrencyReward { Amount = 10 }],
    };

    public static IEnumerable<QuestDef> Quests() =>
    [
        Quest("quest.kill", new KillObjective { MonsterId = Tame, Count = 2 }),
        Quest("quest.kill_fierce", new KillObjective { MonsterId = Fierce, Count = 1 }),
        Quest("quest.kill_near", new KillObjective { MonsterId = Near, Count = 1 }),
        Quest("quest.kill_far", new KillObjective { MonsterId = Far, Count = 1 }),
        Quest("quest.kill_walled", new KillObjective { MonsterId = Walled, Count = 1 }),
        Quest("quest.kill_nowhere", new KillObjective { MonsterId = Nowhere, Count = 1 }),
        Quest("quest.collect", new CollectObjective { ItemId = Wanted, Count = 2, Consume = true }),
        Quest("quest.reach", new ReachObjective { MapId = Alpha, X = 20, Y = 10, Radius = 1 }),
        Quest("quest.reach_far", new ReachObjective { MapId = Gamma, X = 12, Y = 4, Radius = 1 }),
        Quest("quest.reach_wall", new ReachObjective { MapId = Alpha, X = 25, Y = 2, Radius = 0 }),
        Quest("quest.talk", new TalkObjective { NpcId = Keeper }),
        Quest("quest.talk_far", new TalkObjective { NpcId = FarKeeper }),
        Quest("quest.talk_mute", new TalkObjective { NpcId = Mute }),
        Quest("quest.talk_trader", new TalkObjective { NpcId = Trader }),
        Quest("quest.level", new LevelObjective { BaseLevel = 50 }),
        Quest("quest.job", new JobObjective { JobId = "job.warden" }),
        Quest("quest.equip", new EquipObjective { ItemId = "item.leather_cap" }),
        Quest("quest.use", new UseItemObjective { ItemId = "item.healing_draught", Count = 1 }),
        Quest("quest.level_and_kill", new LevelObjective { BaseLevel = 50 }, new KillObjective { MonsterId = Tame, Count = 1 }),
        Steps("quest.steps", [new KillObjective { MonsterId = Tame, Count = 1 }], [new TalkObjective { NpcId = Keeper }]),
        Quest("quest.full", new CollectObjective { ItemId = Wanted, Count = 1 }, new TalkObjective { NpcId = Keeper }) with { Rewards = [new ItemReward { ItemId = "item.padded_vest", Count = 1 }] },
        Quest("quest.other", new KillObjective { MonsterId = Tame, Count = 1 }),
    ];

    /// <summary>An NPC that offers a quest, a service, a save point, a reset, an item trade and a quest nobody ticked: nothing but the talk itself is the autopilot's.</summary>
    public static DialogueDef KeeperDialogue => new()
    {
        Id = "dialogue.keeper", Name = "keeper", Start = "greet",
        Nodes = new Dictionary<string, DialogueNodeDef>
        {
            ["greet"] = new()
            {
                Text = "Hello.",
                Choices =
                [
                    new() { Text = "shop", Actions = [new OpenServiceAction { Service = ServiceIds.Refine }] },
                    new() { Text = "home", Actions = [new SetSavePointAction { X = 5, Y = 5 }] },
                    new() { Text = "reset", Actions = [new RunEffectsAction { Effects = [new ResetStatsEffect()] }] },
                    new() { Text = "trade", Actions = [new GiveItemAction { ItemId = Other, Quantity = 1 }] },
                    new() { Text = "other quest", Actions = [new StartQuestAction { QuestId = "quest.other" }] },
                    new() { Text = "bye" },
                ],
            },
        },
    };

    /// <summary>A long talk: a continue, then a step choice of <c>quest.talk_trader</c>; every other choice opens something.</summary>
    public static DialogueDef TraderDialogue => new()
    {
        Id = "dialogue.trader", Name = "trader", Start = "one",
        Nodes = new Dictionary<string, DialogueNodeDef>
        {
            ["one"] = new() { Text = "One.", Next = "two" },
            ["two"] = new()
            {
                Text = "Two.",
                Choices =
                [
                    new() { Text = "wares", Actions = [new OpenServiceAction { Service = ServiceIds.Refine }] },
                    new() { Text = "about that", Conditions = [new QuestStepCondition { QuestId = "quest.kill", Step = 1 }], Next = "three" },
                ],
            },
            ["three"] = new() { Text = "Three." },
        },
    };

    private static MapGrid Grid(string mapId, GridSpawn[] spawns, GridWarp[] warps, GridNpc[] npcs, GridRect? safe = null, GridCell[]? walls = null)
    {
        const int width = 30, height = 12;
        var cells = new byte[width * height];
        Array.Fill(cells, CellFlagBits.Walkable);
        foreach (var wall in walls ?? []) cells[wall.Y * width + wall.X] = CellFlagBits.BlocksLos;
        return MapGrid.Create(new MapGridFile
        {
            MapId = mapId, Width = width, Height = height, Cells = Convert.ToBase64String(cells), Spawns = spawns, Warps = warps, Npcs = npcs,
            Zones = safe is { } s ? [new GridZone { Kind = ZoneKinds.Safe, Area = s }] : [],
        });
    }

    private static GridSpawn Spawn(string monster, int x, int y, int w, int h, int count = 1, double respawn = 2) =>
        new() { MonsterId = monster, Area = new GridRect(x, y, w, h), Count = count, RespawnSeconds = respawn };

    private static GridWarp Warp(int x, int y, string map, int tx, int ty) => new() { Cell = new GridCell(x, y), TargetMapId = map, TargetCell = new GridCell(tx, ty) };

    private static GridNpc Npc(string id, GridCell cell) => new() { NpcId = id, Cell = cell, Facing = Directions.S };

    /// <summary>A ring of walls around (25, 2): what stands in it cannot be walked to.</summary>
    public static readonly GridCell[] Ring = (from x in Enumerable.Range(23, 5) from y in Enumerable.Range(0, 5) where x is 23 or 27 || y is 0 or 4 select new GridCell(x, y)).ToArray();

    public static IReadOnlyDictionary<string, MapGrid> Grids(int tame = 2, int fierce = 0, GridRect? safe = null, bool warps = true)
    {
        var alpha = new List<GridSpawn> { Spawn(Tame, 12, 5, 3, 3, tame), Spawn(Walled, 24, 1, 3, 3) };
        if (fierce > 0) alpha.Add(Spawn(Fierce, 20, 6, 2, 2, fierce));
        return new[]
        {
            Grid(Alpha, alpha.ToArray(), warps ? [Warp(29, 6, Beta, 1, 6)] : [], [Npc(Keeper, KeeperCell), Npc(Mute, new GridCell(8, 10)), Npc(Trader, TraderCell)], safe, Ring),
            Grid(Beta, [Spawn(Near, 14, 5, 3, 3)], warps ? [Warp(0, 6, Alpha, 28, 6), Warp(29, 6, Gamma, 1, 6)] : [], []),
            Grid(Gamma, [Spawn(Far, 14, 5, 3, 3)], warps ? [Warp(0, 6, Beta, 28, 6)] : [], [Npc(FarKeeper, new GridCell(20, 6))]),
        }.ToDictionary(g => g.MapId, StringComparer.Ordinal);
    }

    public static MonsterDef Monster(string id, bool aggressive = false, int maxHp = 8, int atk = 1, double speed = 0.5, string[]? drops = null) =>
        TestCombat.Monster(id, maxHp, atk, speed: speed, aggressive: aggressive, aggroRange: aggressive ? 6 : 0, drops: drops);

    public static ContentDatabase Build(Func<AutopilotBalanceDef, AutopilotBalanceDef>? autopilot = null, IReadOnlyDictionary<string, MapGrid>? grids = null, int tameHp = 8, double globChance = 1,
        Func<BalanceDef, BalanceDef>? balance = null, IEnumerable<QuestDef>? quests = null)
    {
        var sample = TestGrids.SampleContent;
        grids ??= Grids();
        var settings = sample.Balance with
        {
            Autopilot = (autopilot ?? (a => a))(sample.Balance.Autopilot),
            NewCharacter = sample.Balance.NewCharacter with { StartMapId = Alpha, StartX = Start.X, StartY = Start.Y },
            Loot = sample.Balance.Loot with { PriorityMs = 3000 },
        };
        if (balance is not null) settings = balance(settings);
        var monsters = new[]
        {
            Monster(Tame, maxHp: tameHp, drops: ["drop.tame"]), Monster(Far), Monster(Near), Monster(Fierce, aggressive: true, maxHp: 60, speed: 5), Monster(Walled), Monster(Nowhere),
        };
        var drops = sample.Drops.All.Append(new DropTableDef
        {
            Id = "drop.tame", Name = "tame",
            Entries = [new DropEntryDef { ItemId = Wanted, Chance = globChance }, new DropEntryDef { ItemId = Other, Chance = 1 }],
        });
        var npcs = new[]
        {
            new NpcDef { Id = Keeper, Name = "Keeper", DialogueId = "dialogue.keeper" }, new NpcDef { Id = FarKeeper, Name = "Far", DialogueId = "dialogue.keeper" },
            new NpcDef { Id = Mute, Name = "Mute" }, new NpcDef { Id = Trader, Name = "Trader", DialogueId = "dialogue.trader" },
        };
        var maps = grids.Keys.Select(id => new MapDef { Id = id, Name = id, Respawn = new CellDef { X = Start.X, Y = Start.Y } });
        return new ContentDatabase(sample.Jobs, sample.Skills, sample.Items, new Registry<MonsterDef>(monsters), sample.Statuses, new Registry<QuestDef>(quests ?? Quests()),
            new Registry<NpcDef>(npcs), new Registry<DialogueDef>([KeeperDialogue, TraderDialogue]), new Registry<DropTableDef>(drops), new Registry<MapDef>(maps), sample.ExpTables,
            sample.Formulas, sample.Elements, settings, sample.Appearance, grids: grids, refine: sample.Refine);
    }
}

/// <summary>The whole pipeline on given content, ticked by hand, with every event recorded (T-604).</summary>
public sealed class AutopilotRig
{
    public readonly ContentDatabase Content;
    public readonly GameWorld World;
    public readonly ManualClock Clock = new();
    public readonly ListLog Log = new();
    public readonly InMemoryInventoryStore Inventories = new();
    public readonly InMemoryProgressionStore Progressions = new();
    public readonly InMemorySavePointStore SavePoints = new();
    public readonly List<(string Map, long Tick, IGameEvent Event)> Events = new();
    private readonly NewCharacters _start;
    private readonly InventoryContent _items;
    private readonly MonsterCatalog _monsters;

    /// <param name="content">The content; the kit's by default.</param>
    /// <param name="seed">The world's seed.</param>
    /// <param name="questSystem">False leaves the quest system out: nothing the player does counts, so a talk stays a talk (the dialogue rules then have to carry it).</param>
    public AutopilotRig(ContentDatabase? content = null, ulong seed = 604, bool questSystem = true)
    {
        Content = content ?? AutopilotContentKit.Build();
        _start = new NewCharacters(Content);
        _items = InventoryContent.From(Content);
        _monsters = MonsterCatalog.From(Content);
        World = new GameWorld(Content.Grids.Values, questSystem ? ServerPipeline.Create(Content, Progressions, Inventories, Clock, SavePoints) : WithoutQuests(), seed, Clock, Log);
        foreach (var instance in World.Instances) instance.Events.SubscribeAll(e => Events.Add((instance.MapId, instance.TickNumber, e)));
    }

    private SystemPipeline WithoutQuests()
    {
        var autopilot = AutopilotContent.From(Content);
        return new SystemPipeline()
            .Add(() => MovementSystem.Create(Content.Balance))
            .Add(() => new DialogueSystem(DialogueContent.From(Content), SavePoints, Progressions, Inventories))
            .Add(() => new CombatSystem(CombatContent.From(Content, _monsters), Progressions, SavePoints))
            .Add(() => new InventorySystem(_items, Inventories, Clock, Progressions))
            .Add(() => new AutopilotSystem(autopilot, Progressions, Clock));
    }

    /// <summary>Puts a quest in the log without any system noticing.</summary>
    public void PutInLog(EntityId player, string quest) => Progress(player).ActiveQuests[quest] = new Game.Shared.Quests.QuestProgress(quest, Content.Quests.Get(quest).Steps[0].Objectives.Count);

    public EntityId Join(GridCell? at = null, string? map = null, bool boost = true)
    {
        var id = World.Spawn(EntityKind.Player, map ?? _start.MapId, at ?? _start.Cell, Directions.S, e =>
        {
            e.Stats = _start.CreateStats();
            e.DefinitionId = _start.JobId;
        });
        Ticks(2);
        if (boost) Boost(id);
        return id;
    }

    /// <summary>Hits that always land and kill a tame monster at once; enough HP not to die by accident.</summary>
    public void Boost(EntityId player, int atk = 5000)
    {
        var sheet = Get(player).Stats!;
        sheet.Modifiers.Add("test:boost", "atk", ModifierOp.Flat, atk);
        sheet.Modifiers.Add("test:boost", "hit", ModifierOp.Flat, 5000);
        sheet.Modifiers.Add("test:boost", "max_hp", ModifierOp.Flat, 50000);
        Ticks();
        Get(player).Vitals!.Hp = Get(player).Vitals!.MaxHp;
    }

    public Entity Get(EntityId id)
    {
        foreach (var instance in World.Instances)
            if (instance.TryGetEntity(id, out var e)) return e;
        throw new InvalidOperationException($"{id} is gone");
    }

    public ProgressionState Progress(EntityId id) => Progressions.TryGet(id, out var s) ? s : throw new InvalidOperationException("no progression");

    public InventoryState Bag(EntityId id) => Inventories.TryGet(id, out var s) ? s : throw new InvalidOperationException("no inventory");

    public int Count(EntityId id, string itemId) => Bag(id).Entries.Where(e => e.ItemId == itemId && e.Slot is null).Sum(e => e.Quantity);

    public long Put(EntityId id, string itemId, int quantity = 1)
    {
        var delta = new InventoryDelta();
        _items.Rules.TryAdd(Bag(id), itemId, quantity, int.MaxValue, Inventories.NextEntryId, delta).ShouldBeNull();
        return delta.Upserted.Last().Id;
    }

    /// <summary>Accepts quests as a dialogue would.</summary>
    public void Accept(EntityId player, params string[] quests)
    {
        var instance = World.GetInstance(Get(player).MapId);
        foreach (var quest in quests) instance.Events.Publish(new QuestStartRequested(player, quest, "npc.test"));
        TickUntil(() => quests.All(q => Progress(player).ActiveQuests.ContainsKey(q)), 5, "accepted");
    }

    public void Learn(EntityId player, string skillId, int level = 1) => Progress(player).Skills[skillId] = level;

    public EntityId SpawnMonster(string monsterId, GridCell cell, string map)
    {
        _monsters.TryGet(monsterId, out var def).ShouldBeTrue();
        var id = World.Spawn(EntityKind.Monster, map, cell, Directions.W, e =>
        {
            e.DefinitionId = monsterId;
            e.Stats = _monsters.CreateStats(def);
        });
        Ticks();
        return id;
    }

    public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

    public void Start(EntityId player, params string[] quests) => Send(new StartAutopilot(player, quests, []));

    public void StartWith(EntityId player, string[] quests, params string[] skills) => Send(new StartAutopilot(player, quests, skills.Select(s => new SkillLevelRecord(s, 0)).ToArray()));

    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++)
            foreach (var instance in World.Instances) instance.Tick();
    }

    public void TickUntil(Func<bool> done, int max = 600, string? what = null)
    {
        for (var i = 0; i < max && !done(); i++) Ticks();
        done().ShouldBeTrue($"{what ?? "the condition"} not reached after {max} ticks; statuses: {string.Join(" | ", Of<AutopilotChanged>().Select(e => e.Status))}");
    }

    /// <summary>Ticks until the run has ended and checks why.</summary>
    public void RunsTo(EntityId player, string stop, int max = 1200)
    {
        TickUntil(() => Status(player).Mode == AutopilotModes.Off && Get(player).Autopilot is { Active: false }, max, "the end of the run");
        Status(player).ShouldBe(new AutopilotStatus(AutopilotModes.Off, null, null, stop));
    }

    /// <summary>Ticks until the run pauses and checks why.</summary>
    public void PausesWith(EntityId player, string pause, int max = 600)
    {
        Ticks();   // a start is taken up first
        TickUntil(() => Get(player).Autopilot is { Running: false }, max, "the pause");
        (Status(player).Mode, Status(player).Reason).ShouldBe((AutopilotModes.Paused, pause));
    }

    public AutopilotStatus Status(EntityId player) => Of<AutopilotChanged>().LastOrDefault(e => e.Player == player)?.Status ?? new AutopilotStatus("none", null, null, null);

    public List<string?> Activities(EntityId player) => Of<AutopilotChanged>().Where(e => e.Player == player && e.Status.Mode == AutopilotModes.Running).Select(e => e.Status.Doing).ToList();

    public List<T> Of<T>() where T : IGameEvent => Events.Select(e => e.Event).OfType<T>().ToList();

    public bool Completed(EntityId player, string quest) => Progress(player).CompletedQuests.Contains(quest);
}
