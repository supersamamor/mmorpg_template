using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Admin.Commands;

/// <summary>One field of the generic form, as staff filled it.</summary>
public sealed record CommandField(string Name, string Kind, string Value);

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

    /// <summary>The commands with a page of their own, and the id fields each takes.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Known = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [AdminActions.Kick] = [AdminCommandFields.AccountId],
    };

    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // shown HTML-encoded by Razor

    /// <summary>The reason as it is sent: line ends as one character, trimmed.</summary>
    public static string CleanReason(string? reason) => (reason ?? string.Empty).Replace("\r\n", "\n").Trim();

    /// <summary>The body of <paramref name="action"/> from its fields (empty rows of the generic form are left out).</summary>
    public static CommandBody Build(string action, IReadOnlyList<CommandField> fields, string? reason)
    {
        var why = CleanReason(reason);
        if (why.Length is 0 or > AdminCommandFields.MaxReasonLength) return new CommandBody(null, $"A reason is required: 1 to {AdminCommandFields.MaxReasonLength} characters.");
        var body = new Dictionary<string, object>(StringComparer.Ordinal);
        var known = Known.TryGetValue(action, out var expected);
        if (known)
        {
            foreach (var name in expected!)
            {
                var value = fields.FirstOrDefault(f => f.Name == name)?.Value.Trim() ?? string.Empty;
                if (!Guid.TryParseExact(value, "D", out _)) return new CommandBody(null, $"{name} must be an id.");
                body[name] = value;
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
}
