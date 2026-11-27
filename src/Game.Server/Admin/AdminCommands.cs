using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Server.Accounts;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Admin;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Admin;

/// <summary>One admin command as the server received it: who, which request, why, and the command's own fields (<see cref="Body"/>, a JSON object).</summary>
public sealed record AdminCall(StaffIdentity Staff, string RequestId, string Reason, JsonElement Body);

/// <summary>What came of a command: an "Admin outcomes" term, the audit row (none when refused), an "Admin errors" term when refused, and the command's result (ids and numbers).</summary>
public sealed record AdminResult(string Outcome, long? AuditId, string? Error, IReadOnlyDictionary<string, object?>? Values)
{
    /// <summary>Refused: nothing was changed and nothing audited.</summary>
    public static AdminResult Refused(string error) => new(AdminOutcomes.Refused, null, error, null);
}

/// <summary>
/// An admin command (T-804; T-806 adds more): it validates its fields, reads what it changes (before), makes the change with its audit row in one transaction when the change is in
/// the database, or writes the row before it answers when it is not, and says what came of it. The role and the reason are checked before it is called
/// (<see cref="AdminCommands"/>); a command that changes the world does it through <see cref="AdminRoad.OnTickAsync"/>.
/// </summary>
public interface IAdminCommand
{
    /// <summary>Its "Admin actions" term; it must be in <see cref="AdminCommandTable"/>.</summary>
    string Action { get; }

    /// <summary>Runs it.</summary>
    Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation);
}

/// <summary>A step on the tick that did not come back in time (the instance is stalled or stopping, or the entity left meanwhile).</summary>
public sealed class AdminStepTimeoutException() : Exception("The admin step did not come back in time.");

/// <summary>
/// What commands go through: the world (steps on the tick), the gateway, the store and its audit rows, the clock. One per server.
/// </summary>
public sealed class AdminRoad(GameWorld world, Gateway gateway, IAdminStore store, AdminOptions options, TimeProvider time)
{
    /// <summary>The world.</summary>
    public GameWorld World { get; } = world;

    /// <summary>The gateway (the sessions and the characters it holds).</summary>
    public Gateway Gateway { get; } = gateway;

    /// <summary>The store.</summary>
    public IAdminStore Store { get; } = store;

