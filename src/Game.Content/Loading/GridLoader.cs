using System.Text.Json;
using Game.Shared.Definitions;
using Game.Shared.World;

namespace Game.Content.Loading;

/// <summary>Reads the logic grid named by a <see cref="MapDef"/> from <c>content/maps/</c>.</summary>
internal static class GridLoader
{
    /// <summary>
    /// Loads and checks the grid of <paramref name="map"/>. Returns null after adding errors that name the grid file
    /// (or, for a bad <c>grid_file</c> value, the map's YAML at <paramref name="mapAt"/>).
    /// </summary>
    public static MapGrid? Load(string root, MapDef map, SourceLocation mapAt, List<ContentError> errors)
    {
        var name = map.GridFile;
        if (!IsBareFileName(name))
        {
            errors.Add(new ContentError(mapAt.File, mapAt.Line, mapAt.Column, "grid_file", ContentErrorCodes.GridFileName,
                $"grid_file '{name}' must be a bare file name in content/maps/ (no directories), e.g. 'starter_meadow.grid.json'."));
            return null;
        }

        var rel = ContentLayout.GridPath(name);
        void Fail(string code, string message, int line = 0, int column = 0) =>
            errors.Add(new ContentError(rel, line, column, "", code, message));

        var full = Path.Combine(root, ContentLayout.MapsFolder, name);
        if (!File.Exists(full))
        {
            errors.Add(new ContentError(mapAt.File, mapAt.Line, mapAt.Column, "grid_file", ContentErrorCodes.GridMissing,
                $"Grid file '{rel}' for {map.Id} does not exist."));
            return null;
        }

        string text;
        try { text = File.ReadAllText(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(ContentErrorCodes.GridUnreadable, e.Message);
            return null;
        }

        MapGridFile file;
        try { file = GridFileJson.Read(text); }
        catch (JsonException e)
        {
            Fail(ContentErrorCodes.GridMalformed, "Malformed grid: " + e.Message,
                (int)(e.LineNumber ?? -1) + 1, (int)(e.BytePositionInLine ?? -1) + 1);
            return null;
        }

        if (file.FormatVersion != MapGridFile.CurrentFormatVersion)
        {
            Fail(ContentErrorCodes.GridVersion,
                $"Unsupported format_version {file.FormatVersion}; this build reads version {MapGridFile.CurrentFormatVersion}.");
            return null;
        }
        if (file.MapId != map.Id)
        {
            Fail(ContentErrorCodes.GridMapId, $"Grid map_id '{file.MapId}' does not match the map '{map.Id}' that names this file.");
            return null;
        }
        if (!MapGrid.TryCreate(file, out var grid, out var error))
        {
            Fail(ContentErrorCodes.GridMalformed, "Malformed grid: " + error + ".");
            return null;
        }
        return grid;
    }

    private static bool IsBareFileName(string name) =>
        name.Length > 0 && name != "." && name != ".." && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
