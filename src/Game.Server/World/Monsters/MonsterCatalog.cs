using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Shared.Definitions;
using Game.Shared.Stats;

namespace Game.Server.World.Monsters;

/// <summary>
/// The monster definitions the spawner and the AI read, and how a monster's stat sheet is built. Shared by every instance: lookups are
/// read-only and <see cref="CreateStats"/> builds a fresh sheet from immutable compiled formulas, so it is safe from any instance thread.
/// </summary>
public sealed class MonsterCatalog
{
    private readonly Dictionary<string, MonsterDef> _byId;
    private readonly Func<MonsterDef, StatSheet> _stats;

    /// <summary>A catalog of <paramref name="monsters"/>; <paramref name="stats"/> builds one monster's sheet (it must be thread-safe).</summary>
    public MonsterCatalog(IEnumerable<MonsterDef> monsters, Func<MonsterDef, StatSheet> stats)
    {
        ArgumentNullException.ThrowIfNull(monsters);
        _byId = monsters.ToDictionary(m => m.Id, StringComparer.Ordinal);
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
    }

    /// <summary>
    /// The content's monsters, with sheets from the stats pipeline (<see cref="BalanceSimulator.Monster"/>: primary stats as base values,
    /// listed derived stats as overrides, level as base level), the same sheets the balance tools use.
    /// </summary>
    public static MonsterCatalog From(ContentDatabase content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var simulator = new BalanceSimulator(content);
        return new MonsterCatalog(content.Monsters.All, simulator.Monster);
    }

    /// <summary>Finds a monster definition.</summary>
    public bool TryGet(string id, out MonsterDef def) => _byId.TryGetValue(id, out def!);

    /// <summary>A new stat sheet for one monster of <paramref name="def"/>.</summary>
    public StatSheet CreateStats(MonsterDef def) => _stats(def);
}
