using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Content.Loading;
using Game.Shared.Definitions;
using Game.Shared.World;

namespace Game.Content.Registry;

/// <summary>What changed in one registry between two content sets: ids added, removed and changed, each list ordered (ordinal).</summary>
/// <param name="Kind">The registry's folder under <c>content/</c> (<see cref="ContentLayout.Folders"/>), or the definition type's name for one that has none.</param>
public sealed record KindChange(string Kind, Type DefinitionType, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed)
{
    /// <summary>True when nothing of this kind changed.</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

/// <summary>
/// The difference between two loaded content sets (T-801), worked out generically: each definition is compared by its JSON form, each root file
/// as a whole, <c>balance.yaml</c> by top-level section (its YAML key) and each map grid by its size, flags and placements. No list of fields or ids:
/// a definition type added to <see cref="ContentDatabase"/> is compared like the others (hard rule 2).
/// </summary>
public sealed class ContentDiff
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false };

    private ContentDiff(IReadOnlyList<KindChange> kinds, IReadOnlyList<string> rootFiles, IReadOnlyList<string> balanceSections, IReadOnlyList<string> grids)
    {
        Kinds = kinds;
        RootFiles = rootFiles;
        BalanceSections = balanceSections;
        Grids = grids;
    }

    /// <summary>Every registry with a change, in <see cref="ContentDatabase.Registries"/> order.</summary>
    public IReadOnlyList<KindChange> Kinds { get; }

    /// <summary>Root files whose content changed (<see cref="ContentLayout"/> names, e.g. <c>formulas.yaml</c>), ordered.</summary>
    public IReadOnlyList<string> RootFiles { get; }

    /// <summary>The top-level sections of <c>balance.yaml</c> that changed, by YAML key (<c>aoi</c>, <c>new_character</c>, ...), ordered.</summary>
    public IReadOnlyList<string> BalanceSections { get; }

    /// <summary>Map ids whose logic grid was added, removed or changed, ordered.</summary>
    public IReadOnlyList<string> Grids { get; }

    /// <summary>True when the two sets hold the same content.</summary>
    public bool IsEmpty => Kinds.Count == 0 && RootFiles.Count == 0 && Grids.Count == 0;

    /// <summary>The change of the registry holding <typeparamref name="T"/>, or an empty one.</summary>
    public KindChange Of<T>() where T : IDefinition =>
        Kinds.FirstOrDefault(k => k.DefinitionType == typeof(T)) ?? new KindChange(KindName(typeof(T)), typeof(T), [], [], []);

    /// <summary>One line per change, for the log: <c>items: changed item.a, item.b; added item.c</c>.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var kind in Kinds)
        {
            var parts = new List<string>();
            if (kind.Changed.Count > 0) parts.Add("changed " + string.Join(", ", kind.Changed));
            if (kind.Added.Count > 0) parts.Add("added " + string.Join(", ", kind.Added));
            if (kind.Removed.Count > 0) parts.Add("removed " + string.Join(", ", kind.Removed));
            yield return $"{kind.Kind}: {string.Join("; ", parts)}";
        }
        foreach (var file in RootFiles)
            yield return file == ContentLayout.BalanceFile && BalanceSections.Count > 0 ? $"{file}: {string.Join(", ", BalanceSections)}" : $"{file}: changed";
        foreach (var map in Grids) yield return $"grid of {map}: changed";
    }

    /// <summary>Compares <paramref name="before"/> with <paramref name="after"/>.</summary>
    public static ContentDiff Compare(ContentDatabase before, ContentDatabase after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var kinds = new List<KindChange>();
        foreach (var registry in after.Registries)
        {
            var old = before.Registries.FirstOrDefault(r => r.DefinitionType == registry.DefinitionType);
            var oldDefs = (old?.Definitions ?? []).ToDictionary(d => d.Id, Form, StringComparer.Ordinal);
            var newDefs = registry.Definitions.ToDictionary(d => d.Id, Form, StringComparer.Ordinal);
            var added = newDefs.Keys.Where(id => !oldDefs.ContainsKey(id)).Order(StringComparer.Ordinal).ToList();
            var removed = oldDefs.Keys.Where(id => !newDefs.ContainsKey(id)).Order(StringComparer.Ordinal).ToList();
            var changed = newDefs.Where(p => oldDefs.TryGetValue(p.Key, out var was) && was != p.Value).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
            var change = new KindChange(KindName(registry.DefinitionType), registry.DefinitionType, added, removed, changed);
            if (!change.IsEmpty) kinds.Add(change);
        }

        var roots = new List<string>();
        void Root(string file, object a, object b)
        {
            if (Form(a) != Form(b)) roots.Add(file);
        }
        Root(ContentLayout.FormulasFile, before.Formulas, after.Formulas);
        Root(ContentLayout.ElementsFile, before.Elements, after.Elements);
        Root(ContentLayout.AppearanceFile, before.Appearance, after.Appearance);
        Root(ContentLayout.RefineFile, before.Refine, after.Refine);
        var sections = Sections(before.Balance, after.Balance);
        if (sections.Count > 0) roots.Add(ContentLayout.BalanceFile);
        roots.Sort(StringComparer.Ordinal);

        var grids = before.Grids.Keys.Union(after.Grids.Keys, StringComparer.Ordinal)
            .Where(id => !before.Grids.TryGetValue(id, out var a) || !after.Grids.TryGetValue(id, out var b) || GridForm(a) != GridForm(b))
            .Order(StringComparer.Ordinal).ToList();
        return new ContentDiff(kinds, roots, sections, grids);
    }

    /// <summary>The folder of a definition type under <c>content/</c>, or its root file (<c>exp_tables.yaml</c>), or the type's name.</summary>
    public static string KindName(Type definitionType)
    {
        foreach (var (folder, type) in ContentLayout.Folders)
            if (type == definitionType) return folder;
        return definitionType == typeof(ExpTableDef) ? ContentLayout.ExpTablesFile : definitionType.Name;
    }

    private static string Form(object value) => JsonSerializer.Serialize(value, value.GetType(), Json);

    private static List<string> Sections(BalanceDef before, BalanceDef after)
    {
        var a = JsonNode.Parse(Form(before))!.AsObject();
        var b = JsonNode.Parse(Form(after))!.AsObject();
        return a.Select(p => p.Key).Union(b.Select(p => p.Key), StringComparer.Ordinal)
            .Where(key => a[key]?.ToJsonString() != b[key]?.ToJsonString())
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>A grid's size, every cell's flags and its placements, as one hash.</summary>
    private static string GridForm(MapGrid grid)
    {
        var text = new StringBuilder();
        text.Append(grid.MapId).Append('|').Append(grid.Width).Append('x').Append(grid.Height).Append('|').Append(grid.SourceSceneHash).Append('|');
        for (var y = 0; y < grid.Height; y++)
        for (var x = 0; x < grid.Width; x++)
            text.Append(grid.IsWalkable(x, y) ? 'w' : '-').Append(grid.BlocksLos(x, y) ? 'l' : '-');
        text.Append('|').Append(Form(grid.Spawns)).Append(Form(grid.Warps)).Append(Form(grid.Npcs)).Append(Form(grid.Zones));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
