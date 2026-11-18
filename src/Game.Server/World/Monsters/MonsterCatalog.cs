using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Shared.Definitions;
using Game.Shared.Effects;
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

    /// <summary>
    /// A catalog of <paramref name="monsters"/>; <paramref name="stats"/> builds one monster's sheet (it must be thread-safe). <paramref name="effects"/>
    /// is the compiled content the monsters' skills (and every other effect) come from; none means no skills.
    /// </summary>
    public MonsterCatalog(IEnumerable<MonsterDef> monsters, Func<MonsterDef, StatSheet> stats, EffectLibrary? effects = null)
    {
        ArgumentNullException.ThrowIfNull(monsters);
        _byId = monsters.ToDictionary(m => m.Id, StringComparer.Ordinal);
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        Effects = effects ?? EffectLibrary.Empty;
    }

    /// <summary>
    /// Every effect list of the content, compiled once (skills, consumables, statuses; T-501): the AI reads its monsters' skills here, and combat runs
    /// all of them through its effect runner. Immutable, so shared by every instance.
    /// </summary>
    public EffectLibrary Effects { get; }

    /// <summary>
    /// The content's monsters, with sheets from the stats pipeline (<see cref="BalanceSimulator.Monster"/>: primary stats as base values,
    /// listed derived stats as overrides, level as base level), the same sheets the balance tools use.
    /// </summary>
    public static MonsterCatalog From(ContentDatabase content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var simulator = new BalanceSimulator(content);
        return new MonsterCatalog(content.Monsters.All, simulator.Monster, CompileEffects(content));
    }

    /// <summary>The content's skills, consumables and statuses compiled into one <see cref="EffectLibrary"/>.</summary>
    public static EffectLibrary CompileEffects(ContentDatabase content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new EffectLibrary(content.Skills.All, content.Items.All, content.Statuses.All);
    }

    /// <summary>Finds a monster definition.</summary>
    public bool TryGet(string id, out MonsterDef def) => _byId.TryGetValue(id, out def!);

    /// <summary>A new stat sheet for one monster of <paramref name="def"/>.</summary>
    public StatSheet CreateStats(MonsterDef def) => _stats(def);
}
