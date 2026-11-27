using System.Security.Claims;
using System.Text.Json;
using Game.Shared.Vocabulary;

namespace Game.Admin.Auth;

/// <summary>Turns the provider's claims into a staff member (ARCHITECTURE §12.2): the role from the configured groups, the id from <c>sub</c>, a display name.</summary>
public static class RoleMapping
{
    /// <summary>The longest staff id the server takes (docs/ADMIN_API.md).</summary>
    public const int MaxIdLength = 128;

    /// <summary>The longest display name the server takes.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// The highest role any value of the role claim gives, or null. Values are compared exactly (letter case too). A claim whose value is a JSON array of texts counts as
    /// each of them.
    /// </summary>
    public static string? RoleOf(IEnumerable<Claim> claims, OidcSettings settings)
    {
        var groups = new HashSet<string>(claims.Where(c => c.Type == settings.RoleClaim).SelectMany(c => Values(c.Value)), StringComparer.Ordinal);
        return StaffRoles.All.Ordered.Reverse().FirstOrDefault(role => settings.Roles.TryGetValue(role, out var wanted) && wanted.Any(groups.Contains));
    }

    private static IEnumerable<string> Values(string value)
    {
        if (!value.StartsWith('[')) return [value];
        try
        {
            using var json = JsonDocument.Parse(value);
            return json.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
        catch (JsonException)
        {
            return [value];
        }
    }

    /// <summary>True when <paramref name="id"/> fits the server's staff id: 1 to 128 of <c>A-Z a-z 0-9 . _ - @ : |</c>.</summary>
    public static bool IsStaffId(string? id) =>
        id is { Length: > 0 and <= MaxIdLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' or ':' or '|');

    /// <summary>A display name the server takes: no control characters, trimmed, at most 64; <paramref name="fallback"/> (cut to fit) when nothing is left.</summary>
    public static string DisplayName(string? name, string fallback)
    {
        var text = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length == 0) text = fallback;
        if (text.Length <= MaxNameLength) return text;
        var cut = char.IsHighSurrogate(text[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
        return text[..cut].TrimEnd();
    }
}
