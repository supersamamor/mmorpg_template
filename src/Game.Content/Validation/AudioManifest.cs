using System.Text.Json;

namespace Game.Content.Validation;

/// <summary>
/// One sound entry of the theme manifest (<c>sfx.*</c>, <c>ambience.*</c>, <c>music.*</c>): the simple form
/// <c>{ "file": ... }</c> or the rich form with <c>files</c>, <c>volume_db</c>, <c>pitch</c>, <c>bus</c>, <c>loop</c>.
/// Values are as written; the audio rule checks them.
/// </summary>
/// <param name="Key">Asset key.</param>
/// <param name="Files">The files, from <c>file</c> (one) or <c>files</c> (one is picked at random).</param>
/// <param name="FilesListed">True when the entry has a <c>files</c> list, so an empty list can be told from a missing one.</param>
/// <param name="VolumeDb">Gain in decibels, or null.</param>
/// <param name="PitchMin">Lower end of the pitch scale, or null.</param>
/// <param name="PitchMax">Upper end of the pitch scale, or null.</param>
/// <param name="Bus">Bus name, or null (the bus then follows the key's prefix).</param>
/// <param name="Loop">True to loop.</param>
public sealed record SoundEntry(
    string Key, IReadOnlyList<string> Files, bool FilesListed, double? VolumeDb, double? PitchMin, double? PitchMax, string? Bus, bool Loop);

/// <summary>A malformed part of the manifest's audio data (wrong JSON type, unknown field, ...).</summary>
/// <param name="Token">Text to find the line with (an entry key or a name).</param>
/// <param name="Path">Location in the manifest, e.g. <c>sfx.hit.light.pitch</c>.</param>
/// <param name="Message">What is wrong.</param>
public sealed record AudioManifestProblem(string Token, string Path, string Message);

/// <summary>
/// The audio parts of <c>themes/&lt;theme&gt;/asset-manifest.json</c> (ARCHITECTURE §7.8, §8): the sound entries, the
/// <c>audio.defaults</c> table (category → sound key) and the <c>audio.events</c> table (event name → sound key).
/// </summary>
public sealed class AudioManifest
{
    private static readonly string[] SoundPrefixes = { "sfx.", "ambience.", "music." };
    private static readonly HashSet<string> EntryFields = new(StringComparer.Ordinal) { "file", "files", "volume_db", "pitch", "bus", "loop" };

    private readonly string _text;

    private AudioManifest(string displayPath, string text)
    {
        DisplayPath = displayPath;
        _text = text;
    }

    /// <summary>Manifest path as shown in issues.</summary>
    public string DisplayPath { get; }

    /// <summary>The sound entries, in file order.</summary>
    public List<SoundEntry> Sounds { get; } = new();

    /// <summary><c>audio.defaults</c>, in file order.</summary>
    public List<KeyValuePair<string, string>> Defaults { get; } = new();

    /// <summary><c>audio.events</c>, in file order.</summary>
    public List<KeyValuePair<string, string>> Events { get; } = new();

    /// <summary>Shape problems found while reading.</summary>
    public List<AudioManifestProblem> Problems { get; } = new();

    /// <summary>True when <paramref name="key"/> names a sound (<c>sfx.</c>, <c>ambience.</c> or <c>music.</c> prefix).</summary>
    public static bool IsSoundKey(string key) => SoundPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    /// <summary>1-based line of the first occurrence of <paramref name="token"/> in quotes, or 0.</summary>
    public int LineOf(string token)
    {
        var at = _text.IndexOf("\"" + token + "\"", StringComparison.Ordinal);
        return at < 0 ? 0 : _text.AsSpan(0, at).Count('\n') + 1;
    }

