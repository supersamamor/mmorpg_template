using Game.Content.ClientExport;
using Game.Content.Loading;
using Game.Content.Registry;
using Game.Shared.Definitions;

namespace Game.Admin.ContentView;

/// <summary>
/// The names content gives ids (items, jobs, maps, ...), read once at start, for showing only. The panel never writes content and takes no rule from it. Without a
/// readable <c>content/</c> the pages show ids.
/// </summary>
public sealed class ContentNames
{
    private readonly IReadOnlyDictionary<string, string> _names;

    private ContentNames(IReadOnlyDictionary<string, string> names, IReadOnlyList<string> items, string? hash, string? problem)
    {
        ItemIds = items;
        _names = names;
        Hash = hash;
        Problem = problem;
    }

    /// <summary>The content's item ids, by name then id, for the item picker; empty without content.</summary>
    public IReadOnlyList<string> ItemIds { get; }

    /// <summary>The loaded content's display-bundle hash, as the server's status reports its own; null when unknown.</summary>
    public string? Hash { get; }

    /// <summary>Why there are no names, in words for staff, or null.</summary>
    public string? Problem { get; }

    /// <summary>No names: every id is shown as it is.</summary>
    public static ContentNames None(string problem) => new(new Dictionary<string, string>(), [], null, problem);

    /// <summary>The names of <paramref name="content"/>.</summary>
    public static ContentNames Of(ContentDatabase content)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add<T>(Registry<T> registry, Func<T, string> name) where T : class, IDefinition
        {
            foreach (var definition in registry.All)
                if (name(definition) is { Length: > 0 } text) names[definition.Id] = text;
        }
        Add(content.Items, d => d.Name);
        Add(content.Monsters, d => d.Name);
        Add(content.Jobs, d => d.Name);
        Add(content.Maps, d => d.Name);
        Add(content.Skills, d => d.Name);
        Add(content.Quests, d => d.Name);
        Add(content.Outfits, d => d.Name);
        Add(content.Statuses, d => d.Name);
        string? hash = null;
        try
        {
            hash = ClientBundleExporter.Hash(content);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            // content that does not make a bundle still has names
        }
        var items = content.Items.All.Select(d => d.Id).OrderBy(id => names.GetValueOrDefault(id, id), StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => id, StringComparer.Ordinal).ToList();
        return new ContentNames(names, items, hash, null);
    }

    /// <summary>
    /// Loads <paramref name="directory"/>, or the <c>content/</c> found above the working directory or the app. <paramref name="detail"/> says for the log what went wrong.
    /// </summary>
    public static ContentNames Load(string? directory, out string? detail)
    {
        detail = null;
        var path = directory ?? Find(Directory.GetCurrentDirectory()) ?? Find(AppContext.BaseDirectory);
        if (path is null)
        {
            detail = "no content/ folder found; set AdminPanel:ContentDirectory";
            return None("The panel has no content folder, so ids are shown without names.");
        }
        var load = ContentLoader.Load(path);
        if (load.Database is { } content) return Of(content);
        detail = $"{path}: {load.Errors.Count} load error(s), first: {load.Errors.FirstOrDefault()}";
        return None("The panel could not load its content folder, so ids are shown without names.");
    }

    private static string? Find(string from)
    {
        for (var folder = new DirectoryInfo(from); folder is not null; folder = folder.Parent)
        {
            var content = Path.Combine(folder.FullName, "content");
            if (File.Exists(Path.Combine(content, ContentLayout.FormulasFile))) return content;
        }
        return null;
    }

    /// <summary>The name of <paramref name="id"/>, or null.</summary>
    public string? NameOf(string? id) => id is not null && _names.TryGetValue(id, out var name) ? name : null;

    /// <summary><c>Name (id)</c>, or the id alone when content has no name for it.</summary>
    public string Describe(string? id) => id is null ? "—" : NameOf(id) is { } name ? $"{name} ({id})" : id;
}
