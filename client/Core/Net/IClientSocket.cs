using System;
using Game.Server.Net;
using Game.Shared.Protocol;

namespace Game.Client.Core.Net;

/// <summary>What a socket reports.</summary>
public enum SocketEventKind
{
    /// <summary>The connection is open.</summary>
    Opened,

    /// <summary>A server message arrived and parsed.</summary>
    Message,

    /// <summary>A message of a type this build does not know: ignored, so a newer server can add types.</summary>
    UnknownType,

    /// <summary>A message that could not be read: the view can no longer be trusted, so the connection is treated as dropped.</summary>
    Malformed,

    /// <summary>The connection closed or failed.</summary>
    Closed,
}

/// <summary>One thing that happened on a socket.</summary>
public readonly struct SocketEvent
{
    /// <summary>Creates an event.</summary>
    public SocketEvent(SocketEventKind kind, ServerMessage? message = null)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>What happened.</summary>
    public SocketEventKind Kind { get; }

    /// <summary>The message, for <see cref="SocketEventKind.Message"/>.</summary>
    public ServerMessage? Message { get; }
}

/// <summary>
/// The transport seam. An implementation does its I/O and parsing (<see cref="InboundParser"/>) off the main thread; every member here is
/// called from the main thread. Each <see cref="Open"/> starts a new connection, and events of the previous one are never returned.
/// </summary>
public interface IClientSocket
{
    /// <summary>Starts connecting to <paramref name="url"/>, closing any current connection.</summary>
    void Open(string url);

    /// <summary>Queues one text message. Ignored when there is no connection.</summary>
    void Send(byte[] utf8);

    /// <summary>Closes the connection. No more of its events are returned.</summary>
    void Close();

    /// <summary>The next event, if any.</summary>
    bool TryDequeue(out SocketEvent socketEvent);
}

/// <summary>Turns one frame into an event. Runs on the socket thread and touches nothing shared.</summary>
public static class InboundParser
{
    /// <summary>Parses a server frame with the protocol mapping the server itself uses.</summary>
    public static SocketEvent Parse(ReadOnlySpan<byte> utf8)
    {
        var read = ProtocolJson.ReadServer(utf8);
        if (read.Message is { } message) return new SocketEvent(SocketEventKind.Message, message);
        return new SocketEvent(read.Error == ByeReasons.UnknownType ? SocketEventKind.UnknownType : SocketEventKind.Malformed);
    }

    /// <summary>
    /// The <c>bye</c> a close frame stands for, or null. The server puts the reason in the close frame too. It matters: a socket may hand over no
    /// packet that arrived together with the close frame (Godot's WebSocketPeer clears them), and then the frame is the only trace of the <c>bye</c>.
    /// </summary>
    public static ByeMessage? ByeFromClose(string? closeReason) =>
        closeReason is not null && ByeReasons.All.Contains(closeReason) ? new ByeMessage(closeReason) : null;
}
