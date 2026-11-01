using System.Text;
using System.Text.Json;
using Game.Content.Loading;

namespace Game.Content.Validation;

/// <summary>Renders load errors and validation reports for the <c>validate</c> command.</summary>
public static class ValidationReportFormatter
{
    /// <summary>Converts loader errors to issues so both kinds print the same way.</summary>
    public static IReadOnlyList<ValidationIssue> FromLoadErrors(IEnumerable<ContentError> errors) =>
        errors.Select(e => new ValidationIssue(ValidationSeverity.Error, e.File, e.Line, e.Column, e.Path, e.Code, e.Message)).ToList();

    /// <summary>
    /// Text: issues grouped by file, each line <c>file:line:col: code: message [path]</c>; warning codes start with
    /// <c>warn.</c>. Ends with a one-line summary.
    /// </summary>
    public static string ToText(IReadOnlyList<ValidationIssue> issues)
    {
        var sb = new StringBuilder();
        foreach (var group in issues.GroupBy(i => i.File))
        {
            sb.AppendLine(group.Key.Length > 0 ? group.Key : "(general)");
            foreach (var issue in group) sb.AppendLine("  " + issue);
        }
        var errors = issues.Count(i => i.Severity == ValidationSeverity.Error);
        var warnings = issues.Count - errors;
        sb.Append(errors == 0 ? "OK: " : "FAILED: ").Append($"{errors} error(s), {warnings} warning(s)");
        return sb.ToString();
    }

    /// <summary>JSON: <c>{ "valid": bool, "errors": n, "warnings": n, "issues": [ ... ] }</c>.</summary>
    public static string ToJson(IReadOnlyList<ValidationIssue> issues)
    {
        var errors = issues.Count(i => i.Severity == ValidationSeverity.Error);
        var payload = new
        {
            valid = errors == 0,
            errors,
            warnings = issues.Count - errors,
            issues = issues.Select(i => new
            {
                severity = i.Severity == ValidationSeverity.Error ? "error" : "warning",
                file = i.File, line = i.Line, column = i.Column, path = i.Path, code = i.Code, message = i.Message,
            }),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
