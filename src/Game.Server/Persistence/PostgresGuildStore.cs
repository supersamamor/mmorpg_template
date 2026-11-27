using Game.Server.Accounts;
using Game.Server.Guild;
using Game.Shared.Guild;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Server.Persistence;

/// <summary>
/// Guilds in PostgreSQL (T-904): <c>guilds</c>, <c>guild_ranks</c>, <c>guild_members</c>. Every change is one transaction that locks the guild's row first (<c>FOR UPDATE</c>), then reads its ranks
/// and members and runs <see cref="GuildRules"/> on them: whoever comes second, on any server process, sees the first one's change. Whatever adds a member row takes the character's row
/// (<c>FOR KEY SHARE</c>) before the guild's, as erasing an account and deleting a character take the character first and the guild second (<see cref="RemoveCharacterAsync"/>), so the
/// two never wait on each other in a circle. A reserved guild (<c>founded_at</c> null) is founded only by the founder's save (<see cref="FoundAsync"/>), in that save's transaction.
/// </summary>
public sealed class PostgresGuildStore(GameDatabase database) : IGuildStore
{
    /// <inheritdoc />
    public Task<long?> GuildOfAsync(Guid character, CancellationToken cancellation) => RunAsync(async db =>
        await db.GuildMembers.AsNoTracking().Where(m => m.CharacterId == character).Join(db.Guilds.Where(g => g.FoundedAt != null), m => m.GuildId, g => g.Id, (m, g) => (long?)g.Id)
            .SingleOrDefaultAsync(cancellation).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<GuildSnapshot?> LoadAsync(long guild, CancellationToken cancellation) => RunAsync(async db =>
    {
        var row = await db.Guilds.AsNoTracking().SingleOrDefaultAsync(g => g.Id == guild && g.FoundedAt != null, cancellation).ConfigureAwait(false);
        return row is null ? null : await SnapshotAsync(db, row, cancellation).ConfigureAwait(false);
    });

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, long>> VersionsAsync(IReadOnlyCollection<long> guilds, CancellationToken cancellation) => RunAsync<IReadOnlyDictionary<long, long>>(async db =>
    {
        var ids = guilds.ToArray();
        return await db.Guilds.AsNoTracking().Where(g => ids.Contains(g.Id) && g.FoundedAt != null).ToDictionaryAsync(g => g.Id, g => g.Version, cancellation).ConfigureAwait(false);
    });

    /// <inheritdoc />
    public Task<(string? Refusal, long Guild)> ReserveAsync(Guid founder, string name, string nameKey, string emblem, IReadOnlyList<GuildRankRecord> ranks, IReadOnlyCollection<long> keep,
        DateTime now, CancellationToken cancellation) => RunAsync<(string?, long)>(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        if (!await LockCharacterAsync(db, founder, cancellation).ConfigureAwait(false)) return (GuildReasons.Unavailable, 0);
        await DropStaleAsync(db, founder, keep, cancellation).ConfigureAwait(false);
        var existing = await db.GuildMembers.AsNoTracking().Where(m => m.CharacterId == founder).Join(db.Guilds, m => m.GuildId, g => g.Id, (m, g) => g.FoundedAt)
            .ToListAsync(cancellation).ConfigureAwait(false);
        if (existing.Count > 0) return (existing[0] is null ? GuildReasons.Busy : GuildReasons.AlreadyInGuild, 0);
        var guild = new GuildRow { Name = name, NameKey = nameKey, Emblem = emblem, Notice = string.Empty, Version = 1, CreatedAt = now };
        db.Guilds.Add(guild);
        if (!await TrySaveAsync(db, GameDb.GuildNameIndex, cancellation).ConfigureAwait(false)) return (GuildReasons.NameTaken, 0);
        for (var i = 0; i < ranks.Count; i++) db.GuildRanks.Add(new GuildRankRow { GuildId = guild.Id, Position = i, Name = ranks[i].Name, Permissions = ranks[i].Permissions.ToArray() });
        db.GuildMembers.Add(new GuildMemberRow { CharacterId = founder, GuildId = guild.Id, Rank = GuildRules.LeaderRank, JoinedAt = now });
        if (!await TrySaveAsync(db, GameDb.GuildMemberIndex, cancellation).ConfigureAwait(false)) return (GuildReasons.AlreadyInGuild, 0);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return (null, guild.Id);
    });

