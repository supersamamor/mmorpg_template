using System;
using System.Collections.Generic;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Accounts;

/// <summary>Which account screen shows.</summary>
public enum AccountScreen
{
    /// <summary>Username and password.</summary>
    SignIn,

    /// <summary>The character slots.</summary>
    Characters,

    /// <summary>Making a new character (T-706).</summary>
    Creation,
}

/// <summary>What the line under the screen's title says. The UI picks the text (<see cref="UiText.Account"/>).</summary>
public enum AccountNotice
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The server refused; <see cref="AccountFlow.Error"/> says why.</summary>
    Refused,

    /// <summary>The server could not be reached, or did not answer like one.</summary>
    Unreachable,

    /// <summary>The session has ended: sign in again.</summary>
    SessionEnded,
}

/// <summary>
/// Signing in and picking a character (T-701), as a pure state machine over an <see cref="IAccountTransport"/>. The server decides everything; this holds what it
/// said. One request at a time.
/// </summary>
/// <remarks>
/// <para><b>Secrets.</b> The password is written into the request and not kept. The session token lives in this object only: the engine offers no secure storage
/// without native code, so nothing is written to the device and a restarted app signs in again.</para>
/// <para>Every request goes through <see cref="TransportPolicy.AccountBase"/>, so a release build never sends credentials over plain HTTP.</para>
/// </remarks>
public sealed class AccountFlow
{
    private readonly ClientConfig _config;
    private readonly IAccountTransport? _transport;
    private readonly List<CharacterRecord> _characters = new List<CharacterRecord>();
    private AccountCall _pending;
    private string? _sessionToken;

    /// <summary>Creates the flow on the sign-in screen. Without a transport every request is unreachable (tests of other things).</summary>
    public AccountFlow(ClientConfig config, IAccountTransport? transport, CharacterCreator? creator = null)
    {
        _config = config;
        _transport = transport;
        Creator = creator;
    }

    /// <summary>What is chosen on the creation screen (T-706), or null (tests of other things).</summary>
    public CharacterCreator? Creator { get; }

    /// <summary>The screen.</summary>
    public AccountScreen Screen { get; private set; }

    /// <summary>What to tell the player.</summary>
    public AccountNotice Notice { get; private set; }

    /// <summary>The "Account errors" term of the last refusal, for <see cref="AccountNotice.Refused"/>.</summary>
    public string Error { get; private set; } = string.Empty;

    /// <summary>Seconds the server asked to wait, or 0.</summary>
    public long RetryAfterSeconds { get; private set; }

    /// <summary>True while a request is on its way: the screen takes no other.</summary>
    public bool Busy { get; private set; }

    /// <summary>True while a session token is held.</summary>
    public bool SignedIn => _sessionToken is not null;

    /// <summary>The token for <c>hello</c> and the requests, or null. Never store or log it.</summary>
    public string? SessionToken => _sessionToken;

    /// <summary>How many slots the account has.</summary>
    public int Slots { get; private set; }

    /// <summary>Shortest character name (the server checks; this is for the hint).</summary>
    public int NameMin { get; private set; }

    /// <summary>Longest character name.</summary>
    public int NameMax { get; private set; }

    /// <summary>The account's characters, by slot.</summary>
    public IReadOnlyList<CharacterRecord> Characters => _characters;

    /// <summary>The character whose deletion waits for a second press, or null.</summary>
    public string? Deleting { get; private set; }

    /// <summary>Counts changes, so a UI redraws only on one.</summary>
    public int Version { get; private set; }

    /// <summary>Creates an account and signs in.</summary>
    public void Register(string username, string password) => Send(AccountCall.Register, "POST", AccountPaths.Register, AccountJson.Write(new CredentialsRequest(username, password)), null);

    /// <summary>Signs in.</summary>
    public void SignIn(string username, string password) => Send(AccountCall.SignIn, "POST", AccountPaths.Login, AccountJson.Write(new CredentialsRequest(username, password)), null);

    /// <summary>Signs out: the token is dropped at once, whatever the server answers.</summary>
    public void SignOut()
    {
        if (Busy || _sessionToken is null) return;
        var token = _sessionToken;
        Forget();
        Creator?.Reset(); // the next one to sign in starts fresh; a session that ended by itself keeps the choices
        Send(AccountCall.SignOut, "POST", AccountPaths.Logout, null, token);
    }

