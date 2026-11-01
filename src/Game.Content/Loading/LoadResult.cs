using Game.Content.Registry;

namespace Game.Content.Loading;

/// <summary>
/// Outcome of <see cref="ContentLoader.Load"/>. <see cref="Database"/> is non-null if and only if
/// <see cref="Errors"/> is empty: a content set with parse/shape errors is never handed out half-built.
/// </summary>
public sealed class LoadResult
{
    /// <summary>Creates a result.</summary>
    public LoadResult(ContentDatabase? database, IReadOnlyList<ContentError> errors)
    {
        Database = database;
        Errors = errors;
    }

    /// <summary>The loaded content, or null when there are errors.</summary>
    public ContentDatabase? Database { get; }

    /// <summary>All errors found, ordered by file then position. Loading does not stop at the first one.</summary>
    public IReadOnlyList<ContentError> Errors { get; }

    /// <summary>True when loading produced no errors.</summary>
    public bool Success => Errors.Count == 0;
}
