using System;
using System.Collections.Generic;

namespace Game.Client.Core.Theme;

/// <summary>
/// Which worn-visual models to load before they are needed (T-405, ARCHITECTURE §7.5, ADR-002 row 182): the pieces worn by characters on screen first, then the equipment
/// in the player's own bag, so putting a bag item on finds its meshes ready instead of stalling the frame that loads them. Pure: it names paths, the store loads them.
/// A path appears once, however many items or characters use it; and at most <c>limit</c> paths are named, worn first, so the memory it can hold is bounded.
/// It resolves exactly as <see cref="EquipmentCatalog"/> does, so the paths are the ones worn pieces ask the model store for: one shared cache.
/// </summary>
public sealed class GearPreload
{
    private readonly ThemeCatalog _catalog;
    private readonly Func<string, string?> _visualKeyOf;
    private readonly int _limit;

    /// <summary>Creates a selector.</summary>
    /// <param name="catalog">The theme chain.</param>
    /// <param name="visualKeyOf">An item id's worn visual asset key from the display bundle, or null.</param>
    /// <param name="limit">Most paths to name (at least 1).</param>
    public GearPreload(ThemeCatalog catalog, Func<string, string?> visualKeyOf, int limit)
    {
        _catalog = catalog;
        _visualKeyOf = visualKeyOf;
        _limit = Math.Max(1, limit);
    }

    /// <summary>
    /// The project-relative model paths to load, worn items before bag items, each once, at most the limit. Items with no worn visual, a key no theme has, or a manifest
    /// entry the worn-piece resolver would reject (neither or both of <c>attach</c> and <c>skinned</c>) are left out; they draw the missing model, which loads anyway.
    /// </summary>
    public IReadOnlyList<string> Select(IEnumerable<string> worn, IEnumerable<string> bag)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Add(worn, paths, seen);
        Add(bag, paths, seen);
        return paths;
    }

    private void Add(IEnumerable<string> items, List<string> paths, HashSet<string> seen)
    {
        foreach (var item in items)
        {
            if (paths.Count >= _limit) return;
            if (_visualKeyOf(item) is not { } key || !_catalog.TryFind(key, out var path, out var asset)) continue;
            if ((asset.Attach is null) == (asset.Skinned is null)) continue;
            if (seen.Add(path)) paths.Add(path);
        }
    }
}
