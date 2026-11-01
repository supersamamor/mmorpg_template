using System.Collections.Concurrent;
using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Server.World.Monsters;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Stats;
using Game.Shared.World;

namespace Game.Server.World.Combat;

/// <summary>
/// The read-only content the combat system reads: compiled combat rules, monsters, drop tables, job stat bonuses, the base EXP table and
/// <c>balance.yaml</c>. Built once at boot and shared by every instance's combat system (everything here is immutable or thread-safe).
/// </summary>
public sealed class CombatContent
{
    private readonly IReadOnlyDictionary<string, DropTableDef> _drops;
    private readonly IReadOnlyDictionary<string, (string MapId, GridCell Cell)> _respawns;
    private readonly ConcurrentDictionary<string, CompiledModifier[]> _jobBonuses = new(StringComparer.Ordinal);

    /// <summary>
    /// Combat content from parts (tests use their own monsters and balance). <paramref name="progression"/> is what
    /// <see cref="ProgressionRules"/> reads (jobs, skills, EXP tables).
    /// </summary>
    public CombatContent(CombatRules rules, MonsterCatalog monsters, BalanceDef balance, IReadOnlyDictionary<string, DropTableDef> drops,
        IProgressionContent progression, IReadOnlyDictionary<string, (string MapId, GridCell Cell)>? respawns = null)
    {
        _respawns = respawns ?? new Dictionary<string, (string MapId, GridCell Cell)>();
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));
        Monsters = monsters ?? throw new ArgumentNullException(nameof(monsters));
        Balance = balance ?? throw new ArgumentNullException(nameof(balance));
        _drops = drops ?? throw new ArgumentNullException(nameof(drops));
        Progression = progression ?? throw new ArgumentNullException(nameof(progression));
        if (!progression.TryGetExpTable(balance.Progression.BaseExpTableId, out var table) || table is null)
            throw new ArgumentException($"Base EXP table '{balance.Progression.BaseExpTableId}' not found.", nameof(progression));
        BaseExpTable = table;
        Check(balance.Combat.PlayerAttackRange >= 1, "combat.player_attack_range must be at least 1.");
        Check(balance.Combat.PlayerRepathIntervalMs > 0, "combat.player_repath_interval_ms must be above 0.");
        Check(balance.Death.RespawnHpPercent is >= 1 and <= 100, "death.respawn_hp_percent must be between 1 and 100.");
        Check(balance.Death.ExpPenaltyPercent is >= 0 and <= 100, "death.exp_penalty_percent must be between 0 and 100.");
        Check(balance.Loot.PriorityMs >= 0, "loot.priority_ms must not be negative.");
        Check(balance.Loot.LifetimeMs > 0, "loot.lifetime_ms must be above 0.");
        Check(balance.Loot.SpreadRadius >= 0, "loot.spread_radius must not be negative.");
        Check(balance.Death.CorpseMs >= 0, "death.corpse_ms must not be negative.");
        Check(balance.Regen.OutOfCombatDelayMs >= 0, "regen.out_of_combat_delay_ms must not be negative.");
    }

    /// <summary>Combat content of a loaded database, with <paramref name="monsters"/> (the catalog the spawner and the AI share).</summary>
    public static CombatContent From(ContentDatabase content, MonsterCatalog monsters)
    {
        ArgumentNullException.ThrowIfNull(content);
        var rules = CombatRules.Compile(content.Formulas, content.Elements, content.Balance.Combat);
        var drops = content.Drops.All.ToDictionary(d => d.Id, StringComparer.Ordinal);
        return new CombatContent(rules, monsters, content.Balance, drops, new ContentProgression(content), RespawnPoints(content.Maps.All, content.Grids));
    }

    internal static Dictionary<string, (string MapId, GridCell Cell)> RespawnPoints(IEnumerable<MapDef> maps, IReadOnlyDictionary<string, MapGrid> grids)
    {
        var points = new Dictionary<string, (string MapId, GridCell Cell)>(StringComparer.Ordinal);
        foreach (var map in maps)
            if (map.Respawn is { } cell) points[map.Id] = (map.Id, new GridCell(cell.X, cell.Y));
        foreach (var map in maps)   // the first warp that arrives on a map without a respawn cell of its own
        {
            if (!grids.TryGetValue(map.Id, out var grid)) continue;
            foreach (var warp in grid.Warps)
                if (!points.ContainsKey(warp.TargetMapId)) points[warp.TargetMapId] = (warp.TargetMapId, warp.TargetCell);
        }
        return points;
    }

    /// <summary>Compiled damage, hit and crit formulas, element table and clamps.</summary>
    public CombatRules Rules { get; }

    /// <summary>Monster definitions (element, race, size, EXP, drop tables) by id.</summary>
    public MonsterCatalog Monsters { get; }

    /// <summary><c>balance.yaml</c>: <c>combat</c>, <c>death</c>, <c>loot</c>, <c>progression</c>, <c>new_character</c>.</summary>
    public BalanceDef Balance { get; }

    /// <summary>Jobs, skills and EXP tables for <see cref="ProgressionRules"/>.</summary>
    public IProgressionContent Progression { get; }

    /// <summary>The base EXP table (<c>progression.base_exp_table_id</c>): the death penalty is a share of the current level's entry.</summary>
    public ExpTableDef BaseExpTable { get; }

    /// <summary>
    /// Where a player who dies on <paramref name="mapId"/> comes back: the map's own <c>respawn</c> cell, else the first warp (in map file
    /// order) that arrives on it, else the <c>new_character</c> start.
    /// </summary>
    public (string MapId, GridCell Cell) RespawnPointFor(string mapId) =>
        _respawns.TryGetValue(mapId, out var point)
            ? point
            : (Balance.NewCharacter.StartMapId, new GridCell(Balance.NewCharacter.StartX, Balance.NewCharacter.StartY));

    /// <summary>Finds a drop table.</summary>
    public bool TryGetDropTable(string id, out DropTableDef table) => _drops.TryGetValue(id, out table!);

    /// <summary>The job's stat bonuses, compiled once per job (thread-safe); empty for an unknown job.</summary>
    public CompiledModifier[] JobBonuses(string jobId) =>
        _jobBonuses.GetOrAdd(jobId, id => Progression.TryGetJob(id, out var job) && job is not null
            ? job.StatBonuses.Select(CompiledModifier.Compile).ToArray()
            : []);

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new ArgumentOutOfRangeException(nameof(Balance), message);
    }
}

