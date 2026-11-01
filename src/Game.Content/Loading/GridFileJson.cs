using System.Text;
using System.Text.Json;
using Game.Shared.World;

namespace Game.Content.Loading;

/// <summary>
/// JSON reading and writing of <see cref="MapGridFile"/> (<c>snake_case</c> keys, unknown keys are errors).
/// The writer is deterministic (indented, LF, trailing newline) so regenerated grids do not produce diffs.
/// </summary>
public static class GridFileJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Parses grid JSON. Throws <see cref="JsonException"/> (with line and position) on bad syntax, unknown keys or wrong value types.</summary>
    public static MapGridFile Read(string json) =>
        JsonSerializer.Deserialize<MapGridFile>(json, Options) ?? throw new JsonException("The grid file is empty (null).");

    /// <summary>Serializes a grid file as stable text.</summary>
    public static string Write(MapGridFile file) => JsonSerializer.Serialize(file, Options) + "\n";

    /// <summary>Encodes one flag byte per cell as the <c>cells</c> string.</summary>
    public static string EncodeCells(byte[] flags) => Convert.ToBase64String(flags);

    internal static readonly UTF8Encoding Utf8 = new(false);
}
