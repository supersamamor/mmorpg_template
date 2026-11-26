using Game.Server.Accounts;
using Game.Server.World;
using Game.Server.World.Admin;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Admin;

/// <summary>What the moderation and support commands share (T-806).</summary>
internal static class Moderation
{
    /// <summary>The account's ban as the account service asks for it: until when (null: never ends), or null when there is none. Reads the database; any thread but a tick.</summary>
    public static async Task<BanState?> BanOfAsync(IModerationStore store, Guid account, TimeProvider time, CancellationToken cancellation) =>
        await store.ActiveSanctionAsync(account, SanctionKinds.Ban, time.GetUtcNow().UtcDateTime, cancellation).ConfigureAwait(false) is { } ban ? new BanState(ban.Until) : null;

    /// <summary>Seconds until <paramref name="until"/> (at least 1), or null for a ban that never ends.</summary>
    /// <summary><paramref name="time"/> cut to what the database keeps (microseconds), so a ban's end read back is the one that was answered: rounding it up would add a second.</summary>
    public static DateTime AsStored(DateTime time) => new(time.Ticks - time.Ticks % 10, time.Kind);

    public static long? SecondsLeft(DateTime? until, DateTime now) => until is { } u ? Math.Max(1, (long)Math.Ceiling((u - now).TotalSeconds)) : null;

    public static AdminResult Result(string outcome, long? auditId, string targetKind, string targetId, Dictionary<string, object?>? values = null)
    {
        var all = new Dictionary<string, object?> { ["target_kind"] = targetKind, ["target_id"] = targetId };
        if (values is not null) foreach (var (key, value) in values) all[key] = value;
        return new AdminResult(outcome, auditId, null, all);
    }
}

// ---- bans and mutes ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// <c>ban</c> (T-806, <c>gm</c>): the account may not sign in or play, for <c>minutes</c> or, without them, for good. In one transaction the ban is stored, every session of the account ends and
/// the audit row is written; then the gateway says <c>bye banned</c> (with how long is left) to whatever plays, and the character leaves the world, saved. Sign-in and <c>hello</c> ask the
/// database, so the ban holds on every server process and after restarts; a game on another process ends within that process's ban watch (<see cref="BanWatch"/>).
/// </summary>
public sealed class BanCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.Ban;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Id(call.Body, AdminCommandFields.AccountId, out var account)
            || !AdminFields.Number(call.Body, AdminCommandFields.Minutes, 1, AdminCommandFields.MaxMinutes, out long? minutes, optional: true))
            return AdminResult.Refused(AdminErrors.InvalidRequest);
        if (road.Moderation is not { } store) return AdminResult.Refused(AdminErrors.Unavailable);
        var now = Moderation.AsStored(road.Now);
        DateTime? until = minutes is { } m ? now.AddMinutes(m) : null;
        var online = road.Gateway.LinksOf(account).Count > 0;
        var target = account.ToString();
        var draft = road.Draft(call, Action, AdminTargetKinds.Account, target, new Dictionary<string, object?> { ["banned"] = false, ["online"] = online },
            new Dictionary<string, object?> { ["banned"] = true, ["until"] = AdminFields.Iso(until), ["minutes"] = minutes });
        var change = await store.AddSanctionAsync(account, SanctionKinds.Ban, until, now, draft, CancellationToken.None).ConfigureAwait(false);   // once committed, the gateway must hear of it
        if (change.Error is { } error) return AdminResult.Refused(error);
        road.Gateway.EndSessions(new EndedSessions(account, change.EndedSessions.Select(SessionTokens.KeyOf).ToList(), All: true), ByeReasons.Banned, "banned by staff",
            Moderation.SecondsLeft(until, now));
        return Moderation.Result(online ? AdminOutcomes.Accepted : AdminOutcomes.Done, change.AuditId, AdminTargetKinds.Account, target, new()
        {
            ["until"] = AdminFields.Iso(until), ["was_online"] = online, ["sessions_ended"] = change.EndedSessions.Count,
        });
    }
}

/// <summary><c>unban</c> (T-806, <c>gm</c>): lifts the ban in force; the player can sign in again at once.</summary>
public sealed class UnbanCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.Unban;

    /// <inheritdoc />
    public Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation) => Lift.RunAsync(call, road, Action, SanctionKinds.Ban);
}

