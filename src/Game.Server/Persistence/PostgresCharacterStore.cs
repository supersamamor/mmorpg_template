using System.Text.Json;
using Game.Server.Accounts;
using Game.Server.World.Dialogue;
using Game.Server.World.Saving;
using Game.Shared.Items;
using Game.Shared.Progression;
using Game.Shared.World;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Game.Server.Persistence;

/// <summary>
/// Saved characters in PostgreSQL (T-702). One transaction per method; row locks (character, then state, by id) decide who comes first, the claim decides whose save counts, and the
/// primary key of <c>character_items</c> decides whose an item is. READ COMMITTED is enough: everything read is locked or checked again after it is written.
/// </summary>
public sealed class PostgresCharacterStore(GameDatabase database) : ICharacterStore
{
    /// <summary>A run that has not said it is alive for this long holds nothing any more.</summary>
    public const int LeaseSeconds = 20;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Test hook: called between the steps of a save, by name (<see cref="SaveSteps"/>). Throwing there is a process that dies there.</summary>
    internal Func<string, Task>? OnStep { get; set; }

    /// <summary>The steps of <see cref="SaveAsync"/> a fault may come after, in order; the last one is after the commit.</summary>
    internal static readonly IReadOnlyList<string> SaveSteps = ["begun", "locked", "states", "items_removed", "slots_cleared", "items_written", "outfits_written", "checked", "committed"];

    /// <inheritdoc />
    public Task<long> StartRunAsync(CancellationToken cancellation) => RunAsync(async db =>
        (await db.Database.SqlQuery<long>($"INSERT INTO server_runs (started_at, seen_at) VALUES (now(), now()) RETURNING id AS \"Value\"").ToListAsync(cancellation).ConfigureAwait(false))[0]);

    /// <inheritdoc />
    public Task BeatAsync(long run, CancellationToken cancellation) => RunAsync(db =>
        db.Database.ExecuteSqlAsync($"UPDATE server_runs SET seen_at = now() WHERE id = {run} AND ended_at IS NULL", cancellation));

