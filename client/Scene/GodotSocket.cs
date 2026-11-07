using System.Collections.Concurrent;
using Game.Client.Core.Net;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The transport: Godot's <see cref="WebSocketPeer"/> (the socket T-005 tested for TLS) on its own thread. That thread polls, sends what is queued and
/// parses what arrives, so a big message never costs the frame; the main thread only dequeues parsed messages. One thread per connection,
/// created only on connecting: each has its own queues, so a stale connection's events can never reach the session.
/// </summary>
public sealed class GodotSocket : IClientSocket
{
    private const int InboundBufferBytes = 1 << 18;

    private readonly int _pollMs;
    private Connection? _current;

    /// <summary>Creates a socket that polls every <paramref name="pollMs"/> ms.</summary>
    public GodotSocket(int pollMs) => _pollMs = pollMs;

    /// <inheritdoc />
    public void Open(string url)
    {
        _current?.Stop();
        _current = new Connection(url, _pollMs);
    }

    /// <inheritdoc />
    public void Send(byte[] utf8) => _current?.Outbound.Enqueue(utf8);

    /// <inheritdoc />
    public void Close()
    {
        _current?.Stop();
        _current = null;
    }

    /// <inheritdoc />
    public bool TryDequeue(out SocketEvent socketEvent)
    {
        if (_current is { } connection) return connection.Inbound.TryDequeue(out socketEvent);
        socketEvent = default;
        return false;
    }

    /// <summary>Ends the connection and waits for its thread, which must not touch the engine after the engine has shut down.</summary>
    public void Shutdown()
    {
        var connection = _current;
        _current = null;
        connection?.StopAndWait();
    }

    /// <summary>Drops the connection with no close handshake, as a lost signal would: the session sees a socket drop. For the scripted run.</summary>
    public void Kill() => _current?.Kill();

    private sealed class Connection
    {
        private readonly string _url;
        private readonly int _pollMs;
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile bool _kill;

        public Connection(string url, int pollMs)
        {
            _url = url;
            _pollMs = pollMs;
            _thread = new Thread(Run) { IsBackground = true, Name = "socket" };
            _thread.Start();
        }

        public ConcurrentQueue<SocketEvent> Inbound { get; } = new();

        public ConcurrentQueue<byte[]> Outbound { get; } = new();

        public void Stop() => _stop = true;

        public void Kill() => _kill = true;

        public void StopAndWait()
        {
            _stop = true;
            _thread.Join(1000);
        }

        private void Run()
        {
            var peer = new WebSocketPeer { InboundBufferSize = InboundBufferBytes };
            var error = _url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ? peer.ConnectToUrl(_url, TlsOptions.Client()) : peer.ConnectToUrl(_url);
            if (error != Error.Ok)
            {
                Inbound.Enqueue(new SocketEvent(SocketEventKind.Closed));
                return;
            }

            var opened = false;
            while (true)
            {
                if (_stop)
                {
                    SendPending(peer);
                    peer.Close(1000);
                    peer.Poll();
                    return;
                }
                if (_kill)
                {
                    peer.Close(-1); // no close frame: the server sees the connection vanish
                    Inbound.Enqueue(new SocketEvent(SocketEventKind.Closed));
                    return;
                }

                peer.Poll();
                var state = peer.GetReadyState();
                if (state == WebSocketPeer.State.Open)
                {
                    if (!opened)
                    {
                        opened = true;
                        Inbound.Enqueue(new SocketEvent(SocketEventKind.Opened));
                    }
                    SendPending(peer);
                }
                while (peer.GetAvailablePacketCount() > 0) Inbound.Enqueue(InboundParser.Parse(peer.GetPacket()));
                if (state == WebSocketPeer.State.Closed)
                {
                    if (InboundParser.ByeFromClose(peer.GetCloseReason()) is { } bye) Inbound.Enqueue(new SocketEvent(SocketEventKind.Message, bye));
                    Inbound.Enqueue(new SocketEvent(SocketEventKind.Closed));
                    return;
                }
                Thread.Sleep(_pollMs);
            }
        }

        private void SendPending(WebSocketPeer peer)
        {
            while (Outbound.TryDequeue(out var bytes)) peer.Send(bytes, WebSocketPeer.WriteMode.Text);
        }
    }
}
