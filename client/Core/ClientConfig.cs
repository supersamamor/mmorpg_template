using System;
using System.Globalization;
using System.Text.Json;

namespace Game.Client.Core;

/// <summary>
/// Every client-side number and the server address (ARCHITECTURE §7.2: camera numbers are client config, not content). Values come from
/// <c>client_settings.json</c> and then the command line. No secrets and nothing from content belong here.
/// </summary>
public sealed class ClientConfig
{
    /// <summary>WebSocket URL of the gateway.</summary>
    public string ServerUrl { get; set; } = "ws://127.0.0.1:7780/ws";

    /// <summary>
    /// Whether plain <c>ws://</c> may be used (<see cref="Net.TransportPolicy"/>). False unless the owner of the process says so: the engine start-up sets it from
    /// whether this is a debug build and nothing else does. It has no settings key and no command-line switch, so a <c>client_settings.json</c> or an argument
    /// edited after install cannot turn it on in a release build (ARCHITECTURE §10).
    /// </summary>
    public bool AllowCleartext { get; set; }

    /// <summary>Name for the dev login stub (T-701 replaces it).</summary>
    public string DevName { get; set; } = "dev";

    /// <summary>Where the update screen sends the player; empty shows the message only.</summary>
    public string StoreUrl { get; set; } = string.Empty;

    // ---- connection (ARCHITECTURE §10) ----

    /// <summary>First reconnect delay, in seconds.</summary>
    public double BackoffInitialSeconds { get; set; } = 0.5;

    /// <summary>Longest reconnect delay before jitter, in seconds.</summary>
    public double BackoffCapSeconds { get; set; } = 4;

    /// <summary>Jitter as a share of the delay: 0.25 means ±25%.</summary>
    public double BackoffJitter { get; set; } = 0.25;

    /// <summary>The server's <c>grace-period-ms</c> as seconds. It is server config the protocol does not send, so the client keeps a copy.</summary>
    public double GraceSeconds { get; set; } = 60;

    /// <summary>Added to <see cref="GraceSeconds"/>, because the server may notice a silent client late (its <c>idle-timeout-ms</c>, default 30 s).</summary>
    public double GraceMarginSeconds { get; set; } = 30;

    /// <summary>Time allowed from opening a socket until <c>welcome</c>, in seconds.</summary>
    public double ConnectTimeoutSeconds { get; set; } = 8;

    /// <summary>Seconds between keep-alive pings; below the server's <c>idle-timeout-ms</c>.</summary>
    public double PingIntervalSeconds { get; set; } = 10;

    /// <summary>Silence from the server that counts as a dead socket, in seconds; above <see cref="PingIntervalSeconds"/>.</summary>
    public double SilenceTimeoutSeconds { get; set; } = 25;

    /// <summary>After returning to the foreground, how long a socket that looks open gets to answer a ping, in seconds.</summary>
    public double ForegroundProbeSeconds { get; set; } = 4;

    /// <summary>After <c>bye shutdown</c>, seconds until the next normal login attempt.</summary>
    public double ShutdownRetrySeconds { get; set; } = 30;

    /// <summary>Milliseconds the socket thread sleeps between polls.</summary>
    public int SocketPollMs { get; set; } = 15;

    // ---- rendering ----

    /// <summary>Frame cap (§9). 0 means no cap (for measuring headroom only).</summary>
    public int FpsCap { get; set; } = 30;

    /// <summary>How many server ticks the view renders behind what it has received (§10).</summary>
    public double RenderDelayTicks { get; set; } = 2;

    /// <summary>Largest turn per second while facing changes, in degrees.</summary>
    public double TurnDegreesPerSecond { get; set; } = 540;

    /// <summary>A correction longer than this many cells snaps instead of gliding.</summary>
    public double SnapDistanceCells { get; set; } = 4;

    /// <summary>Speed of a correction that glides when the entity has no speed of its own, in cells per second.</summary>
    public double GlideCellsPerSecond { get; set; } = 6;

    // ---- camera (§7.2, ADR-002 row 65) ----

    /// <summary>Degrees the fixed camera looks down from the horizon.</summary>
    public double CameraPitchDegrees { get; set; } = 50;

    /// <summary>Vertical field of view, in degrees; the horizontal one follows the screen's aspect.</summary>
    public double CameraFovDegrees { get; set; } = 30;

    /// <summary>Default distance from the player to the camera, in world units.</summary>
    public double CameraDistance { get; set; } = 15;

    /// <summary>Closest zoom.</summary>
    public double CameraDistanceMin { get; set; } = 8;