    /// <inheritdoc />
    public Task DropReservationAsync(long guild, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var row = await db.Guilds.FromSql($"SELECT * FROM guilds WHERE id = {guild} AND founded_at IS NULL FOR UPDATE").SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (row is not null) await DeleteGuildAsync(db, guild, cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return 0;
    });

    /// <inheritdoc />
    public Task<int> DropStaleReservationsAsync(Guid founder, IReadOnlyCollection<long> keep, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var dropped = await DropStaleAsync(db, founder, keep, cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return dropped;
    });

    /// <inheritdoc />
    public Task<GuildInviteCheck> CheckInviteAsync(Guid inviter, Guid invited, int maxMembers, CancellationToken cancellation) => RunAsync(async db =>
    {
        var mine = await db.GuildMembers.AsNoTracking().SingleOrDefaultAsync(m => m.CharacterId == inviter, cancellation).ConfigureAwait(false);
        var guild = mine is null ? null : await db.Guilds.AsNoTracking().SingleOrDefaultAsync(g => g.Id == mine.GuildId && g.FoundedAt != null, cancellation).ConfigureAwait(false);
        if (mine is null || guild is null) return new GuildInviteCheck(GuildReasons.NotInGuild);
        var ranks = await RanksAsync(db, guild.Id, cancellation).ConfigureAwait(false);
        if (GuildRules.MayInvite(ranks, mine.Rank) is { } refused) return new GuildInviteCheck(refused);
        if (await db.GuildMembers.AnyAsync(m => m.CharacterId == invited, cancellation).ConfigureAwait(false)) return new GuildInviteCheck(GuildReasons.AlreadyInGuild);
        if (await db.GuildMembers.CountAsync(m => m.GuildId == guild.Id, cancellation).ConfigureAwait(false) >= maxMembers) return new GuildInviteCheck(GuildReasons.Full);
        return new GuildInviteCheck(null, guild.Id, guild.Name, guild.Emblem);
    });

