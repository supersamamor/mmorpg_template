using System;

namespace Game.Client.Core.Net;

/// <summary>Whether a server URL may be used.</summary>
public enum TransportVerdict
{
    /// <summary>Allowed.</summary>
    Allowed,

    /// <summary>Plain <c>ws://</c> in a build that must not use it (ARCHITECTURE §10).</summary>
    CleartextRefused,

    /// <summary>Not a <c>ws://</c> or <c>wss://</c> URL with a host.</summary>
    Invalid,
}

/// <summary>
/// The rule "TLS everywhere except debug builds" (ARCHITECTURE §10), as a pure function. The OS does not enforce it for Godot's WebSocket (native sockets
/// are outside Android's network-security config and iOS App Transport Security, MOBILE_BUILD §7), so the client does, and this is the one place.
/// The caller passes whether the running build is a debug build; nothing read from settings or the command line can change that.
/// </summary>
public static class TransportPolicy
{
    /// <summary>Judges <paramref name="url"/>. <c>wss://</c> is always fine; <c>ws://</c> only when <paramref name="allowCleartext"/> (a debug build).</summary>
    public static TransportVerdict Check(string? url, bool allowCleartext)
    {
        if (string.IsNullOrWhiteSpace(url)) return TransportVerdict.Invalid;
        var trimmed = url!.Trim();
        var secure = trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
        var plain = trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);
        if (!secure && !plain) return TransportVerdict.Invalid;
        var rest = trimmed.Substring(secure ? 6 : 5);
        if (rest.Length == 0 || rest[0] == '/' || rest[0] == ':' || rest[0] == '?' || rest[0] == '#' || rest[0] == '@' || char.IsWhiteSpace(rest[0])) return TransportVerdict.Invalid;
        if (secure) return TransportVerdict.Allowed;
        return allowCleartext ? TransportVerdict.Allowed : TransportVerdict.CleartextRefused;
    }

    /// <summary>
    /// Where the account endpoints are (T-701): the same host and port as the game socket, <c>https://</c> for <c>wss://</c> and <c>http://</c> only for a
    /// <c>ws://</c> address this build may use. Null when <see cref="Check"/> does not allow the address, so credentials are never sent where the game would not go.
    /// </summary>
    public static string? AccountBase(string? serverUrl, bool allowCleartext)
    {
        if (Check(serverUrl, allowCleartext) != TransportVerdict.Allowed) return null;
        var url = serverUrl!.Trim();
        var secure = url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
        var rest = url.Substring(secure ? 6 : 5);
        var end = rest.IndexOfAny(new[] { '/', '?', '#' });
        var authority = end < 0 ? rest : rest.Substring(0, end);
        // user@host would send the credentials to the part after the @, which is not what the address looks like
        return authority.IndexOf('@') >= 0 ? null : (secure ? "https://" : "http://") + authority;
    }
}