    /// <summary>Asks for the characters again.</summary>
    public void Refresh()
    {
        if (_sessionToken is not null) Send(AccountCall.List, "GET", AccountPaths.Characters, null, _sessionToken);
    }

    /// <summary>Opens the creation screen with what was chosen there before.</summary>
    public void OpenCreation()
    {
        if (Busy || Screen != AccountScreen.Characters) return;
        Screen = AccountScreen.Creation;
        ClearNotice();
    }

    /// <summary>Back to the character list; the choices stay.</summary>
    public void CloseCreation()
    {
        if (Busy || Screen != AccountScreen.Creation) return;
        Screen = AccountScreen.Characters;
        ClearNotice();
    }

    /// <summary>Creates a character with the chosen look (null: the server's defaults).</summary>
    public void Create(string name, AppearanceChoice? appearance = null)
    {
        if (_sessionToken is not null) Send(AccountCall.Create, "POST", AccountPaths.Characters, AccountJson.Write(new CreateCharacterRequest(name, appearance)), _sessionToken);
    }

    /// <summary>First press: asks whether to delete the character. Second press on the same one: deletes it.</summary>
    public void Delete(string id)
    {
        if (Busy || _sessionToken is null) return;
        if (Deleting != id)
        {
            Deleting = id;
            Version++;
            return;
        }
        Send(AccountCall.Delete, "DELETE", AccountPaths.Characters + "/" + id, null, _sessionToken);
    }

    /// <summary>Forgets a deletion that waits for its second press.</summary>
    public void KeepCharacter()
    {
        if (Deleting is null) return;
        Deleting = null;
        Version++;
    }

    /// <summary>Takes what the transport answered. Allocates nothing when nothing did.</summary>
    public void Update()
    {
        if (_transport is null || !_transport.TryDequeue(out var response)) return;
        Busy = false;
        Version++;
        var call = _pending;
        if (call == AccountCall.SignOut) return; // the session ends here whatever the server said
        if (response.Status < 200 || response.Status >= 300)
        {
            Fail(response);
            return;
        }
        switch (call)
        {
            case AccountCall.Register:
            case AccountCall.SignIn:
                if (AccountJson.ReadSession(response.Body) is { } session)
                {
                    _sessionToken = session.Token;
                    Refresh();
                }
                else Notice = AccountNotice.Unreachable;
                break;
            case AccountCall.List:
                if (AccountJson.ReadCharacters(response.Body) is { } list)
                {
                    Slots = list.Slots;
                    NameMin = list.NameMin;
                    NameMax = list.NameMax;
                    _characters.Clear();
                    _characters.AddRange(list.Characters);
                    Screen = AccountScreen.Characters;
                }
                else Notice = AccountNotice.Unreachable;
                break;
            case AccountCall.Create:
                Creator?.Reset();
                Refresh();
                break;
            case AccountCall.Delete:
                Refresh();
                break;
        }
    }

    private void Fail(AccountHttpResponse response)
    {
        if (response.Status == 0 || AccountJson.ReadError(response.Body) is not { } error)
        {
            Notice = AccountNotice.Unreachable;
            return;
        }
        if (error.Error == AccountErrors.InvalidSession)
        {
            Forget();
            Notice = AccountNotice.SessionEnded;
            return;
        }
        Notice = AccountNotice.Refused;
        Error = error.Error;
        RetryAfterSeconds = error.RetryAfterSeconds;
    }

    private void Forget()
    {
        _sessionToken = null;
        _characters.Clear();
        Deleting = null;
        Screen = AccountScreen.SignIn;
        Version++;
    }

    private void ClearNotice()
    {
        Version++;
        Notice = AccountNotice.None;
        Error = string.Empty;
        RetryAfterSeconds = 0;
        Deleting = null;
    }

    private void Send(AccountCall call, string method, string path, byte[]? body, string? token)
    {
        if (Busy) return;
        ClearNotice();
        if (_transport is null || TransportPolicy.AccountBase(_config.ServerUrl, _config.AllowCleartext) is not { } root)
        {
            Notice = AccountNotice.Unreachable; // Main refuses a bad server address at start-up with a screen; this is the second lock
            return;
        }
        Busy = true;
        _pending = call;
        _transport.Send(new AccountHttpRequest(call, method, root + path, body, token));
    }
}
