using System.Collections.ObjectModel;
using Game.Shared.Definitions;

namespace Game.Content.Registry;

/// <summary>
/// Immutable id → definition map. Built once (by the loader) and never changed afterwards.
/// <see cref="All"/> is ordered by id (ordinal) so iteration is deterministic.
/// </summary>
public sealed class Registry<T> : IRegistry where T : class, IDefinition
{
    private readonly Dictionary<string, T> _byId;

    /// <summary>Builds a registry. Throws <see cref="ArgumentException"/> on a duplicate id (the loader reports duplicates as errors before this point).</summary>
    public Registry(IEnumerable<T> definitions)
    {
        _byId = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var def in definitions)
        {
            if (!_byId.TryAdd(def.Id, def))
                throw new ArgumentException($"Duplicate {typeof(T).Name} id '{def.Id}'.", nameof(definitions));
        }
        All = new ReadOnlyCollection<T>(_byId.Values.OrderBy(d => d.Id, StringComparer.Ordinal).ToList());
    }

    /// <summary>An empty registry.</summary>
    public static Registry<T> Empty() => new(Array.Empty<T>());

    /// <summary>Every definition, ordered by id.</summary>
    public IReadOnlyList<T> All { get; }

    /// <summary>Number of definitions.</summary>
    public int Count => _byId.Count;

    /// <summary>True if a definition with this id exists.</summary>
    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>Returns the definition, or false if the id is unknown.</summary>
    public bool TryGet(string id, out T def)
    {
        if (_byId.TryGetValue(id, out var found)) { def = found; return true; }
        def = null!;
        return false;
    }

    /// <summary>Returns the definition. Throws <see cref="KeyNotFoundException"/> naming the type, the id, and similar ids.</summary>
    public T Get(string id)
    {
        if (_byId.TryGetValue(id, out var def)) return def;
        var tail = id.Substring(id.LastIndexOf('.') + 1);
        var near = All.Select(d => d.Id)
            .Where(k => k.EndsWith(tail, StringComparison.OrdinalIgnoreCase) || k.Contains(tail, StringComparison.OrdinalIgnoreCase))
            .Take(5).ToList();
        var hint = near.Count > 0 ? " Similar ids: " + string.Join(", ", near) + "." : "";
        throw new KeyNotFoundException($"No {typeof(T).Name} with id '{id}' ({Count} loaded).{hint}");
    }

    Type IRegistry.DefinitionType => typeof(T);

    IEnumerable<IDefinition> IRegistry.Definitions => All;
}
