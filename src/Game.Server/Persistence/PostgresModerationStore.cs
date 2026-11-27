using System.Text.Json.Nodes;
using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Shared.Vocabulary;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Server.Persistence;

/// <summary>
/// T-806's moderation and support data in PostgreSQL: bans and mutes (<c>account_sanctions</c>), the support queue (<c>support_requests</c>), items staff made (<c>admin_items</c>) and offline
/// character changes. Every write takes the account's row first (as a sign-in, an erasure and a kick do), or the character's and its state's in a save's order, and writes its audit row in the
/// same transaction (<see cref="PostgresAdminStore.WriteAuditAsync"/>).
/// </summary>
public sealed class PostgresModerationStore(GameDatabase database) : IModerationStore
{
    // ---- bans and mutes --------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<Sanction?> ActiveSanctionAsync(Guid account, string kind, DateTime now, CancellationToken cancellation) => RunAsync(async db =>
        await InForce(db, account, kind, now).Select(AsSanction).FirstOrDefaultAsync(cancellation).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> BannedAmongAsync(IReadOnlyCollection<Guid> accounts, DateTime now, CancellationToken cancellation) => RunAsync<IReadOnlyList<Guid>>(async db =>
    {
        if (accounts.Count == 0) return [];
        var ids = accounts.ToArray();
        return await db.Sanctions.AsNoTracking()
            .Where(s => ids.Contains(s.AccountId) && s.Kind == SanctionKinds.Ban && s.LiftedAt == null && (s.Until == null || s.Until > now))
            .Select(s => s.AccountId).Distinct().ToListAsync(cancellation).ConfigureAwait(false);
    });

    /// <inheritdoc />
    public Task<SanctionChange> AddSanctionAsync(Guid account, string kind, DateTime? until, DateTime now, AuditDraft audit, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        if (await LockAccountAsync(db, account, cancellation).ConfigureAwait(false) is null) return SanctionChange.Refused(AdminErrors.UnknownTarget);
        if (await InForce(db, account, kind, now).AnyAsync(cancellation).ConfigureAwait(false))
            return SanctionChange.Refused(kind == SanctionKinds.Ban ? AdminErrors.AlreadyBanned : AdminErrors.AlreadyMuted);
        var auditId = await PostgresAdminStore.WriteAuditAsync(db, audit, cancellation).ConfigureAwait(false);
        var row = new AccountSanctionRow { AccountId = account, Kind = kind, CreatedAt = now, Until = until, AuditId = auditId };
        db.Sanctions.Add(row);
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        List<byte[]> ended = [];
        if (kind == SanctionKinds.Ban)
        {
            // The account's row is locked: no sign-in adds a session while this runs, so none survives the ban.
            ended = await db.Sessions.Where(s => s.AccountId == account).Select(s => s.TokenHash).ToListAsync(cancellation).ConfigureAwait(false);
            await db.Sessions.Where(s => s.AccountId == account).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new SanctionChange(null, ToSanction(row), auditId, ended);
    });