    /// <summary>Now (UTC).</summary>
    public DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>The clock (T-806: maintenance's timer).</summary>
    public TimeProvider Time => time;

    /// <summary>Bans, mutes, the support queue and offline character changes (T-806); null without them (a command that needs them answers <c>unavailable</c>).</summary>
    public IModerationStore? Moderation { get; init; }

    /// <summary>Players' reports of chat lines (T-901); null without a database (<c>close_report</c> answers <c>unavailable</c>).</summary>
    public Chat.IChatReportStore? ChatReports { get; init; }

    /// <summary>The moderation checks (T-806), to forget a cached mute when one changes here; null without them.</summary>
    public ModerationChecks? Checks { get; init; }

    /// <summary>New permanent item ids (<c>CharacterSaves.NextItemId</c>, T-702) for items staff make offline; null without saving.</summary>
    public Func<long>? NextItemId { get; init; }

    /// <summary>The loaded content: item ids, stack sizes, the bag's size, walkable cells and the start point (T-806).</summary>
    public Game.Content.Registry.ContentDatabase? Content { get; init; }

    /// <summary>How long an account waits to be erased after a deletion request (<c>--account-erase-days</c>, T-703).</summary>
    public TimeSpan EraseWait { get; init; } = TimeSpan.FromDays(14);

    /// <summary>How long an item or currency change in the world waits for the save that carries its audit row before it answers <c>accepted</c> instead of <c>done</c> (T-806).</summary>
    public int SaveWaitMs { get; init; } = 10_000;

    /// <summary>Maintenance mode's timer (T-806): one per server.</summary>
    public MaintenanceClock Maintenance { get; } = new();

    /// <summary>
    /// Queues a change that a game system runs on the tick of the instance holding its entity (T-806, <see cref="StaffIntent"/>) and waits for it like <see cref="OnTickAsync"/>. When it has
    /// not started in time it is given up for good (<see cref="StaffIntent.TryAbandon"/>) and <see cref="AdminStepTimeoutException"/> is thrown: nothing was changed. One that started is waited
    /// for to the end (it is one step of one tick).
    /// </summary>
    public async Task<AdminStepResult> SendAsync(StaffIntent intent, CancellationToken cancellation)
    {
        if (World.TryEnqueue(intent) != EnqueueResult.Accepted) throw new AdminStepTimeoutException();
        try
        {
            return await intent.Done.WaitAsync(TimeSpan.FromMilliseconds(options.StepTimeoutMs), time, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            if (intent.TryAbandon()) throw new AdminStepTimeoutException();
            return await intent.Done.ConfigureAwait(false);   // it is running on the tick right now
        }
    }

    /// <summary>
    /// Queues <paramref name="step"/> into the tick of the instance that holds <paramref name="entity"/> (as an <see cref="AdminIntent"/>, validated and routed like a player's intent)
    /// and waits for it. Throws <see cref="AdminStepTimeoutException"/> when the world does not know the entity, its queue is full, or the step has not run within
    /// <see cref="AdminOptions.StepTimeoutMs"/> (the step may still run later: a step must be safe to run late, or check what it finds).
    /// </summary>
    public async Task<AdminStepResult> OnTickAsync(EntityId entity, string action, Func<TickContext, Entity, AdminStepResult> step, CancellationToken cancellation)
    {
        var intent = new AdminIntent(entity, action, step);
        if (World.TryEnqueue(intent) != EnqueueResult.Accepted) throw new AdminStepTimeoutException();
        try
        {
            return await intent.Done.WaitAsync(TimeSpan.FromMilliseconds(options.StepTimeoutMs), time, cancellation).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new AdminStepTimeoutException();
        }
    }

    /// <summary>An audit row for <paramref name="call"/>; <paramref name="before"/> and <paramref name="after"/> become JSON objects (ids and numbers only).</summary>
    public AuditDraft Draft(AdminCall call, string action, string targetKind, string targetId, IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after) =>
        new(call.Staff, call.RequestId, action, targetKind, targetId, AdminJson.Object(before), AdminJson.Object(after), call.Reason, Now);
}

/// <summary>
/// Runs admin commands (ARCHITECTURE §12.1): looks the command up, re-checks the staff role against <see cref="AdminCommandTable"/> and the reason, runs it, and publishes
/// <see cref="AdminActionApplied"/> for what it changed. Thread-safe. Logs the staff id, the action and the target id, never the reason or a display name.
/// </summary>
public sealed class AdminCommands
{
    private readonly Dictionary<string, IAdminCommand> _commands = new(StringComparer.Ordinal);
    private readonly AdminRoad _road;
    private readonly AdminEvents _events;
    private readonly IServerLog _log;

    /// <summary>Creates the runner; throws when a command is not in the table, or twice. <paramref name="table"/> is <see cref="AdminCommandTable.LowestRole"/> (a test may give its own).</summary>
    public AdminCommands(IEnumerable<IAdminCommand> commands, AdminRoad road, AdminEvents events, IServerLog log, IReadOnlyDictionary<string, string>? table = null)
    {
        Table = table ?? AdminCommandTable.LowestRole;
        foreach (var command in commands)
        {
            if (!Table.ContainsKey(command.Action)) throw new ArgumentException($"Admin command '{command.Action}' has no row in AdminCommandTable.", nameof(commands));
            if (!_commands.TryAdd(command.Action, command)) throw new ArgumentException($"Two admin commands are '{command.Action}'.", nameof(commands));
        }
        _road = road;
        _events = events;
        _log = log;
    }

    /// <summary>The commands there are.</summary>
    public IReadOnlyCollection<string> Actions => _commands.Keys;

    /// <summary>The lowest role of each command.</summary>
    public IReadOnlyDictionary<string, string> Table { get; }

    /// <summary>
    /// Runs the command <paramref name="action"/> for <paramref name="staff"/>. Refused (nothing changed) for an unknown command (<c>not_found</c>), a role below its lowest
    /// (<c>forbidden</c>), a body that is not an object (<c>invalid_request</c>), a missing or overlong reason (<c>reason_required</c>), a database that does not answer
    /// (<c>unavailable</c>) or a world that does not (<c>timeout</c>).
    /// </summary>
    public async Task<AdminResult> RunAsync(string action, StaffIdentity staff, string requestId, JsonElement body, CancellationToken cancellation)
    {
        if (!_commands.TryGetValue(action, out var command)) return AdminResult.Refused(AdminErrors.NotFound);
        if (!(Table.TryGetValue(action, out var lowest) && StaffRoles.AtLeast(staff.Role, lowest)))
        {
            _log.Warn($"[admin] {staff} may not run '{action}' (request {requestId})");
            return AdminResult.Refused(AdminErrors.Forbidden);
        }
        if (body.ValueKind != JsonValueKind.Object) return AdminResult.Refused(AdminErrors.InvalidRequest);
        if (ReasonOf(body) is not { } reason) return AdminResult.Refused(AdminErrors.ReasonRequired);
        AdminResult result;
        try
        {
            result = await command.RunAsync(new AdminCall(staff, requestId, reason, body), _road, cancellation).ConfigureAwait(false);
        }
        catch (StoreUnavailableException e)
        {
            _log.Warn($"[admin] '{action}' by {staff} not run: {e.Message} (request {requestId})");
            return AdminResult.Refused(AdminErrors.Unavailable);
        }
        catch (AdminStepTimeoutException)
        {
            _log.Warn($"[admin] '{action}' by {staff} not run: the world did not answer in time (request {requestId})");
            return AdminResult.Refused(AdminErrors.Timeout);
        }
        if (result.AuditId is { } audit && result.Outcome != AdminOutcomes.Refused)
        {
            var target = result.Values is { } v && v.TryGetValue("target_id", out var t) ? t?.ToString() ?? string.Empty : string.Empty;
            var kind = result.Values is { } w && w.TryGetValue("target_kind", out var k) ? k?.ToString() ?? string.Empty : string.Empty;
            _log.Info($"[admin] {staff} ran '{action}' on {kind} {target}: {result.Outcome} (audit {audit}, request {requestId})");
            _events.Raise(new AdminActionApplied(audit, requestId, staff.Id, staff.Role, action, kind, target));
        }
        return result;
    }

    /// <summary>The reason, trimmed: 1 to <see cref="AdminCommandFields.MaxReasonLength"/> characters, line breaks and tabs the only control characters; else null.</summary>
    public static string? ReasonOf(JsonElement body)
    {
        if (!body.TryGetProperty(AdminCommandFields.Reason, out var field) || field.ValueKind != JsonValueKind.String) return null;
        var reason = field.GetString()!.Trim();
        if (reason.Length is 0 or > AdminCommandFields.MaxReasonLength) return null;
        return reason.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ? null : reason;
    }
}

/// <summary>
/// <c>kick</c> (T-804, <c>support</c> and above): ends the account's live game as T-710's session end does. In one transaction every session of the account ends and the audit row is
/// written; then the gateway says <c>bye kicked</c> to whatever plays with them, and the character, also one in its grace period, leaves the world on its next tick, saved like on any
/// other leave, its resume token dead. The player signs in again. Before the change, a step on the character's tick reads where it is (validated there like an intent).
/// </summary>
public sealed class KickCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.Kick;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!call.Body.TryGetProperty(AdminCommandFields.AccountId, out var field) || field.ValueKind != JsonValueKind.String || !Guid.TryParse(field.GetString(), out var account))
            return AdminResult.Refused(AdminErrors.InvalidRequest);
        var before = new Dictionary<string, object?> { ["online"] = false };
        var link = road.Gateway.LinksOf(account).FirstOrDefault();
        if (link is not null)
        {
            before["online"] = true;
            before["character_id"] = link.CharacterId?.ToString();
            before["in_grace"] = link.InGrace;
            if (road.World.LocationOf(link.Character) is not null)
            {
                var seen = await road.OnTickAsync(link.Character, Action, static (tick, entity) => AdminStepResult.Ok(new Dictionary<string, object?>
                {
                    ["map"] = entity.MapId,
                    ["cell_x"] = entity.Cell.X,
                    ["cell_y"] = entity.Cell.Y,
                    ["dead"] = entity.Dead,
                    ["tick"] = tick.TickNumber,
                }), cancellation).ConfigureAwait(false);
                if (seen.Error is null)
                    foreach (var (key, value) in seen.Values) before[key] = value;
            }
        }
        var targetId = account.ToString();
        var ended = await road.Store.EndSessionsAsync(account, count => road.Draft(call, Action, AdminTargetKinds.Account, targetId, before,
            new Dictionary<string, object?> { ["online"] = false, ["sessions_ended"] = count }), CancellationToken.None).ConfigureAwait(false); // not cut off halfway: once committed, the gateway must hear of it
        if (ended is null) return AdminResult.Refused(AdminErrors.UnknownTarget);
        road.Gateway.EndSessions(new EndedSessions(account, ended.TokenHashes.Select(SessionTokens.KeyOf).ToList(), All: true), ByeReasons.Kicked, "kicked by staff");
        return new AdminResult(link is null ? AdminOutcomes.Done : AdminOutcomes.Accepted, ended.AuditId, null, new Dictionary<string, object?>
        {
            ["target_kind"] = AdminTargetKinds.Account,
            ["target_id"] = targetId,
            ["was_online"] = link is not null,
            ["sessions_ended"] = ended.TokenHashes.Count,
        });
    }
}

/// <summary>JSON for audit values: an object of ids, numbers, booleans and text (nothing else is written).</summary>
public static class AdminJson
{
    /// <summary>The values as a JSON object, keys in order.</summary>
    public static string Object(IReadOnlyDictionary<string, object?> values)
    {
        var node = new JsonObject();
        foreach (var (key, value) in values)
            node[key] = value switch
            {
                null => null,
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                string s => JsonValue.Create(s),
                Guid g => JsonValue.Create(g.ToString()),
                _ => throw new ArgumentException($"Audit value '{key}' is a {value.GetType().Name}; only ids, numbers, booleans and text go in an audit row.", nameof(values)),
            };
        return node.ToJsonString();
    }
}