/// <summary>
/// <c>mute</c> (T-806, <c>support</c>): the account may not speak for <c>minutes</c>. There is no chat yet (T-901): the mute is stored, shown in the reads, and answered by
/// <see cref="ModerationChecks.MaySpeakAsync"/>, which chat will call before it sends anything a player says.
/// </summary>
public sealed class MuteCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.Mute;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Id(call.Body, AdminCommandFields.AccountId, out var account)
            || !AdminFields.Number(call.Body, AdminCommandFields.Minutes, 1, AdminCommandFields.MaxMinutes, out long minutes))
            return AdminResult.Refused(AdminErrors.InvalidRequest);
        if (road.Moderation is not { } store) return AdminResult.Refused(AdminErrors.Unavailable);
        var now = Moderation.AsStored(road.Now);
        var until = now.AddMinutes(minutes);
        var target = account.ToString();
        var draft = road.Draft(call, Action, AdminTargetKinds.Account, target, new Dictionary<string, object?> { ["muted"] = false },
            new Dictionary<string, object?> { ["muted"] = true, ["until"] = AdminFields.Iso(until), ["minutes"] = minutes });
        var change = await store.AddSanctionAsync(account, SanctionKinds.Mute, until, now, draft, CancellationToken.None).ConfigureAwait(false);
        if (change.Error is { } error) return AdminResult.Refused(error);
        road.Checks?.Forget(account);
        return Moderation.Result(AdminOutcomes.Done, change.AuditId, AdminTargetKinds.Account, target, new() { ["until"] = AdminFields.Iso(until) });
    }
}

/// <summary><c>unmute</c> (T-806, <c>support</c>): lifts the mute in force.</summary>
public sealed class UnmuteCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.Unmute;

    /// <inheritdoc />
    public Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation) => Lift.RunAsync(call, road, Action, SanctionKinds.Mute);
}

internal static class Lift
{
    public static async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, string action, string kind)
    {
        if (!AdminFields.Id(call.Body, AdminCommandFields.AccountId, out var account)) return AdminResult.Refused(AdminErrors.InvalidRequest);
        if (road.Moderation is not { } store) return AdminResult.Refused(AdminErrors.Unavailable);
        var target = account.ToString();
        var flag = kind == SanctionKinds.Ban ? "banned" : "muted";
        var change = await store.LiftSanctionAsync(account, kind, road.Now, s => road.Draft(call, action, AdminTargetKinds.Account, target,
            new Dictionary<string, object?> { [flag] = true, ["until"] = AdminFields.Iso(s.Until), ["sanction_id"] = s.Id }, new Dictionary<string, object?> { [flag] = false }),
            CancellationToken.None).ConfigureAwait(false);
        if (change.Error is { } error) return AdminResult.Refused(error);
        road.Checks?.Forget(account);
        return Moderation.Result(AdminOutcomes.Done, change.AuditId, AdminTargetKinds.Account, target, new() { ["sanction_id"] = change.Sanction!.Id });
    }
}

// ---- support fixes ----------------------------------------------------------------------------------------------------------------------

