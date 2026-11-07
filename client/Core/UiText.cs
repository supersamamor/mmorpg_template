using System.Globalization;
using Game.Client.Core.Net;
using Game.Shared.Vocabulary;

namespace Game.Client.Core;

/// <summary>
/// The client's own words: the only place UI text is written, so localisation (T-211) has one table to replace. Names of game things never
/// come from here; they come from the bundle by <c>display_key</c>.
/// </summary>
public static class UiText
{
    /// <summary>The login button.</summary>
    public const string Play = "Play";

    /// <summary>The logout button.</summary>
    public const string Logout = "Log out";

    /// <summary>The death screen's title.</summary>
    public const string YouDied = "You died";

    /// <summary>The respawn button.</summary>
    public const string Respawn = "Respawn";

    /// <summary>The button that leaves a stop for the login screen.</summary>
    public const string BackToLogin = "Back to login";

    /// <summary>The update button.</summary>
    public const string GoToStore = "Get the update";

    /// <summary>The screen when the packed bundle has another format.</summary>
    public static string BundleRefused(int version) =>
        "This build cannot read its game data (format " + version.ToString(CultureInfo.InvariantCulture) + "). Please update the app.";

    /// <summary>The screen when the packed bundle cannot be read at all.</summary>
    public const string BundleCorrupt = "The game data in this build is damaged. Please reinstall the app.";

    /// <summary>The screen when the server names a map the bundle does not have.</summary>
    public const string MapMissing = "This build does not know the map you are on. Please update the app.";

    /// <summary>The screen when a release build is pointed at a plain <c>ws://</c> server (ARCHITECTURE §10).</summary>
    public const string CleartextRefused = "This build only connects over a secure connection (wss://). The server address in this build is not secure. Please update the app.";

    /// <summary>The screen when the server address is not a <c>ws://</c> or <c>wss://</c> URL.</summary>
    public const string ServerAddressInvalid = "The server address in this build is not valid. Please update the app.";

    /// <summary>The refusal screen's text for a verdict that is not <see cref="TransportVerdict.Allowed"/>.</summary>
    public static string TransportRefused(TransportVerdict verdict) => verdict == TransportVerdict.CleartextRefused ? CleartextRefused : ServerAddressInvalid;

    /// <summary>The line for a connection notice.</summary>
    public static string Notice(ConnectionNotice notice)
    {
        switch (notice)
        {
            case ConnectionNotice.Reconnecting: return "Connection lost. Reconnecting...";
            case ConnectionNotice.SessionExpired: return "You were away too long. Please log in again.";
            case ConnectionNotice.ResumeFailed: return "Your session has ended. Please log in again.";
            case ConnectionNotice.ResumeRateLimited: return "Too many reconnect attempts. Please log in again.";
            case ConnectionNotice.LoginRefused: return "The server refused this login.";
            case ConnectionNotice.ServerFull: return "The server is full. Please try again soon.";
            case ConnectionNotice.ConnectionFailed: return "Could not reach the server.";
            case ConnectionNotice.UpdateRequired: return "A new version is required to play. Please update the app.";
            case ConnectionNotice.Replaced: return "You are playing on another connection.";
            case ConnectionNotice.LoggedOut: return "You have logged out.";
            case ConnectionNotice.ServerShutdown: return "The server is restarting. Trying again soon...";
            default: return string.Empty;
        }
    }

    /// <summary>The line while a connection is being made, with no notice.</summary>
    public const string Connecting = "Connecting...";

    /// <summary>The flash when this client hits something.</summary>
    public static string YouHit(string target, int amount, string result)
    {
        if (result == HitResults.Miss) return "You miss " + target;
        var verb = result == HitResults.Crit ? "You crit " : "You hit ";
        return verb + target + " for " + amount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The flash when something hits this client.</summary>
    public static string HitYou(string attacker, int amount, string result)
    {
        if (result == HitResults.Miss) return attacker + " misses you";
        var verb = result == HitResults.Crit ? " crits you for " : " hits you for ";
        return attacker + verb + amount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The flash when a drop this client may take appears.</summary>
    public static string Dropped(string item, int quantity) =>
        quantity > 1 ? "Dropped: " + item + " x" + quantity.ToString(CultureInfo.InvariantCulture) : "Dropped: " + item;

    /// <summary>The flash for a level reached.</summary>
    public static string LevelUp(int level) => "Level " + level.ToString(CultureInfo.InvariantCulture) + "!";

    /// <summary>The flash for a job level reached.</summary>
    public static string JobLevelUp(int level) => "Job level " + level.ToString(CultureInfo.InvariantCulture) + "!";

    /// <summary>The HUD's HP line.</summary>
    public static string Hp(int hp, int max) => "HP " + hp.ToString(CultureInfo.InvariantCulture) + " / " + max.ToString(CultureInfo.InvariantCulture);

    /// <summary>The HUD's SP line.</summary>
    public static string Sp(int sp, int max) => "SP " + sp.ToString(CultureInfo.InvariantCulture) + " / " + max.ToString(CultureInfo.InvariantCulture);

    /// <summary>The HUD's EXP line (<paramref name="next"/> 0 means the cap).</summary>
    public static string Exp(int level, long exp, long next) =>
        "Lv " + level.ToString(CultureInfo.InvariantCulture) + "  EXP " + exp.ToString(CultureInfo.InvariantCulture) +
        (next > 0 ? " / " + next.ToString(CultureInfo.InvariantCulture) : string.Empty);
}
