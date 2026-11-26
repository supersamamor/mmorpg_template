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
/// refers to no table, because it outlives what it is about; a copy of it says which accounts to erase again after a backup was restored (docs/STORE_COMPLIANCE.md §6). A request that is taken back leaves no row.
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

    /// <summary>jsonb: outfit slot → the outfit chosen for it (T-708). Which outfits it owns is in <see cref="OutfitRow"/>.</summary>
    public string OutfitSlots { get; set; } = "{}";

    /// <summary>Every outfit is hidden (T-708).</summary>
    public bool OutfitsHidden { get; set; }

    /// <summary>jsonb: what the content had no place for, items excepted (<see cref="ItemRow.Aside"/>).</summary>
    public string SetAside { get; set; } = "[]";
}

/// <summary>Table <c>character_outfits</c> (T-708): one outfit a character owns. The primary key is what keeps it from owning one twice. The id is kept whatever the content says.</summary>
public sealed class OutfitRow
{
    /// <summary>Whose it is.</summary>
    public Guid CharacterId { get; set; }

    /// <summary>The outfit.</summary>
    public string OutfitId { get; set; } = string.Empty;
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

/// <summary>
/// Table <c>admin_staff</c> (T-804): the staff members the audit log names, as the panel last said (ARCHITECTURE §12.2). Staff, not players: no row here is about a player. Updated
/// at each of their writes; never deleted while an audit row names it.
/// </summary>
public sealed class AdminStaffRow
{
    /// <summary>The identity provider's subject.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The display name the panel last sent.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The role the panel last sent (each audit row keeps the role of its moment).</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Their first write (UTC).</summary>
    public DateTime FirstSeenAt { get; set; }

    /// <summary>Their latest write (UTC).</summary>
    public DateTime LastSeenAt { get; set; }
}

/// <summary>
/// Table <c>admin_audit</c> (T-804, ARCHITECTURE §12.3): one row per admin change, append-only. Triggers refuse every <c>UPDATE</c>, <c>DELETE</c> and <c>TRUNCATE</c>, whoever asks.
/// It refers to no player table: a target is an id that may outlive what it names (an erased account's id leads nowhere). Before and after hold ids and numbers only.
/// </summary>
public sealed class AdminAuditRow
{
    /// <summary>From an identity sequence: the order rows were written in.</summary>
    public long Id { get; set; }

    /// <summary>When (UTC).</summary>
    public DateTime At { get; set; }

    /// <summary>The request that made it.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Who (<see cref="AdminStaffRow"/>).</summary>
    public string StaffId { get; set; } = string.Empty;

    /// <summary>The role they had then.</summary>
    public string StaffRole { get; set; } = string.Empty;

    /// <summary>An "Admin actions" term.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>An "Admin target kinds" term.</summary>
    public string TargetKind { get; set; } = string.Empty;

    /// <summary>The target's id.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>What it was (a JSON object).</summary>
    public string Before { get; set; } = "{}";

    /// <summary>What it became (a JSON object).</summary>
    public string After { get; set; } = "{}";

    /// <summary>Why, in the staff member's words.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Table <c>account_sanctions</c> (T-806): one ban or mute of an account (<c>kind</c>, a "Sanction kinds" term). It is in force from <see cref="CreatedAt"/> until <see cref="Until"/> (never ends when
/// null; a mute always ends) unless it was lifted. Why is in the audit rows it names, not here: this row holds ids and times only. Erased with the account (docs/STORE_COMPLIANCE.md).
/// </summary>
public sealed class AccountSanctionRow
{
    /// <summary>From an identity sequence.</summary>
    public long Id { get; set; }

    /// <summary>Whose.</summary>
    public Guid AccountId { get; set; }

    /// <summary><c>ban</c> or <c>mute</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>When it began (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When it ends (UTC), or null for a ban that never does.</summary>
    public DateTime? Until { get; set; }

    /// <summary>The audit row that made it.</summary>
    public long AuditId { get; set; }

    /// <summary>When staff lifted it (UTC), or null.</summary>
    public DateTime? LiftedAt { get; set; }

    /// <summary>The audit row that lifted it, or null.</summary>
    public long? LiftedAuditId { get; set; }
}

/// <summary>
/// Table <c>support_requests</c> (T-806, ARCHITECTURE §12.4 "Store compliance"): a deletion or data-export request that came in through support, and where it stands. Ids, terms and times only;
/// what the player wrote stays in the support tool, the staff member's reason in the audit rows. Erased with the account.
/// </summary>
public sealed class SupportRequestRow
{
    /// <summary>From an identity sequence: the request's number.</summary>
    public long Id { get; set; }

    /// <summary>Whose.</summary>
    public Guid AccountId { get; set; }

    /// <summary>A "Support request kinds" term.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>A "Support request statuses" term.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>When it was put in the queue (UTC).</summary>
    public DateTime OpenedAt { get; set; }

    /// <summary>The audit row that put it there.</summary>
    public long OpenedAuditId { get; set; }

    /// <summary>When it was completed or cancelled (UTC), or null.</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>The audit row that closed it, or null.</summary>
    public long? ClosedAuditId { get; set; }
}

/// <summary>
/// Table <c>admin_items</c> (T-806, ARCHITECTURE §12.3): where <c>source = admin</c> lives. An inventory entry whose permanent id is here was made by staff (<c>give_item</c>), so a dupe audit can
/// tell it from one the game made. It names no character (the item may change hands; the audit row says who got it): the item id, how many were made, and the audit row. Rows go when the item is
/// erased with its owner's account.
/// </summary>
public sealed class AdminItemRow
{
    /// <summary>The entry's permanent id (<c>character_items.id</c>).</summary>
    public long ItemInstanceId { get; set; }

    /// <summary>The item.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>How many units staff put in this entry.</summary>
    public int Quantity { get; set; }

    /// <summary>When (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>The audit row of the <c>give_item</c> that made it.</summary>
    public long AuditId { get; set; }
}
