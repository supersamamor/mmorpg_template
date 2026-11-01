namespace Game.Content.Loading;

/// <summary>
/// One problem found while loading content.
/// </summary>
/// <param name="File">Path relative to the content root, <c>/</c>-separated.</param>
/// <param name="Line">1-based line, or 0 if not applicable.</param>
/// <param name="Column">1-based column, or 0 if not applicable.</param>
/// <param name="Path">Location inside the document, e.g. <c>effects[1].formula</c>; empty for the document root.</param>
/// <param name="Code">Stable machine-readable code, see <see cref="ContentErrorCodes"/>.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record ContentError(string File, int Line, int Column, string Path, string Code, string Message)
{
    /// <summary><c>file(line,col): code: message [path]</c></summary>
    public override string ToString()
    {
        var pos = Line > 0 ? $"({Line},{Column})" : "";
        var path = Path.Length > 0 ? $" [{Path}]" : "";
        return $"{File}{pos}: {Code}: {Message}{path}";
    }
}

/// <summary>Codes used by <see cref="ContentError.Code"/>. T-102 adds its own codes for cross-reference errors.</summary>
public static class ContentErrorCodes
{
    /// <summary>The content root directory does not exist.</summary>
    public const string RootMissing = "root.missing";
    /// <summary>A required root file (formulas, elements, exp tables) is absent.</summary>
    public const string FileMissing = "file.missing";
    /// <summary>A YAML file sits outside the content layout.</summary>
    public const string FileUnexpected = "file.unexpected";
    /// <summary>The file could not be read.</summary>
    public const string FileUnreadable = "file.unreadable";
    /// <summary>Malformed YAML (syntax).</summary>
    public const string YamlSyntax = "yaml.syntax";
    /// <summary>A key that the definition record does not have.</summary>
    public const string UnknownKey = "yaml.unknown_key";
    /// <summary>A <c>type</c> discriminator value that is not in the type table.</summary>
    public const string UnknownType = "yaml.unknown_type";
    /// <summary>A polymorphic object without a <c>type</c> key.</summary>
    public const string MissingType = "yaml.missing_type";
    /// <summary>A value of the wrong shape or type (e.g. text where a number belongs).</summary>
    public const string InvalidValue = "yaml.invalid_value";
    /// <summary>A document with no content.</summary>
    public const string EmptyDocument = "yaml.empty";
    /// <summary><c>MapDef.GridFile</c> is not a bare file name.</summary>
    public const string GridFileName = "grid.file_name";
    /// <summary>The grid file named by a map does not exist.</summary>
    public const string GridMissing = "grid.missing";
    /// <summary>The grid file could not be read.</summary>
    public const string GridUnreadable = "grid.unreadable";
    /// <summary>The grid file is not valid JSON, has the wrong shape, bad base64 or the wrong number of cells.</summary>
    public const string GridMalformed = "grid.malformed";
    /// <summary>The grid file's <c>format_version</c> is not one this build reads.</summary>
    public const string GridVersion = "grid.version";
    /// <summary>The grid file's <c>map_id</c> is not the map that names it.</summary>
    public const string GridMapId = "grid.map_id";
    /// <summary>A single-document root file holds several documents.</summary>
    public const string ExtraDocument = "yaml.extra_document";
    /// <summary>The <c>id</c> is missing or empty.</summary>
    public const string IdMissing = "id.missing";
    /// <summary>The <c>id</c> does not start with the prefix for its type.</summary>
    public const string IdWrongPrefix = "id.wrong_prefix";
    /// <summary>The <c>id</c> has characters outside <c>a-z0-9_</c> after the prefix.</summary>
    public const string IdInvalid = "id.invalid";
    /// <summary>Two definitions share an id.</summary>
    public const string IdDuplicate = "id.duplicate";
}
