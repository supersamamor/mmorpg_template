using System;
using System.Collections.Generic;
using Game.Shared.Protocol;
using Game.Shared.Random;

namespace Game.Client.Core.Net;

/// <summary>Where the connection is.</summary>
public enum ConnectionPhase
{
    /// <summary>No session: the login screen. <see cref="ConnectionMachine.Notice"/> says why, if there is a reason.</summary>
    LoginScreen,

    /// <summary>A socket is opening.</summary>
    Connecting,

    /// <summary><c>hello</c> was sent; waiting for <c>welcome</c>.</summary>
    AwaitingWelcome,

    /// <summary>Playing.</summary>
    Online,

    /// <summary>Waiting before the next attempt.</summary>
    Backoff,

    /// <summary><c>logout</c> was sent; waiting for <c>bye logout</c>.</summary>
    LoggingOut,

    /// <summary>Stopped until the player acts: no automatic retry (<see cref="ConnectionNotice.Replaced"/>, <see cref="ConnectionNotice.UpdateRequired"/>).</summary>
    Stopped,
}

/// <summary>What the player is told. The UI picks the text.</summary>
public enum ConnectionNotice
{
    /// <summary>Nothing to say.</summary>
    None,

    /// <summary>Reconnecting after a drop.</summary>
    Reconnecting,

    /// <summary>The grace period must be over; log in again.</summary>
    SessionExpired,

    /// <summary><c>bye resume_failed</c>.</summary>
    ResumeFailed,

    /// <summary><c>bye rate_limit</c> during a resume: no automatic retry.</summary>
    ResumeRateLimited,

    /// <summary><c>bye login_refused</c>.</summary>
    LoginRefused,

    /// <summary><c>bye server_full</c> on a new login.</summary>
    ServerFull,

    /// <summary><c>bye load_failed</c>: the character could not be loaded (T-702).</summary>
    LoadFailed,

    /// <summary><c>bye save_pending</c>: the character is still being saved (T-702).</summary>
    SavePending,

    /// <summary><c>bye save_lost</c>: the server could no longer save the character and ended the session (T-702).</summary>
    SaveLost,

    /// <summary><c>bye session_ended</c>: the account session was ended elsewhere (T-710).</summary>
    SessionEnded,

    /// <summary><c>bye kicked</c>: staff ended the game, and the account's sessions with it (T-804).</summary>
    Kicked,

    /// <summary><c>bye banned</c>: staff banned the account (T-806); <see cref="ConnectionMachine.NoticeSeconds"/> says how long is left (null: no end).</summary>
    Banned,

    /// <summary><c>bye maintenance</c>: the server is in maintenance (T-806): no game now; try later.</summary>
    Maintenance,

    /// <summary>A new login could not reach the server, or the server ended it.</summary>
    ConnectionFailed,

    /// <summary><c>bye update_required</c> or <c>bye bad_version</c>: go to the store.</summary>
    UpdateRequired,

    /// <summary><c>bye replaced</c>: the same player is on another connection.</summary>
    Replaced,

    /// <summary><c>bye logout</c>.</summary>
    LoggedOut,

    /// <summary><c>bye shutdown</c>: retrying later with a normal login.</summary>
    ServerShutdown,
}

/// <summary>What the machine asks its owner to do.</summary>
public enum ConnectionCommandKind
{
    /// <summary>Open a new socket.</summary>
    OpenSocket,

    /// <summary>Close the socket; its later events do not matter.</summary>
    CloseSocket,

    /// <summary>Send <c>hello</c> with a name.</summary>
    SendLogin,

    /// <summary>Send <c>hello</c> with the resume token.</summary>
    SendResume,

    /// <summary>Send a <c>ping</c>.</summary>
    SendPing,

    /// <summary>Send <c>logout</c>.</summary>
    SendLogout,
}

/// <summary>
/// The client's half of the session lifecycle and resume flow (ARCHITECTURE §10), as a pure state machine: events and the time go in,
/// commands come out. The time is a clock that keeps running while the app is suspended, in seconds. The token lives in this object only:
/// never on disk, in preferences or in a log (§10 step 1).
/// </summary>
public sealed class ConnectionMachine
{
    private readonly ClientConfig _config;
    private readonly IRandom _random;
    private readonly Queue<ConnectionCommandKind> _commands = new Queue<ConnectionCommandKind>();
    private string? _token;
    private bool _resuming;
    private bool _background;
    private int _attempt;
    private double _retryAt;
    private double _deadline;     // the grace period must be over by now (only while resuming)
    private double _phaseEnds;    // connect timeout, or the foreground probe
    private double _lastHeard;
    private double _silenceSince;
    private double _nextPing;
    private bool _probing;

