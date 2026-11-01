using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Server.World;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Stats;
using Game.Shared.World;

namespace Game.Server.Net;

/// <summary>
/// The outcome of a login: a display name for a new character, the character a resume token belongs to, or a refusal (a
/// <see cref="ByeReasons"/> term).
/// </summary>
public sealed record LoginResult(string? DisplayName, string? Refusal, EntityId? Character = null)
{
    /// <summary>Accepted.</summary>
    public static LoginResult Accept(string displayName) => new(displayName, null);

    /// <summary>Refused for <paramref name="reason"/>.</summary>
    public static LoginResult Refuse(string reason) => new(null, reason);

    /// <summary>The resume token belongs to <paramref name="character"/>.</summary>
    public static LoginResult Resume(EntityId character) => new(null, null, character);
}

/// <summary>
/// Decides whether a <c>hello</c> may play. Called on the session's receive loop; must be thread-safe. <b>Seam for T-701:</b> real
/// accounts replace <see cref="DevLogin"/> here (a session token in <c>hello</c>, checked against the account service) and return the
/// account's character instead of a name, and may keep resume tokens across restarts.
/// </summary>
public interface ILoginService
{
    /// <summary>Checks a new login's hello (<see cref="HelloMessage.Name"/> set; version and content hash already checked).</summary>
    LoginResult Login(HelloMessage hello);

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
    }

    /// <summary>Start map id.</summary>
    public string MapId { get; }

    /// <summary>Start cell.</summary>
    public GridCell Cell { get; }

    /// <summary>Start facing (a <c>Directions</c> term).</summary>
    public string Facing { get; }

    /// <summary>Start job id; also the character's display key.</summary>
    public string JobId { get; }

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
