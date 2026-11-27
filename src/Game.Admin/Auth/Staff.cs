using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Game.Shared.Vocabulary;

namespace Game.Admin.Auth;

/// <summary>
/// The signed-in staff member. It comes only from the sign-in (the protected cookie), never from a field, header or address the browser sends with a request.
/// </summary>
/// <param name="Id">The provider's subject; the server's <c>X-Staff-Id</c>.</param>
/// <param name="Name">The display name; <c>X-Staff-Name</c>.</param>
/// <param name="Role">A "Staff roles" term; <c>X-Staff-Role</c>.</param>
/// <param name="SessionId">This sign-in, so signing out can end it.</param>
/// <param name="ExpiresAt">When this sign-in ends.</param>
public sealed record Staff(string Id, string Name, string Role, string SessionId, DateTimeOffset ExpiresAt)
{
    /// <summary>True when the role is at least <paramref name="lowest"/>. A lowest role the panel does not know is never reached: its button stays grey.</summary>
    public bool Reaches(string lowest) => StaffRoles.All.Contains(lowest) && StaffRoles.AtLeast(Role, lowest);

    /// <summary>The id and role only: names stay out of logs.</summary>
    public override string ToString() => $"staff {Id} ({Role})";
}

/// <summary>
/// The panel's sign-ins: what goes into the cookie, and whether one still holds. A signed-out sign-in is remembered until it would have ended, so a copy of its cookie
/// and a page still open stop working too (in this process: after a restart only the expiry holds).
/// </summary>
public sealed class StaffSessions(TimeProvider time, PanelSettings settings)
{
    private const string IdClaim = "panel:staff_id";
    private const string NameClaim = "panel:staff_name";
    private const string RoleClaim = "panel:staff_role";
    private const string SessionClaim = "panel:session";
    private const string ExpiresClaim = "panel:expires";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _ended = new(StringComparer.Ordinal);

    /// <summary>When a sign-in made now ends.</summary>
    public DateTimeOffset NewExpiry() => time.GetUtcNow().AddMinutes(settings.SessionMinutes);

    /// <summary>A principal that holds the staff member and nothing else of what the provider sent.</summary>
    public ClaimsPrincipal Principal(string id, string name, string role, DateTimeOffset expires, string scheme) => new(new ClaimsIdentity(
    [
        new Claim(IdClaim, id), new Claim(NameClaim, name), new Claim(RoleClaim, role), new Claim(SessionClaim, Guid.NewGuid().ToString("N")),
        new Claim(ExpiresClaim, expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
    ], scheme, NameClaim, RoleClaim));

    /// <summary>The staff member of <paramref name="principal"/>, or null when it is none, has ended or was signed out.</summary>
    public Staff? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var id = principal.FindFirst(IdClaim)?.Value;
        var name = principal.FindFirst(NameClaim)?.Value;
        var role = principal.FindFirst(RoleClaim)?.Value;
        var session = principal.FindFirst(SessionClaim)?.Value;
        if (!RoleMapping.IsStaffId(id) || name is null || role is null || session is null || !StaffRoles.All.Contains(role)) return null;
        if (!long.TryParse(principal.FindFirst(ExpiresClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        var expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (expires <= time.GetUtcNow() || _ended.ContainsKey(session)) return null;
        return new Staff(id!, name, role, session, expires);
    }

    /// <summary>Ends a sign-in.</summary>
    public void End(Staff staff)
    {
        var now = time.GetUtcNow();
        foreach (var old in _ended.Where(e => e.Value <= now).ToList()) _ended.TryRemove(old.Key, out _);
        _ended[staff.SessionId] = staff.ExpiresAt;
    }
}
