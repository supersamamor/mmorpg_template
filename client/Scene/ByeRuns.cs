using System.Diagnostics;
using System.Net.WebSockets;
using Game.Client.Core;
using Game.Client.Core.Net;
using Game.Server.Net;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Live runs of the <c>bye</c> reasons and the grace period against a real server (T-216, ARCHITECTURE §10), on the real Godot socket. Each one is started
/// by <c>run-e2e.ps1 -Run &lt;name&gt;</c>, which sets up the server the run needs and, where the run needs the server to disappear or come back, does it when this
/// class prints a line starting with <c>[e2e] @</c>. Nothing here is needed to play.
/// <list type="bullet">
/// <item><c>resume_failed</c>: the socket drops, the client waits longer than the server's grace period, and its resume is answered with <c>bye resume_failed</c>.</item>
/// <item><c>replaced</c>: a second connection resumes the character with the client's token; the client gets <c>bye replaced</c> and must not reconnect.</item>
/// <item><c>shutdown</c>: the server stops itself (<c>--duration</c>) and says <c>bye shutdown</c>; the client waits, and logs in again when a server is back.</item>
/// <item><c>grace_expiry</c>: the server is killed; the client resumes with backoff until its own grace deadline, then asks for a new login.</item>
/// </list>
/// </summary>
public sealed class ByeRuns
{
    private readonly Main _main;

    /// <summary>Prepares the runs.</summary>
    public ByeRuns(Main main) => _main = main;

    private ClientSession Session => _main.Session!;

    /// <summary>Runs one scenario by name; throws on a difference from what ARCHITECTURE §10 says.</summary>
    public async Task RunAsync(string name)
    {
        switch (name)
        {
            case "resume_failed": await ResumeFailedAsync(); break;
            case "replaced": await ReplacedAsync(); break;
            case "shutdown": await ShutdownAsync(); break;
            case "grace_expiry": await GraceExpiryAsync(); break;
            default: throw new InvalidOperationException("unknown bye run: " + name);
        }
    }