/// <summary>
/// The road of a support fix to one character (T-806, ARCHITECTURE §3 "Saving characters"): never both at once, never lost by a save that lands later.
/// <list type="bullet">
/// <item>In this server's world (played, or in its grace period): the system that owns the bag or the place changes it on the tick (<see cref="AdminRoad.SendAsync"/>). An item or currency
/// change of a saved character carries its audit row into the next save's transaction (<see cref="World.Saving.StaffNote"/>): the command waits for that save and answers <c>done</c>
/// with the row, or <c>accepted</c> (no row yet) when it takes longer than <see cref="AdminRoad.SaveWaitMs"/>; the row then lands with the save. A place change is written to the log
/// before the answer, as T-804's writes that are not in the database are.</item>
/// <item>Not in the world: in the database, under T-702's claim rules (<see cref="IModerationStore.EditCharacterAsync"/>): refused while any live server holds it
/// (<c>character_busy</c>: another server plays it, or its last save has not landed), and the claim moves on, so a late save of an earlier stay is refused.</item>
/// </list>
/// </summary>
internal static class CharacterFix
{
    public static async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, string action, Guid character, Func<EntityId, StaffIntent> onTick,
        Func<OfflineCharacter, IReadOnlyDictionary<string, object?>, OfflinePlan> offline, CancellationToken cancellation)
    {
        var target = character.ToString();
        var detail = await road.Store.GetCharacterAsync(character, cancellation).ConfigureAwait(false);
        if (detail is null || detail.Character.DeletedAt is not null) return AdminResult.Refused(AdminErrors.UnknownTarget);
        var link = road.Gateway.LinksOf(detail.Character.AccountId).FirstOrDefault(l => l.CharacterId == character);
        if (link is not null && road.World.LocationOf(link.Character) is not null)
        {
            var intent = onTick(link.Character);
            var step = await road.SendAsync(intent, cancellation).ConfigureAwait(false);
            if (step.Error is { } refused) return AdminResult.Refused(refused);
            return await SettleAsync(call, road, action, target, intent, step).ConfigureAwait(false);
        }
        if (road.Moderation is not { } store) return AdminResult.Refused(AdminErrors.Unavailable);
        var edit = await store.EditCharacterAsync(character, view => offline(view, new Dictionary<string, object?> { ["online"] = false }), CancellationToken.None).ConfigureAwait(false);
        if (edit.Error is { } error) return AdminResult.Refused(error);
        var values = new Dictionary<string, object?>(edit.Result!) { ["was_online"] = false };
        return Moderation.Result(AdminOutcomes.Done, edit.AuditId, AdminTargetKinds.Character, target, values);
    }

    private static async Task<AdminResult> SettleAsync(AdminCall call, AdminRoad road, string action, string target, StaffIntent intent, AdminStepResult step)
    {
        if (intent is StaffItemChange items)
        {
            var before = (IReadOnlyDictionary<string, object?>)step.Values["before"]!;
            var after = (IReadOnlyDictionary<string, object?>)step.Values["after"]!;
            var values = new Dictionary<string, object?>(after) { ["was_online"] = true };
            if (items.Note is not { } note)   // not saved (a dev login): the change lives in memory only, and its row is written here
                return Moderation.Result(AdminOutcomes.Done, await road.Store.AppendAuditAsync(items.Audit(before, after), CancellationToken.None).ConfigureAwait(false),
                    AdminTargetKinds.Character, target, values);
            bool landed;
            try
            {
                landed = await note.Landed.WaitAsync(TimeSpan.FromMilliseconds(road.SaveWaitMs), road.Time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                values["saving"] = "pending";
                return Moderation.Result(AdminOutcomes.Accepted, null, AdminTargetKinds.Character, target, values);   // the row lands with the save
            }
            if (!landed) return AdminResult.Refused(AdminErrors.Unavailable);   // the server lost the character before that save: the change went with its unsaved time
            var auditId = note.AuditId != 0 ? note.AuditId : await FindAsync(road, call, action, target).ConfigureAwait(false);
            return Moderation.Result(AdminOutcomes.Done, auditId, AdminTargetKinds.Character, target, values);
        }
        var moved = new Dictionary<string, object?>(step.Values);
        var row = await road.Store.AppendAuditAsync(road.Draft(call, action, AdminTargetKinds.Character, target,
            new Dictionary<string, object?> { ["online"] = true, ["map"] = moved["from_map"], ["cell_x"] = moved["from_x"], ["cell_y"] = moved["from_y"], ["dead"] = moved["dead"] },
            new Dictionary<string, object?> { ["map"] = moved["to_map"], ["cell_x"] = moved["to_x"], ["cell_y"] = moved["to_y"] }), CancellationToken.None).ConfigureAwait(false);
        moved["was_online"] = true;
        return Moderation.Result(AdminOutcomes.Done, row, AdminTargetKinds.Character, target, moved);
    }

    /// <summary>The row a retried save wrote the first time (its answer was lost): found by the request id.</summary>
    private static async Task<long?> FindAsync(AdminRoad road, AdminCall call, string action, string target)
    {
        var page = await road.Store.SearchAuditAsync(new AuditQuery(target, call.Staff.Id, action, null, null, null, 20), CancellationToken.None).ConfigureAwait(false);
        return page.Items.FirstOrDefault(r => r.RequestId == call.RequestId)?.Id;
    }

    public static long Carried(OfflineCharacter view, string itemId) => view.Items.Where(i => i.ItemId == itemId).Sum(i => (long)i.Quantity);
}

