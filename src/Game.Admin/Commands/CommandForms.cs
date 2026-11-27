using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Admin.Commands;

/// <summary>One field of the generic form, as staff filled it.</summary>
public sealed record CommandField(string Name, string Kind, string Value);

/// <summary>How a field of a command with a page of its own is asked for and sent.</summary>
public enum FieldInput
{
    /// <summary>An account or character id, sent as text.</summary>
    Id,

    /// <summary>An item id, picked from the content the panel reads, sent as text.</summary>
    Item,

    /// <summary>An inventory entry's number, picked from the character's saved state.</summary>
    Entry,

    /// <summary>A whole number.</summary>
    Number,

    /// <summary>One line of text.</summary>
    Line,

    /// <summary>A "Support request kinds" term.</summary>
    RequestKind,
}

/// <summary>
/// One field of a command with a page of its own. The bounds are only what the field needs to be sent as typed, or limits <c>Game.Shared</c> states; the server checks every field.
/// </summary>
public sealed record FieldSpec(string Name, FieldInput Input, string Label, string Help = "", long Min = 1, long Max = long.MaxValue, bool Optional = false);

/// <summary>A command's body ready to send, or what is wrong with the form.</summary>
public sealed record CommandBody(string? Json, string? Problem);

/// <summary>
/// The forms of the commands. A command with a page of its own has fixed fields. Any other command the server lists gets the generic form: named fields, each sent as
/// text, a whole number or true/false, and the reason. The generic form is safe because it decides nothing: the confirmation shows the exact JSON, and the server checks
/// the role, every field and the reason of every command, and audits it.
/// </summary>
public static class CommandForms
{
    /// <summary>The most extra fields of the generic form.</summary>
    public const int MaxFields = 8;

    /// <summary>The longest value of a field.</summary>
    public const int MaxValueLength = 1_000;

    /// <summary>A field sent as text.</summary>
    public const string Text = "text";

    /// <summary>A field sent as a whole number.</summary>
    public const string Number = "number";

    /// <summary>A field sent as true or false.</summary>
    public const string Boolean = "boolean";

    /// <summary>The kinds of the generic form.</summary>
    public static readonly IReadOnlyList<string> Kinds = [Text, Number, Boolean];

    private static readonly FieldSpec Account = new(AdminCommandFields.AccountId, FieldInput.Id, "Account id");

    private static readonly FieldSpec Character = new(AdminCommandFields.CharacterId, FieldInput.Id, "Character id");

    private static readonly FieldSpec Request = new(AdminCommandFields.RequestId, FieldInput.Number, "Support request number");

    private static readonly FieldSpec Minutes = new(AdminCommandFields.Minutes, FieldInput.Number, "Minutes", "1440 is a day, 10080 a week.", Max: AdminCommandFields.MaxMinutes);

    /// <summary>The commands with a page of their own, and the fields each takes, in the order they are sent.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<FieldSpec>> Known = new Dictionary<string, IReadOnlyList<FieldSpec>>(StringComparer.Ordinal)
    {
        [AdminActions.Kick] = [Account],
        [AdminActions.Ban] = [Account, Minutes with { Optional = true, Help = "Leave empty for a ban without an end. 1440 is a day, 10080 a week." }],
        [AdminActions.Unban] = [Account],
        [AdminActions.Mute] = [Account, Minutes],
        [AdminActions.Unmute] = [Account],
        [AdminActions.GiveItem] = [Character, new(AdminCommandFields.ItemId, FieldInput.Item, "Item"), new(AdminCommandFields.Quantity, FieldInput.Number, "Quantity")],
        [AdminActions.RemoveItem] =
        [
            Character, new(AdminCommandFields.EntryId, FieldInput.Entry, "Inventory entry"),
            new(AdminCommandFields.Quantity, FieldInput.Number, "Quantity", "Units to take from that entry."),
        ],
        [AdminActions.ChangeCurrency] =
            [Character, new(AdminCommandFields.Amount, FieldInput.Number, "Amount", "More than 0 gives, less than 0 takes.", Min: long.MinValue)],
        [AdminActions.Unstick] = [Character],
        [AdminActions.Announce] =
        [
            new(AdminCommandFields.Text, FieldInput.Line, "Announcement",
                "Every player on this server sees it. It is kept in the audit log as written: no names or anything else about a person.", Max: AnnouncementKinds.MaxTextLength),
        ],
        [AdminActions.StartMaintenance] =
        [
            new(AdminCommandFields.WarningSeconds, FieldInput.Number, "Warning (seconds)", "How long those playing have before their games end.", Min: 0,
                Max: AdminCommandFields.MaxMaintenanceWarningSeconds),
        ],
        [AdminActions.EndMaintenance] = [],
        [AdminActions.OpenRequest] = [Account, new(AdminCommandFields.Kind, FieldInput.RequestKind, "Kind", "Only after support has checked that the owner asks.")],
        [AdminActions.CompleteRequest] = [Request],
        [AdminActions.CancelRequest] = [Request],
        [AdminActions.CloseReport] = [new(AdminCommandFields.ReportId, FieldInput.Number, "Chat report number")],
    };

