using System.Text.Json;

namespace Game.Admin.Api;

// The admin API's read answers as docs/ADMIN_API.md lists them. Game.Shared has the status and command answers; these the server writes without a shared type.
// Property names are the JSON's in PascalCase (snake_case on the wire). Unknown fields are ignored, so a newer server does not break the panel.

/// <summary>A page of a lookup: pass <see cref="Next"/> as the next request's cursor.</summary>
public sealed record PageAnswer<T>(IReadOnlyList<T> Items, string? Next);

/// <summary>A page of the audit log: pass <see cref="Next"/> as <c>before</c>.</summary>
public sealed record AuditPageAnswer(IReadOnlyList<AuditAnswer> Items, string? Next);

/// <summary>An account as staff see it.</summary>
public sealed record AccountAnswer(Guid Id, string Username, DateTime CreatedAt, DateTime PasswordChangedAt, DateTime? EraseAfter, int Characters, int Sessions, bool Online);

/// <summary>One account with its characters.</summary>
public sealed record AccountDetailAnswer(AccountAnswer Account, IReadOnlyList<CharacterAnswer> Characters);

/// <summary>A character as staff see it.</summary>
public sealed record CharacterAnswer(Guid Id, Guid AccountId, int Slot, string Name, string JobId, DateTime CreatedAt, DateTime? DeletedAt, bool Online);

/// <summary>One character and its saved state (null when never saved).</summary>
public sealed record CharacterDetailAnswer(CharacterAnswer Character, StateAnswer? State);

/// <summary>A saved state. The parts whose shape is the server's own are passed on as JSON and shown as they are.</summary>
public sealed record StateAnswer(long Version, DateTime? SavedAt, long Claim, long? HeldBy, string MapId, int CellX, int CellY, string Facing, int Hp, int Sp,
    string? SaveMapId, int? SaveCellX, int? SaveCellY, int BaseLevel, long BaseExp, string JobId, int JobLevel, long JobExp, int StatPoints, int SkillPoints, long Currency,
    JsonElement Stats, JsonElement Skills, JsonElement Quests, JsonElement Cooldowns, JsonElement SetAside, JsonElement OutfitSlots, bool OutfitsHidden,
    IReadOnlyList<string> Outfits, IReadOnlyList<ItemAnswer> Items);

/// <summary>One saved item.</summary>
public sealed record ItemAnswer(long Id, string ItemId, int Quantity, string? Slot, IReadOnlyList<string> Cards, int Refine, string? Aside);

/// <summary>One audit row.</summary>
public sealed record AuditAnswer(long Id, DateTime At, string RequestId, string StaffId, string StaffName, string StaffRole, string Action, string TargetKind, string TargetId,
    JsonElement Before, JsonElement After, string Reason);

/// <summary>One command the server has, and the lowest role that may run it.</summary>
public sealed record CommandInfo(string Action, string LowestRole);

/// <summary><c>GET /admin/v1/commands</c>.</summary>
public sealed record CommandListAnswer(IReadOnlyList<CommandInfo> Commands, string ReadRole, IReadOnlyList<string> Roles);

/// <summary>One ban or mute (<c>/accounts/{id}/moderation</c>). <see cref="Until"/> null: no end.</summary>
public sealed record SanctionAnswer(long Id, string Kind, DateTime From, DateTime? Until, bool InForce, long? AuditId, DateTime? LiftedAt, long? LiftedAuditId);

/// <summary>One entry of the support queue.</summary>
public sealed record SupportRequestAnswer(long Id, Guid AccountId, string Kind, string Status, DateTime OpenedAt, long? OpenedAuditId, DateTime? ClosedAt, long? ClosedAuditId);

/// <summary>An account's bans, mutes and support requests, newest first.</summary>
public sealed record ModerationAnswer(Guid AccountId, bool Banned, bool Muted, IReadOnlyList<SanctionAnswer> Bans, IReadOnlyList<SanctionAnswer> Mutes,
    IReadOnlyList<SupportRequestAnswer> Requests);

/// <summary>A player's report of a chat line. <see cref="Text"/> is what was said: personal data, shown to staff and put nowhere else.</summary>
public sealed record ChatReportAnswer(long Id, DateTime ReportedAt, Guid ReporterAccountId, Guid ReporterCharacterId, Guid SenderAccountId, Guid SenderCharacterId, string Channel,
    string Text, DateTime SaidAt, string Status, DateTime? ClosedAt, long? ClosedAuditId)
{
    /// <summary>Never the line: a report written to a log by mistake does not carry it.</summary>
    public override string ToString() => $"ChatReportAnswer {{ Id = {Id}, Status = {Status} }}";
}