    /// <inheritdoc />
    public Task<GuildOutcome> JoinAsync(long guild, Guid inviter, Guid joiner, int maxMembers, DateTime now, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        if (!await LockCharacterAsync(db, joiner, cancellation).ConfigureAwait(false)) return GuildOutcome.Refused(GuildReasons.Unavailable);
        if (await LockGuildAsync(db, guild, cancellation).ConfigureAwait(false) is not { } row) return GuildOutcome.Refused(GuildReasons.NoInvite);
        var members = await db.GuildMembers.Where(m => m.GuildId == guild).ToListAsync(cancellation).ConfigureAwait(false);
        var ranks = await RanksAsync(db, guild, cancellation).ConfigureAwait(false);
        // The invite is re-checked here: an inviter who left, was kicked or lost the permission since cannot bring anyone in.
        if (members.FirstOrDefault(m => m.CharacterId == inviter) is not { } by || GuildRules.MayInvite(ranks, by.Rank) is not null) return GuildOutcome.Refused(GuildReasons.NoInvite);
        if (await db.GuildMembers.AnyAsync(m => m.CharacterId == joiner, cancellation).ConfigureAwait(false)) return GuildOutcome.Refused(GuildReasons.AlreadyInGuild);
        if (members.Count >= maxMembers) return GuildOutcome.Refused(GuildReasons.Full);
        db.GuildMembers.Add(new GuildMemberRow { CharacterId = joiner, GuildId = guild, Rank = ranks.Count - 1, JoinedAt = now });
        row.Version++;
        if (!await TrySaveAsync(db, GameDb.GuildMemberIndex, cancellation).ConfigureAwait(false)) return GuildOutcome.Refused(GuildReasons.AlreadyInGuild);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return new GuildOutcome(null, guild, [new GuildNewsMessage(GuildNews.Joined, await NameAsync(db, joiner, cancellation).ConfigureAwait(false))]);
    });

    /// <inheritdoc />
    public Task<GuildOutcome> LeaveAsync(Guid character, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var outcome = await RemoveCharacterAsync(db, character, GuildNews.Left, cancellation).ConfigureAwait(false);
        if (outcome is null) return GuildOutcome.Refused(GuildReasons.NotInGuild);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return outcome;
    });

    /// <inheritdoc />
    public Task<GuildOutcome> KickAsync(Guid actor, long member, CancellationToken cancellation) => ActAsync(actor, async (db, g) =>
    {
        if (g.Target(member) is not { } target || target.CharacterId == actor) return GuildOutcome.Refused(GuildReasons.InvalidTarget);
        if (GuildRules.MayKick(g.Ranks, g.Actor.Rank, target.Rank) is { } refused) return GuildOutcome.Refused(refused);
        db.GuildMembers.Remove(target);
        var name = await NameAsync(db, target.CharacterId, cancellation).ConfigureAwait(false);
        var news = new GuildNewsMessage(GuildNews.Kicked, name);
        return new GuildOutcome(null, g.Row.Id, [news], target.CharacterId, news);
    }, cancellation);

    /// <inheritdoc />
    public Task<GuildOutcome> SetRankAsync(Guid actor, long member, int rank, CancellationToken cancellation) => ActAsync(actor, async (db, g) =>
    {
        if (g.Target(member) is not { } target || target.CharacterId == actor) return GuildOutcome.Refused(GuildReasons.InvalidTarget);
        if (GuildRules.MaySetRank(g.Ranks, g.Actor.Rank, target.Rank, rank) is { } refused) return GuildOutcome.Refused(refused);
        target.Rank = rank;
        return new GuildOutcome(null, g.Row.Id, [new GuildNewsMessage(GuildNews.Rank, await NameAsync(db, target.CharacterId, cancellation).ConfigureAwait(false))]);
    }, cancellation);

    /// <inheritdoc />
    public Task<GuildOutcome> SetRanksAsync(Guid actor, IReadOnlyList<GuildRankRecord> below, CancellationToken cancellation) => ActAsync(actor, async (db, g) =>
    {
        if (GuildRules.MayEditRanks(g.Actor.Rank) is { } refused) return GuildOutcome.Refused(refused);
        if (below.Count < 1) return GuildOutcome.Refused(GuildReasons.InvalidRanks);
        await db.GuildRanks.Where(r => r.GuildId == g.Row.Id && r.Position > GuildRules.LeaderRank).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        for (var i = 0; i < below.Count; i++)
            db.GuildRanks.Add(new GuildRankRow { GuildId = g.Row.Id, Position = i + 1, Name = below[i].Name, Permissions = below[i].Permissions.ToArray() });
        foreach (var m in g.Members)
            if (m.Rank > below.Count) m.Rank = below.Count;   // a place the new list no longer has: its last rank
        return new GuildOutcome(null, g.Row.Id, [new GuildNewsMessage(GuildNews.Ranks)]);
    }, cancellation);

    /// <inheritdoc />
    public Task<GuildOutcome> LeadAsync(Guid actor, long member, CancellationToken cancellation) => ActAsync(actor, async (db, g) =>
    {
        if (GuildRules.MayLead(g.Actor.Rank) is { } refused) return GuildOutcome.Refused(refused);
        if (g.Target(member) is not { } target || target.CharacterId == actor) return GuildOutcome.Refused(GuildReasons.InvalidTarget);
        (g.Actor.Rank, target.Rank) = (target.Rank, GuildRules.LeaderRank);
        return new GuildOutcome(null, g.Row.Id, [new GuildNewsMessage(GuildNews.Leader, await NameAsync(db, target.CharacterId, cancellation).ConfigureAwait(false))]);
    }, cancellation);

    /// <inheritdoc />
    public Task<GuildOutcome> SetNoticeAsync(Guid actor, string notice, CancellationToken cancellation) => ActAsync(actor, (db, g) =>
    {
        if (GuildRules.MayEditNotice(g.Ranks, g.Actor.Rank) is { } refused) return Task.FromResult(GuildOutcome.Refused(refused));
        g.Row.Notice = notice;
        return Task.FromResult(new GuildOutcome(null, g.Row.Id, [new GuildNewsMessage(GuildNews.Notice)]));
    }, cancellation);

    // ---- what other transactions call (in theirs) --------------------------------------------------------------------------------------------

    /// <summary>
    /// The founder's save (T-904), in its transaction: the reserved guild is founded, as the save lacks the cost. Nothing when it was founded already (a save that lands twice) or dropped.
    /// </summary>
    internal static async Task FoundAsync(GameDb db, long guild, Guid founder, CancellationToken cancellation) =>
        await db.Database.ExecuteSqlAsync(
            $"UPDATE guilds g SET founded_at = now(), version = g.version + 1 WHERE g.id = {guild} AND g.founded_at IS NULL AND EXISTS (SELECT 1 FROM guild_members m WHERE m.guild_id = g.id AND m.character_id = {founder})",
            cancellation).ConfigureAwait(false);

    /// <summary>
    /// Takes <paramref name="character"/> out of its guild, in the caller's transaction (leaving, and T-703's erasing and deleting, which hold the character's row already): a reserved guild goes
    /// whole; a leader hands over to <see cref="GuildRules.Successor"/>; the last member disbands the guild, its name free. Null when it is in none.
    /// </summary>
    internal static async Task<GuildOutcome?> RemoveCharacterAsync(GameDb db, Guid character, string news, CancellationToken cancellation)
    {
        var mine = await db.GuildMembers.AsNoTracking().SingleOrDefaultAsync(m => m.CharacterId == character, cancellation).ConfigureAwait(false);
        if (mine is null) return null;
        var row = await db.Guilds.FromSql($"SELECT * FROM guilds WHERE id = {mine.GuildId} FOR UPDATE").SingleAsync(cancellation).ConfigureAwait(false);
        if (row.FoundedAt is null)
        {
            await DeleteGuildAsync(db, row.Id, cancellation).ConfigureAwait(false);
            return new GuildOutcome(null, row.Id, Disbanded: true);
        }
        var name = await NameAsync(db, character, cancellation).ConfigureAwait(false);
        var members = await db.GuildMembers.Where(m => m.GuildId == row.Id).OrderBy(m => m.Id).ToListAsync(cancellation).ConfigureAwait(false);
        if (members.FirstOrDefault(m => m.CharacterId == character) is not { } me) return null;   // kicked in the meantime
        var rest = members.Where(m => m != me).ToList();
        if (rest.Count == 0)
        {
            await DeleteGuildAsync(db, row.Id, cancellation).ConfigureAwait(false);
            return new GuildOutcome(null, row.Id, [], character, new GuildNewsMessage(GuildNews.Disbanded), Disbanded: true);
        }
        db.GuildMembers.Remove(me);
        var told = new List<GuildNewsMessage> { new(news, name) };
        if (me.Rank == GuildRules.LeaderRank)
        {
            var next = rest[GuildRules.Successor(rest.Select(m => (m.Rank, m.Id)).ToList())];
            next.Rank = GuildRules.LeaderRank;
            told.Add(new GuildNewsMessage(GuildNews.Leader, await NameAsync(db, next.CharacterId, cancellation).ConfigureAwait(false)));
        }
        row.Version++;
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        return new GuildOutcome(null, row.Id, told, character, null);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>A change by <paramref name="actor"/>: its guild's row locked, then its ranks and members read, then <paramref name="act"/>; the version goes up and it commits when it passed.</summary>
    private Task<GuildOutcome> ActAsync(Guid actor, Func<GameDb, Locked, Task<GuildOutcome>> act, CancellationToken cancellation) => RunAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        var guildId = await db.GuildMembers.AsNoTracking().Where(m => m.CharacterId == actor).Select(m => (long?)m.GuildId).SingleOrDefaultAsync(cancellation).ConfigureAwait(false);
        if (guildId is null || await LockGuildAsync(db, guildId.Value, cancellation).ConfigureAwait(false) is not { } row) return GuildOutcome.Refused(GuildReasons.NotInGuild);
        var members = await db.GuildMembers.Where(m => m.GuildId == row.Id).OrderBy(m => m.Id).ToListAsync(cancellation).ConfigureAwait(false);
        if (members.FirstOrDefault(m => m.CharacterId == actor) is not { } me) return GuildOutcome.Refused(GuildReasons.NotInGuild);   // it left in the meantime
        var outcome = await act(db, new Locked(row, me, members, await RanksAsync(db, row.Id, cancellation).ConfigureAwait(false))).ConfigureAwait(false);
        if (outcome.Refusal is not null) return outcome;
        row.Version++;
        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);
        return outcome;
    });

    /// <summary>A guild row locked for a change, the acting member, all members and the ranks.</summary>
    private sealed record Locked(GuildRow Row, GuildMemberRow Actor, List<GuildMemberRow> Members, IReadOnlyList<GuildRankRecord> Ranks)
    {
        public GuildMemberRow? Target(long member) => Members.FirstOrDefault(m => m.Id == member);
    }

    private static async Task<bool> LockCharacterAsync(GameDb db, Guid character, CancellationToken cancellation) =>
        await db.Characters.FromSql($"SELECT * FROM characters WHERE id = {character} AND deleted_at IS NULL FOR KEY SHARE").AsNoTracking().AnyAsync(cancellation).ConfigureAwait(false);

    private static Task<GuildRow?> LockGuildAsync(GameDb db, long guild, CancellationToken cancellation) =>
        db.Guilds.FromSql($"SELECT * FROM guilds WHERE id = {guild} AND founded_at IS NOT NULL FOR UPDATE").SingleOrDefaultAsync(cancellation);

    private static async Task<IReadOnlyList<GuildRankRecord>> RanksAsync(GameDb db, long guild, CancellationToken cancellation) =>
        (await db.GuildRanks.AsNoTracking().Where(r => r.GuildId == guild).OrderBy(r => r.Position).ToListAsync(cancellation).ConfigureAwait(false))
        .Select(r => new GuildRankRecord(r.Name, r.Permissions)).ToList();

    private static async Task<string> NameAsync(GameDb db, Guid character, CancellationToken cancellation) =>
        await db.Characters.AsNoTracking().Where(c => c.Id == character).Select(c => c.Name).SingleOrDefaultAsync(cancellation).ConfigureAwait(false) ?? string.Empty;

    private static async Task<GuildSnapshot> SnapshotAsync(GameDb db, GuildRow row, CancellationToken cancellation)
    {
        var members = await (from m in db.GuildMembers.AsNoTracking()
                             where m.GuildId == row.Id
                             join c in db.Characters on m.CharacterId equals c.Id
                             join s in db.CharacterStates on m.CharacterId equals s.CharacterId into states
                             from s in states.DefaultIfEmpty()
                             orderby m.Rank, m.Id
                             select new GuildMember(m.Id, m.CharacterId, c.Name, m.Rank, s == null ? 1 : s.BaseLevel)).ToListAsync(cancellation).ConfigureAwait(false);
        return new GuildSnapshot(row.Id, row.Name, row.Emblem, row.Notice, row.Version, await RanksAsync(db, row.Id, cancellation).ConfigureAwait(false), members);
    }

    /// <summary>Drops the founder's reservations not in <paramref name="keep"/>, in the caller's transaction.</summary>
    private static async Task<int> DropStaleAsync(GameDb db, Guid founder, IReadOnlyCollection<long> keep, CancellationToken cancellation)
    {
        var kept = keep.ToArray();
        var stale = await db.GuildMembers.AsNoTracking().Where(m => m.CharacterId == founder && !kept.Contains(m.GuildId))
            .Join(db.Guilds.Where(g => g.FoundedAt == null), m => m.GuildId, g => g.Id, (m, g) => g.Id).ToListAsync(cancellation).ConfigureAwait(false);
        foreach (var guild in stale) await DeleteGuildAsync(db, guild, cancellation).ConfigureAwait(false);
        return stale.Count;
    }

    private static async Task DeleteGuildAsync(GameDb db, long guild, CancellationToken cancellation)
    {
        await db.GuildMembers.Where(m => m.GuildId == guild).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        await db.GuildRanks.Where(r => r.GuildId == guild).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
        await db.Guilds.Where(g => g.Id == guild).ExecuteDeleteAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Saves; false when <paramref name="uniqueIndex"/> refused the row.</summary>
    private static async Task<bool> TrySaveAsync(GameDb db, string uniqueIndex, CancellationToken cancellation)
    {
        try
        {
            await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } p && p.ConstraintName == uniqueIndex)
        {
            return false;
        }
    }

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
