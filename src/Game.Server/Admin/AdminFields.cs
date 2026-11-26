using System.Globalization;
using System.Text.Json;

namespace Game.Server.Admin;

/// <summary>Reading a command's own fields (T-806). Each returns false for a field that is missing (when required), of the wrong type or out of range: the command answers <c>invalid_request</c>.</summary>
public static class AdminFields
{
    /// <summary>A GUID field (the <c>D</c> form).</summary>
    public static bool Id(JsonElement body, string name, out Guid id)
    {
        id = Guid.Empty;
        return body.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && Guid.TryParseExact(field.GetString(), "D", out id);
    }

    /// <summary>A whole number in <paramref name="min"/>..<paramref name="max"/>; missing is false unless <paramref name="optional"/> (then <paramref name="value"/> is null).</summary>
    public static bool Number(JsonElement body, string name, long min, long max, out long? value, bool optional = false)
    {
        value = null;
        if (!body.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return optional;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var n) || n < min || n > max) return false;
        value = n;
        return true;
    }

    /// <summary>A required whole number in <paramref name="min"/>..<paramref name="max"/>.</summary>
    public static bool Number(JsonElement body, string name, long min, long max, out long value)
    {
        var ok = Number(body, name, min, max, out long? found);
        value = found ?? 0;
        return ok && found is not null;
    }

    /// <summary>A text field, trimmed: 1 to <paramref name="maxLength"/> characters, line breaks and tabs the only control characters.</summary>
    public static bool Text(JsonElement body, string name, int maxLength, out string text)
    {
        text = string.Empty;
        if (!body.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return false;
        var trimmed = field.GetString()!.Trim();
        if (trimmed.Length is 0 || trimmed.Length > maxLength || trimmed.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) return false;
        text = trimmed;
        return true;
    }

    /// <summary>A term of <paramref name="terms"/>.</summary>
    public static bool Term(JsonElement body, string name, Game.Shared.Vocabulary.VocabularySet terms, out string term)
    {
        term = string.Empty;
        if (!body.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || field.GetString() is not { } value || !terms.Contains(value)) return false;
        term = value;
        return true;
    }

    /// <summary>A time for an audit row or an answer: ISO 8601, UTC.</summary>
    public static string? Iso(DateTime? at) => at?.ToString("O", CultureInfo.InvariantCulture);
}
