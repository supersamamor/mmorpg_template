namespace Game.Content.Maps;

/// <summary>
/// The asset key of a map's minimap picture (T-812, ARCHITECTURE §5.3): <c>minimap.</c> and the map's id, so <c>map.starter_meadow</c>'s picture is
/// <c>minimap.map.starter_meadow</c>. The map export draws the picture from the baked grid; the theme's manifest maps the key to the file, as for every asset (hard rule 3).
/// The key follows from the id, so the display bundle carries nothing new. Compiled into the client too, so both sides name the same key.
/// </summary>
public static class MinimapKeys
{
    /// <summary>The prefix of every minimap key (VOCABULARY "Asset key prefixes").</summary>
    public const string Prefix = "minimap.";

    /// <summary>The file the map export writes beside a map's scenes in the theme, after the map's name: <c>maps/&lt;name&gt;.minimap.png</c>, which the manifest line names.</summary>
    public const string FileSuffix = ".minimap.png";

    /// <summary>The key of the picture of the map with this id.</summary>
    public static string For(string mapId) => Prefix + mapId;
}
