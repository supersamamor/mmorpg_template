namespace Game.Content.Loading;

/// <summary>A position in a content file. <see cref="File"/> is relative to the content root, with <c>/</c> separators. Line and column are 1-based; 0 means unknown.</summary>
public readonly record struct SourceLocation(string File, int Line, int Column = 0)
{
    /// <inheritdoc />
    public override string ToString() => Line > 0 ? $"{File}:{Line}" : File;
}
