using System.Diagnostics;
using System.Globalization;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted runs (docs/MOBILE_BUILD.md): <c>--e2e</c> plays login → map → walk → kill → drop → dropped socket → resume → warp → logout against a real
/// server, tapping through the same input path as a finger; <c>--e2e-death</c> gets killed, drops the socket while dead and checks the death screen
/// comes back; <c>--measure N</c> reports fps, draw calls, triangles and allocations with N entities on screen. Which monster to hunt comes from the
/// command line, never from code. Nothing here is needed to play, and none of it can do more than a player's intents.
/// </summary>
public sealed class AutoRun
{
    private readonly Main _main;
    private readonly string[] _args;
    private readonly string? _monster;
    private readonly string? _screenshot;

    /// <summary>Prepares a run.</summary>
    public AutoRun(Main main, string[] args)
    {
        _main = main;
        _args = args;
        _monster = Value("--e2e-monster");
        _screenshot = Value("--screenshot");
    }

    private ClientSession Session => _main.Session!;

    private WorldView World => Session.World;

    /// <summary>True if the command line asks for a scripted run.</summary>
    public static bool Wanted(string[] args) =>
        Array.IndexOf(args, "--e2e") >= 0 || Array.IndexOf(args, "--e2e-death") >= 0 || Array.IndexOf(args, "--measure") >= 0;

    /// <summary>For a refused bundle: save a picture of the refusal screen if asked, then quit.</summary>
    public static async void QuitAfterShot(Main main, string[] args)
    {
        for (var i = 0; i < 10; i++) await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
        var at = Array.IndexOf(args, "--screenshot");
        if (at >= 0 && at + 1 < args.Length && DisplayServer.GetName() != "headless")
        {
            OS.LowProcessorUsageMode = false;
            await main.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            main.GetViewport().GetTexture().GetImage().SavePng(args[at + 1]);
        }
        Print("refused: the client stopped at the bundle screen, as it must");
        main.GetTree().Quit(0);
    }

