namespace Game.Server.Persistence;

/// <summary>Table <c>accounts</c>: who may sign in. No personal data beyond the username (docs/STORE_COMPLIANCE.md lists everything that is stored).</summary>
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

    /// <summary>From when it may be erased (UTC; T-703), or null. While it is set nobody can sign in to the account; its owner can still take the request back.</summary>
    public DateTime? EraseAfter { get; set; }
}

/// <summary>
/// Table <c>account_deletions</c> (T-703): that an account was asked to be deleted, and when it was erased. It holds no personal data: the id of an account that is gone leads nowhere. It
/// refers to no table, because it outlives what it is about; after a restored backup it says which accounts to erase again. A request that is taken back leaves no row.
/// </summary>
public sealed class AccountDeletionRow
{
    /// <summary>The account's id.</summary>
    public Guid AccountId { get; set; }

    /// <summary>When its owner asked (UTC).</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>When its data was erased (UTC), or null while it waits.</summary>
    public DateTime? ErasedAt { get; set; }
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
/// Table <c>characters</c>: who exists. What a character owns or has done is in <see cref="CharacterStateRow"/> and <see cref="ItemRow"/>, its looks (T-705) are on this row: all hang off <see cref="Id"/>.
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

    /// <summary>Its job id, for the character list: what it was at its last save.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>
    /// Its looks (T-705, ARCHITECTURE §7.5.1): ids from <c>appearance.yaml</c>, chosen at creation and never changed. Each is null for a character that existed before T-705 (and for
    /// one created without): content's defaults apply at load, and nothing is written back. An id the content has lost stays here; it is replaced only when shown.
    /// </summary>
    public string? Gender { get; set; }

    /// <summary>Skin tone id, or null (see <see cref="Gender"/>).</summary>
    public string? SkinToneId { get; set; }

    /// <summary>Face id, or null.</summary>
    public string? FaceId { get; set; }

    /// <summary>Hairstyle id, or null.</summary>
    public string? HairstyleId { get; set; }

    /// <summary>Hair colour id, or null.</summary>
    public string? HairColorId { get; set; }

    /// <summary>When it was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When it was deleted (UTC), or null. Deletion is soft: support can undo it.</summary>
    public DateTime? DeletedAt { get; set; }
}

/// <summary>Table <c>server_runs</c> (T-702): one row per server start. Its id is the high part of every item id the run hands out, so none is ever used twice.</summary>
public sealed class ServerRunRow
{
    /// <summary>From the table's identity sequence, which never gives a number twice (not even after a rollback).</summary>
    public long Id { get; set; }

    /// <summary>When it started (the database's clock, as every time here).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When it last said it was alive: a run silent for longer than the lease, or ended, holds nothing.</summary>
    public DateTime SeenAt { get; set; }

    /// <summary>When it stopped cleanly, or null.</summary>
    public DateTime? EndedAt { get; set; }
}

/// <summary>
/// Table <c>character_states</c> (T-702): everything saved about one character but its items. <see cref="Version"/> 0: never saved, the rest means nothing. Content is never here: ids
/// and numbers only.
/// </summary>
public sealed class CharacterStateRow
{
    /// <summary>The character.</summary>
    public Guid CharacterId { get; set; }

    /// <summary>Counts who took the character; only the newest may save it.</summary>
    public long Claim { get; set; }

    /// <summary>The run that holds it, or null.</summary>
    public long? HeldBy { get; set; }

    /// <summary>The number of the last save: a save with a lower one changes nothing.</summary>
    public long Version { get; set; }

    /// <summary>When the last save's copy was made (UTC).</summary>
    public DateTime? SavedAt { get; set; }

    /// <summary>Map id.</summary>
    public string MapId { get; set; } = string.Empty;

    /// <summary>Cell.</summary>
    public int CellX { get; set; }

    /// <summary>Cell.</summary>
    public int CellY { get; set; }

    /// <summary>Facing.</summary>
    public string Facing { get; set; } = string.Empty;

    /// <summary>HP; 0 is dead.</summary>
    public int Hp { get; set; }

    /// <summary>SP.</summary>
    public int Sp { get; set; }

    /// <summary>Save point, or null.</summary>
    public string? SaveMapId { get; set; }

    /// <summary>Save point.</summary>
    public int? SaveCellX { get; set; }

    /// <summary>Save point.</summary>
    public int? SaveCellY { get; set; }

    /// <summary>Base level.</summary>
    public int BaseLevel { get; set; }

    /// <summary>Base EXP.</summary>
    public long BaseExp { get; set; }

    /// <summary>Job id.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>Job level.</summary>
    public int JobLevel { get; set; }

    /// <summary>Job EXP.</summary>
    public long JobExp { get; set; }

    /// <summary>Unspent stat points.</summary>
    public int StatPoints { get; set; }

    /// <summary>Unspent skill points.</summary>
    public int SkillPoints { get; set; }

    /// <summary>Currency.</summary>
    public long Currency { get; set; }

    /// <summary>The last quest turn-in number given out.</summary>
    public long TurnInSequence { get; set; }

    /// <summary>Autopilot time used.</summary>
    public long AutopilotUsedMs { get; set; }

    /// <summary>When the autopilot's day began, or null.</summary>
    public DateTime? AutopilotDayStartedAt { get; set; }

    /// <summary>jsonb: stat → value.</summary>
    public string Stats { get; set; } = "{}";

    /// <summary>jsonb: skill → level.</summary>
    public string Skills { get; set; } = "{}";

    /// <summary>jsonb: active and completed quests, unlocked jobs, paid turn-ins, owed rewards.</summary>
    public string Quests { get; set; } = "{}";

    /// <summary>jsonb: skill and item cooldowns, milliseconds left at <see cref="SavedAt"/>.</summary>
    public string Cooldowns { get; set; } = "{}";

    /// <summary>jsonb: what the content had no place for, items excepted (<see cref="ItemRow.Aside"/>).</summary>
    public string SetAside { get; set; } = "[]";
}

/// <summary>Table <c>character_items</c> (T-702): one inventory entry. The primary key is what keeps one item from being two characters'.</summary>
public sealed class ItemRow
{
    /// <summary>The permanent id: the run that made it and a counter (<c>CharacterSaves.NextItemId</c>).</summary>
    public long Id { get; set; }

    /// <summary>Whose it is.</summary>
    public Guid CharacterId { get; set; }

    /// <summary>The item.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>How many.</summary>
    public int Quantity { get; set; }

    /// <summary>The slot it is worn in, or null.</summary>
    public string? Slot { get; set; }

    /// <summary>Socketed cards, in socket order.</summary>
    public string[] Cards { get; set; } = [];

    /// <summary>Refine level.</summary>
    public int Refine { get; set; }

    /// <summary>Why it is kept out of the bag (the content does not have it), or null.</summary>
    public string? Aside { get; set; }
}
