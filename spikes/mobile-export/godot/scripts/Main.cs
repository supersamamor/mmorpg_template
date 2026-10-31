using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace MobileSpike;

/// <summary>
/// T-005 test scene, built in code: 48x48-cell ground, props, N skinned walking characters,
/// one directional light, a fixed-pitch 3/4 camera, tap-to-cell and pinch zoom, an echo-server
/// probe and an on-screen stats/event panel. "Copy report" puts everything on the clipboard.
/// </summary>
public partial class Main : Node3D
{
    const int GroundCells = 48;
    const float Pitch = 50f;
    const float MinZoom = 6f, MaxZoom = 30f;
    const string ConfigPath = "user://spike.cfg";
    const string OverridePath = "user://override.cfg";

    enum ShadowMode { Off, PlayerOnly, All }

    Camera3D _camera = null!;
    DirectionalLight3D _sun = null!;
    MeshInstance3D _marker = null!;
    Node3D _characterRoot = null!;
    readonly List<(Node3D node, Vector3 center, float radius, float speed, float phase)> _walkers = new();
    float _zoom = 16f;
    Vector3 _focus = Vector3.Zero;
    ShadowMode _shadows = ShadowMode.PlayerOnly;
    int _fpsCap = 30;
    int _characterCount = 40;
    PlaceholderCharacter.Detail _detail = PlaceholderCharacter.Detail.High;

    readonly Dictionary<int, Vector2> _touches = new();
    float _pinchStartDist, _pinchStartZoom;
    bool _pinched;
    Vector2 _tapStart;
    ulong _tapStartMs;
    string _lastTap = "-";

    Label _stats = null!, _events = null!;
    LineEdit _host = null!;
    Button _shadowButton = null!, _capButton = null!, _countButton = null!, _detailButton = null!, _reconnectButton = null!;
    Control _hudRoot = null!;
    readonly List<string> _log = new();
    EchoClient _echo = null!;
    string _shared = "";

    readonly Queue<(ulong at, float dt)> _frames = new();
    double _statsTimer;
    bool _autoreported;

    public override void _Ready()
    {
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);

        Engine.MaxFps = _fpsCap;
        _echo = new EchoClient(Log);
        BuildWorld();
        SpawnCharacters(_characterCount);
        BuildHud((string)cfg.GetValue("net", "host", "192.168.1.10"));
        ApplyShadows();

        _shared = SharedProbe.Run();
        Log(_shared.Split('\n')[0]);
        Log($"{OS.GetName()} {OS.GetModelName()} | {RenderingServer.GetCurrentRenderingMethod()} / " +
            $"{RenderingServer.GetCurrentRenderingDriverName()} | {RenderingServer.GetVideoAdapterName()} {RenderingServer.GetVideoAdapterApiVersion()}");
        Log($"character: High {PlaceholderCharacter.Triangles(PlaceholderCharacter.Detail.High)} / Low {PlaceholderCharacter.Triangles(PlaceholderCharacter.Detail.Low)} triangles, 1 material, 7 bones");