    /// <summary>Creates a machine on the login screen.</summary>
    public ConnectionMachine(ClientConfig config, IRandom random)
    {
        _config = config;
        _random = random;
    }

    /// <summary>The current phase.</summary>
    public ConnectionPhase Phase { get; private set; } = ConnectionPhase.LoginScreen;

    /// <summary>The message to show for the current phase.</summary>
    public ConnectionNotice Notice { get; private set; }

    /// <summary>For <see cref="ConnectionNotice.Banned"/>: seconds the ban had left when it was told, or null when it has no end (T-806).</summary>
    public long? NoticeSeconds { get; private set; }

    /// <summary>Counts changes of <see cref="Phase"/> and <see cref="Notice"/>, so a UI redraws only on a change.</summary>
    public int Version { get; private set; }

    /// <summary>The name of the current login (memory only, for the retry after a shutdown).</summary>
    public string LoginName { get; private set; } = string.Empty;

    /// <summary>The character's entity id, from the latest <c>welcome</c>.</summary>
    public long EntityId { get; private set; }

    /// <summary>True while a resume token is held.</summary>
    public bool HasToken => _token is not null;

    /// <summary>The token to put in <c>hello</c>, or null. For <see cref="ConnectionCommandKind.SendResume"/> only; never store or log it.</summary>
    public string? ResumeToken => _token;

    /// <summary>The next command to carry out, if any. Call until false after every event.</summary>
    public bool TryNextCommand(out ConnectionCommandKind command)
    {
        if (_commands.Count > 0)
        {
            command = _commands.Dequeue();
            return true;
        }
        command = default;
        return false;
    }

    /// <summary>The player asks to log in (from the login screen, or from a stop that lets them).</summary>
    public void Login(string name, double now)
    {
        if (Phase != ConnectionPhase.LoginScreen && Phase != ConnectionPhase.Stopped) return;
        LoginName = name;
        _token = null;
        _resuming = false;
        _attempt = 0;
        Open(now, ConnectionNotice.None);
    }

    /// <summary>The player leaves a stop (<see cref="ConnectionPhase.Stopped"/>) for the login screen.</summary>
    public void BackToLogin()
    {
        if (Phase == ConnectionPhase.Stopped) Set(ConnectionPhase.LoginScreen, ConnectionNotice.None);
    }

    /// <summary>The player chooses to leave (§10 step 6). Without a live socket there is nothing to tell the server: back to the login screen.</summary>
    public void Logout(double now)
    {
        if (Phase == ConnectionPhase.Online)
        {
            _commands.Enqueue(ConnectionCommandKind.SendLogout);
            _phaseEnds = now + _config.ConnectTimeoutSeconds;
            Set(ConnectionPhase.LoggingOut, ConnectionNotice.None);
            return;
        }
        if (Phase == ConnectionPhase.Connecting || Phase == ConnectionPhase.AwaitingWelcome || Phase == ConnectionPhase.Backoff) ToLogin(ConnectionNotice.None);
    }

    /// <summary>The socket opened: say hello.</summary>
    public void OnSocketOpened()
    {
        if (Phase != ConnectionPhase.Connecting) return;
        _commands.Enqueue(_resuming ? ConnectionCommandKind.SendResume : ConnectionCommandKind.SendLogin);
        Set(ConnectionPhase.AwaitingWelcome, Notice);
    }

    /// <summary>Any server message arrived. Call before the message's own event.</summary>
    public void OnHeard(double now)
    {
        _lastHeard = now;
        _silenceSince = now;
        _probing = false;
    }

    /// <summary><c>welcome</c>: keep the new token and the id (§10 step 3). The first message on the new session (the ping) acknowledges the resume.</summary>
    public void OnWelcome(WelcomeMessage welcome, double now)
    {
        if (Phase != ConnectionPhase.AwaitingWelcome) return;
        _token = welcome.Token;
        EntityId = welcome.Id;
        _resuming = false;
        _attempt = 0;
        _lastHeard = now;
        _silenceSince = now;
        _nextPing = now + _config.PingIntervalSeconds;
        _commands.Enqueue(ConnectionCommandKind.SendPing);
        Set(ConnectionPhase.Online, ConnectionNotice.None);
    }

