using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Server.Accounts;
using Game.Server.World;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.Net;

/// <summary>
/// The outcome of a login: a display name for a new character (with the account and the permanent character id it stands for, unless it is a dev login), the
/// character a resume token belongs to, or a refusal (a <see cref="ByeReasons"/> term).
/// </summary>
public sealed record LoginResult(string? DisplayName, string? Refusal, EntityId? Character = null, Guid? AccountId = null, Guid? CharacterId = null,
    AppearanceRecord? Appearance = null)
{
    /// <summary>For a <c>banned</c> refusal (T-806): seconds until the ban ends, or null when it never does.</summary>
    public long? RetryAfterSeconds { get; init; }

    /// <summary>Accepted.</summary>
    public static LoginResult Accept(string displayName) => new(displayName, null);

    /// <summary>Accepted: <paramref name="account"/> plays its character <paramref name="character"/>.</summary>
    public static LoginResult Play(string displayName, Guid account, Guid character, AppearanceRecord? appearance = null) =>
        new(displayName, null, null, account, character, appearance);

    /// <summary>Refused for <paramref name="reason"/>.</summary>
    public static LoginResult Refuse(string reason) => new(null, reason);

    /// <summary>The resume token belongs to <paramref name="character"/>.</summary>
    public static LoginResult Resume(EntityId character) => new(null, null, character);
}

/// <summary>
/// Decides whether a <c>hello</c> may play. Called on the session's receive loop, never on a tick thread; must be thread-safe.
/// <see cref="AccountLogin"/> checks real accounts; <see cref="DevLogin"/> is the development stub.
/// </summary>
public interface ILoginService
{
    /// <summary>
    /// Checks a new login's hello (<see cref="HelloMessage.Name"/>, or <see cref="HelloMessage.Session"/> and <see cref="HelloMessage.Character"/>, set; version
    /// and content hash already checked). <paramref name="address"/> is the remote address without its port. May read the database.
    /// </summary>
    ValueTask<LoginResult> LoginAsync(HelloMessage hello, string address, CancellationToken cancellation);

    /// <summary>
    /// Checks a resume's hello (<see cref="HelloMessage.Resume"/> set; version and content hash already checked): the character the token
    /// belongs to, or <see cref="ByeReasons.ResumeFailed"/>. It only looks the token up; the gateway redeems it (single use) when it hands
    /// the character over.
    /// </summary>
    LoginResult Resume(HelloMessage hello);
}

/// <summary>
/// The dev login stub: no password, any display name of 1 to <c>maxNameLength</c> characters without control characters. Only works in
/// development; outside it every login is refused (<c>login_refused</c>), so a production server without real auth lets nobody in.
/// Resume works in every environment: it checks the token against the gateway's <see cref="ResumeTokens"/>, and only characters this
/// server created (by a login it accepted) have tokens.
/// </summary>
public sealed class DevLogin(bool development, int maxNameLength, ResumeTokens tokens) : ILoginService
{
    /// <inheritdoc />
    public ValueTask<LoginResult> LoginAsync(HelloMessage hello, string address, CancellationToken cancellation) => new(Login(hello));

    /// <summary>Checks the name. A hello with a session is refused: there are no accounts here.</summary>
    public LoginResult Login(HelloMessage hello)
    {
        if (!development) return LoginResult.Refuse(ByeReasons.LoginRefused);
        var name = hello.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > maxNameLength || name.Any(char.IsControl)) return LoginResult.Refuse(ByeReasons.LoginRefused);
        return LoginResult.Accept(name);
    }

    /// <inheritdoc />
    public LoginResult Resume(HelloMessage hello) =>
        hello.Resume is { } token && tokens.Find(token) is { } character ? LoginResult.Resume(character) : LoginResult.Refuse(ByeReasons.ResumeFailed);
}

/// <summary>
/// The real login (T-701): a live session token and one of its account's characters. A bad session and another account's character get the same refusal. A
/// <c>hello</c> with a name, and every resume, go to <see cref="DevLogin"/>, which refuses names outside development.
/// </summary>
public sealed class AccountLogin(AccountService accounts, DevLogin dev) : ILoginService
{
    /// <inheritdoc />
    public async ValueTask<LoginResult> LoginAsync(HelloMessage hello, string address, CancellationToken cancellation)
    {
        if (hello.Session is not { } session || hello.Character is not { } character) return dev.Login(hello);
        var result = await accounts.ResolveAsync(session, character, AddressKeys.Of(address), cancellation).ConfigureAwait(false);
        if (result.Ok) return LoginResult.Play(result.Value.Character.Name, result.Value.Account, result.Value.Character.Id, result.Value.Appearance);
        if (result.Error == AccountErrors.AccountBanned)   // T-806: its own answer, with how long is left; never staff's reason
            return LoginResult.Refuse(ByeReasons.Banned) with { RetryAfterSeconds = result.RetryAfterMs > 0 ? (result.RetryAfterMs + 999) / 1000 : null };
        return LoginResult.Refuse(result.Error switch
        {
            AccountErrors.RateLimited => ByeReasons.RateLimit,
            AccountErrors.Unavailable => ByeReasons.ServerError,
            _ => ByeReasons.LoginRefused,
        });
    }

    /// <inheritdoc />
    public LoginResult Resume(HelloMessage hello) => dev.Resume(hello);
}

/// <summary>
/// How a new character starts, from <c>balance.yaml</c> <c>new_character</c>: map, cell, facing and job, and its stat sheet built the way
/// the content tools build one (<see cref="ProgressionRules.NewCharacter"/> for levels and starting stats, then
/// <see cref="BalanceSimulator.Player"/> for the job's bonuses). Thread-safe.
/// </summary>
public sealed class NewCharacters
{
    private readonly BalanceSimulator _simulator;

    /// <summary>Reads the start from validated content.</summary>
    public NewCharacters(ContentDatabase content)
    {
        _simulator = new BalanceSimulator(content);
        var start = content.Balance.NewCharacter;
        MapId = start.StartMapId;
        Cell = new GridCell(start.StartX, start.StartY);
        Facing = start.StartFacing;
        JobId = start.StartJobId;
        Appearance = new AppearanceRules(content.Appearance).Defaults;
    }

    /// <summary>Start map id.</summary>
    public string MapId { get; }

    /// <summary>Start cell.</summary>
    public GridCell Cell { get; }

    /// <summary>Start facing (a <c>Directions</c> term).</summary>
    public string Facing { get; }

    /// <summary>Start job id; also the character's display key.</summary>
    public string JobId { get; }

    /// <summary>The looks of a player that has none stored (T-705): the dev login's.</summary>
    public AppearanceRecord Appearance { get; }

    /// <summary>The stat sheet of a loaded character (T-702): its levels, stats and job, the way a new character's is built; equipment and passives come as it enters.</summary>
    public StatSheet CreateStats(ProgressionState state)
    {
        lock (_simulator) return _simulator.Player(state.JobId, state.BaseLevel, state.JobLevel, state.Stats, state.StatPoints, weapon: null).Sheet;
    }

    /// <summary>A fresh stat sheet for a new character (base and job level 1, starting stat values, the start job's bonuses).</summary>
    public StatSheet CreateStats()
    {
        lock (_simulator)
        {
            var state = _simulator.Progression.NewCharacter(JobId);
            return _simulator.Player(JobId, state.BaseLevel, state.JobLevel, state.Stats, state.StatPoints, weapon: null).Sheet;
        }
    }
}
