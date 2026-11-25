namespace Game.Server.Persistence;

/// <summary>Table <c>accounts</c>: who may sign in. No personal data beyond the username (T-703 owns privacy).</summary>
public sealed class AccountRow
{
    /// <summary>Permanent id.</summary>
    public Guid Id { get; set; }

    /// <summary>The username in its normalised form (<c>AccountRules.NormalizeUsername</c>): the only form stored. Unique.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Argon2id hash with its parameters and salt, in PHC string format.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>When it was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When its password was last set by its owner (UTC); a rehash does not count.</summary>
    public DateTime PasswordChangedAt { get; set; }
}

/// <summary>Table <c>account_sessions</c>: one live sign-in. Logout and a password change delete the row; an expired one goes at the account's next sign-in.</summary>
public sealed class SessionRow
{
    /// <summary>SHA-256 of the session token (32 bytes). The token itself is never stored.</summary>
    public byte[] TokenHash { get; set; } = [];

    /// <summary>The account signed in.</summary>
    public Guid AccountId { get; set; }

    /// <summary>When it signed in (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the session stops working (UTC).</summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Table <c>characters</c>: who exists. What a character owns or has done is T-702, its looks are T-705: both hang off <see cref="Id"/>.
/// </summary>
public sealed class CharacterRow
{
    /// <summary>Permanent id, separate from the runtime <c>EntityId</c> it gets each time it enters the world.</summary>
    public Guid Id { get; set; }

    /// <summary>The account it belongs to.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Its slot, from 0. Unique per account among characters that are not deleted.</summary>
    public int Slot { get; set; }

    /// <summary>Its name, as typed and normalised (<c>CharacterNameRules.Normalize</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What names are compared by (<c>CharacterNameRules.Key</c>). Unique over every row, deleted ones included: a deleted character keeps its name.</summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary>Its job id.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>When it was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When it was deleted (UTC), or null. Deletion is soft: support can undo it.</summary>
    public DateTime? DeletedAt { get; set; }
}
