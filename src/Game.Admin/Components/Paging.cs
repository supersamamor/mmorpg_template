namespace Game.Admin.Components;

/// <summary>Links of a search's pages.</summary>
public static class Paging
{
    /// <summary><paramref name="path"/> with the query's set parts.</summary>
    public static string Link(string path, params (string Name, string? Value)[] query)
    {
        var set = query.Where(q => q.Value is not null).Select(q => q.Name + "=" + Uri.EscapeDataString(q.Value!)).ToList();
        return set.Count == 0 ? path : path + "?" + string.Join('&', set);
    }
}