/// <summary>
/// <c>give_item</c> (T-806, <c>gm</c>): <c>quantity</c> of <c>item_id</c> for <c>character_id</c>, in the world or not. The units go into new entries with new permanent ids, never into a stack
/// the character has, and each new entry is marked <c>source = admin</c> (<c>admin_items</c>, ARCHITECTURE §12.3) in the transaction that stores it, so a dupe audit tells them apart.
/// Refused: <c>unknown_item</c>, <c>inventory_full</c> (more entries than <c>inventory.max_entries</c>); weight is not checked.
/// </summary>
public sealed class GiveItemCommand : IAdminCommand
{
    /// <summary>The most units one command gives.</summary>
    public const int MaxQuantity = 10_000;

    /// <inheritdoc />
    public string Action => AdminActions.GiveItem;

    /// <inheritdoc />
    public Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Id(call.Body, AdminCommandFields.CharacterId, out var character) || !AdminFields.Text(call.Body, AdminCommandFields.ItemId, 128, out var itemId)
            || !AdminFields.Number(call.Body, AdminCommandFields.Quantity, 1, MaxQuantity, out long quantity))
            return Task.FromResult(AdminResult.Refused(AdminErrors.InvalidRequest));
        if (road.Content is not { } content) return Task.FromResult(AdminResult.Refused(AdminErrors.Unavailable));
        if (!content.Items.TryGet(itemId, out var item)) return Task.FromResult(AdminResult.Refused(AdminErrors.UnknownItem));
        var target = character.ToString();
        return CharacterFix.RunAsync(call, road, Action, character,
            entity => new StaffItemChange(entity, Action, (before, after) => road.Draft(call, Action, AdminTargetKinds.Character, target, before, after))
                { GiveItemId = itemId, Quantity = (int)quantity },
            (view, before) =>
            {
                if (road.NextItemId is not { } next) return OfflinePlan.Refuse(AdminErrors.Unavailable);
                var limit = Game.Shared.Items.InventoryRules.StackLimit(item);
                var entries = (quantity + limit - 1) / limit;
                if (view.Items.Count(i => i.Aside is null) + entries > content.Balance.Inventory.MaxEntries) return OfflinePlan.Refuse(AdminErrors.InventoryFull);
                var add = new List<NewItemRow>();
                for (var left = quantity; left > 0; left -= limit) add.Add(new NewItemRow(next(), itemId, (int)Math.Min(left, limit)));
                var was = new Dictionary<string, object?>(before) { ["item_id"] = itemId, ["carried"] = CharacterFix.Carried(view, itemId) };
                var now = new Dictionary<string, object?> { ["carried"] = CharacterFix.Carried(view, itemId) + quantity, ["entry_ids"] = string.Join(',', add.Select(a => a.Id)) };
                return new OfflinePlan(null, add, [], null, road.Draft(call, Action, AdminTargetKinds.Character, target, was, now), now);
            }, cancellation);
    }
}

