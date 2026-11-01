using Game.Content.Loading;
using Game.Shared.Definitions;
using Game.Shared.World;

namespace Game.Content.Registry;

/// <summary>
/// The fully loaded, immutable content set: one <see cref="Registry{T}"/> per definition type plus the
/// singleton root files. Produced by <see cref="ContentLoader.Load"/>; contains no cross-reference validation
/// (T-102), so an id inside a definition may still point at nothing.
/// </summary>
public sealed class ContentDatabase
{
    private readonly IReadOnlyDictionary<string, SourceLocation> _sources;
    private readonly IReadOnlyList<IRegistry> _registries;

    /// <summary>Creates a database. <paramref name="sources"/> maps def id → where it was declared (may be omitted, e.g. in unit tests).</summary>
    public ContentDatabase(
        Registry<JobDef> jobs, Registry<SkillDef> skills, Registry<ItemDef> items, Registry<MonsterDef> monsters,
        Registry<StatusDef> statuses, Registry<QuestDef> quests, Registry<NpcDef> npcs, Registry<DialogueDef> dialogue,
        Registry<DropTableDef> drops, Registry<MapDef> maps, Registry<ExpTableDef> expTables,
        FormulaSetDef formulas, ElementTableDef elements, BalanceDef balance,
        IReadOnlyDictionary<string, SourceLocation>? sources = null,
        IReadOnlyDictionary<string, MapGrid>? grids = null)
    {
        Jobs = jobs; Skills = skills; Items = items; Monsters = monsters; Statuses = statuses; Quests = quests;
        Npcs = npcs; Dialogue = dialogue; Drops = drops; Maps = maps; ExpTables = expTables;
        Formulas = formulas; Elements = elements; Balance = balance;
        _sources = sources ?? new Dictionary<string, SourceLocation>();
        Grids = grids ?? new Dictionary<string, MapGrid>();
        _registries = new IRegistry[] { jobs, skills, items, monsters, statuses, quests, npcs, dialogue, drops, maps, expTables };
    }

    /// <summary>Jobs (<c>content/jobs/</c>).</summary>
    public Registry<JobDef> Jobs { get; }
    /// <summary>Skills (<c>content/skills/</c>).</summary>
    public Registry<SkillDef> Skills { get; }
    /// <summary>Items (<c>content/items/</c>).</summary>
    public Registry<ItemDef> Items { get; }
    /// <summary>Monsters (<c>content/monsters/</c>).</summary>
    public Registry<MonsterDef> Monsters { get; }
    /// <summary>Statuses (<c>content/statuses/</c>).</summary>
    public Registry<StatusDef> Statuses { get; }
    /// <summary>Quests (<c>content/quests/</c>).</summary>
    public Registry<QuestDef> Quests { get; }
    /// <summary>NPCs (<c>content/npcs/</c>).</summary>
    public Registry<NpcDef> Npcs { get; }
    /// <summary>Dialogue trees (<c>content/dialogue/</c>).</summary>
    public Registry<DialogueDef> Dialogue { get; }
    /// <summary>Drop tables (<c>content/drops/</c>).</summary>
    public Registry<DropTableDef> Drops { get; }
    /// <summary>Maps (<c>content/maps/*.yaml</c>).</summary>
    public Registry<MapDef> Maps { get; }
    /// <summary>EXP curves, one per document of <c>content/exp_tables.yaml</c>.</summary>
    public Registry<ExpTableDef> ExpTables { get; }
    /// <summary><c>content/formulas.yaml</c>. Expressions are unparsed strings (T-103).</summary>
    public FormulaSetDef Formulas { get; }
    /// <summary><c>content/elements.yaml</c>.</summary>
    public ElementTableDef Elements { get; }

    /// <summary><c>content/balance.yaml</c>: progression numbers and combat clamps.</summary>
    public BalanceDef Balance { get; }

    /// <summary>Logic grid of every map, by map id. The loader guarantees one per entry of <see cref="Maps"/>.</summary>
    public IReadOnlyDictionary<string, MapGrid> Grids { get; }

    /// <summary>The logic grid of a map; throws <see cref="KeyNotFoundException"/> for an unknown map id.</summary>
    public MapGrid GetGrid(string mapId) =>
        Grids.TryGetValue(mapId, out var grid) ? grid : throw new KeyNotFoundException($"No logic grid for map '{mapId}'.");

    /// <summary>Every id-keyed registry, for uniform iteration.</summary>
    public IReadOnlyList<IRegistry> Registries => _registries;

    /// <summary>True if any registry holds this id. Ids are namespaced, so at most one registry can.</summary>
    public bool ContainsId(string id) => _registries.Any(r => r.Contains(id));

    /// <summary>Where the definition with this id was declared (file and line), or null if unknown.</summary>
    public SourceLocation? SourceOf(string id) => _sources.TryGetValue(id, out var s) ? s : null;
}
