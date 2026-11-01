using Game.Shared.Definitions;

namespace Game.Content.Loading;

/// <summary>
/// Where each kind of content lives under the content root. The folder decides the definition type;
/// content files never declare it. Only <c>*.yaml</c> files are loaded (maps also hold <c>*.grid.json</c>).
/// </summary>
public static class ContentLayout
{
    /// <summary>Per-definition folders (searched recursively) and the record type each holds.</summary>
    public static readonly IReadOnlyDictionary<string, Type> Folders = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["jobs"] = typeof(JobDef),
        ["skills"] = typeof(SkillDef),
        ["items"] = typeof(ItemDef),
        ["monsters"] = typeof(MonsterDef),
        ["statuses"] = typeof(StatusDef),
        ["quests"] = typeof(QuestDef),
        ["npcs"] = typeof(NpcDef),
        ["dialogue"] = typeof(DialogueDef),
        ["drops"] = typeof(DropTableDef),
        ["maps"] = typeof(MapDef),
    };

    /// <summary>Folder that holds map metadata (<c>*.yaml</c>) and the logic grids named by <c>MapDef.GridFile</c>.</summary>
    public const string MapsFolder = "maps";

    /// <summary>Content-root-relative path of a grid file name, e.g. <c>maps/starter_meadow.grid.json</c>.</summary>
    public static string GridPath(string gridFile) => MapsFolder + "/" + gridFile;

    /// <summary>Root file holding the <see cref="FormulaSetDef"/>.</summary>
    public const string FormulasFile = "formulas.yaml";
    /// <summary>Root file holding the <see cref="BalanceDef"/>.</summary>
    public const string BalanceFile = "balance.yaml";
    /// <summary>Root file holding the <see cref="ElementTableDef"/>.</summary>
    public const string ElementsFile = "elements.yaml";
    /// <summary>Root file holding the <see cref="ExpTableDef"/> documents (one per <c>---</c> document).</summary>
    public const string ExpTablesFile = "exp_tables.yaml";
}
