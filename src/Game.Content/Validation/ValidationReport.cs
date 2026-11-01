namespace Game.Content.Validation;

/// <summary>Everything a validation run found, ordered by file, line, column, then code.</summary>
public sealed class ValidationReport
{
    /// <summary>Creates a report (sorts the issues).</summary>
    public ValidationReport(IEnumerable<ValidationIssue> issues)
    {
        Issues = issues
            .OrderBy(i => i.File, StringComparer.Ordinal).ThenBy(i => i.Line).ThenBy(i => i.Column)
            .ThenBy(i => i.Code, StringComparer.Ordinal).ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>All issues.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Only the errors.</summary>
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == ValidationSeverity.Error);

    /// <summary>Only the warnings.</summary>
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == ValidationSeverity.Warning);

    /// <summary>True when there are no errors (warnings allowed).</summary>
    public bool IsValid => !Errors.Any();
}
