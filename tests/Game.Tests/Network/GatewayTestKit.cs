using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Game.Content.ClientExport;
using Game.Content.Registry;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.World;
using Game.Tests.Server;
using Game.Tests.World;

namespace Game.Tests.Network;

/// <summary>
/// The real server stack in-process: the sample content, the real pipeline, instances ticking at 10 Hz on their own threads, and the gateway
/// on a random loopback port. The gateway's clock is a <see cref="ManualClock"/>, so timeouts and the rate limit only move when a test says so.
/// </summary>
public sealed class GatewayRig : IAsyncDisposable
{
    public const string CertificatePassword = "test-only";

    public readonly ContentDatabase Content = TestGrids.SampleContent;
    public readonly string ContentHash;
    public readonly ManualClock Clock = new();
    public readonly ListLog Log = new();
    public readonly GameWorld World;
    public readonly Gateway Gateway;
    public readonly NewCharacters Start;
    private readonly List<InstanceRunner> _runners;
    private readonly List<TestClient> _clients = new();
    private GatewayHost? _host;
    private string? _certificateFile;

    private GatewayRig(GatewayOptions options)
    {
        var clock = new SystemClock();
        ContentHash = ClientBundleExporter.Hash(Content);
        World = new GameWorld(Content.Grids.Values, ServerPipeline.Create(Content), 1, clock, Log);
        Start = new NewCharacters(Content);
        Gateway = new Gateway(World, Start, options, Clock, Log, ContentHash);
        _runners = World.Instances.Select(i => new InstanceRunner(i, clock, Log)).ToList();
    }

    public GatewayHost Host => _host ?? throw new InvalidOperationException("Not started.");

    public Uri Endpoint => Host.Endpoint;

    /// <summary>Starts a rig. <paramref name="tls"/> generates a throw-away self-signed certificate (never stored in the repo).</summary>
    public static async Task<GatewayRig> StartAsync(Func<GatewayOptions, GatewayOptions>? configure = null, bool development = true, bool tls = false)
    {
        var options = new GatewayOptions { Development = development, BindAddress = "127.0.0.1", Port = 0, SweepIntervalMs = 10, CloseTimeoutMs = 1_000 };
        string? certificate = null;
        if (tls)
        {
            certificate = Path.Combine(Path.GetTempPath(), $"game-tests-{Guid.NewGuid():N}.pfx");
            WriteSelfSignedCertificate(certificate);
            options = options with { CertificatePath = certificate };
        }
        if (configure is not null) options = configure(options);
        var rig = new GatewayRig(options) { _certificateFile = certificate };
        rig._host = await GatewayHost.StartAsync(rig.Gateway, name => name == options.CertificatePasswordEnv ? CertificatePassword : null);
        foreach (var runner in rig._runners) runner.Start();
        rig.Gateway.Start();
        return rig;
    }

    public static void WriteSelfSignedCertificate(string path)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, CertificatePassword));
    }

    /// <summary>Opens a connection (no hello yet).</summary>
    public async Task<TestClient> ConnectAsync()
    {
        var client = await TestClient.ConnectAsync(Endpoint);
        lock (_clients) _clients.Add(client);
        return client;
    }

    /// <summary>Connects, says hello and waits for the welcome and the first snapshot.</summary>
    public async Task<TestClient> JoinAsync(string name = "tester")
    {
        var client = await ConnectAsync();
        await client.SendAsync(new HelloMessage(ProtocolVersion.Current, name, ContentHash));
        await client.WaitForAsync<WelcomeMessage>();
        await client.WaitForAsync<SnapshotMessage>();
        return client;
    }

    /// <summary>The session whose character is <paramref name="id"/>.</summary>
    public ClientSession SessionOf(long id) =>
        Poll.Until(() => Gateway.Sessions.FirstOrDefault(s => s.Entity?.Value == id), s => s is not null, "session of #" + id)!;

    /// <summary>A walkable cell near the start: the first of <paramref name="offsets"/> (dx, dy) that is walkable on the start map.</summary>
    public GridCell NearStart(params (int Dx, int Dy)[] offsets)
    {
        var grid = Content.GetGrid(Start.MapId);
        foreach (var (dx, dy) in offsets)
        {
            var cell = new GridCell(Start.Cell.X + dx, Start.Cell.Y + dy);
            if (grid.IsWalkable(cell.X, cell.Y)) return cell;
        }
        throw new InvalidOperationException("No walkable cell among the offsets.");
    }

    public async ValueTask DisposeAsync()
    {
        List<TestClient> clients;
        lock (_clients) clients = _clients.ToList();
        foreach (var client in clients) client.Dispose();
        await Gateway.ShutdownAsync();
        await Host.StopAsync();
        await Host.DisposeAsync();
        foreach (var runner in _runners) runner.Stop();
        if (_certificateFile is not null) File.Delete(_certificateFile);
    }
}