    /// <summary>A <c>bye</c> arrived: every reason does what §10 says.</summary>
    public void OnBye(string reason, double now) => OnBye(reason, now, null);

    /// <summary>The same, with a ban's time left (<c>retry_after</c>, seconds; T-806).</summary>
    public void OnBye(string reason, double now, long? retryAfterSeconds)
    {
        if (Phase == ConnectionPhase.LoginScreen || Phase == ConnectionPhase.Stopped) return;
        var duringResume = _resuming && (Phase == ConnectionPhase.Connecting || Phase == ConnectionPhase.AwaitingWelcome);
        switch (reason)
        {
            case ByeReasons.Logout:
                ToLogin(ConnectionNotice.LoggedOut);
                return;
            case ByeReasons.Replaced:
                ToStopped(ConnectionNotice.Replaced);
                return;
            case ByeReasons.UpdateRequired:
            case ByeReasons.BadVersion:
                ToStopped(ConnectionNotice.UpdateRequired);
                return;
            case ByeReasons.ResumeFailed:
                ToLogin(ConnectionNotice.ResumeFailed);
                return;
            case ByeReasons.LoginRefused:
                ToLogin(ConnectionNotice.LoginRefused);
                return;
            case ByeReasons.LoadFailed:
                ToLogin(ConnectionNotice.LoadFailed);
                return;
            case ByeReasons.SavePending:
                ToLogin(ConnectionNotice.SavePending);
                return;
            case ByeReasons.SaveLost:
                // The character left the world without being saved, so there is nothing to resume.
                ToLogin(ConnectionNotice.SaveLost);
                return;
            case ByeReasons.SessionEnded:
                // Signed out, or the password changed, elsewhere: the token is dead with the session.
                ToLogin(ConnectionNotice.SessionEnded);
                return;
            case ByeReasons.Kicked:
                // Staff ended the game (T-804): every session of the account ended with it, so it is back to signing in; nothing to resume.
                ToLogin(ConnectionNotice.Kicked);
                return;
            case ByeReasons.Banned:
                // T-806: staff banned the account; the sessions are gone with it. Nothing to resume, and signing in says the same until the ban ends.
                NoticeSeconds = retryAfterSeconds;
                ToLogin(ConnectionNotice.Banned);
                return;
            case ByeReasons.Maintenance:
                // T-806: no automatic retry: maintenance takes as long as it takes.
                ToLogin(ConnectionNotice.Maintenance);
                return;
            case ByeReasons.Shutdown:
                // A server restart invalidates every token (§10): the retry is a normal login.
                _token = null;
                _resuming = false;
                _commands.Enqueue(ConnectionCommandKind.CloseSocket);
                _retryAt = now + _config.ShutdownRetrySeconds;
                Set(ConnectionPhase.Backoff, ConnectionNotice.ServerShutdown);
                return;
            case ByeReasons.RateLimit when duringResume:
                ToLogin(ConnectionNotice.ResumeRateLimited);
                return;
            case ByeReasons.ServerFull when !duringResume:
                ToLogin(ConnectionNotice.ServerFull);
                return;
        }
        // timeout, too_slow, server_error, a protocol error, a full socket table, an unknown reason: the server kept the character, like a drop.
        _commands.Enqueue(ConnectionCommandKind.CloseSocket);
        Dropped(now);
    }

    /// <summary>The socket closed or failed.</summary>
    public void OnSocketClosed(double now)
    {
        switch (Phase)
        {
            case ConnectionPhase.Connecting:
            case ConnectionPhase.AwaitingWelcome:
            case ConnectionPhase.Online:
                Dropped(now);
                break;
            case ConnectionPhase.LoggingOut:
                ToLogin(ConnectionNotice.LoggedOut);
                break;
        }
    }

    /// <summary>The app went to the background: keep the socket, but send nothing and start no reconnect while there.</summary>
    public void OnBackgrounded() => _background = true;

