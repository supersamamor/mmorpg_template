using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Input;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The root node: loads the settings and the bundle, builds the placeholder world, and each frame feeds the pure core and lets the presenters read it.
/// It holds no game logic.
/// </summary>
public partial class Main : Node3D
{
    private const string BundlePath = "res://bundle/client-content.json";
    private const string SettingsPath = "res://client_settings.json";

    private ClientConfig _config = new();
    private DisplayBundle? _bundle;
    private ClientSession? _session;
    private GodotSocket? _socket;
    private WorldPresenter? _presenter;
    private FollowCamera? _camera;
    private PlaceholderMap? _map;
    private Hud? _hud;
    private TapGestures? _gestures;
    private string? _loadedMap;
    private MapData? _mapData;

    /// <summary>The settings in use.</summary>
    public ClientConfig Config => _config;

    /// <summary>The bundle, once loaded.</summary>
    public DisplayBundle? Bundle => _bundle;

    /// <summary>The session, unless the bundle was refused.</summary>
    public ClientSession? Session => _session;

    /// <summary>The socket.</summary>
    public GodotSocket? Socket => _socket;

    /// <summary>The camera.</summary>
    public FollowCamera? Camera => _camera;

    /// <summary>The ground.</summary>
    public PlaceholderMap? Map => _map;

    /// <summary>The HUD.</summary>
    public Hud? Hud => _hud;

    /// <summary>The map whose ground is on screen.</summary>
    public string? LoadedMap => _loadedMap;

    /// <summary>Total managed bytes this frame path allocated in frames with no server message, and how many such frames there were (§9).</summary>
    public (long Bytes, long Frames, long BusyBytes, long BusyFrames) Allocations => (_quietBytes, _quietFrames, _busyBytes, _busyFrames);

    /// <summary>Starts or stops counting allocations.</summary>
    public bool CountAllocations { get; set; }

    private long _quietBytes, _quietFrames, _busyBytes, _busyFrames;

