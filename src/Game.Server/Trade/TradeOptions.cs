using System.Globalization;
using System.Reflection;

namespace Game.Server.Trade;

/// <summary>
/// The trade gate's settings (T-903): how often an account may ask others to trade. Server configuration, not game content: it protects players from being pestered and the server
/// from floods; how near two must stand, how long an ask waits and how much one side may offer are game rules, in <c>balance.yaml</c> <c>trade</c>. Each has a default here and can be
/// set with <c>GAME_TRADE_&lt;NAME&gt;</c> or <c>--trade-&lt;name&gt; value</c> (kebab case; the command line wins).
/// </summary>
public sealed record TradeOptions
{
    /// <summary>The command-line prefix.</summary>
    public const string ArgPrefix = "--trade-";

    /// <summary>The environment-variable prefix.</summary>
    public const string EnvPrefix = "GAME_TRADE_";

    /// <summary>Asks an account gets back per minute (the bucket's refill): a few trades a minute, too few to pester a crowd.</summary>
    public double AsksPerMinute { get; init; } = 6;

    /// <summary>Asks an account may send at once (the bucket's size).</summary>
    public int AskBurst { get; init; } = 3;

    /// <summary>Usage text.</summary>
    public static string Usage =>
        "trade options (--trade-<name> <value> or env GAME_TRADE_<NAME>): " + string.Join(", ", Settable.Select(p => Kebab(p.Name) + " (" + p.PropertyType.Name.ToLowerInvariant() + ")"));

    private static IEnumerable<PropertyInfo> Settable => typeof(TradeOptions).GetProperties().Where(p => p.CanWrite);

    /// <summary>Applies the environment, then the command line, over the defaults; null with an error for an unknown name or a value that does not parse.</summary>
    public static TradeOptions? Parse(IReadOnlyList<string> args, Func<string, string?> environment, out string? error)
    {
        var options = new TradeOptions();
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
                    throw new FormatException($"unknown trade option '{arg}'");
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
        if (!(AsksPerMinute > 0) || AskBurst < 1) problems.Add("asks-per-minute and ask-burst must be above 0");
        return problems;
    }

    private static TradeOptions With(TradeOptions options, PropertyInfo property, string text)
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