/// <summary>
/// Where each player character's progression (levels, EXP, points) lives while it is in the world. The seam for persistence (T-702): today
/// <see cref="InMemoryProgressionStore"/>; later a store loaded from and saved to the database. Shared by every instance, so implementations
/// must be thread-safe. A character is in one instance at a time, so its state is only touched from that instance's tick thread.
/// </summary>
public interface IPlayerProgressionStore
{
    /// <summary>The character's progression, created with <paramref name="create"/> the first time the character is seen.</summary>
    ProgressionState GetOrCreate(EntityId player, Func<ProgressionState> create);

    /// <summary>The character's progression, if it has one.</summary>
    bool TryGet(EntityId player, out ProgressionState state);

    /// <summary>Forgets a character that left the world (logout). Persistence would save it here.</summary>
    void Release(EntityId player);
}

/// <summary>Progression kept in memory for the server run only, keyed by entity id. Nothing is saved (T-702).</summary>
public sealed class InMemoryProgressionStore : IPlayerProgressionStore
{
    private readonly ConcurrentDictionary<EntityId, ProgressionState> _states = new();

    /// <summary>Characters held.</summary>
    public int Count => _states.Count;

    /// <inheritdoc />
    public ProgressionState GetOrCreate(EntityId player, Func<ProgressionState> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return _states.TryGetValue(player, out var state) ? state : _states.GetOrAdd(player, _ => create());
    }

    /// <inheritdoc />
    public bool TryGet(EntityId player, out ProgressionState state) => _states.TryGetValue(player, out state!);

    /// <inheritdoc />
    public void Release(EntityId player) => _states.TryRemove(player, out _);
}