    /// <summary>Reads the audio data. Throws <see cref="InvalidDataException"/> when the file is missing or not JSON.</summary>
    /// <param name="manifestPath">File to read.</param>
    /// <param name="displayPath">Path shown in issues; defaults to <paramref name="manifestPath"/>.</param>
    public static AudioManifest Load(string manifestPath, string? displayPath = null)
    {
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"Theme manifest not found: {manifestPath}");
        return Parse(File.ReadAllText(manifestPath), displayPath ?? manifestPath);
    }

    /// <summary>Reads the audio data from manifest text.</summary>
    public static AudioManifest Parse(string text, string displayPath)
    {
        var manifest = new AudioManifest(displayPath, text);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{displayPath}: expected a JSON object at the top level.");
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name == "audio") manifest.ReadAudio(property.Value);
                else if (IsSoundKey(property.Name)) manifest.ReadEntry(property.Name, property.Value);
            }
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{displayPath}: invalid JSON: {e.Message}", e);
        }
        return manifest;
    }

    private void ReadAudio(JsonElement audio)
    {
        if (audio.ValueKind != JsonValueKind.Object)
        {
            Problems.Add(new AudioManifestProblem("audio", "audio", "'audio' must be an object with 'defaults' and 'events'"));
            return;
        }
        foreach (var section in audio.EnumerateObject())
        {
            switch (section.Name)
            {
                case "defaults": ReadTable(section.Value, "defaults", Defaults); break;
                case "events": ReadTable(section.Value, "events", Events); break;
                default:
                    Problems.Add(new AudioManifestProblem(section.Name, "audio." + section.Name,
                        $"unknown section 'audio.{section.Name}' (expected 'defaults' or 'events')"));
                    break;
            }
        }
    }

    private void ReadTable(JsonElement table, string name, List<KeyValuePair<string, string>> into)
    {
        var path = "audio." + name;
        if (table.ValueKind != JsonValueKind.Object)
        {
            Problems.Add(new AudioManifestProblem(name, path, $"'{path}' must be an object of name → sound key"));
            return;
        }
        foreach (var row in table.EnumerateObject())
        {
            if (row.Value.ValueKind != JsonValueKind.String)
                Problems.Add(new AudioManifestProblem(row.Name, $"{path}.{row.Name}", "must be a sound key (a string)"));
            else
                into.Add(new KeyValuePair<string, string>(row.Name, row.Value.GetString()!));
        }
    }

    private void ReadEntry(string key, JsonElement entry)
    {
        void Problem(string field, string message) =>
            Problems.Add(new AudioManifestProblem(key, field.Length == 0 ? key : key + "." + field, message));

        if (entry.ValueKind != JsonValueKind.Object)
        {
            Problem("", "a sound entry must be an object");
            return;
        }

        var files = new List<string>();
        var hasFile = false;
        var filesListed = false;
        double? volume = null, pitchMin = null, pitchMax = null;
        string? bus = null;
        var loop = false;
        foreach (var field in entry.EnumerateObject())
        {
            var v = field.Value;
            switch (field.Name)
            {
                case "file":
                    hasFile = true;
                    if (v.ValueKind == JsonValueKind.String) files.Add(v.GetString()!);
                    else Problem("file", "must be a string");
                    break;
                case "files":
                    if (v.ValueKind != JsonValueKind.Array) { Problem("files", "must be a list of strings"); break; }
                    filesListed = true;
                    foreach (var f in v.EnumerateArray())
                    {
                        if (f.ValueKind == JsonValueKind.String) files.Add(f.GetString()!);
                        else Problem("files", "every item must be a string");
                    }
                    break;
                case "volume_db":
                    if (v.ValueKind == JsonValueKind.Number) volume = v.GetDouble();
                    else Problem("volume_db", "must be a number");
                    break;
                case "pitch":
                    if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2
                        && v.EnumerateArray().All(n => n.ValueKind == JsonValueKind.Number))
                    {
                        pitchMin = v[0].GetDouble();
                        pitchMax = v[1].GetDouble();
                    }
                    else Problem("pitch", "must be [min, max], two numbers");
                    break;
                case "bus":
                    if (v.ValueKind == JsonValueKind.String) bus = v.GetString();
                    else Problem("bus", "must be a string");
                    break;
                case "loop":
                    if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) loop = v.GetBoolean();
                    else Problem("loop", "must be true or false");
                    break;
                default:
                    Problem(field.Name, $"unknown field '{field.Name}' (known: {string.Join(", ", EntryFields.OrderBy(f => f, StringComparer.Ordinal))})");
                    break;
            }
        }
        if (hasFile && filesListed) Problem("", "use either 'file' or 'files', not both");
        Sounds.Add(new SoundEntry(key, files, filesListed, volume, pitchMin, pitchMax, bus, loop));
    }
}
