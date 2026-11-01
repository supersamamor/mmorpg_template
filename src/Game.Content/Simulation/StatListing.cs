using Game.Shared.Stats;

namespace Game.Content.Simulation;

/// <summary>Reads every final stat of a sheet in <see cref="StatIndex"/> order (primary stats, then derived).</summary>
public static class StatListing
{
    public static IReadOnlyList<KeyValuePair<string, double>> All(StatSheet sheet)
    {
        var list = new List<KeyValuePair<string, double>>(StatIndex.Count);
        for (var i = 0; i < StatIndex.Count; i++) list.Add(new(StatIndex.NameOf(i), sheet.Get(i)));
        return list;
    }
}