    /// <inheritdoc />
    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        LoadSettings(args);
        _config.AllowCleartext = OS.IsDebugBuild(); // the only place this is set (TransportPolicy): not from settings, not from arguments
        Engine.MaxFps = _config.FpsCap;

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = PlaceholderColors.Background,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = 0.45f,
        };
        AddChild(new WorldEnvironment { Environment = environment });
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -30, 0),
            LightEnergy = 0.75f,
            ShadowEnabled = true, // only the player casts (§9)
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            DirectionalShadowMaxDistance = 40f,
        });

        var status = ReadBundle(args, out var formatVersion);
        if (status != BundleStatus.Ok)
        {
            _hud = new Hud(this, _config, null, null);
            _hud.ShowRefusal(status == BundleStatus.UnknownFormat ? UiText.BundleRefused(formatVersion) : UiText.BundleCorrupt);
            GD.Print($"[client] bundle refused: {status}, format {formatVersion}");
            if (Array.IndexOf(args, "--e2e") >= 0 || Array.IndexOf(args, "--exit-on-refusal") >= 0) AutoRun.QuitAfterShot(this, args);
            return;
        }

        var transport = TransportPolicy.Check(_config.ServerUrl, _config.AllowCleartext);
        if (transport != TransportVerdict.Allowed)
        {
            _hud = new Hud(this, _config, null, null);
            _hud.ShowRefusal(UiText.TransportRefused(transport));
            GD.Print($"[client] server address refused: {transport}; {(OS.IsDebugBuild() ? "debug" : "release")} build");
            if (Array.IndexOf(args, "--e2e") >= 0 || Array.IndexOf(args, "--exit-on-refusal") >= 0) AutoRun.QuitAfterShot(this, args, "the server address");
            return;
        }

        _socket = new GodotSocket(_config.SocketPollMs);
        _session = new ClientSession(_config, _bundle!, _socket, new Pcg32Random((ulong)System.Random.Shared.NextInt64()));
        _map = new PlaceholderMap(this);
        _camera = new FollowCamera(this, _config);
        _presenter = new WorldPresenter(this, _session.World, _session.Ui, new PlaceholderEntities(this));
        _gestures = new TapGestures(_config);
        _hud = new Hud(this, _config, _session, _bundle);

        GD.Print($"[client] bundle format {_bundle!.FormatVersion}, hash {_bundle.ContentHash}; server {_config.ServerUrl}; {(OS.IsDebugBuild() ? "debug" : "release")} build, cleartext {(_config.AllowCleartext ? "allowed" : "refused")}; renderer {RenderingServer.GetCurrentRenderingMethod()}");
        if (AutoRun.Wanted(args)) _ = new AutoRun(this, args).RunAsync();
        else if (Array.IndexOf(args, "--autologin") >= 0) _session.Login(_config.DevName, Clocks.Wall());
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_session is null) return;
        var before = CountAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var messages = _session.MessagesApplied;

        _session.Update(Clocks.Wall(), Clocks.View());

        var world = _session.World;
        if (_loadedMap != world.MapId) LoadMap(world.MapId);
        _presenter!.Sync();
        if (world.Own is { } own) _camera!.Follow((float)own.X, (float)own.Z);
        _hud!.Refresh();

        if (!CountAllocations) return;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (_session.MessagesApplied == messages)
        {
            _quietBytes += bytes;
            _quietFrames++;
        }
        else
        {
            _busyBytes += bytes;
            _busyFrames++;
        }
    }

    /// <inheritdoc />
    public override void _ExitTree() => _socket?.Shutdown();

    /// <inheritdoc />
    public override void _UnhandledInput(InputEvent @event) => HandleInput(@event);

    /// <summary>The one input path for mouse and touch. The scripted run feeds it too, so it taps exactly as a finger does.</summary>
    public void HandleInput(InputEvent @event)
    {
        if (_gestures is null || _camera is null) return;
        var now = Clocks.View();
        switch (@event)
        {
            case InputEventScreenTouch touch when touch.Pressed:
                _gestures.Press(touch.Index, touch.Position.X, touch.Position.Y, now);
                break;
            case InputEventScreenTouch touch:
                if (_gestures.Release(touch.Index, touch.Position.X, touch.Position.Y, now) == GestureResult.Tap)
                    OnTap(new Vector2((float)_gestures.TapX, (float)_gestures.TapY), now);
                break;
            case InputEventScreenDrag drag:
                if (_gestures.Drag(drag.Index, drag.Position.X, drag.Position.Y) == GestureResult.Zoom) _camera.Zoom(_gestures.ZoomFactor);
                break;
            case InputEventMagnifyGesture magnify:
                _camera.Zoom(1 / magnify.Factor);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _camera.Zoom(1 / _config.CameraWheelStep);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _camera.Zoom(_config.CameraWheelStep);
                break;
        }
    }

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        if (_session is null) return;
        if (what == NotificationApplicationPaused) _session.OnBackgrounded();
        else if (what == NotificationApplicationResumed) _session.OnForegrounded(Clocks.Wall());
    }

    /// <summary>A tap: the monster under it, generously, else the ground. It only asks; range, path and walkability are the server's.</summary>
    private void OnTap(Vector2 position, double now)
    {
        var session = _session!;
        if (PickMonster(position) is { } id)
        {
            session.TapEntity(id);
            return;
        }
        if (!_camera!.TryGround(position, out var ground)) return;
        var x = TouchTargets.CellOf(ground.X);
        var y = TouchTargets.CellOf(ground.Z);
        if (_mapData is { } map && !map.Inside(x, y)) return; // nothing there to tap; everything else goes to the server
        session.TapGround(new Game.Shared.World.GridCell(x, y), now);
    }

    private long? PickMonster(Vector2 position)
    {
        var camera = _camera!.Camera;
        var canvasHeight = GetViewport().GetVisibleRect().Size.Y;
        var touch = TouchTargets.RadiusInCanvasUnits(_config.TouchTargetDp, DisplayServer.ScreenGetDpi(), GetWindow().Size.Y, canvasHeight);
        long? best = null;
        var bestDistance = float.MaxValue;
        var entities = _session!.World.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (entity.Kind != EntityKinds.Monster || entity.Dead) continue;
            var world = new Vector3((float)entity.X, 0.45f, (float)entity.Z);
            if (camera.IsPositionBehind(world)) continue;
            var center = camera.UnprojectPosition(world);
            // The shape's size on screen counts too, so a close-up monster is as easy to hit as it looks.
            var top = camera.UnprojectPosition(world + Vector3.Up * 0.6f);
            var radius = Mathf.Max((float)touch, center.DistanceTo(top));
            var distance = center.DistanceTo(position);
            if (distance <= radius && distance < bestDistance)
            {
                bestDistance = distance;
                best = entity.Id;
            }
        }
        return best;
    }

    private void LoadMap(string? id)
    {
        _loadedMap = id;
        if (id is null) return;
        if (!_bundle!.TryGetMap(id, out var map))
        {
            _hud!.ShowRefusal(UiText.MapMissing);
            GD.PrintErr($"[client] the server named a map the bundle does not have: {id}");
            return;
        }
        _mapData = map;
        _map!.Load(map!);
    }

    private void LoadSettings(string[] args)
    {
        if (Godot.FileAccess.FileExists(SettingsPath))
        {
            try
            {
                _config.ApplyJson(Godot.FileAccess.GetFileAsBytes(SettingsPath));
            }
            catch (System.Text.Json.JsonException e)
            {
                GD.PrintErr($"[client] {SettingsPath} is not valid JSON, using defaults: {e.Message}");
            }
        }
        _config.ApplyArgs(args);
    }

    private BundleStatus ReadBundle(string[] args, out int formatVersion)
    {
        var path = BundlePath;
        var overrideAt = Array.IndexOf(args, "--bundle");
        if (overrideAt >= 0 && overrideAt + 1 < args.Length && OS.IsDebugBuild()) path = args[overrideAt + 1];
        formatVersion = 0;
        if (!Godot.FileAccess.FileExists(path)) return BundleStatus.Corrupt;
        return DisplayBundle.TryRead(Godot.FileAccess.GetFileAsBytes(path), out _bundle, out formatVersion);
    }
}