    private async Task ResumeFailedAsync()
    {
        await LoginAsync();
        var clock = Stopwatch.StartNew();
        Print("resume_failed: killing the socket; the server's grace period is shorter than the client's first retry");
        _main.Socket!.Kill();
        await UntilAsync(() => Session.Connection.Phase != ConnectionPhase.Online, 5, "noticing the drop");
        Check(Session.Connection.Phase == ConnectionPhase.Backoff && Session.Connection.Notice == ConnectionNotice.Reconnecting, "backing off to resume");
        Check(Session.Connection.HasToken, "the token is held while resuming");
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 30, "bye resume_failed");
        Check(Session.Connection.Notice == ConnectionNotice.ResumeFailed, "the notice is ResumeFailed, was " + Session.Connection.Notice);
        Check(!Session.Connection.HasToken, "the token is dropped");
        Print($"resume_failed: after {clock.Elapsed.TotalSeconds:0.0} s the server answered the resume with bye resume_failed; login screen shows \"{_main.Hud!.PanelText}\"");
        await SecondsAsync(2);
        Check(Session.Connection.Phase == ConnectionPhase.LoginScreen, "no automatic retry from the login screen");
    }

    private async Task ReplacedAsync()
    {
        await LoginAsync();
        var token = Session.Connection.ResumeToken ?? throw new InvalidOperationException("no token held");
        var id = Session.World.OwnId;
        using var other = new ClientWebSocket();
        Print("replaced: a second connection resumes this character with the client's token");
        await other.ConnectAsync(new Uri(_main.Config.ServerUrl), CancellationToken.None);
        await SendAsync(other, new HelloMessage(ProtocolVersion.Current, null, _main.Bundle!.ContentHash, token));
        var reply = await ReceiveAsync(other, m => m is WelcomeMessage or ByeMessage);
        Check(reply is WelcomeMessage welcome && welcome.Id == id, "the second connection got the same character back, was " + reply?.GetType().Name);
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Stopped, 10, "bye replaced");
        Check(Session.Connection.Notice == ConnectionNotice.Replaced, "the notice is Replaced, was " + Session.Connection.Notice);
        Check(!Session.Connection.HasToken, "the token is dropped");
        Print($"replaced: bye replaced; the screen shows \"{_main.Hud!.PanelText}\" with a button \"{(_main.Hud.PanelButtonShown ? "yes" : "no")}\"");
        await SecondsAsync(4);
        Check(Session.Connection.Phase == ConnectionPhase.Stopped, "no automatic reconnect after replaced");
        Check(other.State == WebSocketState.Open, "the new connection still holds the character");
        Print("replaced: 4 s later still stopped, no reconnect; the other connection still plays");
        await other.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private async Task ShutdownAsync()
    {
        await LoginAsync();
        Print("shutdown: online; waiting for the server to stop itself");
        var clock = Stopwatch.StartNew();
        await UntilAsync(() => Session.Connection.Notice == ConnectionNotice.ServerShutdown, 60, "bye shutdown");
        Check(Session.Connection.Phase == ConnectionPhase.Backoff, "waiting to retry, was " + Session.Connection.Phase);
        Check(!Session.Connection.HasToken, "the token is dropped (a restart invalidates every token)");
        Print($"shutdown: bye shutdown after {clock.Elapsed.TotalSeconds:0.0} s; the screen shows \"{UiText.Notice(Session.Connection.Notice)}\"");
        Print("@ shutdown-seen");
        // The script starts a new server on the same port; the client's retry is a normal login.
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online, 60, "logging in again after the shutdown");
        await UntilAsync(() => Session.World.Own is not null, 10, "the first snapshot");
        Check(Session.Connection.Notice == ConnectionNotice.None, "the notice is gone");
        Print($"shutdown: logged in again by itself after {clock.Elapsed.TotalSeconds:0.0} s; own at {Session.World.Own!.Cell}");
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
    }

    private async Task GraceExpiryAsync()
    {
        await LoginAsync();
        var config = _main.Config;
        Print($"grace_expiry: client grace {config.GraceSeconds} s + margin {config.GraceMarginSeconds} s; the script kills the server now");
        var clock = Stopwatch.StartNew();
        Print("@ kill-server");
        await UntilAsync(() => Session.Connection.Phase != ConnectionPhase.Online, 15, "noticing the dead server");
        Print($"grace_expiry: {Session.Connection.Phase} \"{UiText.Notice(Session.Connection.Notice)}\" after {clock.Elapsed.TotalSeconds:0.0} s");
        Check(Session.Connection.Notice == ConnectionNotice.Reconnecting, "reconnecting first, was " + Session.Connection.Notice);
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 30, "the grace deadline");
        var elapsed = clock.Elapsed.TotalSeconds;
        Check(Session.Connection.Notice == ConnectionNotice.SessionExpired, "the notice is SessionExpired, was " + Session.Connection.Notice);
        Check(!Session.Connection.HasToken, "the token is dropped");
        Check(elapsed >= 1, "it did not give up at once");
        Check(elapsed <= config.GraceSeconds + config.GraceMarginSeconds + 5, "it gave up by its deadline");
        Print($"grace_expiry: gave up after {elapsed:0.0} s (deadline: last heard + {config.GraceSeconds + config.GraceMarginSeconds:0} s); the screen shows \"{_main.Hud!.PanelText}\"");
    }

    // ---- helpers ----

    private async Task LoginAsync()
    {
        Print($"login: {_main.Config.ServerUrl}, name {_main.Config.DevName}");
        await FrameAsync();
        Session.Login(_main.Config.DevName, Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online, 15, "welcome");
        await UntilAsync(() => Session.World.Own is not null && _main.LoadedMap == Session.World.MapId, 15, "first snapshot and map");
        Print($"login: online as entity {Session.World.OwnId} on {Session.World.MapId}");
    }

    private static async Task SendAsync(ClientWebSocket socket, ClientMessage message) =>
        await socket.SendAsync(ProtocolJson.ToBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<ServerMessage?> ReceiveAsync(ClientWebSocket socket, Func<ServerMessage, bool> wanted)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var length = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token);
                length += result.Count;
            } while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            var read = ProtocolJson.ReadServer(buffer.AsSpan(0, length));
            if (read.Message is { } message && wanted(message)) return message;
        }
    }

    private async Task FrameAsync() => await _main.ToSignal(_main.GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task SecondsAsync(double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds) await FrameAsync();
    }

    private async Task UntilAsync(Func<bool> condition, double seconds, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}, notice {Session.Connection.Notice}");
            await FrameAsync();
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
