using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Client.Core.World;
using Game.Server.Net;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.World;

namespace Game.Client.Core;

/// <summary>
/// The whole client without an engine: socket events go in, the machine, view and UI state change, intents go out. It sends only intents
/// (CLAUDE.md rule 1). It runs on the main thread, and time comes in as parameters: <c>wallNow</c> keeps running while the app is suspended and
/// drives the connection; <c>viewNow</c> is smooth and drives the view.
/// </summary>
public sealed class ClientSession
{
    private readonly ClientConfig _config;
    private readonly DisplayBundle _bundle;
    private readonly IClientSocket _socket;

    /// <summary>Creates a session on the login screen.</summary>
    public ClientSession(ClientConfig config, DisplayBundle bundle, IClientSocket socket, IRandom random)
    {
        _config = config;
        _bundle = bundle;
        _socket = socket;
        Connection = new ConnectionMachine(config, random);
        World = new WorldView(config);
        Ui = new UiState(config, bundle);
    }

    /// <summary>The connection.</summary>
    public ConnectionMachine Connection { get; }

    /// <summary>The world as last told by the server.</summary>
    public WorldView World { get; }

    /// <summary>What the player's actions put on screen.</summary>
    public UiState Ui { get; }

    /// <summary>Server messages applied so far (for measuring).</summary>
    public long MessagesApplied { get; private set; }

    /// <summary>Starts a login with the dev name.</summary>
    public void Login(string name, double wallNow)
    {
        World.Reset();
        Ui.Reset();
        Connection.Login(name, wallNow);
        RunCommands();
    }

    /// <summary>Asks to leave (§10 step 6).</summary>
    public void Logout(double wallNow)
    {
        Connection.Logout(wallNow);
        RunCommands();
    }

    /// <summary>The app went to the background.</summary>
    public void OnBackgrounded() => Connection.OnBackgrounded();

    /// <summary>The app came back.</summary>
    public void OnForegrounded(double wallNow)
    {
        Connection.OnForegrounded(wallNow);
        RunCommands();
    }

    /// <summary>A ground tap: ask to walk. The server may refuse; the client checks nothing.</summary>
    public void TapGround(GridCell cell, double viewNow)
    {
        if (Connection.Phase != ConnectionPhase.Online || Ui.Dead) return;
        Send(new MoveMessage(cell));
        Ui.OnMoveSent(cell, viewNow);
    }

    /// <summary>A monster tap: ask to attack. The server may refuse; the client checks nothing.</summary>
    public void TapEntity(long id)
    {
        if (Connection.Phase != ConnectionPhase.Online || Ui.Dead) return;
        Send(new AttackMessage(id));
        Ui.OnAttackSent(id);
    }

    /// <summary>The death screen's button.</summary>
    public void Respawn()
    {
        if (Connection.Phase == ConnectionPhase.Online) Send(new RespawnMessage());
    }

    /// <summary>One frame: applies what arrived, runs timers, moves the view. Allocates nothing when nothing arrived and no timer fired.</summary>
    public void Update(double wallNow, double viewNow)
    {
        while (_socket.TryDequeue(out var socketEvent))
        {
            switch (socketEvent.Kind)
            {
                case SocketEventKind.Opened:
                    Connection.OnSocketOpened();
                    break;
                case SocketEventKind.Message:
                    Connection.OnHeard(wallNow);
                    Apply(socketEvent.Message!, wallNow, viewNow);
                    break;
                case SocketEventKind.UnknownType:
                    Connection.OnHeard(wallNow);
                    break;
                case SocketEventKind.Malformed:
                    _socket.Close();
                    Connection.OnSocketClosed(wallNow);
                    break;
                case SocketEventKind.Closed:
                    Connection.OnSocketClosed(wallNow);
                    break;
            }
            RunCommands();
        }
        Connection.Tick(wallNow);
        RunCommands();
        World.Advance(viewNow);
        Ui.Tick(World, viewNow);
    }

    private void Apply(ServerMessage message, double wallNow, double viewNow)
    {
        MessagesApplied++;
        switch (message)
        {
            case DeltaMessage delta:
                if (delta.Hits is { Count: > 0 } hits)
                {
                    Ui.OnHits(hits, World, viewNow); // before the deltas: a target that died here is still named
                    World.ApplyHits(hits, viewNow);
                }
                World.ApplyDelta(delta, viewNow);
                if (delta.Appeared.Count > 0) Ui.OnAppeared(delta.Appeared, viewNow);
                if (delta.Vitals is { } vitals) Ui.OnVitals(vitals);
                break;
            case SnapshotMessage snapshot:
                World.ApplySnapshot(snapshot, viewNow);
                if (snapshot.Vitals is { } own) Ui.OnVitals(own);
                break;
            case WelcomeMessage welcome:
                Connection.OnWelcome(welcome, wallNow);
                World.ApplyWelcome(welcome);
                break;
            case ExpMessage exp:
                World.ApplyExp(exp);
                break;
            case RejectedMessage rejected:
                Ui.OnRejected(rejected);
                break;
            case AttackStoppedMessage stopped:
                Ui.OnAttackStopped(stopped);
                break;
            case YouDiedMessage:
                Ui.OnYouDied();
                break;
            case RespawnedMessage:
                Ui.OnRespawned();
                break;
            case LevelUpMessage level:
                Ui.OnLevelUp(level, viewNow);
                break;
            case ByeMessage bye:
                Connection.OnBye(bye.Reason, wallNow);
                break;
        }
    }

    private void RunCommands()
    {
        while (Connection.TryNextCommand(out var command))
        {
            switch (command)
            {
                case ConnectionCommandKind.OpenSocket:
                    // Main refuses a bad URL at start-up with a screen; this stays as the second lock. Nothing is opened, so the attempt times out into "could not reach".
                    if (TransportPolicy.Check(_config.ServerUrl, _config.AllowCleartext) == TransportVerdict.Allowed) _socket.Open(_config.ServerUrl);
                    break;
                case ConnectionCommandKind.CloseSocket:
                    _socket.Close();
                    break;
                case ConnectionCommandKind.SendLogin:
                    Send(new HelloMessage(ProtocolVersion.Current, Connection.LoginName, _bundle.ContentHash));
                    break;
                case ConnectionCommandKind.SendResume:
                    Send(new HelloMessage(ProtocolVersion.Current, null, _bundle.ContentHash, Connection.ResumeToken));
                    break;
                case ConnectionCommandKind.SendPing:
                    Send(new PingMessage(MessagesApplied));
                    break;
                case ConnectionCommandKind.SendLogout:
                    Send(new LogoutMessage());
                    break;
            }
        }
    }

    private void Send(ClientMessage message) => _socket.Send(ProtocolJson.ToBytes(message));
}
