using System.Globalization;
using Game.Admin.Api;
using Game.Admin.Auth;
using Game.Admin.ContentView;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Game.Admin.Components;

/// <summary>A page for signed-in staff: who they are (from the sign-in alone), the server and the names.</summary>
public abstract class PanelPage : ComponentBase
{
    /// <summary>The sign-in's state.</summary>
    [CascadingParameter] public Task<AuthenticationState> Authentication { get; set; } = default!;

    /// <summary>The server.</summary>
    [Inject] public AdminApiClient Api { get; set; } = default!;

    /// <summary>Names from content.</summary>
    [Inject] public ContentNames Names { get; set; } = default!;

    /// <summary>The sign-ins.</summary>
    [Inject] public StaffSessions Sessions { get; set; } = default!;

    /// <summary>The signed-in staff member now, or null once the sign-in has ended.</summary>
    protected async Task<Staff?> StaffAsync() => Sessions.Resolve((await Authentication).User);

    /// <summary>A time as the panel shows it: UTC.</summary>
    public static string Time(DateTime? at) => at is { } t ? t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "—";

    /// <summary>A search box's value, or null when empty.</summary>
    protected static string? Clean(string? value) => value?.Trim() is { Length: > 0 } text ? text : null;
}
