using System.Globalization;
using System.Reflection;

namespace Game.Server.Party;

/// <summary>
/// The party service's settings (T-902): how often an account may invite and how often members' bars refresh. Server configuration, not game content: they protect the server and the
/// network, and a player never plans around them (the size of a party and how long an invite waits are game rules, in <c>balance.yaml</c> <c>party</c>). Each has a default here and can
/// be set with <c>GAME_PARTY_&lt;NAME&gt;</c> or <c>--party-&lt;name&gt; value</c> (kebab case; the command line wins).
/// </summary>
public sealed record PartyOptions
{
    /// <summary>The command-line prefix.</summary>
    public const string ArgPrefix = "--party-";

    /// <summary>The environment-variable prefix.</summary>
    public const string EnvPrefix = "GAME_PARTY_";

    /// <summary>Invites an account gets back per minute (the bucket's refill): enough to gather a party in a minute, too few to spam a crowd.</summary>
    public double InvitesPerMinute { get; init; } = 6;

    /// <summary>Invites an account may send at once (the bucket's size): a full party's worth.</summary>
    public int InviteBurst { get; init; } = 4;

    /// <summary>
    /// How often members' HP, SP, map and cell go to the other members, in milliseconds (a multiple of the 100 ms tick; rounded down to one). Once a second is smooth enough for bars
    /// and marks, and costs a party of five about 1 KB a second in all; nothing goes out when nothing changed.
    /// </summary>
    public int StatusIntervalMs { get; init; } = 1_000;

    /// <summary>Usage text.</summary>
    public static string Usage =>
        "party options (--party-<name> <value> or env GAME_PARTY_<NAME>): " + string.Join(", ", Settable.Select(p => Kebab(p.Name) + " (" + p.PropertyType.Name.ToLowerInvariant() + ")"));

    private static IEnumerable<PropertyInfo> Settable => typeof(PartyOptions).GetProperties().Where(p => p.CanWrite);

    /// <summary>Applies the environment, then the command line, over the defaults; null with an error for an unknown name or a value that does not parse.</summary>
    public static PartyOptions? Parse(IReadOnlyList<string> args, Func<string, string?> environment, out string? error)
    {
        var options = new PartyOptions();
        var byName = Settable.ToDictionary(p => Kebab(p.Name), StringComparer.Ordinal);
        error = null;
        try
        {
            foreach (var (name, property) in byName)
                if (environment(EnvPrefix + name.Replace('-', '_').ToUpperInvariant()) is { Length: > 0 } value)
                    options = With(options, property, value);
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith(ArgPrefix, StringComparison.Ordinal) || !byName.TryGetValue(arg[ArgPrefix.Length..], out var property))
                    throw new FormatException($"unknown party option '{arg}'");
                if (i + 1 >= args.Count) throw new FormatException($"{arg} needs a value");
                options = With(options, property, args[++i]);
            }
        }
        catch (FormatException e)
        {
            error = e.Message;
            return null;
        }
        return options;
    }

    /// <summary>Problems that make these settings unusable (the server refuses to start), or none.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!(InvitesPerMinute > 0) || InviteBurst < 1) problems.Add("invites-per-minute and invite-burst must be above 0");
        if (StatusIntervalMs < World.TickRate.IntervalMs) problems.Add($"status-interval-ms must be at least one tick ({World.TickRate.IntervalMs})");
        return problems;
    }

    /// <summary>The status interval in ticks (at least 1).</summary>
    public int StatusIntervalTicks => Math.Max(1, StatusIntervalMs / World.TickRate.IntervalMs);

    private static PartyOptions With(PartyOptions options, PropertyInfo property, string text)
    {
        object value;
        if (property.PropertyType == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) value = i;
        else if (property.PropertyType == typeof(double) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) value = d;
        else throw new FormatException($"'{text}' is not a valid {Kebab(property.Name)}");
        var copy = options with { };
        property.SetValue(copy, value);
        return copy;
    }

    private static string Kebab(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
}