    /// <summary>A command's name on a button or a heading; a command without a page keeps the server's.</summary>
    public static string Label(string action) =>
        Known.ContainsKey(action) ? char.ToUpperInvariant(action[0]) + action[1..].Replace('_', ' ') : action;

    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // shown HTML-encoded by Razor

    /// <summary>The reason as it is sent: line ends as one character, trimmed.</summary>
    public static string CleanReason(string? reason) => (reason ?? string.Empty).Replace("\r\n", "\n").Trim();

    /// <summary>The body of <paramref name="action"/> from its fields (empty rows of the generic form are left out).</summary>
    public static CommandBody Build(string action, IReadOnlyList<CommandField> fields, string? reason)
    {
        var why = CleanReason(reason);
        if (why.Length is 0 or > AdminCommandFields.MaxReasonLength) return new CommandBody(null, $"A reason is required: 1 to {AdminCommandFields.MaxReasonLength} characters.");
        var body = new Dictionary<string, object>(StringComparer.Ordinal);
        if (Known.TryGetValue(action, out var expected))
        {
            foreach (var spec in expected)
            {
                var value = fields.FirstOrDefault(f => f.Name == spec.Name)?.Value.Trim() ?? string.Empty;
                if (value.Length == 0 && spec.Optional) continue;
                var (typed, problem) = Typed(spec, value);
                if (problem is not null) return new CommandBody(null, problem);
                body[spec.Name] = typed!;
            }
        }
        else
        {
            if (fields.Count > MaxFields) return new CommandBody(null, $"At most {MaxFields} fields.");
            foreach (var field in fields)
            {
                var name = field.Name.Trim();
                var value = field.Value.Trim();
                if (name.Length == 0)
                {
                    if (value.Length == 0) continue;
                    return new CommandBody(null, "A field with a value needs a name.");
                }
                if (name.Length > 64 || !name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return new CommandBody(null, $"'{name}' is not a field name (a-z 0-9 _).");
                if (name == AdminCommandFields.Reason) return new CommandBody(null, "The reason has its own box.");
                if (value.Length > MaxValueLength) return new CommandBody(null, $"{name} is longer than {MaxValueLength} characters.");
                object typed;
                if (field.Kind == Text) typed = value;
                else if (field.Kind == Number && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) typed = number;
                else if (field.Kind == Boolean && value is "true" or "false") typed = value == "true";
                else return new CommandBody(null, field.Kind == Number ? $"{name} must be a whole number." : field.Kind == Boolean ? $"{name} must be true or false." : $"{name} has no known kind.");
                if (!body.TryAdd(name, typed)) return new CommandBody(null, $"{name} is there twice.");
            }
        }
        body[AdminCommandFields.Reason] = why;
        return new CommandBody(JsonSerializer.Serialize(body, Readable), null);
    }

    private static (object? Value, string? Problem) Typed(FieldSpec spec, string value)
    {
        switch (spec.Input)
        {
            case FieldInput.Id:
                return Guid.TryParseExact(value, "D", out _) ? (value, null) : (null, $"{spec.Name} must be an id.");
            case FieldInput.Item:
                return value.Length is > 0 and <= 128 ? (value, null) : (null, "Pick an item.");
            case FieldInput.RequestKind:
                return SupportRequestKinds.All.Contains(value) ? (value, null) : (null, "Pick the kind of request.");
            case FieldInput.Line:
                return value.Length > 0 && value.Length <= spec.Max && !value.Any(char.IsControl) ? (value, null) : (null, $"{spec.Label}: 1 to {spec.Max} characters, one line.");
            default:
                if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number >= spec.Min && number <= spec.Max) return (number, null);
                var range = spec.Max != long.MaxValue ? $" from {spec.Min} to {spec.Max}" : spec.Min == long.MinValue ? string.Empty : $", {spec.Min} or more";
                return (null, $"{spec.Name} must be a whole number{range}.");
        }
    }
}