/// <summary>
/// A protocol client on <see cref="ClientWebSocket"/>: collects every server message (decoded with <see cref="ProtocolJson"/>, as the Godot
/// client will), keeps a view model by applying snapshots and deltas, and answers the server's close frame.
/// </summary>
public sealed class TestClient : IDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly List<ServerMessage> _messages = new();
    private readonly Dictionary<long, EntityRecord> _view = new();
    private readonly Task _receive;
    private long _bytes;
    private long _frames;
    private long _wire;

    private TestClient(ClientWebSocket socket)
    {
        _socket = socket;
        _receive = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<TestClient> ConnectAsync(Uri endpoint)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true; // the rig's throw-away self-signed certificate
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(endpoint, timeout.Token);
        return new TestClient(socket);
    }

    /// <summary>Every message so far.</summary>
    public List<ServerMessage> Messages
    {
        get { lock (_messages) return _messages.ToList(); }
    }

    /// <summary>The map of the last snapshot.</summary>
    public string? Map { get; private set; }

    /// <summary>Payload bytes received.</summary>
    public long BytesReceived => Interlocked.Read(ref _bytes);

    /// <summary>Bytes on the wire for those messages: payload plus the WebSocket frame header (server frames are unmasked: 2 bytes up to
    /// 125, 4 up to 65535). TCP/IP and TLS overhead are not included.</summary>
    public long WireBytesReceived => Interlocked.Read(ref _wire);

    /// <summary>Messages received (each one WebSocket message).</summary>
    public long FramesReceived => Interlocked.Read(ref _frames);

    /// <summary>The close status the server sent, once the connection closed.</summary>
    public WebSocketCloseStatus? CloseStatus { get; private set; }

    /// <summary>True once the receive loop ended (closed or dropped).</summary>
    public bool Closed => _receive.IsCompleted;

    public long Id => Messages.OfType<WelcomeMessage>().First().Id;

    /// <summary>A copy of one entity's current record in this client's view, or null.</summary>
    public EntityRecord? Seen(long id)
    {
        lock (_messages) return _view.TryGetValue(id, out var r) ? r : null;
    }

    public IReadOnlyCollection<long> SeenIds
    {
        get { lock (_messages) return _view.Keys.ToList(); }
    }

    public Task SendAsync(ClientMessage message) => SendRawAsync(ProtocolJson.ToBytes(message));

    public Task SendRawAsync(string text) => SendRawAsync(Encoding.UTF8.GetBytes(text));

    public async Task SendRawAsync(byte[] bytes, WebSocketMessageType type = WebSocketMessageType.Text)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _socket.SendAsync(bytes, type, true, timeout.Token);
    }

    /// <summary>Waits for the first message of type <typeparamref name="T"/> matching <paramref name="match"/> (any received so far counts).</summary>
    public Task<T> WaitForAsync<T>(Func<T, bool>? match = null, int timeoutMs = 10_000) where T : ServerMessage =>
        Poll.UntilAsync(() => Messages.OfType<T>().FirstOrDefault(m => match?.Invoke(m) ?? true), m => m is not null, typeof(T).Name, timeoutMs)!;

    public async Task<string> WaitForByeAsync()
    {
        var bye = await WaitForAsync<ByeMessage>();
        await WaitClosedAsync();
        return bye.Reason;
    }

    public Task WaitClosedAsync(int timeoutMs = 10_000) => Poll.UntilAsync(() => Closed, c => c, "connection closed", timeoutMs);

    /// <summary>Closes from the client side (a clean close handshake).</summary>
    public async Task CloseAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
        }
        catch (WebSocketException)
        {
        }
        await WaitClosedAsync();
    }

    public void Dispose()
    {
        _socket.Abort();
        _socket.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1 << 20];
        try
        {
            while (true)
            {
                var count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                    count += result.Count;
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    CloseStatus = _socket.CloseStatus;
                    if (_socket.State == WebSocketState.CloseReceived)
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                Interlocked.Add(ref _bytes, count);
                Interlocked.Increment(ref _frames);
                Interlocked.Add(ref _wire, count + (count < 126 ? 2 : count < 65536 ? 4 : 10));
                var read = ProtocolJson.ReadServer(buffer.AsSpan(0, count));
                if (read.Message is not { } message) throw new InvalidDataException("Undecodable server message: " + Encoding.UTF8.GetString(buffer, 0, count));
                lock (_messages)
                {
                    _messages.Add(message);
                    Apply(message);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private void Apply(ServerMessage message)
    {
        switch (message)
        {
            case SnapshotMessage s:
                Map = s.MapId;
                _view.Clear();
                foreach (var e in s.Entities) _view[e.Id] = e;
                break;
            case DeltaMessage d:
                foreach (var e in d.Appeared) _view[e.Id] = e;
                foreach (var c in d.Changed)
                {
                    var r = _view[c.Id];
                    if ((c.Fields & ChangedFields.Cell) != 0) r = r with { Cell = c.Cell };
                    if ((c.Fields & ChangedFields.Facing) != 0) r = r with { Facing = c.Facing! };
                    if ((c.Fields & ChangedFields.Next) != 0) r = r with { Next = c.Next };
                    if ((c.Fields & ChangedFields.Speed) != 0) r = r with { Speed = c.Speed };
                    if ((c.Fields & ChangedFields.DisplayKey) != 0) r = r with { DisplayKey = c.DisplayKey };
                    _view[c.Id] = r;
                }
                foreach (var id in d.Disappeared) _view.Remove(id);
                break;
        }
    }
}

/// <summary>Polling with a timeout instead of fixed sleeps.</summary>
public static class Poll
{
    public static T Until<T>(Func<T> read, Func<T, bool> done, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var value = read();
            if (done(value)) return value;
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}.");
            Thread.Sleep(5);
        }
    }

    public static async Task<T> UntilAsync<T>(Func<T> read, Func<T, bool> done, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var value = read();
            if (done(value)) return value;
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}.");
            await Task.Delay(5);
        }
    }
}
