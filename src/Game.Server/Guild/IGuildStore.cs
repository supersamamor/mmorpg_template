using Game.Shared.Protocol;

namespace Game.Server.Guild;

/// <summary>One member as the store read it (T-904): its number, character, name, rank place and base level as last saved.</summary>
public sealed record GuildMember(long Member, Guid Character, string Name, int Rank, int Level);

/// <summary>A founded guild as the store read it (T-904): members highest rank first, then by when they joined.</summary>
public sealed record GuildSnapshot(long Id, string Name, string Emblem, string Notice, long Version, IReadOnlyList<GuildRankRecord> Ranks, IReadOnlyList<GuildMember> Members);

/// <summary>
/// What a guild change did (T-904): a refusal ("Guild reasons"), or the guild it changed with the news for its members and the character it took out, if any (that one is told
/// <see cref="RemovedNews"/>, the others <see cref="News"/>). <see cref="Disbanded"/>: the guild is gone, its name free.
/// </summary>
public sealed record GuildOutcome(string? Refusal, long Guild = 0, IReadOnlyList<GuildNewsMessage>? News = null, Guid? Removed = null, GuildNewsMessage? RemovedNews = null, bool Disbanded = false)
{
    /// <summary>A refusal.</summary>
    public static GuildOutcome Refused(string reason) => new(reason);
}

/// <summary>Whether an invite may go (T-904): a refusal, or the inviter's guild with its name and emblem for the invited.</summary>
public sealed record GuildInviteCheck(string? Refusal, long Guild = 0, string Title = "", string Emblem = "");

/// <summary>
/// Guilds in the database (T-904). Every change is one transaction that takes the guild's row first and checks the actor's rank and permissions on what it then reads, so two officers on two
/// server processes are run one after the other and the second sees the first's change (rule 10). Every method throws <see cref="Accounts.StoreUnavailableException"/> when the database does
/// not answer. A guild that is being founded (reserved, its cost not yet saved) is invisible to all but <see cref="ReserveAsync"/> and the founding save.
/// </summary>
public interface IGuildStore
{
    /// <summary>The founded guild <paramref name="character"/> is in, or null.</summary>
    Task<long?> GuildOfAsync(Guid character, CancellationToken cancellation);

    /// <summary>A founded guild with its ranks and members, or null when there is none of that number.</summary>
    Task<GuildSnapshot?> LoadAsync(long guild, CancellationToken cancellation);

    /// <summary>The versions of those of <paramref name="guilds"/> that still exist (a missing one is gone).</summary>
    Task<IReadOnlyDictionary<long, long>> VersionsAsync(IReadOnlyCollection<long> guilds, CancellationToken cancellation);

    /// <summary>
    /// Reserves a guild for <paramref name="founder"/>: its name (unique by <paramref name="nameKey"/>), emblem, ranks, and the founder as its leader, not yet founded. First drops a reservation
    /// of the founder's that is not in <paramref name="keep"/> (one left by a stay that ended without its save). Refusals: <c>already_in_guild</c>, <c>busy</c> (a reservation in
    /// <paramref name="keep"/>), <c>name_taken</c>, <c>unavailable</c> (no such character).
    /// </summary>
    Task<(string? Refusal, long Guild)> ReserveAsync(Guid founder, string name, string nameKey, string emblem, IReadOnlyList<GuildRankRecord> ranks, IReadOnlyCollection<long> keep, DateTime now,
        CancellationToken cancellation);

    /// <summary>Drops a reservation that will not be founded (its cost was refused, or its save will never land); a founded guild is left alone.</summary>
    Task DropReservationAsync(long guild, CancellationToken cancellation);

    /// <summary>Drops the founder's reservations that are not in <paramref name="keep"/>; how many it dropped.</summary>
    Task<int> DropStaleReservationsAsync(Guid founder, IReadOnlyCollection<long> keep, CancellationToken cancellation);

    /// <summary>May <paramref name="inviter"/> invite <paramref name="invited"/> now? (Checked again when the invite is accepted.)</summary>
    Task<GuildInviteCheck> CheckInviteAsync(Guid inviter, Guid invited, int maxMembers, CancellationToken cancellation);

    /// <summary><paramref name="joiner"/> accepts <paramref name="inviter"/>'s invite to <paramref name="guild"/>: the inviter must still be in it with the <c>invite</c> permission.</summary>
    Task<GuildOutcome> JoinAsync(long guild, Guid inviter, Guid joiner, int maxMembers, DateTime now, CancellationToken cancellation);

    /// <summary><paramref name="character"/> leaves; a leader hands over first (<see cref="Shared.Guild.GuildRules.Successor"/>); the last one disbands the guild.</summary>
    Task<GuildOutcome> LeaveAsync(Guid character, CancellationToken cancellation);

    /// <summary><paramref name="actor"/> kicks member <paramref name="member"/>.</summary>
    Task<GuildOutcome> KickAsync(Guid actor, long member, CancellationToken cancellation);

    /// <summary><paramref name="actor"/> gives member <paramref name="member"/> rank <paramref name="rank"/>.</summary>
    Task<GuildOutcome> SetRankAsync(Guid actor, long member, int rank, CancellationToken cancellation);

    /// <summary><paramref name="actor"/> (the leader) replaces the ranks below its own with <paramref name="below"/> (checked by the rules already).</summary>
    Task<GuildOutcome> SetRanksAsync(Guid actor, IReadOnlyList<GuildRankRecord> below, CancellationToken cancellation);

    /// <summary><paramref name="actor"/> (the leader) hands the leader rank to member <paramref name="member"/>, and takes that member's old rank.</summary>
    Task<GuildOutcome> LeadAsync(Guid actor, long member, CancellationToken cancellation);

    /// <summary><paramref name="actor"/> writes the notice (cleaned already).</summary>
    Task<GuildOutcome> SetNoticeAsync(Guid actor, string notice, CancellationToken cancellation);
}