/// <summary>
/// <c>remove_item</c> (T-806, <c>gm</c>): takes <c>quantity</c> units of the entry <c>entry_id</c> (its permanent instance id) from <c>character_id</c>, in the world or not; a worn entry comes off
/// first. Refused: <c>item_gone</c> (no such entry), <c>not_enough</c>.
/// </summary>
public sealed class RemoveItemCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.RemoveItem;

    /// <inheritdoc />
    public Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Id(call.Body, AdminCommandFields.CharacterId, out var character) || !AdminFields.Number(call.Body, AdminCommandFields.EntryId, 1, long.MaxValue, out long entryId)
            || !AdminFields.Number(call.Body, AdminCommandFields.Quantity, 1, int.MaxValue, out long quantity))
            return Task.FromResult(AdminResult.Refused(AdminErrors.InvalidRequest));
        var target = character.ToString();
        return CharacterFix.RunAsync(call, road, Action, character,
            entity => new StaffItemChange(entity, Action, (before, after) => road.Draft(call, Action, AdminTargetKinds.Character, target, before, after))
                { RemoveEntryId = entryId, Quantity = (int)quantity },
            (view, before) =>
            {
                if (view.Items.FirstOrDefault(i => i.Id == entryId) is not { } entry) return OfflinePlan.Refuse(AdminErrors.ItemGone);
                if (quantity > entry.Quantity) return OfflinePlan.Refuse(AdminErrors.NotEnough);
                var was = new Dictionary<string, object?>(before) { ["entry_id"] = entryId, ["item_id"] = entry.ItemId, ["quantity"] = entry.Quantity, ["worn"] = entry.Slot };
                var now = new Dictionary<string, object?> { ["quantity"] = entry.Quantity - (int)quantity };
                return new OfflinePlan(null, [], [(entryId, entry.Quantity - (int)quantity)], null, road.Draft(call, Action, AdminTargetKinds.Character, target, was, now), now);
            }, cancellation);
    }
}

/// <summary>
/// <c>change_currency</c> (T-806, <c>gm</c>): adds <c>amount</c> to the currency of <c>character_id</c> (a negative amount takes it), in the world or not. Refused: <c>not_enough</c> (it would go
/// below 0), <c>currency_limit</c>.
/// </summary>
public sealed class ChangeCurrencyCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.ChangeCurrency;

    /// <inheritdoc />
    public Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Id(call.Body, AdminCommandFields.CharacterId, out var character)
            || !AdminFields.Number(call.Body, AdminCommandFields.Amount, -long.MaxValue, long.MaxValue, out long amount) || amount == 0)
            return Task.FromResult(AdminResult.Refused(AdminErrors.InvalidRequest));
        var target = character.ToString();
        return CharacterFix.RunAsync(call, road, Action, character,
            entity => new StaffItemChange(entity, Action, (before, after) => road.Draft(call, Action, AdminTargetKinds.Character, target, before, after)) { CurrencyDelta = amount },
            (view, before) =>
            {
                if (amount < 0 && view.Currency + amount < 0) return OfflinePlan.Refuse(AdminErrors.NotEnough);
                if (amount > 0 && !Game.Shared.Shop.ShopRules.TryCredit(view.Currency, amount, out _)) return OfflinePlan.Refuse(AdminErrors.CurrencyLimit);
                var was = new Dictionary<string, object?>(before) { ["currency"] = view.Currency };
                var now = new Dictionary<string, object?> { ["currency"] = view.Currency + amount };
                return new OfflinePlan(view.Currency + amount, [], [], null, road.Draft(call, Action, AdminTargetKinds.Character, target, was, now), now);
            }, cancellation);
    }
}

/// <summary>
/// <c>unstick</c> (T-806, <c>gm</c>): moves <c>character_id</c> to its save point, in the world (as a respawn does: its fight ends, a dead one comes back there) or not (its stored place). With no
/// save point, or one the content no longer has, it goes where a respawn on its map would (in the world), or to the content's start (not in the world).
/// </summary>
public sealed class UnstickCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.Unstick;

    /// <inheritdoc />
    public Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Id(call.Body, AdminCommandFields.CharacterId, out var character)) return Task.FromResult(AdminResult.Refused(AdminErrors.InvalidRequest));
        if (road.Content is not { } content) return Task.FromResult(AdminResult.Refused(AdminErrors.Unavailable));
        var target = character.ToString();
        return CharacterFix.RunAsync(call, road, Action, character, entity => new StaffUnstick(entity, Action), (view, before) =>
        {
            var start = content.Balance.NewCharacter;
            var place = view is { SaveMapId: { } map, SaveCellX: { } x, SaveCellY: { } y } && content.Grids.TryGetValue(map, out var grid) && grid.IsWalkable(x, y)
                ? (map, x, y)
                : (start.StartMapId, start.StartX, start.StartY);
            var was = new Dictionary<string, object?>(before) { ["map"] = view.MapId, ["cell_x"] = view.CellX, ["cell_y"] = view.CellY };
            var now = new Dictionary<string, object?> { ["map"] = place.Item1, ["cell_x"] = place.Item2, ["cell_y"] = place.Item3 };
            return new OfflinePlan(null, [], [], place, road.Draft(call, Action, AdminTargetKinds.Character, target, was, now), now);
        }, cancellation);
    }
}

