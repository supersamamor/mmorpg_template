using Game.Content.Registry;
using Game.Shared.Autopilot;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Quests;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Autopilot;

/// <summary>What to hunt for one <c>kill</c> or <c>collect</c> objective: the monsters, and where they are placed.</summary>
public sealed class HuntPlan
{
    internal HuntPlan(HashSet<string> monsters, List<TravelTarget> grounds)
    {
        Monsters = monsters;
        Grounds = grounds;
    }

    /// <summary>Monster ids that count.</summary>
    public IReadOnlySet<string> Monsters { get; }

    /// <summary>Every spawn area of one of them, on any map, in map id and grid order.</summary>
    public IReadOnlyList<TravelTarget> Grounds { get; }
}

/// <summary>A skill as the autopilot uses it.</summary>
public sealed record AutopilotSkillContent(AutopilotSkill Skill, Formula SpCost);

/// <summary>
/// Everything the autopilot reads from content (T-604), worked out once at boot and shared by every instance: the numbers, the quests, how each skill may be used, and for every
/// quest objective where it can be done, with the way there from anywhere (<see cref="TravelMap"/>). Immutable after construction.
/// </summary>
/// <remarks>The distance fields cost one integer and one byte per map cell for every warp, quest NPC, <c>reach</c> cell and spawn area of a quest monster; nothing else is kept.</remarks>
public sealed class AutopilotContent
{
    private static readonly IReadOnlyList<TravelTarget> Nowhere = [];
    private readonly Dictionary<string, DialogueDef> _dialogues;
    private readonly Dictionary<string, AutopilotSkillContent> _skills;
    private readonly Dictionary<string, HuntPlan> _kills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HuntPlan> _collects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<TravelTarget>> _npcs = new(StringComparer.Ordinal);
    private readonly Dictionary<ReachObjective, TravelTarget> _reaches = new();
    private readonly Dictionary<string, bool[]> _safe = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Map, int Spawn), TravelTarget> _grounds = new();
    private readonly List<MapGrid> _grids;
    private readonly Dictionary<string, (int Level, bool Boss)> _monsters = new(StringComparer.Ordinal);

    /// <summary>Works the content out. Throws <see cref="ArgumentOutOfRangeException"/> for unusable numbers (the validator reports them first).</summary>
    public AutopilotContent(BalanceDef balance, IEnumerable<QuestDef> quests, IEnumerable<MonsterDef> monsters, IEnumerable<DropTableDef> drops, IEnumerable<SkillDef> skills,
        IEnumerable<DialogueDef> dialogues, IEnumerable<MapGrid> grids)
    {
        ArgumentNullException.ThrowIfNull(balance);
        Balance = balance.Autopilot;
        Check(Balance.MaxQuests >= 1, "max_quests must be at least 1.");
        Check(Balance.MaxSkills >= 0, "max_skills must not be negative.");
        Check(Balance.NoProgressMs > 0, "no_progress_ms must be above 0.");
        Check(Balance.DecideIntervalMs > 0, "decide_interval_ms must be above 0.");
        Check(Balance.SearchesPerTick >= 1, "searches_per_tick must be at least 1.");
        Check(Balance.LegCells >= 1, "leg_cells must be at least 1.");
        Limits = new PathLimits(balance.Movement.MaxPathLength, balance.Movement.MaxNodesExpanded);
        TalkRange = balance.Dialogue.TalkRange;
        Sight = balance.Aoi.ViewRadius * balance.Aoi.BucketSize;
        Battle = balance.AutoBattle;
        Check(Battle.RadiusCells >= 1 && Battle.RadiusCells * 2 <= Sight, "auto_battle.radius_cells must be from 1 to half of aoi.view_radius x aoi.bucket_size.");
        Check(Battle.RetaliateExtraCells >= 0 && Battle.RetaliateExtraCells <= Battle.RadiusCells, "auto_battle.retaliate_extra_cells must be from 0 to radius_cells.");
        var questList = quests.ToList();
        Quests = new QuestRules(questList, balance.Quests.MaxActive);
        _dialogues = dialogues.ToDictionary(d => d.Id, StringComparer.Ordinal);
        _skills = skills.ToDictionary(s => s.Id, s => new AutopilotSkillContent(new AutopilotSkill(s), Formula.Compile(s.SpCost)), StringComparer.Ordinal);
        _grids = (Balance.Enabled ? grids : []).OrderBy(g => g.MapId, StringComparer.Ordinal).ToList();   // switched off: nothing to work out
        Travel = new TravelMap(_grids);
        foreach (var grid in _grids) _safe[grid.MapId] = SafeCells(grid);

        var tables = drops.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var monsterList = monsters.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        foreach (var monster in monsterList) _monsters[monster.Id] = (monster.Level, monster.Boss);
        foreach (var objective in questList.SelectMany(q => q.Steps).SelectMany(s => s.Objectives))
        {
            switch (objective)
            {
                case KillObjective kill:
                    if (!_kills.ContainsKey(kill.MonsterId)) _kills[kill.MonsterId] = Plan([kill.MonsterId]);
                    break;
                case CollectObjective collect:
                    if (!_collects.ContainsKey(collect.ItemId))
                        _collects[collect.ItemId] = Plan(monsterList.Where(m => Drops(m, collect.ItemId, tables)).Select(m => m.Id));
                    break;
                case TalkObjective talk:
                    if (!_npcs.ContainsKey(talk.NpcId)) _npcs[talk.NpcId] = NpcPlaces(talk.NpcId);
                    break;
                case ReachObjective reach:
                    if (!_reaches.ContainsKey(reach) && _grids.Any(g => g.MapId == reach.MapId))
                        _reaches[reach] = Travel.AddTarget(reach.MapId, Around(new GridCell(reach.X, reach.Y), Math.Max(0, reach.Radius)));
                    break;
            }
        }
    }

    /// <summary>The loaded database's content.</summary>
    public static AutopilotContent From(ContentDatabase content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new AutopilotContent(content.Balance, content.Quests.All, content.Monsters.All, content.Drops.All, content.Skills.All, content.Dialogue.All, content.Grids.Values);
    }

    /// <summary>The <c>autopilot</c> section of <c>balance.yaml</c>.</summary>
    public AutopilotBalanceDef Balance { get; }

    /// <summary>The <c>auto_battle</c> section of <c>balance.yaml</c> (T-811).</summary>
    public AutoBattleBalanceDef Battle { get; }

    /// <summary>A monster's level and boss flag, as auto-battle's filter needs them (T-811); false for an unknown id.</summary>
    public bool TryGetMonster(string id, out int level, out bool boss)
    {
        var found = _monsters.TryGetValue(id, out var monster);
        level = monster.Level;
        boss = monster.Boss;
        return found;
    }

    /// <summary>The path search limits of <c>movement</c>.</summary>
    public PathLimits Limits { get; }

    /// <summary><c>dialogue.talk_range</c>.</summary>
    public int TalkRange { get; }

    /// <summary>How far (Chebyshev) every player is sure to see: a client can only aim at what it sees, so the autopilot walks up to a target that far before it aims at it.</summary>
    public int Sight { get; }

    /// <summary>The quest rules: objectives and needs. Only read.</summary>
    public QuestRules Quests { get; }

    /// <summary>Ways between and on the maps.</summary>
    public TravelMap Travel { get; }

    /// <summary>What to hunt for a <c>kill</c> objective.</summary>
    public HuntPlan HuntFor(KillObjective kill) => _kills[kill.MonsterId];

    /// <summary>What to hunt for a <c>collect</c> objective: the monsters whose drop tables can give the item.</summary>
    public HuntPlan HuntFor(CollectObjective collect) => _collects[collect.ItemId];

    /// <summary>Where the NPC of a <c>talk</c> objective can be talked to: the cells within talk range of each of its placements.</summary>
    public IReadOnlyList<TravelTarget> PlacesOf(TalkObjective talk) => _npcs.TryGetValue(talk.NpcId, out var places) ? places : Nowhere;

    /// <summary>The place of a <c>reach</c> objective, or null when its map is not there.</summary>
    public TravelTarget? PlaceOf(ReachObjective reach) => _reaches.GetValueOrDefault(reach);

    /// <summary>A dialogue graph.</summary>
    public bool TryGetDialogue(string id, out DialogueDef dialogue) => _dialogues.TryGetValue(id, out dialogue!);

    /// <summary>A skill.</summary>
    public bool TryGetSkill(string id, out AutopilotSkillContent skill) => _skills.TryGetValue(id, out skill!);

    /// <summary>True in a <c>safe</c> zone of the map, where nothing can be attacked from.</summary>
    public bool IsSafe(MapGrid grid, GridCell cell) =>
        grid.InBounds(cell.X, cell.Y) && _safe.TryGetValue(grid.MapId, out var safe) && safe[cell.Y * grid.Width + cell.X];

    private HuntPlan Plan(IEnumerable<string> monsterIds)
    {
        var monsters = new HashSet<string>(monsterIds, StringComparer.Ordinal);
        var grounds = new List<TravelTarget>();
        foreach (var grid in _grids)
        for (var i = 0; i < grid.Spawns.Count; i++)
        {
            var spawn = grid.Spawns[i];
            if (!monsters.Contains(spawn.MonsterId)) continue;
            if (!_grounds.TryGetValue((grid.MapId, i), out var ground))
                _grounds[(grid.MapId, i)] = ground = Travel.AddTarget(grid.MapId, Cells(spawn.Area).Where(c => !IsSafe(grid, c)));
            grounds.Add(ground);
        }
        return new HuntPlan(monsters, grounds);
    }

    private IReadOnlyList<TravelTarget> NpcPlaces(string npcId)
    {
        var places = new List<TravelTarget>();
        foreach (var grid in _grids)
        foreach (var npc in grid.Npcs)
            if (string.Equals(npc.NpcId, npcId, StringComparison.Ordinal)) places.Add(Travel.AddTarget(grid.MapId, Around(npc.Cell, TalkRange)));
        return places;
    }

    private static bool Drops(MonsterDef monster, string itemId, Dictionary<string, DropTableDef> tables)
    {
        foreach (var id in monster.DropTableIds)
            if (tables.TryGetValue(id, out var table) && table.Entries.Any(e => e.Chance > 0 && string.Equals(e.ItemId, itemId, StringComparison.Ordinal))) return true;
        return false;
    }

    private static IEnumerable<GridCell> Around(GridCell cell, int radius) => Cells(new GridRect(cell.X - radius, cell.Y - radius, 2 * radius + 1, 2 * radius + 1));

    private static IEnumerable<GridCell> Cells(GridRect area)
    {
        for (var y = area.Y; y < area.Y + area.H; y++)
        for (var x = area.X; x < area.X + area.W; x++)
            yield return new GridCell(x, y);
    }

    private static bool[] SafeCells(MapGrid grid)
    {
        var safe = new bool[grid.Width * grid.Height];
        foreach (var zone in grid.Zones)
            if (zone.Kind == ZoneKinds.Safe)
                foreach (var cell in Cells(zone.Area))
                    if (grid.InBounds(cell.X, cell.Y)) safe[cell.Y * grid.Width + cell.X] = true;
        return safe;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new ArgumentOutOfRangeException(nameof(AutopilotBalanceDef), message.StartsWith("auto_battle.", StringComparison.Ordinal) ? message : "autopilot." + message);
    }
}
