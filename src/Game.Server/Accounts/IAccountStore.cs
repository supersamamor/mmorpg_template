using Game.Shared.Protocol;

namespace Game.Server.Accounts;

/// <summary>An account's id and stored password hash.</summary>
public sealed record AccountAuth(Guid Id, string PasswordHash, DateTime? EraseAfter = null)
{
    /// <summary>No hash in a log line or an exception.</summary>
    public override string ToString() => $"AccountAuth {{ {Id} }}";
}

/// <summary>A character that is not deleted.</summary>
/// <param name="Id">Permanent id.</param>
/// <param name="Slot">Slot.</param>
/// <param name="Name">Name.</param>
/// <param name="JobId">Job at its last save.</param>
/// <param name="Appearance">The ids stored with it at creation (T-705); a part is null when none was stored (a character from before T-705): the default applies at load.</param>
public sealed record CharacterEntry(Guid Id, int Slot, string Name, string JobId, AppearanceChoice? Appearance = null);

/// <summary>How creating a character ended.</summary>
public enum CreateCharacterOutcome
{
    /// <summary>Created.</summary>
    Created,

    /// <summary>Another character, perhaps a deleted one, has the name.</summary>
    NameTaken,

    /// <summary>Every slot is in use.</summary>
    NoFreeSlot,

    /// <summary>The account is gone, or waits to be erased (T-703): its session has ended meanwhile.</summary>
    AccountGone,
}

/// <summary>How deleting a character ended.</summary>
public enum DeleteCharacterOutcome
{
    /// <summary>Deleted.</summary>
    Deleted,

    /// <summary>The account has no such character.</summary>
    Unknown,

    /// <summary>A live server holds it (this one or another): nothing was changed.</summary>
    InUse,
}

/// <summary>How erasing an account ended.</summary>
public enum EraseOutcome
{
    /// <summary>Everything is gone.</summary>
    Erased,

    /// <summary>A live server still holds one of its characters: later.</summary>
    InUse,

    /// <summary>It is not due, no longer asked for, gone already, or being erased by another server right now.</summary>
    Nothing,
}

/// <summary>The store cannot answer now (the database is away or timed out). The message is safe to log.</summary>
public sealed class StoreUnavailableException(string message) : Exception(message);

/// <summary>
/// What the account service needs kept: accounts, sessions and characters. Implemented on PostgreSQL by <c>PostgresAccountStore</c>; each call is one
/// transaction. Every time is UTC. Throws <see cref="StoreUnavailableException"/> when the database does not answer.
/// </summary>
public interface IAccountStore
{
    /// <summary>The account with this normalised username, or null.</summary>
    Task<AccountAuth?> FindAccountAsync(string username, CancellationToken cancellation);

    /// <summary>The account with this id, or null.</summary>
    Task<AccountAuth?> FindAccountAsync(Guid id, CancellationToken cancellation);

    /// <summary>Creates an account; false when the username is taken.</summary>
    Task<bool> TryCreateAccountAsync(Guid id, string username, string passwordHash, DateTime now, CancellationToken cancellation);

    /// <summary>
    /// Adds a session, if the account's password hash is still <paramref name="expectedHash"/> (false otherwise: the password was changed since it was checked,
    /// and the session must not outlive that). Stores <paramref name="rehash"/> in its place when given. Deletes the account's expired sessions, and its oldest
    /// beyond <paramref name="maxSessions"/>.
    /// </summary>
    /// <returns>The token hashes of the sessions pushed out (not of the expired ones), or null when nothing was added.</returns>
    Task<IReadOnlyList<byte[]>?> TryAddSessionAsync(Guid account, string expectedHash, string? rehash, byte[] tokenHash, DateTime now, DateTime expires, int maxSessions,
        CancellationToken cancellation);

    /// <summary>The account whose live session has this token hash, or null (unknown, expired or revoked).</summary>
    Task<Guid?> FindSessionAsync(byte[] tokenHash, DateTime now, CancellationToken cancellation);

    /// <summary>Ends a session; false when there is none.</summary>
    Task<bool> DeleteSessionAsync(byte[] tokenHash, CancellationToken cancellation);

    /// <summary>Sets a new password hash, if the current one is still <paramref name="expectedHash"/>, and ends every session of the account.</summary>
    /// <returns>The token hashes of the sessions ended, or null when nothing was changed.</returns>
    Task<IReadOnlyList<byte[]>?> TryChangePasswordAsync(Guid account, string expectedHash, string newHash, DateTime now, CancellationToken cancellation);

    /// <summary>
    /// Asks for the account's deletion (T-703), if its password hash is still <paramref name="expectedHash"/>: from <paramref name="eraseAfter"/> on it may be erased. Ends every session of the
    /// account and records the request. One that is waiting already keeps its time.
    /// </summary>
    /// <returns>The token hashes of the sessions ended, or null when nothing was changed.</returns>
    Task<IReadOnlyList<byte[]>?> TryRequestErasureAsync(Guid account, string expectedHash, DateTime now, DateTime eraseAfter, CancellationToken cancellation);

    /// <summary>Takes the account's deletion request back, if its password hash is still <paramref name="expectedHash"/>; false when there is none to take back (never asked, or erased).</summary>
    Task<bool> TryCancelErasureAsync(Guid account, string expectedHash, CancellationToken cancellation);

    /// <summary>The accounts that may be erased now, longest due first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Guid>> ListErasableAsync(DateTime now, int limit, CancellationToken cancellation);

    /// <summary>
    /// Erases the account and everything that hangs off it in one transaction: sessions, characters (deleted ones too) with their states and items, and the account. Only what
    /// <see cref="ListErasableAsync"/> would list, and never while a live server holds one of its characters.
    /// </summary>
    Task<EraseOutcome> EraseAsync(Guid account, DateTime now, CancellationToken cancellation);

    /// <summary>The account's characters, by slot.</summary>
    Task<IReadOnlyList<CharacterEntry>> ListCharactersAsync(Guid account, CancellationToken cancellation);

    /// <summary>The account's character with this id, or null (also when it is another account's, or deleted).</summary>
    Task<CharacterEntry?> FindCharacterAsync(Guid account, Guid character, CancellationToken cancellation);

    /// <summary>
    /// Creates a character in the account's lowest free slot, with its <paramref name="appearance"/> (T-705; null stores none, so the defaults apply), in one transaction. The unique index on <paramref name="nameKey"/>
    /// decides who gets the name.
    /// </summary>
    Task<(CreateCharacterOutcome Outcome, CharacterEntry? Character)> CreateCharacterAsync(Guid account, Guid character, string name, string nameKey, string jobId, int maxSlots,
        AppearanceRecord? appearance, DateTime now, CancellationToken cancellation);

    /// <summary>Marks the account's character deleted, unless a live server holds it (T-710: an answer of its own, and only ever about the account's own character).</summary>
    Task<DeleteCharacterOutcome> DeleteCharacterAsync(Guid account, Guid character, DateTime now, CancellationToken cancellation);
}
