using System;
using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Game.Client.Core.Audio;

namespace Game.Client.Core.Ui;

/// <summary>
/// What the player chooses on this device (T-211, T-305): kept in the engine's per-user storage, never in <c>client_settings.json</c> (which ships with the build)
/// and never on the server. The UI scale, the sound volumes per bus with the mute switch, and whether the corner minimap shows (T-812).
/// </summary>
public sealed class UserPreferences
{
    /// <summary>The chosen UI scale; 0 means the player has not chosen and the configured default applies.</summary>
    public double UiScale { get; set; }

    /// <summary>The player muted all sound.</summary>
    public bool Muted { get; set; }

    /// <summary>T-812: the corner minimap shows (the default); off, the full map is still the chat panel's Map button.</summary>
    public bool Minimap { get; set; } = true;

    /// <summary>The volumes the player chose per bus (0–1), in the order of <see cref="BusMixer.Buses"/>; the mixer's defaults where none was chosen.</summary>
    public double[] Volumes { get; } = DefaultVolumes();

    /// <summary>The UI scale to use: the stored choice, else the default, clamped to the configured range.</summary>
    public double EffectiveUiScale(ClientConfig config) =>
        Ui.UiScale.Clamp(UiScale > 0 ? UiScale : config.UiScaleDefault, config.UiScaleMin, config.UiScaleMax, config.UiScaleDefault);

    /// <summary>Copies the sound choices into a mixer.</summary>
    public void ApplyTo(BusMixer mixer)
    {
        mixer.Muted = Muted;
        for (var i = 0; i < BusMixer.Buses.Length; i++) mixer.SetVolume(BusMixer.Buses[i], Volumes[i]);
    }

    /// <summary>Takes the sound choices from a mixer.</summary>
    public void TakeFrom(BusMixer mixer)
    {
        Muted = mixer.Muted;
        for (var i = 0; i < BusMixer.Buses.Length; i++) Volumes[i] = mixer.Volume(BusMixer.Buses[i]);
    }

    /// <summary>Reads a preferences document. A missing, empty or damaged document gives the defaults; this never throws.</summary>
    public static UserPreferences Parse(ReadOnlySpan<byte> utf8)
    {
        var result = new UserPreferences();
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return result;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString() ?? string.Empty;
                reader.Read();
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                    continue;
                }
                if (key == "sound_muted" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                {
                    result.Muted = reader.TokenType == JsonTokenType.True;
                    continue;
                }
                if (key == "minimap" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                {
                    result.Minimap = reader.TokenType == JsonTokenType.True;
                    continue;
                }
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number)) continue;
                if (key == "ui_scale") result.UiScale = number;
                else if (key.StartsWith("volume_", StringComparison.Ordinal) && BusMixer.IndexOf(key.Substring(7)) is var bus and >= 0)
                    result.Volumes[bus] = Math.Max(0, Math.Min(1, number));
            }
        }
        catch (JsonException)
        {
            return new UserPreferences();
        }
        return result;
    }

    /// <summary>The preferences as a JSON document.</summary>
    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("ui_scale", Math.Round(UiScale, 3));
            writer.WriteBoolean("sound_muted", Muted);
            writer.WriteBoolean("minimap", Minimap);
            for (var i = 0; i < BusMixer.Buses.Length; i++) writer.WriteNumber("volume_" + BusMixer.Buses[i], Math.Round(Volumes[i], 3));
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <inheritdoc />
    public override string ToString() => "ui_scale=" + UiScale.ToString("R", CultureInfo.InvariantCulture);

    private static double[] DefaultVolumes()
    {
        var mixer = new BusMixer();
        var volumes = new double[BusMixer.Buses.Length];
        for (var i = 0; i < volumes.Length; i++) volumes[i] = mixer.Volume(BusMixer.Buses[i]);
        return volumes;
    }
}