// ---- server -----------------------------------------------------------------------------------------------------------------------------

/// <summary>
/// <c>announce</c> (T-806, <c>gm</c>): <c>text</c> (1 to <see cref="AnnouncementKinds.MaxTextLength"/> characters) goes to every client playing on this server as an <c>announcement</c>, which
/// the HUD shows. The text is staff's: it is in the audit row's <c>after</c> as it was sent. This server only (ADR-002 row 320).
/// </summary>
public sealed class AnnounceCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.Announce;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Text(call.Body, AdminCommandFields.Text, AnnouncementKinds.MaxTextLength, out var text) || text.Any(c => c is '\n' or '\r' or '\t'))
            return AdminResult.Refused(AdminErrors.InvalidRequest);
        var players = road.Gateway.SessionCount;
        var audit = await road.Store.AppendAuditAsync(road.Draft(call, Action, AdminTargetKinds.Server, AdminTargetKinds.Server, new Dictionary<string, object?> { ["sessions"] = players },
            new Dictionary<string, object?> { ["text"] = text }), CancellationToken.None).ConfigureAwait(false);
        var sent = road.Gateway.Announce(new AnnouncementMessage(AnnouncementKinds.Staff, text, null));
        return Moderation.Result(AdminOutcomes.Done, audit, AdminTargetKinds.Server, AdminTargetKinds.Server, new() { ["sent"] = sent });
    }
}

/// <summary>
/// Maintenance mode's timer (T-806): when it began, and the one pending end of the games. Thread-safe; one per server (<see cref="AdminRoad.Maintenance"/>).
/// </summary>
public sealed class MaintenanceClock
{
    private readonly object _lock = new();
    private CancellationTokenSource? _pending;

    /// <summary>When maintenance began, or null when it is off.</summary>
    public DateTime? Since { get; private set; }

    /// <summary>When the games end, or null when they have ended (or are not to).</summary>
    public DateTime? GamesEndAt { get; private set; }

    internal bool TryStart(DateTime now, DateTime endAt, out CancellationToken token)
    {
        lock (_lock)
        {
            token = default;
            if (Since is not null) return false;
            Since = now;
            GamesEndAt = endAt;
            _pending = new CancellationTokenSource();
            token = _pending.Token;
            return true;
        }
    }

    internal void GamesEnded(CancellationToken token)
    {
        lock (_lock)
            if (_pending?.Token == token) GamesEndAt = null;
    }

    internal bool TryStop(out DateTime? since)
    {
        lock (_lock)
        {
            since = Since;
            if (Since is null) return false;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
            Since = null;
            GamesEndAt = null;
            return true;
        }
    }

    /// <summary>The status answer's part.</summary>
    public AdminMaintenanceStatus Status()
    {
        lock (_lock) return new AdminMaintenanceStatus(Since is not null, AdminFields.Iso(Since), AdminFields.Iso(GamesEndAt));
    }
}

