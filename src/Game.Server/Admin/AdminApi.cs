using System.Globalization;
using System.Text.Json;
using Game.Server.Accounts;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Accounts;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Game.Server.Admin;

/// <summary>
/// The admin API's requests (ARCHITECTURE §12, docs/ADMIN_API.md): every one is checked in this order. Its address must not be locked out by failed credentials (429); it must
/// carry the service credential (401, counted against the address); the credential must be within its rate (429); the staff member must be well formed (400 <c>invalid_staff</c>)
/// with a role that is a role (403 <c>unknown_role</c>). Then reads answer, and commands go to <see cref="AdminCommands"/>, which checks the role for the command. Every answer is
/// JSON, <c>no-store</c>, with the request id; nothing outside <see cref="AdminPaths.Root"/> is served.
/// </summary>
public sealed class AdminApi(AdminOptions options, ServiceCredential credential, AdminRateLimit limits, AdminCommands commands, IAdminStore store, AdminStatusSource status,
    Gateway gateway, IServerLog log, IModerationStore? moderation = null, MaintenanceClock? maintenance = null, Chat.IChatReportStore? chatReports = null)
{
    /// <summary>The default page size.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>The longest search text (a name prefix, a target or a cursor).</summary>
    public const int MaxQueryLength = 128;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        WriteIndented = false,
    };

    /// <summary>Answers one request. Never throws for a request's content; a failure is logged by type and answered <c>unavailable</c>.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers.XContentTypeOptions = "nosniff";
        var requestId = RequestIdOf(context.Request.Headers[AdminHeaders.RequestId].ToString());
        headers[AdminHeaders.RequestId] = requestId;
        var path = context.Request.Path.Value ?? string.Empty;
        try
        {
            await RouteAsync(context, path, requestId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // the panel went away, or the request ran past its time
        }
        catch (Exception e)
        {
            log.Error($"[admin] {context.Request.Method} {Shape(path)} failed (request {requestId}): {e.GetType().FullName}\n{e.StackTrace}"); // the type only: a message could quote the request
            if (!context.Response.HasStarted) await FailAsync(context, StatusCodes.Status503ServiceUnavailable, AdminErrors.Unavailable, requestId).ConfigureAwait(false);
        }
    }

    private async Task RouteAsync(HttpContext context, string path, string requestId)
    {
        if (path != AdminPaths.Root && !path.StartsWith(AdminPaths.Root + "/", StringComparison.Ordinal))
        {
            await FailAsync(context, StatusCodes.Status404NotFound, AdminErrors.NotFound, requestId).ConfigureAwait(false);
            return;
        }
        var address = context.Connection.RemoteIpAddress;
        if (!limits.MayTry(address, out var wait))
        {
            await FailAsync(context, StatusCodes.Status429TooManyRequests, AdminErrors.RateLimited, requestId, wait).ConfigureAwait(false);
            return;
        }
        if (!credential.Matches(ServiceCredential.FromHeader(context.Request.Headers.Authorization.ToString())))
        {
            limits.CountFailure(address);
            log.Warn($"[admin] a request without the service credential from {AddressKeys.Of(address)} (request {requestId})");
            await FailAsync(context, StatusCodes.Status401Unauthorized, AdminErrors.Unauthorized, requestId).ConfigureAwait(false);
            return;
        }
        if (!limits.AllowRequest(credential.Key, out var retry))
        {
            await FailAsync(context, StatusCodes.Status429TooManyRequests, AdminErrors.RateLimited, requestId, retry).ConfigureAwait(false);
            return;
        }
        var request = context.Request.Headers;
        if (StaffIdentity.Parse(request[AdminHeaders.StaffId].ToString() is { Length: > 0 } i ? i : null, request[AdminHeaders.StaffName].ToString() is { Length: > 0 } n ? n : null,
                request[AdminHeaders.StaffRole].ToString() is { Length: > 0 } r ? r : null, out var staffError) is not { } staff)
        {
            await FailAsync(context, staffError == AdminErrors.UnknownRole ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest, staffError!, requestId).ConfigureAwait(false);
            return;
        }
        var method = context.Request.Method;
        var cancel = context.RequestAborted;
        var rest = path[AdminPaths.Root.Length..];
        if (HttpMethods.IsGet(method))
        {
            if (!StaffRoles.AtLeast(staff.Role, AdminCommandTable.ReadRole))
            {
                await FailAsync(context, StatusCodes.Status403Forbidden, AdminErrors.Forbidden, requestId).ConfigureAwait(false);
                return;
            }
            await ReadAsync(context, rest, requestId, cancel).ConfigureAwait(false);
            return;
        }
        if (HttpMethods.IsPost(method) && rest.StartsWith("/commands/", StringComparison.Ordinal))
        {
            var action = rest["/commands/".Length..];
            using var body = await ReadBodyAsync(context).ConfigureAwait(false);
            if (body is null)
            {
                await FailAsync(context, StatusCodes.Status400BadRequest, AdminErrors.InvalidRequest, requestId).ConfigureAwait(false);
                return;
            }
            var result = await commands.RunAsync(action, staff, requestId, body.RootElement, cancel).ConfigureAwait(false);
            var code = result.Outcome != AdminOutcomes.Refused ? StatusCodes.Status200OK : result.Error switch
            {
                AdminErrors.NotFound => StatusCodes.Status404NotFound,
                AdminErrors.Forbidden => StatusCodes.Status403Forbidden,
                AdminErrors.InvalidRequest or AdminErrors.ReasonRequired => StatusCodes.Status400BadRequest,
                AdminErrors.Unavailable => StatusCodes.Status503ServiceUnavailable,
                AdminErrors.Timeout => StatusCodes.Status504GatewayTimeout,
                _ => StatusCodes.Status409Conflict,
            };
            await WriteAsync(context, code, new AdminCommandAnswer(result.Outcome, requestId, result.AuditId, result.Error, result.Values)).ConfigureAwait(false);
            return;
        }
        await FailAsync(context, StatusCodes.Status404NotFound, AdminErrors.NotFound, requestId).ConfigureAwait(false);
    }

    private async Task ReadAsync(HttpContext context, string rest, string requestId, CancellationToken cancel)
    {
        var query = context.Request.Query;
        try
        {
            switch (rest)
            {
                case "/status":
                    await WriteAsync(context, StatusCodes.Status200OK, status.Read() with { Maintenance = maintenance?.Status() }).ConfigureAwait(false);
                    return;
                case "/commands":
                    await WriteAsync(context, StatusCodes.Status200OK, new
                    {
                        commands = commands.Table.Where(c => commands.Actions.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal)
                            .Select(c => new { action = c.Key, lowest_role = c.Value }),
                        read_role = AdminCommandTable.ReadRole,
                        roles = StaffRoles.All.Ordered,
                    }).ConfigureAwait(false);
                    return;
                case "/accounts":
                {
                    var name = Text(query, "name");
                    var normalized = name is null ? null : UsernamePrefix(name) ?? throw new BadQuery();
                    var page = await store.FindAccountsAsync(new AccountQuery(normalized, Id(query, "id"), Text(query, "after"), Limit(query)), cancel).ConfigureAwait(false);
                    await WriteAsync(context, StatusCodes.Status200OK, new { items = page.Items.Select(WithPresence), next = page.Next }).ConfigureAwait(false);
                    return;
                }
                case "/characters":
                {
                    var name = Text(query, "name");
                    var page = await store.FindCharactersAsync(new CharacterQuery(name is null ? null : CharacterNameRules.Key(name.Trim()), Id(query, "id"), Id(query, "account"),
                        Text(query, "after"), Limit(query)), cancel).ConfigureAwait(false);
                    await WriteAsync(context, StatusCodes.Status200OK, new { items = page.Items.Select(WithPresence), next = page.Next }).ConfigureAwait(false);
                    return;
                }
                case "/requests":
                {
                    if (moderation is null) throw new StoreUnavailableException("no moderation store");
                    var state = Text(query, "status");
                    if (state is not null && !SupportRequestStatuses.All.Contains(state)) throw new BadQuery();
                    var page = await moderation.FindRequestsAsync(state, Id(query, "account"), Number(query, "before"), Limit(query), cancel).ConfigureAwait(false);
                    await WriteAsync(context, StatusCodes.Status200OK, new { items = page.Items.Select(RequestJson), next = page.Next }).ConfigureAwait(false);
                    return;
                }
                case "/chat-reports":
                {
                    // T-901: what a player reported, with what was said (the server's copy) and the ids of who said it and who reported it.
                    if (chatReports is null) throw new StoreUnavailableException("no chat report store");
                    var state = Text(query, "status");
                    if (state is not null && !ChatReportStatuses.All.Contains(state)) throw new BadQuery();
                    var page = await chatReports.FindReportsAsync(state, Id(query, "account"), Number(query, "before"), Limit(query), cancel).ConfigureAwait(false);
                    await WriteAsync(context, StatusCodes.Status200OK, new { items = page.Items.Select(ReportJson), next = page.Next }).ConfigureAwait(false);
                    return;
                }
                case "/audit":
                {
                    var action = Text(query, "action");
                    if (action is not null && !AdminActions.All.Contains(action)) throw new BadQuery();
                    var page = await store.SearchAuditAsync(new AuditQuery(Text(query, "target"), Text(query, "staff"), action, Time(query, "from"), Time(query, "to"),
                        Number(query, "before"), Limit(query)), cancel).ConfigureAwait(false);
                    await WriteAsync(context, StatusCodes.Status200OK, new { items = page.Items.Select(AuditJson), next = page.Next }).ConfigureAwait(false);
                    return;
                }
            }
            if (rest.StartsWith("/accounts/", StringComparison.Ordinal) && rest.EndsWith(AdminPaths.ModerationSuffix, StringComparison.Ordinal)
                && Guid.TryParseExact(rest["/accounts/".Length..^AdminPaths.ModerationSuffix.Length], "D", out var moderated))
            {
                if (moderation is null) throw new StoreUnavailableException("no moderation store");
                if (await store.GetAccountAsync(moderated, cancel).ConfigureAwait(false) is null)
                {
                    await FailAsync(context, StatusCodes.Status404NotFound, AdminErrors.UnknownTarget, requestId).ConfigureAwait(false);
                    return;
                }
                var now = DateTime.UtcNow;
                var sanctions = await moderation.SanctionsAsync(moderated, cancel).ConfigureAwait(false);
                var requests = await moderation.FindRequestsAsync(null, moderated, null, options.MaxPageSize, cancel).ConfigureAwait(false);
                await WriteAsync(context, StatusCodes.Status200OK, new
                {
                    account_id = moderated,
                    banned = sanctions.Any(s => s.Kind == SanctionKinds.Ban && s.InForce(now)),
                    muted = sanctions.Any(s => s.Kind == SanctionKinds.Mute && s.InForce(now)),
                    bans = sanctions.Where(s => s.Kind == SanctionKinds.Ban).Select(s => SanctionJson(s, now)),
                    mutes = sanctions.Where(s => s.Kind == SanctionKinds.Mute).Select(s => SanctionJson(s, now)),
                    requests = requests.Items.Select(RequestJson),
                }).ConfigureAwait(false);
                return;
            }
            if (rest.StartsWith("/characters/", StringComparison.Ordinal) && rest.EndsWith(AdminPaths.StaffChangesSuffix, StringComparison.Ordinal)
                && Guid.TryParseExact(rest["/characters/".Length..^AdminPaths.StaffChangesSuffix.Length], "D", out var changed))
            {
                if (await store.GetCharacterAsync(changed, cancel).ConfigureAwait(false) is null)
                {
                    await FailAsync(context, StatusCodes.Status404NotFound, AdminErrors.UnknownTarget, requestId).ConfigureAwait(false);
                    return;
                }
                // Every audit row about a character is a staff change to it: give_item, remove_item, change_currency, unstick.
                var page = await store.SearchAuditAsync(new AuditQuery(changed.ToString(), null, null, null, null, Number(query, "before"), Limit(query)), cancel).ConfigureAwait(false);
                await WriteAsync(context, StatusCodes.Status200OK, new { items = page.Items.Select(AuditJson), next = page.Next }).ConfigureAwait(false);
                return;
            }
            if (rest.StartsWith("/accounts/", StringComparison.Ordinal) && Guid.TryParseExact(rest["/accounts/".Length..], "D", out var accountId))
            {
                if (await store.GetAccountAsync(accountId, cancel).ConfigureAwait(false) is not { } found)
                {
                    await FailAsync(context, StatusCodes.Status404NotFound, AdminErrors.UnknownTarget, requestId).ConfigureAwait(false);
                    return;
                }
                await WriteAsync(context, StatusCodes.Status200OK, new { account = WithPresence(found.Account), characters = found.Characters.Select(WithPresence) }).ConfigureAwait(false);
                return;
            }
            if (rest.StartsWith("/characters/", StringComparison.Ordinal) && Guid.TryParseExact(rest["/characters/".Length..], "D", out var characterId))
            {
                if (await store.GetCharacterAsync(characterId, cancel).ConfigureAwait(false) is not { } found)
                {
                    await FailAsync(context, StatusCodes.Status404NotFound, AdminErrors.UnknownTarget, requestId).ConfigureAwait(false);
                    return;
                }
                await WriteAsync(context, StatusCodes.Status200OK, new { character = WithPresence(found.Character), state = StateJson(found.State) }).ConfigureAwait(false);
                return;
            }
            await FailAsync(context, StatusCodes.Status404NotFound, AdminErrors.NotFound, requestId).ConfigureAwait(false);
        }
        catch (BadQuery)
        {
            await FailAsync(context, StatusCodes.Status400BadRequest, AdminErrors.InvalidRequest, requestId).ConfigureAwait(false);
        }
        catch (StoreUnavailableException e)
        {
            log.Warn($"[admin] read {Shape(rest)} not answered: {e.Message} (request {requestId})");
            await FailAsync(context, StatusCodes.Status503ServiceUnavailable, AdminErrors.Unavailable, requestId).ConfigureAwait(false);
        }
    }

    private object WithPresence(AdminAccount a) => new
    {
        id = a.Id, username = a.Username, created_at = a.CreatedAt, password_changed_at = a.PasswordChangedAt, erase_after = a.EraseAfter, characters = a.Characters,
        sessions = a.Sessions, online = gateway.LinksOf(a.Id).Count > 0,
    };

    private object WithPresence(AdminCharacter c) => new
    {
        id = c.Id, account_id = c.AccountId, slot = c.Slot, name = c.Name, job_id = c.JobId, created_at = c.CreatedAt, deleted_at = c.DeletedAt, online = gateway.HoldsCharacter(c.Id),
    };

    private static object SanctionJson(Sanction s, DateTime now) => new
    {
        id = s.Id, kind = s.Kind, from = s.CreatedAt, until = s.Until, in_force = s.InForce(now), audit_id = s.AuditId, lifted_at = s.LiftedAt, lifted_audit_id = s.LiftedAuditId,
    };

    private static object RequestJson(SupportRequest r) => new
    {
        id = r.Id, account_id = r.AccountId, kind = r.Kind, status = r.Status, opened_at = r.OpenedAt, opened_audit_id = r.OpenedAuditId, closed_at = r.ClosedAt, closed_audit_id = r.ClosedAuditId,
    };

    private static object ReportJson(Chat.ChatReport r) => new
    {
        id = r.Id, reported_at = r.ReportedAt, reporter_account_id = r.ReporterAccountId, reporter_character_id = r.ReporterCharacterId, sender_account_id = r.SenderAccountId,
        sender_character_id = r.SenderCharacterId, channel = r.Channel, text = r.Text, said_at = r.SaidAt, status = r.Status, closed_at = r.ClosedAt, closed_audit_id = r.ClosedAuditId,
    };

    private static object AuditJson(AuditRecord r) => new
    {
        id = r.Id, at = r.At, request_id = r.RequestId, staff_id = r.StaffId, staff_name = r.StaffName, staff_role = r.StaffRole, action = r.Action, target_kind = r.TargetKind,
        target_id = r.TargetId, before = JsonDocument.Parse(r.Before).RootElement.Clone(), after = JsonDocument.Parse(r.After).RootElement.Clone(), reason = r.Reason,
    };

    private static object? StateJson(AdminSavedState? s) => s is null ? null : new
    {
        version = s.Version, saved_at = s.SavedAt, claim = s.Claim, held_by = s.HeldBy, map_id = s.MapId, cell_x = s.CellX, cell_y = s.CellY, facing = s.Facing, hp = s.Hp, sp = s.Sp,
        save_map_id = s.SaveMapId, save_cell_x = s.SaveCellX, save_cell_y = s.SaveCellY, base_level = s.BaseLevel, base_exp = s.BaseExp, job_id = s.JobId, job_level = s.JobLevel,
        job_exp = s.JobExp, stat_points = s.StatPoints, skill_points = s.SkillPoints, currency = s.Currency, stats = Raw(s.Stats), skills = Raw(s.Skills), quests = Raw(s.Quests),
        cooldowns = Raw(s.Cooldowns), set_aside = Raw(s.SetAside), outfit_slots = Raw(s.OutfitSlots), outfits_hidden = s.OutfitsHidden, outfits = s.Outfits,
        items = s.Items.Select(i => new { id = i.Id, item_id = i.ItemId, quantity = i.Quantity, slot = i.Slot, cards = i.Cards, refine = i.Refine, aside = i.Aside }),
    };

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class BadQuery : Exception;

    /// <summary>A username prefix in the stored form (NFKC, lower case, <c>a-z 0-9 . _ -</c>), or null when it has other characters.</summary>
    internal static string? UsernamePrefix(string text)
    {
        string normalized;
        try
        {
            normalized = text.Trim().Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null; // not well-formed UTF-16
        }
        return normalized.Length > 0 && normalized.Length <= AccountRules.MaxUsernameLength && normalized.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
            ? normalized : null;
    }

    private static string? Text(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var values)) return null;
        if (values.Count != 1 || values[0] is not { Length: > 0 and <= MaxQueryLength } text || text.Any(char.IsControl)) throw new BadQuery();
        return text;
    }

    private static Guid? Id(IQueryCollection query, string name) =>
        Text(query, name) is { } text ? Guid.TryParseExact(text, "D", out var id) ? id : throw new BadQuery() : null;

    private static long? Number(IQueryCollection query, string name) =>
        Text(query, name) is { } text ? long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new BadQuery() : null;

    private static DateTime? Time(IQueryCollection query, string name) =>
        Text(query, name) is { } text
            ? DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.UtcDateTime : throw new BadQuery()
            : null;

    private int Limit(IQueryCollection query) =>
        Text(query, "limit") is { } text
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= options.MaxPageSize ? n : throw new BadQuery()
            : Math.Min(DefaultPageSize, options.MaxPageSize);

    private async Task<JsonDocument?> ReadBodyAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentType is not { } type || !type.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase)) return null;
        if (request.ContentLength > options.MaxBodyBytes) return null;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = options.MaxBodyBytes;
        var buffer = new byte[options.MaxBodyBytes + 1];
        var count = 0;
        try
        {
            while (count < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(count), context.RequestAborted).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > options.MaxBodyBytes) return null;
            return JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (Exception e) when (e is BadHttpRequestException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A request id from the header when it is one (1 to 64 of <c>A-Z a-z 0-9 . _ -</c>), else a new one.</summary>
    public static string RequestIdOf(string? header) =>
        header is { Length: > 0 and <= 64 } id && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? id : Guid.NewGuid().ToString("N");

    /// <summary>A path with its ids cut out, for the log.</summary>
    private static string Shape(string path) => path.Length > 64 ? path[..64] + "..." : string.Concat(path.Select(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' ? c : '?'));

    private static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json, context.RequestAborted).ConfigureAwait(false);
    }

    private static Task FailAsync(HttpContext context, int status, string error, string requestId, int retryAfterSeconds = 0)
    {
        if (retryAfterSeconds > 0) context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return WriteAsync(context, status, new { error, request_id = requestId });
    }
}
