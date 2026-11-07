using System;
using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Game.Client.Core.Ui;

/// <summary>
/// What the player chooses on this device (T-211): kept in the engine's per-user storage, never in <c>client_settings.json</c> (which ships with the build)
/// and never on the server. Only the UI scale so far.
/// </summary>
public sealed class UserPreferences
{
    /// <summary>The chosen UI scale; 0 means the player has not chosen and the configured default applies.</summary>
    public double UiScale { get; set; }

    /// <summary>The UI scale to use: the stored choice, else the default, clamped to the configured range.</summary>
    public double EffectiveUiScale(ClientConfig config) =>
        Ui.UiScale.Clamp(UiScale > 0 ? UiScale : config.UiScaleDefault, config.UiScaleMin, config.UiScaleMax, config.UiScaleDefault);

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
                var key = reader.GetString();
                reader.Read();
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                    continue;
                }
                if (key == "ui_scale" && reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var scale) && !double.IsNaN(scale) && !double.IsInfinity(scale))
                    result.UiScale = scale;
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
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <inheritdoc />
    public override string ToString() => "ui_scale=" + UiScale.ToString("R", CultureInfo.InvariantCulture);
}
