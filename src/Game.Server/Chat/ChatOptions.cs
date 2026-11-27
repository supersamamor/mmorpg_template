using System.Globalization;
using System.Reflection;

namespace Game.Server.Chat;

/// <summary>
/// Chat's settings (T-901): the longest line, the per-account rate, blocks, and how long lines and reports are kept. Server configuration, not game content and not balance: each has a
/// default here and can be set with <c>GAME_CHAT_&lt;NAME&gt;</c> or <c>--chat-&lt;name&gt; value</c> (kebab case; the command line wins). Nothing chat counts with is a number in its code.
/// </summary>
public sealed record ChatOptions
{
    /// <summary>The command-line prefix.</summary>
    public const string ArgPrefix = "--chat-";

    /// <summary>The environment-variable prefix.</summary>
    public const string EnvPrefix = "GAME_CHAT_";

    /// <summary>
    /// The longest line, in UTF-16 code units, after cleaning and trimming (what <c>chat_state</c> tells the client). A client's message escapes each code unit to at most six bytes, so
    /// this must fit the gateway's <c>max-message-bytes</c> with room for the rest (<see cref="Problems"/>).
    /// </summary>
    public int MaxLength { get; init; } = 120;

    /// <summary>Combining marks kept after one letter; more are removed (the "zalgo" trick). Enough for the scripts that stack two or three.</summary>
    public int MarksPerLetter { get; init; } = 3;

    /// <summary>Lines an account gets back per minute (the bucket's refill). Blocks, unblocks and reports take from the same bucket.</summary>
    public double LinesPerMinute { get; init; } = 20;

    /// <summary>Lines an account may send at once (the bucket's size).</summary>
    public int Burst { get; init; } = 5;

    /// <summary>The most characters one account may block.</summary>
    public int BlockLimit { get; init; } = 100;

    /// <summary>How long a line is kept in memory after it was said, so the players it reached can report it.</summary>
    public int ReportWindowSeconds { get; init; } = 600;

    /// <summary>How many of the most recent lines each player can report (older ones fall out of its list first).</summary>
    public int ReportableLinesPerPlayer { get; init; } = 200;

    /// <summary>The most lines kept in memory for reports on this server; past it the oldest go first.</summary>
    public int RecentLinesMax { get; init; } = 20_000;

    /// <summary>How long a closed report is kept before it is erased (an open one is kept until staff close it, or either account is erased).</summary>
    public int ClosedReportDays { get; init; } = 30;

    /// <summary>Usage text.</summary>
    public static string Usage =>
        "chat options (--chat-<name> <value> or env GAME_CHAT_<NAME>): " + string.Join(", ", Settable.Select(p => Kebab(p.Name) + " (" + p.PropertyType.Name.ToLowerInvariant() + ")"));

    private static IEnumerable<PropertyInfo> Settable => typeof(ChatOptions).GetProperties().Where(p => p.CanWrite);

    /// <summary>Applies the environment, then the command line, over the defaults; null with an error for an unknown name or a value that does not parse.</summary>
    public static ChatOptions? Parse(IReadOnlyList<string> args, Func<string, string?> environment, out string? error)
    {
        var options = new ChatOptions();
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
                    throw new FormatException($"unknown chat option '{arg}'");
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

    /// <summary>Bytes of a <c>chat</c> message besides its text: the type, channel and a whisper's name, escaped at worst.</summary>
    public const int EnvelopeBytes = 64 + 6 * 32;

    /// <summary>Problems that make these settings unusable (the server refuses to start), or none. <paramref name="maxMessageBytes"/> is the gateway's limit on a client message.</summary>
    public IReadOnlyList<string> Problems(int maxMessageBytes)
    {
        var problems = new List<string>();
        if (MaxLength < 1) problems.Add("max-length must be at least 1");
        else if (MaxLength * 6 + EnvelopeBytes > maxMessageBytes)
            problems.Add($"max-length {MaxLength} does not fit the gateway's max-message-bytes {maxMessageBytes} (a code unit may take 6 bytes on the wire): at most {(maxMessageBytes - EnvelopeBytes) / 6}");
        if (MarksPerLetter < 0) problems.Add("marks-per-letter must be 0 or more");
        if (!(LinesPerMinute > 0) || Burst < 1) problems.Add("lines-per-minute and burst must be above 0");
        if (BlockLimit < 0) problems.Add("block-limit must be 0 or more");
        if (ReportWindowSeconds < 1 || ReportableLinesPerPlayer < 1 || RecentLinesMax < 1) problems.Add("report-window-seconds, reportable-lines-per-player and recent-lines-max must be above 0");
        if (ClosedReportDays < 0) problems.Add("closed-report-days must be 0 or more");
        return problems;
    }

    private static ChatOptions With(ChatOptions options, PropertyInfo property, string text)
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