/// <summary>
/// <c>start_maintenance</c> (T-806, <c>admin</c>): from now no new login gets in (<c>bye maintenance</c> at its <c>hello</c>); everyone playing is told (an <c>announcement</c> of kind
/// <c>maintenance</c> with the seconds left) and, after <c>warning_seconds</c> (0 to an hour), every game here ends: <c>bye maintenance</c>, the characters leave the world saved, the server runs
/// on. This server only (ADR-002 row 320); it ends with the process or with <c>end_maintenance</c>.
/// </summary>
public sealed class StartMaintenanceCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.StartMaintenance;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Number(call.Body, AdminCommandFields.WarningSeconds, 0, AdminCommandFields.MaxMaintenanceWarningSeconds, out long seconds))
            return AdminResult.Refused(AdminErrors.InvalidRequest);
        var now = road.Now;
        var endAt = now.AddSeconds(seconds);
        if (!road.Maintenance.TryStart(now, endAt, out var token)) return AdminResult.Refused(AdminErrors.MaintenanceOn);
        long audit;
        try
        {
            audit = await road.Store.AppendAuditAsync(road.Draft(call, Action, AdminTargetKinds.Server, AdminTargetKinds.Server,
                new Dictionary<string, object?> { ["maintenance"] = false, ["players_online"] = road.Gateway.CharacterCount },
                new Dictionary<string, object?> { ["maintenance"] = true, ["games_end_at"] = AdminFields.Iso(endAt), ["warning_seconds"] = seconds }), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            road.Maintenance.TryStop(out _);   // not audited: not started
            throw;
        }
        road.Gateway.InMaintenance = true;
        var warned = road.Gateway.Announce(new AnnouncementMessage(AnnouncementKinds.Maintenance, null, seconds));
        _ = EndGamesAsync(road, TimeSpan.FromSeconds(seconds), token);
        return Moderation.Result(AdminOutcomes.Accepted, audit, AdminTargetKinds.Server, AdminTargetKinds.Server, new()
        {
            ["warned"] = warned, ["games_end_at"] = AdminFields.Iso(endAt),
        });
    }

    private static async Task EndGamesAsync(AdminRoad road, TimeSpan wait, CancellationToken token)
    {
        try
        {
            if (wait > TimeSpan.Zero) await Task.Delay(wait, road.Time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;   // maintenance ended first: the games go on
        }
        if (token.IsCancellationRequested) return;
        road.Maintenance.GamesEnded(token);
        road.Gateway.EndGames(ByeReasons.Maintenance, "maintenance");
    }
}

/// <summary><c>end_maintenance</c> (T-806, <c>admin</c>): logins are open again; a pending end of the games is called off.</summary>
public sealed class EndMaintenanceCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.EndMaintenance;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        var pending = road.Maintenance.GamesEndAt;
        if (!road.Maintenance.TryStop(out var since)) return AdminResult.Refused(AdminErrors.MaintenanceOff);
        road.Gateway.InMaintenance = false;
        var audit = await road.Store.AppendAuditAsync(road.Draft(call, Action, AdminTargetKinds.Server, AdminTargetKinds.Server,
            new Dictionary<string, object?> { ["maintenance"] = true, ["since"] = AdminFields.Iso(since), ["games_end_at"] = AdminFields.Iso(pending) },
            new Dictionary<string, object?> { ["maintenance"] = false }), CancellationToken.None).ConfigureAwait(false);
        return Moderation.Result(AdminOutcomes.Done, audit, AdminTargetKinds.Server, AdminTargetKinds.Server, new() { ["games_end_called_off"] = pending is not null });
    }
}

// ---- the support queue ------------------------------------------------------------------------------------------------------------------

/// <summary>
/// <c>open_request</c> (T-806, <c>support</c>): a deletion or data-export request that came in through support (an e-mail, a web form: docs/STORE_COMPLIANCE.md §5) goes into the queue for
/// <c>account_id</c>, once support has checked that it is the owner's. What the player wrote stays in the support tool; the queue holds ids, terms and times.
/// </summary>
public sealed class OpenRequestCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.OpenRequest;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Id(call.Body, AdminCommandFields.AccountId, out var account) || !AdminFields.Term(call.Body, AdminCommandFields.Kind, SupportRequestKinds.All, out var kind))
            return AdminResult.Refused(AdminErrors.InvalidRequest);
        if (road.Moderation is not { } store) return AdminResult.Refused(AdminErrors.Unavailable);
        var target = account.ToString();
        var change = await store.OpenRequestAsync(account, kind, road.Now, id => road.Draft(call, Action, AdminTargetKinds.Account, target, new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["request_id"] = id, ["kind"] = kind, ["status"] = SupportRequestStatuses.Open }), CancellationToken.None).ConfigureAwait(false);
        if (change.Error is { } error) return AdminResult.Refused(error);
        return Moderation.Result(AdminOutcomes.Done, change.AuditId, AdminTargetKinds.Account, target, new() { ["request_id"] = change.Request!.Id, ["kind"] = kind });
    }
}

