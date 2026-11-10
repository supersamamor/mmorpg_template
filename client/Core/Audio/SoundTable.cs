using System;
using System.Collections.Generic;
using System.Text.Json;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Audio;

/// <summary>One sound entry of a theme manifest (ARCHITECTURE §8): the files to pick from, the mix and the bus. Paths are relative to the theme folder.</summary>
public sealed class SoundEntry
{
    internal SoundEntry(string key, string[] files, double volumeDb, double pitchMin, double pitchMax, string bus, bool loop)
    {
        Key = key;
        Files = files;
        VolumeDb = volumeDb;
        PitchMin = pitchMin;
        PitchMax = pitchMax;
        Bus = bus;
        Loop = loop;
    }

    /// <summary>The asset key.</summary>
    public string Key { get; }

    /// <summary>Theme-relative files; one is picked at random per play.</summary>
    public string[] Files { get; }

    /// <summary>Gain in decibels.</summary>
    public double VolumeDb { get; }

    /// <summary>Lowest pitch scale.</summary>
    public double PitchMin { get; }

    /// <summary>Highest pitch scale.</summary>
    public double PitchMax { get; }

    /// <summary>An <see cref="AudioBuses"/> term.</summary>
    public string Bus { get; }

    /// <summary>True for music and ambience that repeats.</summary>
    public bool Loop { get; }
}

/// <summary>
/// The sound part of one theme's <c>asset-manifest.json</c>: every <c>sfx.</c>, <c>music.</c> and <c>ambience.</c> entry plus the <c>audio</c> table
/// (<c>defaults</c> and <c>events</c>). Parsed with <see cref="JsonDocument"/>, so it survives trimming and AOT. A damaged entry is skipped, never thrown.
/// </summary>
public sealed class SoundTable
{
    private readonly Dictionary<string, SoundEntry> _entries = new Dictionary<string, SoundEntry>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _defaults = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _events = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Creates an empty table for a theme folder name.</summary>
    public SoundTable(string theme) => Theme = theme;

    /// <summary>The theme folder name.</summary>
    public string Theme { get; }

    /// <summary>Number of sound entries.</summary>
    public int Count => _entries.Count;

    /// <summary>All sound keys.</summary>
    public IEnumerable<string> Keys => _entries.Keys;

    /// <summary>The <c>audio.defaults</c> table: category to sound key.</summary>
    public IReadOnlyDictionary<string, string> Defaults => _defaults;

    /// <summary>The <c>audio.events</c> table: event name to sound key.</summary>
    public IReadOnlyDictionary<string, string> Events => _events;

    /// <summary>One entry, or false.</summary>
    public bool TryGet(string key, out SoundEntry entry) => _entries.TryGetValue(key, out entry!);

    /// <summary>Adds an entry (tests build tables by hand with this).</summary>
    public void Add(string key, string[] files, double volumeDb = 0, double pitchMin = 1, double pitchMax = 1, string? bus = null, bool loop = false) =>
        _entries[key] = new SoundEntry(key, files, volumeDb, pitchMin, pitchMax, bus ?? DefaultBus(key), loop);

    /// <summary>Sets a category default.</summary>
    public void SetDefault(string category, string key) => _defaults[category] = key;

    /// <summary>Sets an event sound.</summary>
    public void SetEvent(string eventName, string key) => _events[eventName] = key;

    /// <summary>The bus a key plays on when its entry names none: the key's prefix decides (<c>music.</c>, <c>ambience.</c>, else sfx: VOCABULARY "Audio buses").</summary>
    public static string DefaultBus(string key)
    {
        if (key.StartsWith(AssetKeyPrefixes.Music, StringComparison.Ordinal)) return AudioBuses.Music;
        if (key.StartsWith(AssetKeyPrefixes.Ambience, StringComparison.Ordinal)) return AudioBuses.Ambient;
        return AudioBuses.Sfx;
    }

    /// <summary>True for a key that names a sound.</summary>
    public static bool IsSoundKey(string key) =>
        key.StartsWith(AssetKeyPrefixes.Sfx, StringComparison.Ordinal) || key.StartsWith(AssetKeyPrefixes.Music, StringComparison.Ordinal) || key.StartsWith(AssetKeyPrefixes.Ambience, StringComparison.Ordinal);

    /// <summary>Reads the sound entries and the audio table of a manifest. False with a reason only when the document is not a JSON object.</summary>
    public static bool TryParse(string theme, string json, out SoundTable? table, out string error)
    {
        table = null;
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "the manifest must be a JSON object";
                return false;
            }
            var result = new SoundTable(theme);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "audio")
                {
                    ReadAudio(result, property.Value);
                    continue;
                }
                if (!IsSoundKey(property.Name) || property.Value.ValueKind != JsonValueKind.Object) continue;
                var entry = ReadEntry(property.Name, property.Value);
                if (entry is not null) result._entries[property.Name] = entry;
            }
            table = result;
            return true;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }
    }

    private static void ReadAudio(SoundTable table, JsonElement audio)
    {
        if (audio.ValueKind != JsonValueKind.Object) return;
        ReadMap(audio, "defaults", table._defaults);
        ReadMap(audio, "events", table._events);
    }

    private static void ReadMap(JsonElement audio, string name, Dictionary<string, string> into)
    {
        if (!audio.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) return;
        foreach (var item in map.EnumerateObject())
            if (item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { Length: > 0 } key) into[item.Name] = key;
    }

    private static SoundEntry? ReadEntry(string key, JsonElement element)
    {
        var files = new List<string>();
        if (element.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String && file.GetString() is { Length: > 0 } one) files.Add(one);
        if (element.TryGetProperty("files", out var many) && many.ValueKind == JsonValueKind.Array)
            foreach (var item in many.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } path) files.Add(path);
        if (files.Count == 0) return null;

        double pitchMin = 1, pitchMax = 1;
        if (element.TryGetProperty("pitch", out var pitch) && pitch.ValueKind == JsonValueKind.Array && pitch.GetArrayLength() == 2 &&
            pitch[0].TryGetDouble(out var low) && pitch[1].TryGetDouble(out var high) && low > 0 && high >= low)
        {
            pitchMin = low;
            pitchMax = high;
        }
        var volume = element.TryGetProperty("volume_db", out var db) && db.ValueKind == JsonValueKind.Number && db.TryGetDouble(out var v) ? Math.Max(-60, Math.Min(12, v)) : 0;
        var bus = element.TryGetProperty("bus", out var b) && b.ValueKind == JsonValueKind.String && b.GetString() is { } named && AudioBuses.All.Contains(named) ? named : DefaultBus(key);
        var loop = element.TryGetProperty("loop", out var l) && l.ValueKind == JsonValueKind.True;
        return new SoundEntry(key, files.ToArray(), volume, pitchMin, pitchMax, bus, loop);
    }
}