    /// <summary>Farthest zoom. It keeps the far screen corners inside the 12 cells the AOI always sends.</summary>
    public double CameraDistanceMax { get; set; } = 15.5;

    /// <summary>Distance factor of one scroll wheel notch.</summary>
    public double CameraWheelStep { get; set; } = 1.1;

    // ---- input ----

    /// <summary>A press that moves farther than this many canvas units is not a tap.</summary>
    public double TapMaxTravel { get; set; } = 24;

    /// <summary>A press held longer than this is not a tap, in seconds.</summary>
    public double TapMaxSeconds { get; set; } = 0.6;

    /// <summary>Minimum touch target in dp (§7.4); monster picking is at least this wide.</summary>
    public double TouchTargetDp { get; set; } = 48;

    /// <summary>How long the ground tap marker stays when the walk does not end it, in seconds.</summary>
    public double TapMarkerSeconds { get; set; } = 6;

    /// <summary>How long a hit's one-line flash stays, in seconds.</summary>
    public double FlashSeconds { get; set; } = 2.5;

    /// <summary>Applies a <c>client_settings.json</c> document. Unknown keys are ignored; a malformed document throws <see cref="JsonException"/>.</summary>
    public void ApplyJson(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("settings must be a JSON object");
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString() ?? string.Empty;
            reader.Read();
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
                continue;
            }
            var value = reader.TokenType == JsonTokenType.String
                ? reader.GetString() ?? string.Empty
                : reader.TokenType == JsonTokenType.Number ? reader.GetDouble().ToString("R", CultureInfo.InvariantCulture) : string.Empty;
            Set(key, value);
        }
    }

    /// <summary>
    /// Applies command-line arguments of the form <c>--server url</c>, <c>--name n</c> or <c>--set key=value</c> (any settings key).
    /// Other arguments are left to their owners.
    /// </summary>
    public void ApplyArgs(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server": ServerUrl = args[++i]; break;
                case "--name": DevName = args[++i]; break;
                case "--set":
                    var pair = args[++i];
                    var equals = pair.IndexOf('=');
                    if (equals > 0) Set(pair.Substring(0, equals), pair.Substring(equals + 1));
                    break;
            }
        }
    }

    private void Set(string key, string value)
    {
        switch (key)
        {
            case "server_url": ServerUrl = value; break;
            case "dev_name": DevName = value; break;
            case "store_url": StoreUrl = value; break;
            case "backoff_initial_seconds": BackoffInitialSeconds = Number(value); break;
            case "backoff_cap_seconds": BackoffCapSeconds = Number(value); break;
            case "backoff_jitter": BackoffJitter = Number(value); break;
            case "grace_seconds": GraceSeconds = Number(value); break;
            case "grace_margin_seconds": GraceMarginSeconds = Number(value); break;
            case "connect_timeout_seconds": ConnectTimeoutSeconds = Number(value); break;
            case "ping_interval_seconds": PingIntervalSeconds = Number(value); break;
            case "silence_timeout_seconds": SilenceTimeoutSeconds = Number(value); break;
            case "foreground_probe_seconds": ForegroundProbeSeconds = Number(value); break;
            case "shutdown_retry_seconds": ShutdownRetrySeconds = Number(value); break;
            case "socket_poll_ms": SocketPollMs = (int)Number(value); break;
            case "fps_cap": FpsCap = (int)Number(value); break;
            case "render_delay_ticks": RenderDelayTicks = Number(value); break;
            case "turn_degrees_per_second": TurnDegreesPerSecond = Number(value); break;
            case "snap_distance_cells": SnapDistanceCells = Number(value); break;
            case "glide_cells_per_second": GlideCellsPerSecond = Number(value); break;
            case "camera_pitch_degrees": CameraPitchDegrees = Number(value); break;
            case "camera_fov_degrees": CameraFovDegrees = Number(value); break;
            case "camera_distance": CameraDistance = Number(value); break;
            case "camera_distance_min": CameraDistanceMin = Number(value); break;
            case "camera_distance_max": CameraDistanceMax = Number(value); break;
            case "camera_wheel_step": CameraWheelStep = Number(value); break;
            case "tap_max_travel": TapMaxTravel = Number(value); break;
            case "tap_max_seconds": TapMaxSeconds = Number(value); break;
            case "touch_target_dp": TouchTargetDp = Number(value); break;
            case "tap_marker_seconds": TapMarkerSeconds = Number(value); break;
            case "flash_seconds": FlashSeconds = Number(value); break;
        }
    }

    private static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}
