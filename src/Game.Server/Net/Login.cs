using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Stats;
using Game.Shared.World;

namespace Game.Server.Net;

/// <summary>The outcome of a login: the character's display name, or a refusal (a <see cref="ByeReasons"/> term).</summary>
public sealed record LoginResult(string? DisplayName, string? Refusal)
{
    /// <summary>Accepted.</summary>
    public static LoginResult Accept(string displayName) => new(displayName, null);

    /// <summary>Refused for <paramref name="reason"/>.</summary>
    public static LoginResult Refuse(string reason) => new(null, reason);
}

/// <summary>
/// Decides whether a <c>hello</c> may play. Called on the session's receive loop; must be thread-safe. <b>Seam for T-701:</b> real
/// accounts replace <see cref="DevLogin"/> here (a session token in <c>hello</c>, checked against the account service) and return the
/// account's character instead of a name; T-210's resume token is checked here too.
/// </summary>
public interface ILoginService
{
    /// <summary>Checks the hello (its version has already been checked).</summary>
    LoginResult Login(HelloMessage hello);
}

/// <summary>
/// The dev login stub: no password, any display name of 1 to <c>maxNameLength</c> characters without control characters. Only works in
/// development; outside it every login is refused (<c>login_refused</c>), so a production server without real auth lets nobody in.
/// </summary>
public sealed class DevLogin(bool development, int maxNameLength) : ILoginService
{
    /// <inheritdoc />
    public LoginResult Login(HelloMessage hello)
    {
        if (!development) return LoginResult.Refuse(ByeReasons.LoginRefused);
        var name = hello.Name.Trim();
        if (name.Length == 0 || name.Length > maxNameLength || name.Any(char.IsControl)) return LoginResult.Refuse(ByeReasons.LoginRefused);
        return LoginResult.Accept(name);
    }
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