        // Desktop smoke run: `-- --connect wss://127.0.0.1:8443/echo`
        var args = OS.GetCmdlineUserArgs();
        int c = Array.IndexOf(args, "--connect");
        if (c >= 0 && c + 1 < args.Length) _echo.Connect(args[c + 1]);
    }

    // ---------------------------------------------------------------- world

    void BuildWorld()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.55f, 0.7f, 0.85f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.62f, 0.7f),
                AmbientLightEnergy = 0.6f,
            },
        });

        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -35, 0),
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 40f,
        };
        AddChild(_sun);

        // Ground: 2x2 checker texture, one texel per cell.
        var img = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
        img.SetPixel(0, 0, new Color(0.42f, 0.6f, 0.32f)); img.SetPixel(1, 1, new Color(0.42f, 0.6f, 0.32f));
        img.SetPixel(1, 0, new Color(0.38f, 0.55f, 0.29f)); img.SetPixel(0, 1, new Color(0.38f, 0.55f, 0.29f));
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(GroundCells, GroundCells) },
            Position = Vector3.Zero,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = ImageTexture.CreateFromImage(img),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                Uv1Scale = new Vector3(GroundCells / 2f, GroundCells / 2f, 1),
            },
        });

        var rng = new RandomNumberGenerator { Seed = 5 };
        Vector3 RandomCell() => new(rng.RandiRange(-22, 21) + 0.5f, 0, rng.RandiRange(-22, 21) + 0.5f);

        // Small props batched: 2 MultiMeshes (rocks, crates) = 2 draw calls (+ shadow passes).
        AddMulti(new SphereMesh { Radius = 0.35f, Height = 0.5f, RadialSegments = 10, Rings = 5 },
            new Color(0.5f, 0.5f, 0.52f), 60, () => new Transform3D(Basis.Identity, RandomCell() + new Vector3(0, 0.15f, 0)));
        AddMulti(new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) },
            new Color(0.6f, 0.45f, 0.25f), 40, () => new Transform3D(new Basis(Vector3.Up, rng.Randf() * Mathf.Tau), RandomCell() + new Vector3(0, 0.4f, 0)));

        // Larger props as individual nodes (2 draw calls each), like hand-placed map scenery.
        var trunk = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.27f, 0.15f) };
        var leaves = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.45f, 0.2f) };
        for (int i = 0; i < 10; i++)
        {
            var tree = new Node3D { Position = RandomCell() };
            tree.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 0.2f, Height = 1.6f, RadialSegments = 12 }, Position = new Vector3(0, 0.8f, 0), MaterialOverride = trunk });
            tree.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0, BottomRadius = 1.1f, Height = 2.4f, RadialSegments = 16 }, Position = new Vector3(0, 2.6f, 0), MaterialOverride = leaves });
            AddChild(tree);
        }

        _marker = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(0.9f, 0.9f) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1, 0.85f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Visible = false,
        };
        AddChild(_marker);

        _characterRoot = new Node3D { Name = "Characters" };
        AddChild(_characterRoot);

        _camera = new Camera3D { Fov = 45f, Far = 120f };
        AddChild(_camera);
        UpdateCamera();
    }

    void AddMulti(PrimitiveMesh mesh, Color color, int count, Func<Transform3D> place)
    {
        mesh.Material = new StandardMaterial3D { AlbedoColor = color };
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = count };
        for (int i = 0; i < count; i++) mm.SetInstanceTransform(i, place());
        AddChild(new MultiMeshInstance3D { Multimesh = mm });
    }

    void SpawnCharacters(int count)
    {
        foreach (var w in _walkers) w.node.QueueFree();
        _walkers.Clear();
        var rng = new RandomNumberGenerator { Seed = 7 };
        // Spread over the area the camera sees at default zoom, so all of them are on screen.
        for (int i = 0; i < count; i++)
        {
            var node = PlaceholderCharacter.Create(_detail, rng.Randf());
            _characterRoot.AddChild(node);
            var center = new Vector3(rng.RandfRange(-9, 9), 0, rng.RandfRange(-6, 6));
            _walkers.Add((node, center, rng.RandfRange(0.5f, 2f), rng.RandfRange(0.4f, 0.9f) * (rng.Randf() < 0.5f ? -1 : 1), rng.Randf() * Mathf.Tau));
        }
        _characterCount = count;
        ApplyShadows();
    }

    void ApplyShadows()
    {
        if (_sun == null) return;
        _sun.ShadowEnabled = _shadows != ShadowMode.Off;
        for (int i = 0; i < _walkers.Count; i++)
        {
            var body = _walkers[i].node.GetNode<GeometryInstance3D>("Skeleton3D/Body");
            body.CastShadow = _shadows == ShadowMode.All || (_shadows == ShadowMode.PlayerOnly && i == 0)
                ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        }
        // Props only cast shadows in "All"; "PlayerOnly" is the §9 default.
        foreach (var gi in FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
        {
            if (gi.GetParent() is Skeleton3D || gi == _marker) continue;
            if (gi is MeshInstance3D { Mesh: PlaneMesh }) continue;
            gi.CastShadow = _shadows == ShadowMode.All ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        }
        if (_shadowButton != null) _shadowButton.Text = $"Shadows: {_shadows}";
    }

    void UpdateCamera()
    {
        float p = Mathf.DegToRad(Pitch);
        _camera.Position = _focus + new Vector3(0, Mathf.Sin(p) * _zoom, Mathf.Cos(p) * _zoom);
        _camera.LookAt(_focus, Vector3.Up);
    }

    // ---------------------------------------------------------------- frame

    public override void _Process(double delta)
    {
        float t = (float)Time.GetTicksMsec() / 1000f;
        foreach (var (node, center, radius, speed, phase) in _walkers)
        {
            float a = phase + t * speed / radius;
            node.Position = center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius;
            node.Rotation = new Vector3(0, -a + (speed > 0 ? 0 : Mathf.Pi), 0);
        }

        _echo.Poll();

        ulong now = Time.GetTicksMsec();
        _frames.Enqueue((now, (float)delta));
        while (_frames.Count > 0 && now - _frames.Peek().at > 10_000) _frames.Dequeue();

        // Desktop smoke run: `godot --path . -- --autoreport` prints the report after 12 s and quits.
        if (!_autoreported && now > 12_000 && OS.GetCmdlineUserArgs().Contains("--autoreport"))
        {
            _autoreported = true;
            CopyReport();
            var err = GetViewport().GetTexture().GetImage().SavePng("user://autoreport.png");
            GD.Print($"[spike] screenshot user://autoreport.png -> {err}");
            GetTree().Quit();
        }

        _statsTimer += delta;
        if (_statsTimer >= 0.5)
        {
            _statsTimer = 0;
            _stats.Text = StatsText();
            _reconnectButton.Text = $"Auto-reconnect: {(_echo.AutoReconnect ? "on" : "off")}";
        }
    }

    string StatsText()
    {
        var dts = _frames.Select(f => f.dt).ToArray();
        float avgFps = dts.Length > 0 ? dts.Length / dts.Sum() : 0;
        float worstMs = dts.Length > 0 ? dts.Max() * 1000 : 0;
        float budgetMs = _fpsCap > 0 ? 1000f / _fpsCap + 1f : 34.3f;
        int slow = dts.Count(d => d * 1000 > budgetMs);
        double M(Performance.Monitor m) => Performance.GetMonitor(m);
        return
            $"FPS {M(Performance.Monitor.TimeFps):F0}  avg10s {avgFps:F1}  worst {worstMs:F1} ms  >{budgetMs:F0}ms: {slow}/{dts.Length}  cap {(_fpsCap == 0 ? "off" : _fpsCap)}\n" +
            $"Draw calls {M(Performance.Monitor.RenderTotalDrawCallsInFrame):F0}  Triangles {M(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1000:F1}k  Objects {M(Performance.Monitor.RenderTotalObjectsInFrame):F0}\n" +
            $"Mem: engine {M(Performance.Monitor.MemoryStatic) / 1048576:F0} MB  VRAM {M(Performance.Monitor.RenderVideoMemUsed) / 1048576:F0} MB  managed {GC.GetTotalMemory(false) / 1048576.0:F1} MB\n" +
            $"Chars {_characterCount} x {PlaceholderCharacter.Triangles(_detail)} tris ({_detail})  Shadows {_shadows}  {RenderingServer.GetCurrentRenderingMethod()}\n" +
            $"Tap: {_lastTap}  Zoom {_zoom:F1}\n" +
            $"Net: {_echo.State}  TLS {_echo.TlsMode}\n{_echo.Stats()}\n" +
            _shared;
    }

    // ---------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventScreenTouch touch when touch.Pressed:
                _touches[touch.Index] = touch.Position;
                if (_touches.Count == 1) { _pinched = false; _tapStart = touch.Position; _tapStartMs = Time.GetTicksMsec(); }
                if (_touches.Count == 2) { _pinched = true; _pinchStartDist = PinchDistance(); _pinchStartZoom = _zoom; }
                break;
            case InputEventScreenTouch touch:
                _touches.Remove(touch.Index);
                if (_touches.Count == 0 && !_pinched && touch.Position.DistanceTo(_tapStart) < 24
                    && Time.GetTicksMsec() - _tapStartMs < 600)
                    TapGround(touch.Position);
                break;
            case InputEventScreenDrag drag:
                _touches[drag.Index] = drag.Position;
                if (_touches.Count == 2 && _pinchStartDist > 1)
                    SetZoom(_pinchStartZoom * _pinchStartDist / Mathf.Max(PinchDistance(), 1));
                break;
            case InputEventMagnifyGesture mag:
                SetZoom(_zoom / mag.Factor);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                SetZoom(_zoom * 0.9f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                SetZoom(_zoom * 1.1f);
                break;
        }
    }

    float PinchDistance()
    {
        var p = _touches.Values.Take(2).ToArray();
        return p[0].DistanceTo(p[1]);
    }

    void SetZoom(float z)
    {
        _zoom = Mathf.Clamp(z, MinZoom, MaxZoom);
        UpdateCamera();
    }

    void TapGround(Vector2 screen)
    {
        var from = _camera.ProjectRayOrigin(screen);
        var dir = _camera.ProjectRayNormal(screen);
        if (new Plane(Vector3.Up, 0).IntersectsRay(from, dir) is not Vector3 hit) return;
        int cx = Mathf.FloorToInt(hit.X), cz = Mathf.FloorToInt(hit.Z);
        if (Math.Abs(cx) > GroundCells / 2 || Math.Abs(cz) > GroundCells / 2) return;
        _marker.Position = new Vector3(cx + 0.5f, 0.02f, cz + 0.5f);
        _marker.Visible = true;
        _lastTap = $"cell ({cx}, {cz})";
    }

    // ---------------------------------------------------------------- lifecycle

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationPaused) _echo?.OnPaused();
        else if (what == NotificationApplicationResumed) _echo?.OnResumed();
        else if (what == NotificationApplicationFocusOut) Log("focus out");
        else if (what == NotificationApplicationFocusIn) Log("focus in");
    }

    void Log(string line)
    {
        var stamped = $"{Time.GetTimeStringFromSystem()} {line}";
        GD.Print("[spike] " + stamped);
        _log.Add(stamped);
        if (_events != null) _events.Text = string.Join("\n", _log.TakeLast(14));
    }

    // ---------------------------------------------------------------- HUD

    void BuildHud(string host)
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        _hudRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hudRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_hudRoot);
        GetViewport().SizeChanged += ApplySafeArea;

        var left = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        left.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        left.OffsetLeft = 12; left.OffsetTop = 8; left.OffsetRight = 860;
        _hudRoot.AddChild(left);
        _stats = NewLabel(17);
        _events = NewLabel(14);
        _events.Modulate = new Color(1, 1, 0.75f);
        left.AddChild(_stats);
        left.AddChild(_events);

        var right = new VBoxContainer();
        right.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        right.OffsetLeft = -250; right.OffsetRight = -12; right.OffsetTop = 8;
        right.AddThemeConstantOverride("separation", 6);
        _hudRoot.AddChild(right);

        _shadowButton = AddButton(right, "", () => { _shadows = (ShadowMode)(((int)_shadows + 1) % 3); ApplyShadows(); });
        _capButton = AddButton(right, "FPS cap: 30", CycleCap);
        _countButton = AddButton(right, "Characters: 40", () =>
        {
            int[] steps = { 40, 60, 20, 0 };
            SpawnCharacters(steps[(Array.IndexOf(steps, _characterCount) + 1) % steps.Length]);
            _countButton.Text = $"Characters: {_characterCount}";
        });
        _detailButton = AddButton(right, "Char detail: High", () =>
        {
            _detail = _detail == PlaceholderCharacter.Detail.High ? PlaceholderCharacter.Detail.Low : PlaceholderCharacter.Detail.High;
            SpawnCharacters(_characterCount);
            _detailButton.Text = $"Char detail: {_detail}";
        });
        AddButton(right, "Renderer (restart)", SwitchRenderer);

        _host = new LineEdit { Text = host, CustomMinimumSize = new Vector2(0, 52), PlaceholderText = "server LAN IP" };
        _host.AddThemeFontSizeOverride("font_size", 20);
        right.AddChild(_host);
        var row = new HBoxContainer();
        right.AddChild(row);
        AddButton(row, "ws", () => ConnectEcho("ws", 8080)).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        AddButton(row, "wss", () => ConnectEcho("wss", 8443)).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        AddButton(row, "close", () => _echo.Close()).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _reconnectButton = AddButton(right, "", () => _echo.AutoReconnect = !_echo.AutoReconnect);
        AddButton(right, "Copy report", CopyReport);

        ApplySafeArea();
    }

    void ApplySafeArea()
    {
        // Safe area is in window pixels; the HUD lives in the stretched canvas.
        var safe = DisplayServer.GetDisplaySafeArea();
        var win = DisplayServer.WindowGetSize();
        var vp = GetViewport().GetVisibleRect().Size;
        if (win.X <= 0 || safe.Size.X <= 0 || OS.GetName() is not ("Android" or "iOS")) return;
        float sx = vp.X / win.X, sy = vp.Y / win.Y;
        _hudRoot.OffsetLeft = safe.Position.X * sx;
        _hudRoot.OffsetTop = safe.Position.Y * sy;
        _hudRoot.OffsetRight = -(win.X - safe.End.X) * sx;
        _hudRoot.OffsetBottom = -(win.Y - safe.End.Y) * sy;
    }

    static Label NewLabel(int size)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
        l.AddThemeConstantOverride("shadow_offset_x", 1);
        l.AddThemeConstantOverride("shadow_offset_y", 1);
        return l;
    }

    static Button AddButton(Container parent, string text, Action onPressed)
    {
        // 52 canvas px ≈ 48 dp or more on a 1080p-class phone (ARCHITECTURE §7.4).
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 52) };
        b.AddThemeFontSizeOverride("font_size", 20);
        b.Pressed += onPressed;
        parent.AddChild(b);
        return b;
    }

    void CycleCap()
    {
        _fpsCap = _fpsCap switch { 30 => 60, 60 => 0, _ => 30 };
        Engine.MaxFps = _fpsCap;
        DisplayServer.WindowSetVsyncMode(_fpsCap == 0 ? DisplayServer.VSyncMode.Disabled : DisplayServer.VSyncMode.Enabled);
        _capButton.Text = $"FPS cap: {(_fpsCap == 0 ? "off (vsync off)" : _fpsCap.ToString())}";
        _frames.Clear();
    }

    void ConnectEcho(string scheme, int port)
    {
        var host = _host.Text.Trim();
        var cfg = new ConfigFile();
        cfg.SetValue("net", "host", host);
        cfg.Save(ConfigPath);
        _echo.Connect($"{scheme}://{host}:{port}/echo");
    }

    void SwitchRenderer()
    {
        bool toCompat = RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility";
        if (toCompat)
        {
            var cfg = new ConfigFile();
            cfg.SetValue("rendering", "renderer/rendering_method", "gl_compatibility");
            cfg.SetValue("rendering", "renderer/rendering_method.mobile", "gl_compatibility");
            cfg.Save(OverridePath);
        }
        else
        {
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(OverridePath));
        }
        Log($"renderer -> {(toCompat ? "gl_compatibility" : "mobile")} on next launch; quitting");
        GetTree().Quit();
    }

    void CopyReport()
    {
        var report = $"T-005 report {Time.GetDatetimeStringFromSystem()}\n" +
                     $"{OS.GetName()} {OS.GetModelName()} | {OS.GetProcessorName()} x{OS.GetProcessorCount()}\n" +
                     $"{RenderingServer.GetCurrentRenderingMethod()} / {RenderingServer.GetCurrentRenderingDriverName()} | " +
                     $"{RenderingServer.GetVideoAdapterVendor()} {RenderingServer.GetVideoAdapterName()} {RenderingServer.GetVideoAdapterApiVersion()}\n" +
                     $"Window {DisplayServer.WindowGetSize()} safe {DisplayServer.GetDisplaySafeArea()}\n" +
                     StatsText() + "\n--- events ---\n" + string.Join("\n", _log);
        DisplayServer.ClipboardSet(report);
        GD.Print("[spike-report]\n" + report);
        Log("report copied to clipboard and logcat");
    }
}
