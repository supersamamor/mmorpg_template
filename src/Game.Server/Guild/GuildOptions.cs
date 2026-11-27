using System.Globalization;
using System.Reflection;

namespace Game.Server.Guild;

/// <summary>
/// The guild service's settings (T-904): how long the notice may be, how often an account may act on guilds, and how often this server looks for other processes' changes. Server
/// configuration, not game content (the size of a guild, its ranks and what founding costs are game rules, in <c>balance.yaml</c> <c>guild</c>). Each has a default here and can be set
/// with <c>GAME_GUILD_&lt;NAME&gt;</c> or <c>--guild-&lt;name&gt; value</c> (kebab case; the command line wins).
/// </summary>
public sealed record GuildOptions
{
    /// <summary>The command-line prefix.</summary>
    public const string ArgPrefix = "--guild-";

    /// <summary>The environment-variable prefix.</summary>
    public const string EnvPrefix = "GAME_GUILD_";

    /// <summary>The longest notice, in characters after cleaning: a few lines on the guild panel of the smallest phone.</summary>
    public int NoticeLength { get; init; } = 200;

    /// <summary>Guild intents an account gets back per minute (the bucket's refill): every one reads the database, so a flood is cut off early.</summary>
    public double IntentsPerMinute { get; init; } = 20;

    /// <summary>Guild intents an account may send at once (the bucket's size).</summary>
    public int IntentBurst { get; init; } = 6;

    /// <summary>
    /// How often invites expire, online states and emblem tags go out, in milliseconds. Once a second is quick enough for an online dot and costs nothing when nothing changed.
    /// </summary>
    public int PulseMs { get; init; } = 1_000;

    /// <summary>
    /// How often this server asks the database whether guilds its players are in changed on another server process (one small query of their versions), in milliseconds.
    /// </summary>
    public int RefreshMs { get; init; } = 5_000;

    /// <summary>How long a founding waits for its tick to take the cost, in milliseconds; then it is given up and nothing is taken.</summary>
    public int PaymentWaitMs { get; init; } = 10_000;

    /// <summary>Usage text.</summary>
    public static string Usage =>
        "guild options (--guild-<name> <value> or env GAME_GUILD_<NAME>): " + string.Join(", ", Settable.Select(p => Kebab(p.Name) + " (" + p.PropertyType.Name.ToLowerInvariant() + ")"));

    private static IEnumerable<PropertyInfo> Settable => typeof(GuildOptions).GetProperties().Where(p => p.CanWrite);

    /// <summary>Applies the environment, then the command line, over the defaults; null with an error for an unknown name or a value that does not parse.</summary>
    public static GuildOptions? Parse(IReadOnlyList<string> args, Func<string, string?> environment, out string? error)
    {
        var options = new GuildOptions();
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
                    throw new FormatException($"unknown guild option '{arg}'");
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

    /// <summary>Problems that make these settings unusable (the server refuses to start), or none. <paramref name="maxMessageBytes"/>: a notice must fit a message.</summary>
    public IReadOnlyList<string> Problems(int maxMessageBytes)
    {
        var problems = new List<string>();
        if (NoticeLength < 1 || NoticeLength * 4 + 256 > maxMessageBytes) problems.Add("notice-length must be above 0 and fit a message (--max-message-bytes)");
        if (!(IntentsPerMinute > 0) || IntentBurst < 1) problems.Add("intents-per-minute and intent-burst must be above 0");
        if (PulseMs < 100 || RefreshMs < PulseMs) problems.Add("pulse-ms must be at least 100 and refresh-ms at least pulse-ms");
        if (PaymentWaitMs < 1_000) problems.Add("payment-wait-ms must be at least 1000");
        return problems;
    }

    private static GuildOptions With(GuildOptions options, PropertyInfo property, string text)
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