/// <summary>
/// <c>complete_request</c> (T-806, <c>support</c>): handles the open request <c>request_id</c>. A <c>deletion</c> takes T-703's own path, not a shortcut: the account's sessions and game end,
/// nobody can sign in, and it is erased after the wait (<c>--account-erase-days</c>); its owner can still keep it by signing in during the wait. An <c>export</c> answers with everything
/// stored about the account as JSON (<c>result.export</c>) for staff to send; the server sends no e-mail. The audit row says what was done, never the data.
/// </summary>
public sealed class CompleteRequestCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.CompleteRequest;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Number(call.Body, AdminCommandFields.RequestId, 1, long.MaxValue, out long id)) return AdminResult.Refused(AdminErrors.InvalidRequest);
        if (road.Moderation is not { } store) return AdminResult.Refused(AdminErrors.Unavailable);
        var request = await store.GetRequestAsync(id, cancellation).ConfigureAwait(false);
        if (request is null) return AdminResult.Refused(AdminErrors.UnknownTarget);
        if (request.Status != SupportRequestStatuses.Open) return AdminResult.Refused(AdminErrors.RequestClosed);
        var now = road.Now;
        System.Text.Json.Nodes.JsonObject? export = null;
        if (request.Kind == SupportRequestKinds.Export)
        {
            export = await store.ExportAsync(request.AccountId, now, cancellation).ConfigureAwait(false);   // read first: a request is closed only when its answer is in hand
            if (export is null) return AdminResult.Refused(AdminErrors.UnknownTarget);
        }
        var target = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var eraseAfter = now + road.EraseWait;
        var change = await store.CloseRequestAsync(id, SupportRequestStatuses.Done, now, eraseAfter, r => road.Draft(call, Action, AdminTargetKinds.Request, target,
            new Dictionary<string, object?> { ["account_id"] = r.AccountId, ["kind"] = r.Kind, ["status"] = r.Status },
            r.Kind == SupportRequestKinds.Deletion
                ? new Dictionary<string, object?> { ["status"] = SupportRequestStatuses.Done, ["erase_after"] = AdminFields.Iso(eraseAfter) }
                : new Dictionary<string, object?> { ["status"] = SupportRequestStatuses.Done, ["exported_characters"] = export?["characters"]?.AsArray().Count }),
            CancellationToken.None).ConfigureAwait(false);
        if (change.Error is { } error) return AdminResult.Refused(error);
        var values = new Dictionary<string, object?> { ["account_id"] = request.AccountId.ToString(), ["kind"] = request.Kind };
        if (request.Kind == SupportRequestKinds.Deletion)
        {
            road.Gateway.EndSessions(new EndedSessions(request.AccountId, change.EndedSessions.Select(SessionTokens.KeyOf).ToList(), All: true));   // as the in-app request does
            values["erase_after"] = AdminFields.Iso(eraseAfter);
            values["sessions_ended"] = change.EndedSessions.Count;
        }
        else values["export"] = export;
        return Moderation.Result(AdminOutcomes.Done, change.AuditId, AdminTargetKinds.Request, target, values);
    }
}

/// <summary><c>cancel_request</c> (T-806, <c>support</c>): closes the open request <c>request_id</c> without handling it (the owner took it back, or support could not check who asked).</summary>
public sealed class CancelRequestCommand : IAdminCommand
{
    /// <inheritdoc />
    public string Action => AdminActions.CancelRequest;

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
    {
        if (!AdminFields.Number(call.Body, AdminCommandFields.RequestId, 1, long.MaxValue, out long id)) return AdminResult.Refused(AdminErrors.InvalidRequest);
        if (road.Moderation is not { } store) return AdminResult.Refused(AdminErrors.Unavailable);
        var target = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var change = await store.CloseRequestAsync(id, SupportRequestStatuses.Cancelled, road.Now, road.Now, r => road.Draft(call, Action, AdminTargetKinds.Request, target,
            new Dictionary<string, object?> { ["account_id"] = r.AccountId, ["kind"] = r.Kind, ["status"] = r.Status },
            new Dictionary<string, object?> { ["status"] = SupportRequestStatuses.Cancelled }), CancellationToken.None).ConfigureAwait(false);
        if (change.Error is { } error) return AdminResult.Refused(error);
        return Moderation.Result(AdminOutcomes.Done, change.AuditId, AdminTargetKinds.Request, target);
    }
}