    /// <summary>The app is back (§10 step 2): retry at once if the socket is gone, and make a socket that looks open prove itself.</summary>
    public void OnForegrounded(double now)
    {
        _background = false;
        if (Phase == ConnectionPhase.Online)
        {
            _commands.Enqueue(ConnectionCommandKind.SendPing);
            _probing = true;
            _silenceSince = now; // nothing could be heard while suspended; the probe decides
            _phaseEnds = now + _config.ForegroundProbeSeconds;
            _nextPing = now + _config.PingIntervalSeconds;
        }
        else if (Phase == ConnectionPhase.Backoff && Notice == ConnectionNotice.Reconnecting)
        {
            _attempt = 0;
            _retryAt = now;
        }
    }

    /// <summary>Advances timers. Allocates nothing when no command results.</summary>
    public void Tick(double now)
    {
        if (_background) return;
        switch (Phase)
        {
            case ConnectionPhase.Backoff:
                if (_resuming && now >= _deadline)
                {
                    ToLogin(ConnectionNotice.SessionExpired);
                    return;
                }
                if (now >= _retryAt) Open(now, Notice);
                break;
            case ConnectionPhase.Connecting:
            case ConnectionPhase.AwaitingWelcome:
                if (_resuming && now >= _deadline)
                {
                    ToLogin(ConnectionNotice.SessionExpired);
                    return;
                }
                if (now >= _phaseEnds)
                {
                    _commands.Enqueue(ConnectionCommandKind.CloseSocket);
                    Dropped(now);
                }
                break;
            case ConnectionPhase.Online:
                if ((_probing && now >= _phaseEnds) || now - _silenceSince >= _config.SilenceTimeoutSeconds)
                {
                    _commands.Enqueue(ConnectionCommandKind.CloseSocket);
                    Dropped(now);
                    return;
                }
                if (now >= _nextPing)
                {
                    _nextPing = now + _config.PingIntervalSeconds;
                    _commands.Enqueue(ConnectionCommandKind.SendPing);
                }
                break;
            case ConnectionPhase.LoggingOut:
                if (now >= _phaseEnds) ToLogin(ConnectionNotice.LoggedOut);
                break;
        }
    }

    /// <summary>The socket is gone without a final answer: resume if a token is held; otherwise this was a login that failed.</summary>
    private void Dropped(double now)
    {
        if (Notice == ConnectionNotice.ServerShutdown)
        {
            _retryAt = now + _config.ShutdownRetrySeconds;
            Set(ConnectionPhase.Backoff, ConnectionNotice.ServerShutdown);
            return;
        }
        if (_token is null)
        {
            ToLogin(ConnectionNotice.ConnectionFailed);
            return;
        }
        if (!_resuming)
        {
            // The grace period started no earlier than the last thing the server said.
            _resuming = true;
            _attempt = 0;
            _deadline = _lastHeard + _config.GraceSeconds + _config.GraceMarginSeconds;
        }
        if (now >= _deadline)
        {
            ToLogin(ConnectionNotice.SessionExpired);
            return;
        }
        _retryAt = now + NextBackoff();
        Set(ConnectionPhase.Backoff, ConnectionNotice.Reconnecting);
    }

    /// <summary>Delay of the current attempt: doubling from the initial delay up to the cap, then jitter of ±<see cref="ClientConfig.BackoffJitter"/>.</summary>
    private double NextBackoff()
    {
        var delay = _config.BackoffInitialSeconds * Math.Pow(2, Math.Min(_attempt, 16));
        if (delay > _config.BackoffCapSeconds) delay = _config.BackoffCapSeconds;
        _attempt++;
        return delay * (1 + _config.BackoffJitter * (2 * _random.NextDouble() - 1));
    }

    private void Open(double now, ConnectionNotice notice)
    {
        _commands.Enqueue(ConnectionCommandKind.OpenSocket);
        _phaseEnds = now + _config.ConnectTimeoutSeconds;
        Set(ConnectionPhase.Connecting, notice);
    }

    private void ToLogin(ConnectionNotice notice)
    {
        End();
        Set(ConnectionPhase.LoginScreen, notice);
    }

    private void ToStopped(ConnectionNotice notice)
    {
        End();
        Set(ConnectionPhase.Stopped, notice);
    }

    private void End()
    {
        _token = null;
        _resuming = false;
        _probing = false;
        _commands.Enqueue(ConnectionCommandKind.CloseSocket);
    }

    private void Set(ConnectionPhase phase, ConnectionNotice notice)
    {
        if (Phase == phase && Notice == notice) return;
        Phase = phase;
        Notice = notice;
        Version++;
    }
}
