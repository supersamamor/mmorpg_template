using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Game.Tests.Golden;

/// <summary>
/// A flat, sorted "key": value snapshot kept in a committed JSON file. One line per value, so a changed formula shows up as
/// a git diff or a test failure naming each key with its old and new value. Regenerate on purpose with <c>UPDATE_GOLDEN=1</c>.
/// </summary>
internal sealed class GoldenFile
{
    public const string UpdateVariable = "UPDATE_GOLDEN";

    private static readonly Regex Line = new("^  \"(?<k>[^\"]+)\": (?<v>.*?),?$", RegexOptions.Compiled);

    private readonly SortedDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Records a number with at most 6 decimals (invariant culture, no exponent).</summary>
    public void Add(string key, double value) =>
        _values.Add(key, Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture));

    public void Add(string key, long value) => _values.Add(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Records a string, or null as JSON null.</summary>
    public void Add(string key, string? value) => _values.Add(key, value == null ? "null" : "\"" + value + "\"");

    public void AddOrNull(string key, double? value)
    {
        if (value == null) _values.Add(key, "null");
        else Add(key, value.Value);
    }

    public string Serialize()
    {
        var sb = new StringBuilder("{\n");
        var i = 0;
        foreach (var pair in _values)
            sb.Append("  \"").Append(pair.Key).Append("\": ").Append(pair.Value).Append(++i < _values.Count ? ",\n" : "\n");
        return sb.Append("}\n").ToString();
    }

    /// <summary>Compares with the committed file <paramref name="name"/> in tests/Game.Tests/Golden, or rewrites it when UPDATE_GOLDEN=1.</summary>
    public void AssertMatches(string name)
    {
        var path = Path.Combine(RepoRoot(), "tests", "Game.Tests", "Golden", name);
        var actual = Serialize();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        File.Exists(path).ShouldBeTrue($"Golden file {name} is missing. Create it on purpose with {UpdateVariable}=1.");
        var expected = Parse(File.ReadAllText(path));
        var diffs = new List<string>();
        foreach (var pair in _values)
        {
            if (!expected.TryGetValue(pair.Key, out var old)) diffs.Add($"  + {pair.Key}: (new) {pair.Value}");
            else if (old != pair.Value) diffs.Add($"  ~ {pair.Key}: {old} -> {pair.Value}");
        }
        foreach (var key in expected.Keys.Where(k => !_values.ContainsKey(k)))
            diffs.Add($"  - {key}: {expected[key]} (removed)");

        if (diffs.Count > 0)
        {
            var shown = string.Join("\n", diffs.Take(60));
            var more = diffs.Count > 60 ? $"\n  ... and {diffs.Count - 60} more" : "";
            throw new Xunit.Sdk.XunitException(
                $"{name}: {diffs.Count} value(s) differ from the committed snapshot (key: old -> new).\n{shown}{more}\n" +
                $"If the change is intended, regenerate with {UpdateVariable}=1 and review the git diff of {name}. See docs/BALANCING.md.");
        }
    }

    private static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var m = Line.Match(line.TrimEnd('\r'));
            if (m.Success) result[m.Groups["k"].Value] = m.Groups["v"].Value;
        }
        return result;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }
}
