namespace Game.Server.Accounts;

/// <summary>An account's id and stored password hash.</summary>
public sealed record AccountAuth(Guid Id, string PasswordHash)
{
    /// <summary>No hash in a log line or an exception.</summary>
    public override string ToString() => $"AccountAuth {{ {Id} }}";
}

/// <summary>A character that is not deleted.</summary>
public sealed record CharacterEntry(Guid Id, int Slot, string Name, string JobId);

/// <summary>How creating a character ended.</summary>
public enum CreateCharacterOutcome
{
    /// <summary>Created.</summary>
    Created,

    /// <summary>Another character, perhaps a deleted one, has the name.</summary>
    NameTaken,

    /// <summary>Every slot is in use.</summary>
    NoFreeSlot,
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
    Task<bool> TryAddSessionAsync(Guid account, string expectedHash, string? rehash, byte[] tokenHash, DateTime now, DateTime expires, int maxSessions,
        CancellationToken cancellation);

    /// <summary>The account whose live session has this token hash, or null (unknown, expired or revoked).</summary>
    Task<Guid?> FindSessionAsync(byte[] tokenHash, DateTime now, CancellationToken cancellation);

    /// <summary>Ends a session; false when there is none.</summary>
    Task<bool> DeleteSessionAsync(byte[] tokenHash, CancellationToken cancellation);

    /// <summary>Sets a new password hash, if the current one is still <paramref name="expectedHash"/>, and ends every session of the account.</summary>
    Task<bool> TryChangePasswordAsync(Guid account, string expectedHash, string newHash, DateTime now, CancellationToken cancellation);

    /// <summary>The account's characters, by slot.</summary>
    Task<IReadOnlyList<CharacterEntry>> ListCharactersAsync(Guid account, CancellationToken cancellation);

    /// <summary>The account's character with this id, or null (also when it is another account's, or deleted).</summary>
    Task<CharacterEntry?> FindCharacterAsync(Guid account, Guid character, CancellationToken cancellation);

    /// <summary>Creates a character in the account's lowest free slot. The unique index on <paramref name="nameKey"/> decides who gets the name.</summary>
    Task<(CreateCharacterOutcome Outcome, CharacterEntry? Character)> CreateCharacterAsync(Guid account, Guid character, string name, string nameKey, string jobId, int maxSlots,
        DateTime now, CancellationToken cancellation);

    /// <summary>Marks the account's character deleted; false when it has no such character.</summary>
    Task<bool> DeleteCharacterAsync(Guid account, Guid character, DateTime now, CancellationToken cancellation);
}