    /// <summary>Runs the script and quits with 0 on success, 1 on failure.</summary>
    public async Task RunAsync()
    {
        var code = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            if (Array.IndexOf(_args, "--measure") >= 0) await MeasureAsync();
            else if (Array.IndexOf(_args, "--e2e-death") >= 0) await DeathRunAsync();
            else await PlayRunAsync();
            Print($"PASS in {clock.Elapsed.TotalSeconds:0.0} s");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Print($"FAIL after {clock.Elapsed.TotalSeconds:0.0} s: {e.Message}");
            code = 1;
        }
        _main.GetTree().Quit(code);
    }

    // ---- the runs ----

    private async Task PlayRunAsync()
    {
        await LoginAsync();

        // Walk: a tap on the ground, past the safe zone, because combat is refused inside it.
        var map = CurrentMap();
        var start = Own().Cell;
        var cell = NearestOpenCellOutsideSafeZones(map, start);
        Print($"walk: tap ground at cell {cell}, from {start}");
        await TapCellAsync(cell);
        Check(Session.Ui.Marker is { } m && m.X == cell.X && m.Y == cell.Y, "the tap marker shows at once");
        await UntilAsync(() => Own().Cell.X == cell.X && Own().Cell.Y == cell.Y && !Own().IsWalking, 30, "walking to the tapped cell");
        Print($"walk: arrived at {Own().Cell}; drawn at ({Own().X:0.00}, {Own().Z:0.00})");

        await RealMouseTapAsync();

        // Hunt until something drops.
        var kills = 0;
        EntityView? drop = null;
        var deaths = 0;
        for (var attempt = 0; attempt < 40 && drop is null; attempt++)
        {
            if (await HuntOneAsync(90)) kills++;
            if (Session.Ui.Dead)
            {
                deaths++;
                Print($"hunt: died ({deaths}), screen \"{_main.Hud!.PanelText}\"; pressing respawn");
                await FramesAsync(2);
                _main.Hud!.PressPanelButton();
                await UntilAsync(() => !Session.Ui.Dead, 10, "respawn");
                await SecondsAsync(1);
                Print($"hunt: respawned at {Own().Cell}, HP {World.Vitals?.Hp}");
                var open = NearestOpenCellOutsideSafeZones(CurrentMap(), Own().Cell);
                await TapCellAsync(open);
                await UntilAsync(() => !Own().IsWalking && Own().Cell.X == open.X && Own().Cell.Y == open.Y, 30, "leaving the safe zone again");
                continue;
            }
            drop = FindDrop();
            if (drop is null) await SecondsAsync(1);
            drop ??= FindDrop();
        }
        Check(kills > 0, "killed a monster");
        Check(drop is not null, "saw a drop");
        Print($"drop: {_main.Bundle!.NameOf(drop!.DisplayKey)} x{drop.Quantity} at {drop.Cell}, mine={drop.Mine}, after {kills} kill(s); exp {Exp()}");
        await ShotAsync();

        await ResumeAfterKillAsync();

        // A foreground return with a live socket: a ping proves it and nothing else happens.
        Session.OnBackgrounded();
        await SecondsAsync(1.5);
        Session.OnForegrounded(Clocks.Wall());
        await SecondsAsync(1.5);
        Check(Session.Connection.Phase == ConnectionPhase.Online, "still online after background and foreground");
        Print("background: 1.5 s in the background, socket kept, still online");

        await WarpAsync();

        Print("logout: sending logout");
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        Check(Session.Connection.Notice == ConnectionNotice.LoggedOut, "the bye was logout");
        Check(!Session.Connection.HasToken, "the token is gone");
        Print($"logout: bye logout, login screen shows \"{_main.Hud!.PanelText}\"");
    }

    private async Task DeathRunAsync()
    {
        await LoginAsync();
        var cell = NearestOpenCellOutsideSafeZones(CurrentMap(), Own().Cell);
        await TapCellAsync(cell);
        await UntilAsync(() => Own().Cell.X == cell.X && Own().Cell.Y == cell.Y && !Own().IsWalking, 30, "leaving the safe zone");

        // Fight until the character is dead.
        var clock = Stopwatch.StartNew();
        while (!Session.Ui.Dead)
        {
            Check(clock.Elapsed.TotalSeconds < 300, "died within 5 minutes");
            await HuntOneAsync(30);
        }
        Print($"death: you_died, panel \"{_main.Hud!.PanelText}\", respawn button {_main.Hud.PanelButtonShown}; own HP {World.Vitals?.Hp}");
        await FrameAsync();
        Check(_main.Hud.PanelButtonShown && _main.Hud.PanelText == UiText.YouDied, "the death screen shows");

        // Row 102: drop the socket while dead; the snapshot after the resume alone must bring the screen back.
        await ResumeAfterKillAsync();
        await FramesAsync(3);
        Check(Session.Ui.Dead, "the snapshot after the resume shows the character dead");
        Check(_main.Hud.PanelButtonShown && _main.Hud.PanelText == UiText.YouDied, "the death screen is back after the resume");
        Print($"death: after the resume the death screen is up; own HP {World.Vitals?.Hp}");

        _main.Hud.PressPanelButton();
        await UntilAsync(() => !Session.Ui.Dead && World.Vitals is { Hp: > 0 }, 10, "respawn");
        await FramesAsync(3);
        Check(_main.Hud.PanelText.Length == 0, "the death screen left");
        Print($"death: respawned at {Own().Cell} with HP {World.Vitals?.Hp}");

        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        Print("logout: bye logout");
    }

    private async Task MeasureAsync()
    {
        var wanted = int.Parse(Value("--measure") ?? "40", CultureInfo.InvariantCulture);
        await LoginAsync();
        ReportCamera();
        Print($"measure: waiting for {wanted} entities on screen");
        await UntilAsync(() => CountOnScreen() >= wanted, 120, $"{wanted} entities on screen");
        OS.LowProcessorUsageMode = false; // measure every frame, not only frames that drew
        await SecondsAsync(5); // shaders settle

        _main.CountAllocations = true;
        var frames = 0;
        double worstMs = 0, maxTriangles = 0, maxDraws = 0, sumDraws = 0, sumTriangles = 0, sumOnScreen = 0;
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalMilliseconds;
        while (clock.Elapsed.TotalSeconds < 15)
        {
            await FrameAsync();
            var now = clock.Elapsed.TotalMilliseconds;
            worstMs = Math.Max(worstMs, now - last);
            last = now;
            var draws = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            var triangles = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
            maxDraws = Math.Max(maxDraws, draws);
            maxTriangles = Math.Max(maxTriangles, triangles);
            sumDraws += draws;
            sumTriangles += triangles;
            sumOnScreen += CountOnScreen();
            frames++;
        }
        _main.CountAllocations = false;
        var seconds = clock.Elapsed.TotalSeconds;
        var (quietBytes, quietFrames, busyBytes, busyFrames) = _main.Allocations;
        Print($"measure: cap {_main.Config.FpsCap}, {frames} frames in {seconds:0.0} s = {frames / seconds:0.0} fps, worst {worstMs:0.0} ms");
        Print($"measure: entities on screen avg {sumOnScreen / frames:0.0}; in view {World.Entities.Count}");
        Print($"measure: draw calls avg {sumDraws / frames:0.0}, max {maxDraws:0}; triangles avg {sumTriangles / frames:0}, max {maxTriangles:0}; ground {_main.Map!.Triangles}");
        Print($"measure: frame path allocated {quietBytes} bytes in {quietFrames} frames with no message; {busyBytes} bytes in {busyFrames} frames with messages");
        Print($"measure: static memory {OS.GetStaticMemoryUsage() / (1024 * 1024)} MB; video {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024 * 1024):0} MB; adapter {RenderingServer.GetVideoAdapterName()}");
        await ShotAsync();

        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
    }

    // ---- steps ----

    private async Task LoginAsync()
    {
        Print($"login: {_main.Config.ServerUrl}, name {_main.Config.DevName}; bundle format {_main.Bundle!.FormatVersion}");
        await FrameAsync();
        Check(Session.Connection.Phase == ConnectionPhase.LoginScreen, "starts on the login screen");
        Session.Login(_main.Config.DevName, Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online, 15, "welcome");
        await UntilAsync(() => World.Own is not null && _main.LoadedMap == World.MapId, 15, "first snapshot and map");
        var map = CurrentMap();
        Print($"login: welcome id {World.OwnId}, map {map.Id} \"{map.Name}\" {map.Width}x{map.Height}, render delay {World.RenderDelay * 1000:0} ms, {World.Entities.Count} entities, HP {World.Vitals?.Hp}/{World.Vitals?.MaxHp}, {Exp()}");
        Print($"map: ground built, {_main.Map!.Triangles} triangles");
        ReportCamera();
    }

    /// <summary>Taps the nearest wanted monster until it dies or the time runs out. True if it died by this client's attack.</summary>
    private async Task<bool> HuntOneAsync(double seconds)
    {
        var clock = Stopwatch.StartNew();
        long? target = null;
        while (clock.Elapsed.TotalSeconds < seconds && !Session.Ui.Dead)
        {
            var monster = NearestMonster();
            if (monster is null)
            {
                await WalkTowardMapMiddleAsync();
                continue;
            }
            if (target != monster.Id)
            {
                target = monster.Id;
                Print($"hunt: {_main.Bundle!.NameOf(monster.DisplayKey)} #{monster.Id} at {monster.Cell}, own {Own().Cell}, HP {World.Vitals?.Hp}");
            }

            if (!OnScreen(monster))
            {
                await TapCellAsync(StepToward(monster.Cell));
                await SecondsAsync(1);
                continue;
            }

            var id = monster.Id;
            await TapWorldAsync(new Vector3((float)monster.X, 0.45f, (float)monster.Z));
            await FrameAsync();
            if (Session.Ui.Target != id)
            {
                await SecondsAsync(0.5);
                continue;
            }

            var fight = Stopwatch.StartNew();
            while (Session.Ui.Target == id && !Session.Ui.Dead && fight.Elapsed.TotalSeconds < 30) await FrameAsync();
            if (World.Find(id) is { Dead: true })
            {
                Print($"hunt: #{id} is dead; flash \"{Session.Ui.Flash}\"; {Exp()}");
                return true;
            }
            await SecondsAsync(0.5);
        }
        return false;
    }

    private async Task ResumeAfterKillAsync()
    {
        var id = World.OwnId;
        var messages = Session.MessagesApplied;
        var clock = Stopwatch.StartNew();
        Print("resume: killing the socket (no close frame)");
        _main.Socket!.Kill();
        await UntilAsync(() => Session.Connection.Phase != ConnectionPhase.Online, 5, "noticing the drop");
        Print($"resume: {Session.Connection.Phase}, \"{UiText.Notice(Session.Connection.Notice)}\", token held {Session.Connection.HasToken}");
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online, 20, "the resume");
        await UntilAsync(() => Session.MessagesApplied > messages + 1 && World.Own is not null, 5, "the snapshot");
        Check(World.OwnId == id, "the same character came back");
        Print($"resume: welcome after {clock.Elapsed.TotalSeconds:0.00} s, same id {id}, new token held, {World.Entities.Count} entities, own at {Own().Cell}");
    }

    private async Task WarpAsync()
    {
        var map = CurrentMap();
        if (map.Warps.Count == 0)
        {
            Print("warp: this map has none, skipped");
            return;
        }
        var own = Own().Cell;
        var warp = map.Warps[0];
        foreach (var candidate in map.Warps)
            if (GridDistanceSquared(candidate, own) < GridDistanceSquared(warp, own)) warp = candidate;
        Print($"warp: walking from {own} to the warp at {warp} on {map.Id}");
        var clock = Stopwatch.StartNew();
        var triangles = _main.Map!.Triangles;
        while (World.MapId == map.Id)
        {
            Check(clock.Elapsed.TotalSeconds < 120, "reached the warp in 2 minutes");
            if (Session.Ui.Dead)
            {
                Print("warp: died on the way, respawning");
                _main.Hud!.PressPanelButton();
                await SecondsAsync(1);
            }
            if (!Own().IsWalking)
            {
                Session.TapGround(warp, Clocks.View()); // may be off screen, so it is the intent a far tap would make
                await SecondsAsync(1);
            }
            await FrameAsync();
        }
        await UntilAsync(() => _main.LoadedMap == World.MapId && World.Own is not null, 10, "the new map");
        var next = CurrentMap();
        Check(next.Id != map.Id, "the map changed");
        Print($"warp: now on {next.Id} \"{next.Name}\" {next.Width}x{next.Height}, ground rebuilt ({triangles} -> {_main.Map.Triangles} triangles), {World.Entities.Count} entities, own at {Own().Cell}");
    }

    private async Task WalkTowardMapMiddleAsync()
    {
        var map = CurrentMap();
        await TapCellAsync(StepToward(new GridCell(map.Width / 2, map.Height / 2)));
        await SecondsAsync(1.5);
    }

    /// <summary>A cell on the way to <paramref name="target"/> that is surely on screen.</summary>
    private GridCell StepToward(GridCell target)
    {
        var map = CurrentMap();
        var own = Own().Cell;
        double dx = target.X - own.X, dy = target.Y - own.Y;
        var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        for (var reach = Math.Min(4, length); reach >= 1; reach--)
        {
            var cell = new GridCell(own.X + (int)Math.Round(dx / length * reach), own.Y + (int)Math.Round(dy / length * reach));
            if (map.IsWalkable(cell.X, cell.Y)) return cell;
        }
        return own;
    }

    // ---- tapping: through Main.HandleInput, exactly as a finger ----

    private Task TapCellAsync(GridCell cell) => TapWorldAsync(new Vector3(cell.X + 0.5f, 0, cell.Y + 0.5f));

    private async Task TapWorldAsync(Vector3 world)
    {
        var position = _main.Camera!.Camera.UnprojectPosition(world);
        _main.HandleInput(new InputEventScreenTouch { Index = 0, Position = position, Pressed = true });
        await FrameAsync();
        _main.HandleInput(new InputEventScreenTouch { Index = 0, Position = position, Pressed = false });
    }

    /// <summary>
    /// With a window: a real mouse click through the engine's own input dispatch, which proves the whole path (mouse to emulated touch, past the HUD, window
    /// pixels to canvas units under stretching) lands on the cell under the pointer. Headless has no dispatch.
    /// </summary>
    private async Task RealMouseTapAsync()
    {
        if (DisplayServer.GetName() == "headless") return;
        var own = Own().Cell;
        var cell = StepToward(new GridCell(own.X + 3, own.Y));
        var canvas = _main.Camera!.Camera.UnprojectPosition(new Vector3(cell.X + 0.5f, 0, cell.Y + 0.5f));
        var rect = _main.GetViewport().GetVisibleRect().Size;
        var window = (Vector2)_main.GetWindow().Size;
        var pixel = canvas * window / rect;
        foreach (var pressed in new[] { true, false })
        {
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = pixel, GlobalPosition = pixel });
            Input.FlushBufferedEvents();
            await FramesAsync(2);
        }
        Check(Session.Ui.Marker is { } m && m.X == cell.X && m.Y == cell.Y, $"a real mouse click at {pixel} lands on cell {cell}");
        Print($"input: mouse click at window pixel {pixel} ({window.X:0}x{window.Y:0} window, {rect.X:0}x{rect.Y:0} canvas) -> move to {cell}");
        await UntilAsync(() => Own().Cell.X == cell.X && Own().Cell.Y == cell.Y && !Own().IsWalking, 30, "walking to the clicked cell");
    }

    // ---- looking ----

    private EntityView Own() => World.Own ?? throw new InvalidOperationException("own character not in view");

    private MapData CurrentMap() => _main.Bundle!.TryGetMap(World.MapId ?? string.Empty, out var map) ? map! : throw new InvalidOperationException("map not in the bundle");

    private EntityView? NearestMonster()
    {
        var own = Own().Cell;
        EntityView? best = null;
        var entities = World.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e.Kind != EntityKinds.Monster || e.Dead) continue;
            if (_monster is not null && e.DisplayKey != _monster) continue;
            if (best is null || GridDistanceSquared(e.Cell, own) < GridDistanceSquared(best.Cell, own)) best = e;
        }
        return best;
    }

    private EntityView? FindDrop()
    {
        var entities = World.Entities;
        for (var i = 0; i < entities.Count; i++)
            if (entities[i].Kind == EntityKinds.Item) return entities[i];
        return null;
    }

    private bool OnScreen(EntityView entity)
    {
        var camera = _main.Camera!.Camera;
        var world = new Vector3((float)entity.X, 0.45f, (float)entity.Z);
        return !camera.IsPositionBehind(world) && _main.GetViewport().GetVisibleRect().HasPoint(camera.UnprojectPosition(world));
    }

    private int CountOnScreen()
    {
        var count = 0;
        var entities = World.Entities;
        for (var i = 0; i < entities.Count; i++)
            if (OnScreen(entities[i])) count++;
        return count;
    }

    private static int GridDistanceSquared(GridCell a, GridCell b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private static GridCell NearestOpenCellOutsideSafeZones(MapData map, GridCell from)
    {
        bool Safe(int x, int y)
        {
            foreach (var zone in map.Zones)
                if (zone.Kind == ZoneKinds.Safe && x >= zone.Area.X && y >= zone.Area.Y && x < zone.Area.X + zone.Area.W && y < zone.Area.Y + zone.Area.H) return true;
            return false;
        }

        for (var radius = 0; radius < Math.Max(map.Width, map.Height); radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                    if (map.IsWalkable(from.X + dx, from.Y + dy) && !Safe(from.X + dx, from.Y + dy)) return new GridCell(from.X + dx, from.Y + dy);
                }
        return from;
    }

    private string Exp() => World.Exp is { } exp ? UiText.Exp(exp.BaseLevel, exp.BaseExp, exp.BaseNext) : "no exp yet";

    /// <summary>Visible ground measured with the engine's own camera rays, at the current window size, to set against <see cref="CameraMath"/> and the AOI.</summary>
    private void ReportCamera()
    {
        var camera = _main.Camera!;
        var rect = _main.GetViewport().GetVisibleRect().Size;
        var own = new Vector2(camera.Camera.Position.X, camera.Camera.Position.Z - camera.Distance * Mathf.Cos(Mathf.DegToRad((float)_main.Config.CameraPitchDegrees)));
        Vector3 Hit(float u, float v) => camera.TryGround(new Vector2(rect.X * u, rect.Y * v), out var g) ? g : default;
        var topLeft = Hit(0, 0);
        var bottomLeft = Hit(0, 1);
        var midLeft = Hit(0, 0.5f);
        var top = Hit(0.5f, 0);
        var bottom = Hit(0.5f, 1);
        var center = Hit(0.5f, 0.5f);
        var math = CameraMath.Visible(_main.Config.CameraPitchDegrees, _main.Config.CameraFovDegrees, camera.Distance, rect.X / rect.Y);
        Print($"camera: {rect.X:0}x{rect.Y:0} aspect {rect.X / rect.Y:0.00}, distance {camera.Distance:0.0}, looks at ({center.X - own.X:0.00}, {center.Z - own.Y:0.00}) from the player");
        Print($"camera: rays: top edge {own.X - topLeft.X:0.0} cells either side and {own.Y - top.Z:0.0} ahead; through the player {own.X - midLeft.X:0.0}; bottom edge {own.X - bottomLeft.X:0.0} either side and {bottom.Z - own.Y:0.0} behind");
        Print($"camera: math: top {math.FarHalfWidth:0.0}, middle {math.MiddleHalfWidth:0.0}, bottom {math.NearHalfWidth:0.0}, ahead {math.Far:0.0}, behind {math.Near:0.0}; reach {math.Reach:0.0} cells");
    }

    private async Task ShotAsync()
    {
        if (_screenshot is null) return;
        if (DisplayServer.GetName() == "headless")
        {
            Print("screenshot: headless, skipped");
            return;
        }
        OS.LowProcessorUsageMode = false;
        await _main.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        await _main.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        var error = _main.GetViewport().GetTexture().GetImage().SavePng(_screenshot);
        Print($"screenshot: {_screenshot} ({error})");
    }

    // ---- waiting ----

    private Task FrameAsync() => AwaitFrame();

    private async Task AwaitFrame() => await _main.ToSignal(_main.GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task FramesAsync(int count)
    {
        for (var i = 0; i < count; i++) await FrameAsync();
    }

    private async Task SecondsAsync(double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds) await FrameAsync();
    }

    private async Task UntilAsync(Func<bool> condition, double seconds, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}, notice {Session.Connection.Notice}");
            await FrameAsync();
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);

    private string? Value(string name)
    {
        var at = Array.IndexOf(_args, name);
        return at >= 0 && at + 1 < _args.Length ? _args[at + 1] : null;
    }
}