    /// <inheritdoc />
    public Task EndRunAsync(long run, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"UPDATE character_states SET held_by = NULL WHERE held_by = {run}", cancellation).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"UPDATE server_runs SET ended_at = now() WHERE id = {run}", cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return true;
    });

    /// <inheritdoc />
    public Task<CharacterClaim> ClaimAsync(Guid character, long run, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        // Locked from here: a delete of the character waits for this, and then sees that it is held (and the other way round).
        if (await db.Characters.FromSql($"SELECT * FROM characters WHERE id = {character} AND deleted_at IS NULL FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(cancellation)
                .ConfigureAwait(false) is null) return new CharacterClaim(ClaimOutcome.NotFound);
        var row = await db.CharacterStates.FromSql($"SELECT * FROM character_states WHERE character_id = {character} FOR UPDATE").SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (row is null)
        {
            row = new CharacterStateRow { CharacterId = character };
            db.CharacterStates.Add(row);
        }
        else if (row.HeldBy is { } holder && holder != run && await IsAliveAsync(db, holder, cancellation).ConfigureAwait(false)) return new CharacterClaim(ClaimOutcome.HeldElsewhere);
        row.Claim++;
        row.HeldBy = run;
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        var items = row.Version == 0
            ? []
            : await db.Items.AsNoTracking().Where(i => i.CharacterId == character).OrderBy(i => i.Id).ToListAsync(cancellation).ConfigureAwait(false);
        var outfits = row.Version == 0
            ? []
            : await db.Outfits.Where(o => o.CharacterId == character).OrderBy(o => o.OutfitId).Select(o => o.OutfitId).ToListAsync(cancellation).ConfigureAwait(false);
        var state = row.Version == 0 ? null : Read(row, items, outfits);   // before the commit: a state that cannot be read changes nothing
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new CharacterClaim(ClaimOutcome.Claimed, row.Claim, row.Version, state);
    });

    /// <inheritdoc />
    public Task ReleaseAsync(Guid character, long run, long claim, CancellationToken cancellation) => RunAsync(db =>
        db.Database.ExecuteSqlAsync($"UPDATE character_states SET held_by = NULL WHERE character_id = {character} AND held_by = {run} AND ({claim} = 0 OR claim = {claim})", cancellation));

    /// <inheritdoc />
    public Task<SaveResult> SaveAsync(IReadOnlyList<CharacterSave> group, CancellationToken cancellation) => RunAsync<SaveResult>(async db =>
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        await StepAsync("begun").ConfigureAwait(false);

        // Who may save: the newest claim of a character that is not deleted.
        var ids = group.Select(s => s.CharacterId).ToArray();
        var rows = (await db.CharacterStates
                .FromSql($"SELECT s.* FROM characters c JOIN character_states s ON s.character_id = c.id WHERE c.id = ANY({ids}) AND c.deleted_at IS NULL ORDER BY c.id FOR UPDATE")
                .ToListAsync(cancellation).ConfigureAwait(false)).ToDictionary(r => r.CharacterId);
        var lost = group.Where(s => !rows.TryGetValue(s.CharacterId, out var row) || row.Claim != s.Claim).Select(s => s.CharacterId).ToList();
        if (lost.Count > 0) return new SaveResult.Lost(lost);
        await StepAsync("locked").ConfigureAwait(false);

        // What is there already (this is a second try after an answer that was lost, or a late save): it changes nothing.
        var write = group.Where(s => rows[s.CharacterId].Version < s.Version).ToList();
        if (write.Count == 0) return new SaveResult.Saved();
        var stored = write.ToDictionary(s => s.CharacterId, s => rows[s.CharacterId].Version);   // T-806: which staff notes are new in this save
        foreach (var save in write) Write(rows[save.CharacterId], save);
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await StepAsync("states").ConfigureAwait(false);

        if (await WriteItemsAsync(db, write, cancellation).ConfigureAwait(false) is { Count: > 0 } duplicated) return new SaveResult.Duplicated(duplicated);
        await WriteOutfitsAsync(db, write, cancellation).ConfigureAwait(false);
        await StepAsync("outfits_written").ConfigureAwait(false);

        var jobs = write.Select(s => s.State.JobId).ToArray();
        var owners = write.Select(s => s.CharacterId).ToArray();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE characters c SET job_id = n.job_id FROM unnest({owners}, {jobs}) AS n(id, job_id) WHERE c.id = n.id AND c.job_id <> n.job_id", cancellation).ConfigureAwait(false);
        await StepAsync("checked").ConfigureAwait(false);
        var notes = await WriteNotesAsync(db, write, stored, cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        await StepAsync("committed").ConfigureAwait(false);
        foreach (var (note, id) in notes) note.AuditId = id;
        return new SaveResult.Saved();
    });

    /// <summary>
    /// T-806: the audit rows (and <c>admin_items</c> marks) of staff changes this save is the first to have: each note above the stored save number and not above this one, so a save that
    /// lands twice, or a later one, never writes a note again. In the save's transaction: the change and its row land together.
    /// </summary>
    private static async Task<List<(StaffNote Note, long Id)>> WriteNotesAsync(GameDb db, List<CharacterSave> write, Dictionary<Guid, long> stored, CancellationToken cancellation)
    {
        var written = new List<(StaffNote, long)>();
        foreach (var save in write)
            foreach (var note in save.StaffNotes)
            {
                if (note.Version <= stored[save.CharacterId] || note.Version > save.Version) continue;
                var id = await PostgresAdminStore.WriteAuditAsync(db, note.Audit, cancellation).ConfigureAwait(false);
                foreach (var (item, itemId, quantity) in note.Made)
                    db.AdminItems.Add(new AdminItemRow { ItemInstanceId = item, ItemId = itemId, Quantity = quantity, CreatedAt = note.Audit.At, AuditId = id });
                written.Add((note, id));
            }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        return written;
    }

    // ---- items --------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes the items of the group's characters exactly the saves': first what each no longer has goes (so an item that moves within the group is free), then what it has is written.
    /// Returns ids that belong to somebody else; the caller rolls back.
    /// </summary>
    private async Task<IReadOnlyCollection<long>> WriteItemsAsync(GameDb db, List<CharacterSave> write, CancellationToken cancellation)
    {
        var items = write.SelectMany(s => s.State.Items.Select(i => (Owner: s.CharacterId, Entry: i, Aside: (string?)null))
            .Concat(s.State.AsideItems.Select(a => (Owner: s.CharacterId, a.Entry, Aside: (string?)a.Reason)))).ToList();
        var twice = items.GroupBy(i => i.Entry.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (twice.Count > 0) return twice;

        var characters = write.Select(s => s.CharacterId).ToArray();
        NpgsqlParameter[] Values() =>
        [
            new("characters", characters),
            new("ids", items.Select(i => i.Entry.Id).ToArray()),
            new("owners", items.Select(i => i.Owner).ToArray()),
            Texts("item_ids", items.Select(i => (string?)i.Entry.ItemId)),
            new("quantities", items.Select(i => i.Entry.Quantity).ToArray()),
            Texts("slots", items.Select(i => i.Entry.Slot)),
            Texts("cards", items.Select(i => i.Entry.Cards.Count == 0 ? null : string.Join(',', i.Entry.Cards))),   // ids hold no comma
            new("refines", items.Select(i => i.Entry.Refine).ToArray()),
            Texts("asides", items.Select(i => i.Aside)),
        ];

        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM character_items i WHERE i.character_id = ANY(@characters) AND NOT EXISTS (SELECT 1 FROM unnest(@ids, @owners) AS n(id, owner) WHERE n.id = i.id AND n.owner = i.character_id)",
            Values(), cancellation).ConfigureAwait(false);
        await StepAsync("items_removed").ConfigureAwait(false);

        // The slot index is checked row by row, so what changes its slot leaves it first.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE character_items i SET slot = NULL FROM unnest(@ids, @slots) AS n(id, slot) WHERE i.id = n.id AND i.character_id = ANY(@characters) AND i.slot IS NOT NULL AND i.slot IS DISTINCT FROM n.slot",
            Values(), cancellation).ConfigureAwait(false);
        await StepAsync("slots_cleared").ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO character_items AS i (id, character_id, item_id, quantity, slot, cards, refine, aside)
            SELECT n.id, n.owner, n.item_id, n.quantity, n.slot, COALESCE(string_to_array(n.cards, ','), ARRAY[]::text[]), n.refine, n.aside
            FROM unnest(@ids, @owners, @item_ids, @quantities, @slots, @cards, @refines, @asides) AS n(id, owner, item_id, quantity, slot, cards, refine, aside)
            ORDER BY n.id
            ON CONFLICT (id) DO UPDATE
            SET item_id = EXCLUDED.item_id, quantity = EXCLUDED.quantity, slot = EXCLUDED.slot, cards = EXCLUDED.cards, refine = EXCLUDED.refine, aside = EXCLUDED.aside
            WHERE i.character_id = EXCLUDED.character_id
              AND (i.item_id, i.quantity, i.slot, i.cards, i.refine, i.aside) IS DISTINCT FROM (EXCLUDED.item_id, EXCLUDED.quantity, EXCLUDED.slot, EXCLUDED.cards, EXCLUDED.refine, EXCLUDED.aside)
            """, Values(), cancellation).ConfigureAwait(false);
        await StepAsync("items_written").ConfigureAwait(false);

        // Every item must be there now and be its owner's: one that is another character's was left alone above.
        var ids = items.Select(i => i.Entry.Id).ToArray();
        var owners = items.Select(i => i.Owner).ToArray();
        return await db.Database.SqlQuery<long>(
            $"SELECT n.id AS \"Value\" FROM unnest({ids}, {owners}) AS n(id, owner) WHERE NOT EXISTS (SELECT 1 FROM character_items i WHERE i.id = n.id AND i.character_id = n.owner)")
            .ToListAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Makes the owned outfits of the group's characters exactly the saves' (T-708): one statement, and nothing is written where nothing differs.</summary>
    private static Task WriteOutfitsAsync(GameDb db, List<CharacterSave> write, CancellationToken cancellation)
    {
        var outfits = write.SelectMany(s => s.State.Outfits.Distinct(StringComparer.Ordinal).Select(o => (Owner: s.CharacterId, Outfit: o))).ToList();
        return db.Database.ExecuteSqlRawAsync(
            """
            WITH gone AS (
                DELETE FROM character_outfits o WHERE o.character_id = ANY(@characters)
                AND NOT EXISTS (SELECT 1 FROM unnest(@owners, @outfits) AS n(owner, outfit) WHERE n.owner = o.character_id AND n.outfit = o.outfit_id))
            INSERT INTO character_outfits (character_id, outfit_id)
            SELECT n.owner, n.outfit FROM unnest(@owners, @outfits) AS n(owner, outfit)
            ON CONFLICT (character_id, outfit_id) DO NOTHING
            """,
            [new NpgsqlParameter("characters", write.Select(s => s.CharacterId).ToArray()), new NpgsqlParameter("owners", outfits.Select(o => o.Owner).ToArray()),
                Texts("outfits", outfits.Select(o => (string?)o.Outfit))], cancellation);
    }

    private static NpgsqlParameter Texts(string name, IEnumerable<string?> values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };

    // ---- rows ---------------------------------------------------------------------------------------------------------------------------

    private sealed record QuestsJson(List<SavedQuest> Active, List<string> Done, List<string> Unlocked, List<long> Paid, List<OwedQuestRewards> Owed);

    private sealed record CooldownsJson(Dictionary<string, long> Skills, Dictionary<string, long> Items);

    private static void Write(CharacterStateRow row, CharacterSave save)
    {
        var s = save.State;
        row.Version = save.Version;
        row.SavedAt = s.TakenAt;
        if (save.Final) row.HeldBy = null;
        row.MapId = s.MapId;
        row.CellX = s.Cell.X;
        row.CellY = s.Cell.Y;
        row.Facing = s.Facing;
        row.Hp = s.Hp;
        row.Sp = s.Sp;
        row.SaveMapId = s.SavePoint?.MapId;
        row.SaveCellX = s.SavePoint?.Cell.X;
        row.SaveCellY = s.SavePoint?.Cell.Y;
        row.BaseLevel = s.BaseLevel;
        row.BaseExp = s.BaseExp;
        row.JobId = s.JobId;
        row.JobLevel = s.JobLevel;
        row.JobExp = s.JobExp;
        row.StatPoints = s.StatPoints;
        row.SkillPoints = s.SkillPoints;
        row.Currency = s.Currency;
        row.TurnInSequence = s.TurnInSequence;
        row.AutopilotUsedMs = s.AutopilotUsedMs;
        row.AutopilotDayStartedAt = s.AutopilotDayStartedAt;
        row.Stats = JsonSerializer.Serialize(s.Stats);
        row.Skills = JsonSerializer.Serialize(s.Skills);
        row.Quests = JsonSerializer.Serialize(new QuestsJson(s.ActiveQuests.ToList(), s.CompletedQuests.ToList(), s.UnlockedJobs.ToList(), s.SettledTurnIns.ToList(), s.OwedRewards.ToList()), Json);
        row.Cooldowns = JsonSerializer.Serialize(new CooldownsJson(new(s.SkillCooldowns), new(s.ItemCooldowns)), Json);
        row.SetAside = JsonSerializer.Serialize(s.Aside, Json);
        row.OutfitSlots = JsonSerializer.Serialize(s.OutfitSlots);
        row.OutfitsHidden = s.OutfitsHidden;
    }

    private static CharacterState Read(CharacterStateRow row, List<ItemRow> items, List<string> outfits)
    {
        var quests = Parse<QuestsJson>(row.Quests, Json);
        var cooldowns = Parse<CooldownsJson>(row.Cooldowns, Json);
        return new CharacterState
        {
            TakenAt = row.SavedAt ?? throw new InvalidDataException("no saved_at"),
            MapId = row.MapId,
            Cell = new GridCell(row.CellX, row.CellY),
            Facing = row.Facing,
            Hp = row.Hp,
            Sp = row.Sp,
            SavePoint = row is { SaveMapId: { } map, SaveCellX: { } x, SaveCellY: { } y } ? new SavePoint(map, new GridCell(x, y)) : null,
            BaseLevel = row.BaseLevel,
            BaseExp = row.BaseExp,
            JobId = row.JobId,
            JobLevel = row.JobLevel,
            JobExp = row.JobExp,
            StatPoints = row.StatPoints,
            SkillPoints = row.SkillPoints,
            Stats = Parse<Dictionary<string, int>>(row.Stats, null),
            Skills = Parse<Dictionary<string, int>>(row.Skills, null),
            ActiveQuests = quests.Active,
            CompletedQuests = quests.Done,
            UnlockedJobs = quests.Unlocked,
            TurnInSequence = row.TurnInSequence,
            OwedRewards = quests.Owed,
            Items = items.Where(i => i.Aside is null).Select(Entry).ToList(),
            Currency = row.Currency,
            SettledTurnIns = quests.Paid,
            SkillCooldowns = cooldowns.Skills,
            ItemCooldowns = cooldowns.Items,
            AutopilotUsedMs = row.AutopilotUsedMs,
            AutopilotDayStartedAt = row.AutopilotDayStartedAt,
            AsideItems = items.Where(i => i.Aside is not null).Select(i => new SetAsideItem(Entry(i), i.Aside!)).ToList(),
            Aside = Parse<List<SetAside>>(row.SetAside, Json),
            Outfits = outfits,
            OutfitSlots = Parse<Dictionary<string, string>>(row.OutfitSlots, null),
            OutfitsHidden = row.OutfitsHidden,
        };
    }

    private static InventoryEntry Entry(ItemRow i) => new(i.Id, i.ItemId, i.Quantity, i.Slot) { Cards = i.Cards, Refine = i.Refine };

    private static T Parse<T>(string json, JsonSerializerOptions? options) where T : class =>
        JsonSerializer.Deserialize<T>(json, options) ?? throw new InvalidDataException("null " + typeof(T).Name);

    // ---- helpers ------------------------------------------------------------------------------------------------------------------------

    /// <summary>Whether a run holds what it claimed: not ended, and heard of within the lease.</summary>
    internal static async Task<bool> IsAliveAsync(GameDb db, long run, CancellationToken cancellation) => (await db.Database.SqlQuery<long>(
        $"SELECT id AS \"Value\" FROM server_runs WHERE id = {run} AND ended_at IS NULL AND seen_at > now() - make_interval(secs => {LeaseSeconds})").ToListAsync(cancellation).ConfigureAwait(false)).Count > 0;

    private Task StepAsync(string step) => OnStep?.Invoke(step) ?? Task.CompletedTask;

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
