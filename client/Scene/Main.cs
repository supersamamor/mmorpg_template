using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Client.Scene.Audio;
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
    private ThemedEntities? _entities;
    private ToonLook? _look;
    private FollowCamera? _camera;
    private ThemeMapView? _map;
    private Hud? _hud;
    private AudioDriver? _audio;
    private CombatFx? _fx;
    private InputRouter? _input;
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

    /// <summary>The presenter of the world's entities.</summary>
    public WorldPresenter? Presenter => _presenter;

    /// <summary>The theme's entity visuals (for measuring).</summary>
    public ThemedEntities? Entities => _entities;

    /// <summary>The camera.</summary>
    public FollowCamera? Camera => _camera;

    /// <summary>The toon look from the theme (T-222).</summary>
    public ToonLook? Look => _look;

    /// <summary>The map on screen.</summary>
    public ThemeMapView? Map => _map;

    /// <summary>The HUD.</summary>
    public Hud? Hud => _hud;

    /// <summary>The audio system.</summary>
    public AudioDriver? Audio => _audio;
    /// <summary>The combat effects (for measuring).</summary>
    public CombatFx? Fx => _fx;

    /// <summary>The map whose ground is on screen.</summary>
    public string? LoadedMap => _loadedMap;

    /// <summary>Total managed bytes this frame path allocated in frames with no server message, and how many such frames there were (§9).</summary>
    public (long Bytes, long Frames, long BusyBytes, long BusyFrames) Allocations => (_quietBytes, _quietFrames, _busyBytes, _busyFrames);

    /// <summary>Starts or stops counting allocations.</summary>
    public bool CountAllocations { get; set; }

    private long _quietBytes, _quietFrames, _busyBytes, _busyFrames;

    /// <inheritdoc />
    public override void _EnterTree()
    {
        // Scripted and measuring runs with a window (T-711, ADR-002 row 346): Godot 4.7 puts a window it is told to open off every screen (--position) back on one, so the
        // window is moved off once it exists. It only moves a window, so it is safe in any build.
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--offscreen") >= 0 && DisplayServer.GetName() != "headless") DisplayServer.WindowSetPosition(new Vector2I(-10000, -10000));
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        if (AutoRun.Wanted(args)) UserPrefsStore.UseScratch();
        LoadSettings(args);
        _config.AllowCleartext = OS.IsDebugBuild(); // the only place this is set (TransportPolicy): not from settings, not from arguments
        Engine.MaxFps = _config.FpsCap;

        var status = ReadBundle(args, out var formatVersion);
        if (status != BundleStatus.Ok)
        {
            ToonLook.AddBuiltInEnvironment(this);
            _hud = new Hud(this, _config, null, null);
            _hud.ShowRefusal(status == BundleStatus.UnknownFormat ? UiText.BundleRefused(formatVersion) : UiText.BundleCorrupt);
            GD.Print($"[client] bundle refused: {status}, format {formatVersion}");
            if (Array.IndexOf(args, "--e2e") >= 0 || Array.IndexOf(args, "--exit-on-refusal") >= 0) AutoRun.QuitAfterShot(this, args);
            return;
        }

        var transport = TransportPolicy.Check(_config.ServerUrl, _config.AllowCleartext);
        if (transport != TransportVerdict.Allowed)
        {
            ToonLook.AddBuiltInEnvironment(this);
            _hud = new Hud(this, _config, null, null);
            _hud.ShowRefusal(UiText.TransportRefused(transport));
            GD.Print($"[client] server address refused: {transport}; {(OS.IsDebugBuild() ? "debug" : "release")} build");
            if (Array.IndexOf(args, "--e2e") >= 0 || Array.IndexOf(args, "--exit-on-refusal") >= 0) AutoRun.QuitAfterShot(this, args, "the server address");
            return;
        }

        _socket = new GodotSocket(_config.SocketPollMs);
        _session = new ClientSession(_config, _bundle!, _socket, new Pcg32Random((ulong)System.Random.Shared.NextInt64()),
            new GodotAccountTransport(this, _config.ConnectTimeoutSeconds));
        var catalog = ThemeLoader.Load(_config);
        _look = ToonLook.Mount(this, _config, catalog);
        _map = new ThemeMapView(this, catalog, _look);
        _camera = new FollowCamera(this, _config);
        _entities = new ThemedEntities(this, _config, _bundle!, catalog, _look);
        _presenter = new WorldPresenter(this, _config, _session.World, _session.Ui, _entities, _map.Ground) { Quests = _session.Quests };
        var sounds = SoundLoader.Load(_config);
        _audio = new AudioDriver(this, _config, sounds, _bundle!, _session.World, new Pcg32Random((ulong)System.Random.Shared.NextInt64()), UserPrefsStore.Load());
        _session.LevelUpReceived += _audio.OnLevelUp;
        _session.InventoryChanged += _audio.OnInventoryChanged;
        _hud = new Hud(this, _config, _session, _bundle, turning => new LookView(this, _entities!, _look, _config, turning));
        _hud.Audio = _audio;
        _hud.IconLoader = key => catalog.TryFind(key, out var path, out _) && ResourceLoader.Exists("res://" + path) ? ResourceLoader.Load<Texture2D>("res://" + path) : null;
        _session.World.Combat.ImpactDelay = _entities.ImpactDelayOf;
        _fx = new CombatFx(this, _config, _session, _camera, _hud, catalog);
        _presenter.DeathShown += _fx.OnDeath;
        _presenter.StatusStarted += (status, x, y, z, own) => _fx.OnStatusStarted(_bundle?.Status(status)?.VfxKey, x, y, z, own);
        _input = new InputRouter(_config, _session, _bundle!, _camera, _hud, () => _mapData, _map.Ground);

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
        _presenter!.Sync(delta);
        _entities!.WatchGear(world, _session.Inventory);
        _fx!.Update(Clocks.View(), _map!.Ground);
        if (world.Own is { } own) _camera!.Follow((float)own.X, (float)own.Z);
        _hud!.Refresh();
        _audio!.Update(Clocks.View());
        _input!.Tick();

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
    public override void _ExitTree()
    {
        _socket?.Shutdown();
        _entities?.Shutdown();
    }

    /// <inheritdoc />
    public override void _UnhandledInput(InputEvent @event) => HandleInput(@event);

    /// <summary>The one input path for mouse and touch. The scripted run feeds it too, so it taps exactly as a finger does.</summary>
    public void HandleInput(InputEvent @event) => _input?.Handle(@event);

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationPaused) _audio?.OnBackgrounded();
        else if (what == NotificationApplicationResumed) _audio?.OnForegrounded();
        if (_session is null) return;
        if (what == NotificationApplicationPaused) _session.OnBackgrounded();
        else if (what == NotificationApplicationResumed) _session.OnForegrounded(Clocks.Wall());
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
        _session!.Numbers.Clear();
        _map!.Load(map!);
        _audio?.OnMapLoaded(map!);
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