    /// <inheritdoc />
    public Task<SanctionChange> LiftSanctionAsync(Guid account, string kind, DateTime now, Func<Sanction, AuditDraft> audit, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        if (await LockAccountAsync(db, account, cancellation).ConfigureAwait(false) is null) return SanctionChange.Refused(AdminErrors.UnknownTarget);
        var rows = await db.Sanctions.Where(s => s.AccountId == account && s.Kind == kind && s.LiftedAt == null && (s.Until == null || s.Until > now)).ToListAsync(cancellation)
            .ConfigureAwait(false);
        if (rows.Count == 0) return SanctionChange.Refused(kind == SanctionKinds.Ban ? AdminErrors.NotBanned : AdminErrors.NotMuted);
        var lifted = rows.OrderByDescending(r => r.Id).First();
        var auditId = await PostgresAdminStore.WriteAuditAsync(db, audit(ToSanction(lifted)), cancellation).ConfigureAwait(false);
        foreach (var row in rows)   // only ever one: adding refuses while one is in force
        {
            row.LiftedAt = now;
            row.LiftedAuditId = auditId;
        }
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new SanctionChange(null, ToSanction(lifted), auditId, []);
    });

    /// <inheritdoc />
    public Task<IReadOnlyList<Sanction>> SanctionsAsync(Guid account, CancellationToken cancellation) => RunAsync<IReadOnlyList<Sanction>>(async db =>
        await db.Sanctions.AsNoTracking().Where(s => s.AccountId == account).OrderByDescending(s => s.Id).Select(AsSanction).ToListAsync(cancellation).ConfigureAwait(false));

    private static IQueryable<AccountSanctionRow> InForce(GameDb db, Guid account, string kind, DateTime now) =>
        db.Sanctions.AsNoTracking().Where(s => s.AccountId == account && s.Kind == kind && s.LiftedAt == null && (s.Until == null || s.Until > now)).OrderByDescending(s => s.Id);

    private static readonly System.Linq.Expressions.Expression<Func<AccountSanctionRow, Sanction>> AsSanction =
        s => new Sanction(s.Id, s.AccountId, s.Kind, s.CreatedAt, s.Until, s.AuditId, s.LiftedAt, s.LiftedAuditId);

    private static readonly Func<AccountSanctionRow, Sanction> ToSanction = AsSanction.Compile();

    // ---- the support queue -----------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<RequestChange> OpenRequestAsync(Guid account, string kind, DateTime now, Func<long, AuditDraft> audit, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        if (await LockAccountAsync(db, account, cancellation).ConfigureAwait(false) is null) return RequestChange.Refused(AdminErrors.UnknownTarget);
        var row = new SupportRequestRow { AccountId = account, Kind = kind, Status = SupportRequestStatuses.Open, OpenedAt = now };
        db.SupportRequests.Add(row);
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        row.OpenedAuditId = await PostgresAdminStore.WriteAuditAsync(db, audit(row.Id), cancellation).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new RequestChange(null, AsRequest(row), row.OpenedAuditId, []);
    });

    /// <inheritdoc />
    public Task<RequestChange> CloseRequestAsync(long request, string status, DateTime now, DateTime eraseAfter, Func<SupportRequest, AuditDraft> audit, CancellationToken cancellation) =>
        RunAsync(async db =>
    {
        // Whose it is first, without a lock, so the account's row is locked before the request's: the order of an erasure, which deletes the account's requests.
        var account = await db.SupportRequests.AsNoTracking().Where(r => r.Id == request).Select(r => (Guid?)r.AccountId).SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (account is null) return RequestChange.Refused(AdminErrors.UnknownTarget);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var owner = await LockAccountAsync(db, account.Value, cancellation).ConfigureAwait(false);
        var row = await db.SupportRequests.FromSql($"SELECT * FROM support_requests WHERE id = {request} FOR UPDATE").SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (owner is null || row is null) return RequestChange.Refused(AdminErrors.UnknownTarget);
        if (row.Status != SupportRequestStatuses.Open) return RequestChange.Refused(AdminErrors.RequestClosed);
        List<byte[]> ended = [];
        if (row.Kind == SupportRequestKinds.Deletion && status == SupportRequestStatuses.Done)
        {
            // T-703's own request, made for the owner: the wait, the record, every session ended. Only the password is not asked: support checked who asked (docs/STORE_COMPLIANCE.md §5).
            if (owner.EraseAfter is not null) return RequestChange.Refused(AdminErrors.AccountDeleting);
            owner.EraseAfter = eraseAfter;
            db.AccountDeletions.Add(new AccountDeletionRow { AccountId = owner.Id, RequestedAt = now });
            ended = await db.Sessions.Where(s => s.AccountId == owner.Id).Select(s => s.TokenHash).ToListAsync(cancellation).ConfigureAwait(false);
            await db.Sessions.Where(s => s.AccountId == owner.Id).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        }
        var auditId = await PostgresAdminStore.WriteAuditAsync(db, audit(AsRequest(row)), cancellation).ConfigureAwait(false);
        row.Status = status;
        row.ClosedAt = now;
        row.ClosedAuditId = auditId;
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new RequestChange(null, AsRequest(row), auditId, ended);
    });

    /// <inheritdoc />
    public Task<SupportRequest?> GetRequestAsync(long request, CancellationToken cancellation) => RunAsync(async db =>
        await db.SupportRequests.AsNoTracking().Where(r => r.Id == request).Select(r => new SupportRequest(r.Id, r.AccountId, r.Kind, r.Status, r.OpenedAt, r.OpenedAuditId, r.ClosedAt,
            r.ClosedAuditId)).SingleOrDefaultAsync(cancellation).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<AdminPage<SupportRequest>> FindRequestsAsync(string? status, Guid? account, long? before, int limit, CancellationToken cancellation) => RunAsync(async db =>
    {
        var rows = db.SupportRequests.AsNoTracking().AsQueryable();
        if (status is not null) rows = rows.Where(r => r.Status == status);
        if (account is { } id) rows = rows.Where(r => r.AccountId == id);
        if (before is { } b) rows = rows.Where(r => r.Id < b);
        var page = await rows.OrderByDescending(r => r.Id).Take(limit + 1)
            .Select(r => new SupportRequest(r.Id, r.AccountId, r.Kind, r.Status, r.OpenedAt, r.OpenedAuditId, r.ClosedAt, r.ClosedAuditId)).ToListAsync(cancellation).ConfigureAwait(false);
        return page.Count > limit
            ? new AdminPage<SupportRequest>(page.Take(limit).ToList(), page[limit - 1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new AdminPage<SupportRequest>(page, null);
    });

    private static SupportRequest AsRequest(SupportRequestRow r) => new(r.Id, r.AccountId, r.Kind, r.Status, r.OpenedAt, r.OpenedAuditId, r.ClosedAt, r.ClosedAuditId);

    // ---- the data export -------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<JsonObject?> ExportAsync(Guid account, DateTime now, CancellationToken cancellation) => RunAsync(async db =>
    {
        // One snapshot of every table: a save or a sign-in that lands meanwhile is in it whole or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellation).ConfigureAwait(false);
        var a = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(r => r.Id == account, cancellation).ConfigureAwait(false);
        if (a is null) return null;
        var characters = await db.Characters.AsNoTracking().Where(c => c.AccountId == account).OrderBy(c => c.Slot).ThenBy(c => c.CreatedAt).ToListAsync(cancellation).ConfigureAwait(false);
        var ids = characters.Select(c => c.Id).ToList();
        var states = await db.CharacterStates.AsNoTracking().Where(s => ids.Contains(s.CharacterId)).ToDictionaryAsync(s => s.CharacterId, cancellation).ConfigureAwait(false);
        var items = (await db.Items.AsNoTracking().Where(i => ids.Contains(i.CharacterId)).OrderBy(i => i.Id).ToListAsync(cancellation).ConfigureAwait(false)).ToLookup(i => i.CharacterId);
        var outfits = (await db.Outfits.AsNoTracking().Where(o => ids.Contains(o.CharacterId)).OrderBy(o => o.OutfitId).ToListAsync(cancellation).ConfigureAwait(false)).ToLookup(o => o.CharacterId);
        var sessions = await db.Sessions.AsNoTracking().Where(s => s.AccountId == account).OrderBy(s => s.CreatedAt).Select(s => new { s.CreatedAt, s.ExpiresAt })
            .ToListAsync(cancellation).ConfigureAwait(false);
        var deletion = await db.AccountDeletions.AsNoTracking().SingleOrDefaultAsync(d => d.AccountId == account, cancellation).ConfigureAwait(false);
        var sanctions = await db.Sanctions.AsNoTracking().Where(s => s.AccountId == account).OrderBy(s => s.Id).ToListAsync(cancellation).ConfigureAwait(false);
        var requests = await db.SupportRequests.AsNoTracking().Where(r => r.AccountId == account).OrderBy(r => r.Id).ToListAsync(cancellation).ConfigureAwait(false);
        // T-901: whom it blocks (by name, as the player sees them), and its chat reports: those it made, and those about what it said (the line is its own words). Who reported it is not said.
        var blocks = await db.ChatBlocks.AsNoTracking().Where(b => b.AccountId == account).Join(db.Characters, b => b.BlockedCharacterId, c => c.Id, (b, c) => new { c.Name, b.CreatedAt })
            .OrderBy(b => b.CreatedAt).ToListAsync(cancellation).ConfigureAwait(false);
        var reportsMade = await db.ChatReports.AsNoTracking().Where(r => r.ReporterAccountId == account).OrderBy(r => r.Id).ToListAsync(cancellation).ConfigureAwait(false);
        var reportsAbout = await db.ChatReports.AsNoTracking().Where(r => r.SenderAccountId == account).OrderBy(r => r.Id).ToListAsync(cancellation).ConfigureAwait(false);
        // T-904: its characters' guild memberships: the guild's name, the rank's, when it joined. Other members are not named.
        var guilds = await (from m in db.GuildMembers.AsNoTracking()
                            where ids.Contains(m.CharacterId)
                            join g in db.Guilds on m.GuildId equals g.Id
                            join r in db.GuildRanks on new { m.GuildId, Position = m.Rank } equals new { r.GuildId, r.Position } into ranks
                            from r in ranks.DefaultIfEmpty()
                            orderby m.JoinedAt
                            select new { m.CharacterId, g.Name, RankName = r == null ? null : r.Name, m.Rank, m.JoinedAt, Founded = g.FoundedAt != null }).ToListAsync(cancellation).ConfigureAwait(false);
        var targets = ids.Select(i => i.ToString()).Append(account.ToString()).ToList();
        var audit = await db.Audit.AsNoTracking().Where(r => targets.Contains(r.TargetId)).OrderBy(r => r.Id)
            .Select(r => new { r.At, r.Action, r.TargetKind, r.TargetId, r.Before, r.After }).ToListAsync(cancellation).ConfigureAwait(false);

        var characterNodes = new JsonArray();
        foreach (var c in characters)
        {
            var node = new JsonObject
            {
                ["id"] = c.Id.ToString(), ["slot"] = c.Slot, ["name"] = c.Name, ["job_id"] = c.JobId, ["gender"] = c.Gender, ["skin_tone_id"] = c.SkinToneId, ["face_id"] = c.FaceId,
                ["hairstyle_id"] = c.HairstyleId, ["hair_color_id"] = c.HairColorId, ["created_at"] = c.CreatedAt, ["deleted_at"] = c.DeletedAt,
            };
            if (states.TryGetValue(c.Id, out var s) && s.Version > 0)
                node["state"] = new JsonObject
                {
                    ["saved_at"] = s.SavedAt, ["map_id"] = s.MapId, ["cell_x"] = s.CellX, ["cell_y"] = s.CellY, ["facing"] = s.Facing, ["hp"] = s.Hp, ["sp"] = s.Sp,
                    ["save_map_id"] = s.SaveMapId, ["save_cell_x"] = s.SaveCellX, ["save_cell_y"] = s.SaveCellY, ["base_level"] = s.BaseLevel, ["base_exp"] = s.BaseExp,
                    ["job_id"] = s.JobId, ["job_level"] = s.JobLevel, ["job_exp"] = s.JobExp, ["stat_points"] = s.StatPoints, ["skill_points"] = s.SkillPoints, ["currency"] = s.Currency,
                    ["autopilot_used_ms"] = s.AutopilotUsedMs, ["stats"] = JsonNode.Parse(s.Stats), ["skills"] = JsonNode.Parse(s.Skills), ["quests"] = JsonNode.Parse(s.Quests),
                    ["cooldowns"] = JsonNode.Parse(s.Cooldowns), ["outfit_slots"] = JsonNode.Parse(s.OutfitSlots), ["outfits_hidden"] = s.OutfitsHidden, ["set_aside"] = JsonNode.Parse(s.SetAside),
                };
            node["items"] = new JsonArray(items[c.Id].Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id, ["item_id"] = i.ItemId, ["quantity"] = i.Quantity, ["slot"] = i.Slot, ["cards"] = new JsonArray(i.Cards.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["refine"] = i.Refine, ["aside"] = i.Aside,
            }).ToArray());
            node["outfits"] = new JsonArray(outfits[c.Id].Select(o => (JsonNode)JsonValue.Create(o.OutfitId)!).ToArray());
            characterNodes.Add(node);
        }
        return new JsonObject
        {
            ["exported_at"] = now,
            ["account"] = new JsonObject
            {
                ["id"] = a.Id.ToString(), ["username"] = a.Username, ["created_at"] = a.CreatedAt, ["password_changed_at"] = a.PasswordChangedAt, ["erase_after"] = a.EraseAfter,
                ["password"] = "stored only as an Argon2id hash, which is not exported",
            },
            ["sessions"] = new JsonArray(sessions.Select(s => (JsonNode)new JsonObject { ["signed_in_at"] = s.CreatedAt, ["expires_at"] = s.ExpiresAt }).ToArray()),
            ["characters"] = characterNodes,
            ["deletion"] = deletion is null ? null : new JsonObject { ["requested_at"] = deletion.RequestedAt, ["erased_at"] = deletion.ErasedAt },
            ["sanctions"] = new JsonArray(sanctions.Select(s => (JsonNode)new JsonObject
            {
                ["kind"] = s.Kind, ["from"] = s.CreatedAt, ["until"] = s.Until, ["lifted_at"] = s.LiftedAt,
            }).ToArray()),
            ["support_requests"] = new JsonArray(requests.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["kind"] = r.Kind, ["status"] = r.Status, ["opened_at"] = r.OpenedAt, ["closed_at"] = r.ClosedAt,
            }).ToArray()),
            ["guild_memberships"] = new JsonArray(guilds.Select(g => (JsonNode)new JsonObject
            {
                ["character_id"] = g.CharacterId.ToString(), ["guild"] = g.Name, ["rank"] = g.RankName, ["leader"] = g.Rank == 0 ? true : null, ["joined_at"] = g.JoinedAt,
                ["founding"] = g.Founded ? null : true,
            }).ToArray()),
            ["chat_blocks"] = new JsonArray(blocks.Select(b => (JsonNode)new JsonObject { ["character"] = b.Name, ["since"] = b.CreatedAt }).ToArray()),
            ["chat_reports_made"] = new JsonArray(reportsMade.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["reported_at"] = r.ReportedAt, ["by_character_id"] = r.ReporterCharacterId.ToString(), ["channel"] = r.Channel, ["text"] = r.Text, ["said_at"] = r.SaidAt,
                ["status"] = r.Status,
            }).ToArray()),
            ["chat_reports_about"] = new JsonArray(reportsAbout.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["reported_at"] = r.ReportedAt, ["character_id"] = r.SenderCharacterId.ToString(), ["channel"] = r.Channel, ["text"] = r.Text, ["said_at"] = r.SaidAt,
                ["status"] = r.Status,
            }).ToArray()),
            // What staff did to the account and its characters: the action, when, and the ids and numbers changed. Who did it, and their reason, are staff's records, not the player's data.
            ["staff_actions"] = new JsonArray(audit.Select(r => (JsonNode)new JsonObject
            {
                ["at"] = r.At, ["action"] = r.Action, ["target_kind"] = r.TargetKind, ["target_id"] = r.TargetId, ["before"] = JsonNode.Parse(r.Before), ["after"] = JsonNode.Parse(r.After),
            }).ToArray()),
            ["not_stored"] = "IP addresses are in the server's logs only, for their lifetime (docs/STORE_COMPLIANCE.md); preferences are on the device only.",
        };
    });

    // ---- offline character changes ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<OfflineResult> EditCharacterAsync(Guid character, Func<OfflineCharacter, OfflinePlan> plan, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        // The order of a claim and a save: the character, then its state. A login that claims it waits for this, and then reads the change; this waits for a claim, and then sees it held.
        var owner = await db.Characters.FromSql($"SELECT * FROM characters WHERE id = {character} AND deleted_at IS NULL FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(cancellation)
            .ConfigureAwait(false);
        if (owner is null) return new OfflineResult(AdminErrors.UnknownTarget, 0, null);
        var row = await db.CharacterStates.FromSql($"SELECT * FROM character_states WHERE character_id = {character} FOR UPDATE").SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (row is null || row.Version == 0) return new OfflineResult(AdminErrors.NeverSaved, 0, null);
        if (row.HeldBy is { } holder && await PostgresCharacterStore.IsAliveAsync(db, holder, cancellation).ConfigureAwait(false)) return new OfflineResult(AdminErrors.CharacterBusy, 0, null);
        var items = await db.Items.Where(i => i.CharacterId == character).OrderBy(i => i.Id).ToListAsync(cancellation).ConfigureAwait(false);
        var view = new OfflineCharacter(character, owner.AccountId, row.Version, row.Currency,
            items.Select(i => new AdminItem(i.Id, i.ItemId, i.Quantity, i.Slot, i.Cards, i.Refine, i.Aside)).ToList(), row.MapId, row.CellX, row.CellY, row.SaveMapId, row.SaveCellX, row.SaveCellY);
        var change = plan(view);
        if (change.Error is { } refused) return new OfflineResult(refused, 0, null);

        // A new stay: whatever an earlier one (a dead server's) still tries to save is refused from here, so nothing lands later and undoes this.
        row.Claim++;
        row.HeldBy = null;
        row.Version++;
        if (change.Currency is { } currency) row.Currency = currency;
        if (change.Place is { } place)
        {
            row.MapId = place.MapId;
            row.CellX = place.CellX;
            row.CellY = place.CellY;
        }
        foreach (var (id, quantity) in change.Change)
        {
            var item = items.Single(i => i.Id == id);
            if (quantity <= 0) db.Items.Remove(item);
            else item.Quantity = quantity;
        }
        foreach (var added in change.Add)
            db.Items.Add(new ItemRow { Id = added.Id, CharacterId = character, ItemId = added.ItemId, Quantity = added.Quantity });
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        var auditId = await PostgresAdminStore.WriteAuditAsync(db, change.Audit, cancellation).ConfigureAwait(false);
        if (change.Add.Count > 0)
        {
            foreach (var added in change.Add)
                db.AdminItems.Add(new AdminItemRow { ItemInstanceId = added.Id, ItemId = added.ItemId, Quantity = added.Quantity, CreatedAt = change.Audit.At, AuditId = auditId });
            await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new OfflineResult(null, auditId, change.Result);
    });

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------------

    private static Task<AccountRow?> LockAccountAsync(GameDb db, Guid account, CancellationToken cancellation) =>
        db.Accounts.FromSql($"SELECT * FROM accounts WHERE id = {account} FOR UPDATE").SingleOrDefaultAsync(cancellation);

    private async Task<T> RunAsync<T>(Func<GameDb, Task<T>> work)
    {
        try
        {
            await using var db = database.Open();
            return await work(db).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && GameDatabase.CauseOf(e) is { } cause)
        {
            throw new StoreUnavailableException(cause is PostgresException p ? $"database error {p.SqlState}" : $"database not answering ({cause.GetType().Name})");
        }
    }
}
