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
}
