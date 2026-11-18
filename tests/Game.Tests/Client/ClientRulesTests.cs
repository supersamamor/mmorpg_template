using System.Text;
using System.Text.RegularExpressions;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Input;
using Game.Content.ClientExport;
using Game.Shared.ClientContent;
using Game.Tests.World;

namespace Game.Tests.Client;

/// <summary>The client's bundle handling, camera and input numbers, and the rules checked by reading its source (T-206; CLAUDE.md rules 1, 2, 3, 11).</summary>
public class ClientRulesTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Game.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static string[] Sources(string relative) =>
        Directory.GetFiles(Path.Combine(Root, relative), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".godot" + Path.DirectorySeparatorChar)).ToArray();

    // ---- bundle ----

    private static byte[] RealBundle => ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes;

    [Fact]
    public void The_real_bundle_reads()
    {
        var file = ClientBundleExporter.Export(TestGrids.SampleContent);
        DisplayBundle.TryRead(file.FileBytes, out var bundle, out var version).ShouldBe(BundleStatus.Ok);
        version.ShouldBe(ClientBundle.CurrentFormatVersion);
        bundle!.ContentHash.ShouldBe(file.Hash);
        bundle.NameOf("monster.gel_slime").ShouldBe(TestGrids.SampleContent.Monsters.Get("monster.gel_slime").Name);
        bundle.NameOf("item.gel_glob").ShouldNotBe("item.gel_glob");
        bundle.NameOf("job.wanderer").ShouldNotBe("job.wanderer");
        bundle.NameOf(null).ShouldBeEmpty();

        foreach (var (id, grid) in TestGrids.SampleContent.Grids)
        {
            bundle.TryGetMap(id, out var map).ShouldBeTrue();
            map!.Width.ShouldBe(grid.Width);
            map.Height.ShouldBe(grid.Height);
            for (var y = 0; y < grid.Height; y++)
                for (var x = 0; x < grid.Width; x++)
                {
                    map.IsWalkable(x, y).ShouldBe(grid.IsWalkable(x, y));
                    map.BlocksLos(x, y).ShouldBe(grid.BlocksLos(x, y));
                }
            map.Warps.Count.ShouldBe(grid.Warps.Count);
            map.BlocksLos(-1, 0).ShouldBeTrue();
            map.IsWalkable(grid.Width, 0).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(ClientBundle.CurrentFormatVersion + 1)]
    [InlineData(ClientBundle.CurrentFormatVersion - 1)]
    [InlineData(0)]
    public void A_bundle_of_another_format_is_refused(int version)
    {
        var text = Encoding.UTF8.GetString(RealBundle);
        var other = Regex.Replace(text, "\"format_version\":\\d+", "\"format_version\":" + version);
        DisplayBundle.TryRead(Encoding.UTF8.GetBytes(other), out var bundle, out var read).ShouldBe(BundleStatus.UnknownFormat);
        bundle.ShouldBeNull();
        read.ShouldBe(version);
        UiText.BundleRefused(read).ShouldContain(version.ToString());
    }

    [Fact]
    public void A_newer_format_is_refused_before_its_shape_is_read()
    {
        DisplayBundle.TryRead(Encoding.UTF8.GetBytes("{\"format_version\":99,\"anything\":[]}"), out _, out _).ShouldBe(BundleStatus.UnknownFormat);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"format_version\":\"4\"}")]
    [InlineData("{\"format_version\":5}")]
    [InlineData("{\"format_version\":5,\"content_hash\":\"a\",\"items\":[],\"skills\":[],\"jobs\":[],\"statuses\":[],\"monsters\":[],\"npcs\":[],\"quests\":[],\"maps\":[{\"id\":\"map.a\",\"name\":\"A\",\"width\":2,\"height\":2,\"cells\":\"AA==\",\"warps\":[],\"zones\":[]}]}")]
    public void A_broken_bundle_is_refused(string text)
    {
        DisplayBundle.TryRead(Encoding.UTF8.GetBytes(text), out var bundle, out _).ShouldBe(BundleStatus.Corrupt);
        bundle.ShouldBeNull();
    }

    // ---- camera and AOI ----

    private static readonly double[] PhoneAspects = { 16.0 / 9, 19.5 / 9 };

    private static ClientConfig ShippedConfig()
    {
        var config = new ClientConfig();
        config.ApplyJson(File.ReadAllBytes(Path.Combine(Root, "client", "client_settings.json")));
        return config;
    }

    [Fact]
    public void Nothing_can_pop_in_on_screen_at_any_zoom_on_a_phone()
    {
        var config = ShippedConfig();
        var aoi = TestGrids.SampleContent.Balance.Aoi;
        var alwaysSeen = aoi.ViewRadius * aoi.BucketSize;
        foreach (var aspect in PhoneAspects)
        {
            var farthest = CameraMath.Visible(config.CameraPitchDegrees, config.CameraFovDegrees, config.CameraDistanceMax, aspect);
            farthest.Reach.ShouldBeLessThanOrEqualTo(alwaysSeen, $"aspect {aspect:0.00}");
        }
    }

    [Fact]
    public void The_zoom_clamp_contains_the_default()
    {
        var config = ShippedConfig();
        config.CameraDistance.ShouldBeGreaterThanOrEqualTo(config.CameraDistanceMin);
        config.CameraDistance.ShouldBeLessThanOrEqualTo(config.CameraDistanceMax);
    }

    [Fact]
    public void The_camera_math_has_the_right_shape()
    {
        var view = CameraMath.Visible(50, 30, 13.5, 16.0 / 9);
        view.FarHalfWidth.ShouldBeGreaterThan(view.MiddleHalfWidth);
        view.MiddleHalfWidth.ShouldBeGreaterThan(view.NearHalfWidth);
        view.Far.ShouldBeGreaterThan(view.Near);

        // A square screen looking straight down sees the same distance every way.
        var down = CameraMath.Visible(90, 30, 10, 1);
        down.Far.ShouldBe(down.Near, 1e-9);
        down.Far.ShouldBe(down.MiddleHalfWidth, 1e-9);
        down.Far.ShouldBe(10 * Math.Tan(Math.PI / 12), 1e-9);
    }

    // ---- settings ----

    [Fact]
    public void Settings_come_from_the_file_then_the_command_line()
    {
        var config = new ClientConfig();
        config.ApplyJson("""{"server_url":"wss://a/ws","fps_cap":60,"unknown":{"x":[1]},"camera_distance":12.5}"""u8);
        config.ServerUrl.ShouldBe("wss://a/ws");
        config.FpsCap.ShouldBe(60);
        config.CameraDistance.ShouldBe(12.5);

        config.ApplyArgs(["--e2e", "--server", "ws://b/ws", "--name", "Ada", "--set", "fps_cap=0", "--set", "grace_seconds=5.5"]);
        config.ServerUrl.ShouldBe("ws://b/ws");
        config.DevName.ShouldBe("Ada");
        config.FpsCap.ShouldBe(0);
        config.GraceSeconds.ShouldBe(5.5);
    }

    [Fact]
    public void The_default_url_is_the_dev_loopback()
    {
        new ClientConfig().ServerUrl.ShouldStartWith("ws://127.0.0.1:");
        ShippedConfig().ServerUrl.ShouldStartWith("ws://127.0.0.1:");
    }

    // ---- gestures ----

    private static TapGestures Gestures() => new(new ClientConfig());

    [Fact]
    public void A_press_and_release_in_place_is_a_tap()
    {
        var gestures = Gestures();
        gestures.Press(0, 100, 200, 1);
        gestures.Release(0, 103, 202, 1.1).ShouldBe(GestureResult.Tap);
        gestures.TapX.ShouldBe(103);
        gestures.TapY.ShouldBe(202);
    }

    [Fact]
    public void A_drag_or_a_long_hold_is_not_a_tap()
    {
        var drag = Gestures();
        drag.Press(0, 100, 200, 1);
        drag.Drag(0, 200, 200);
        drag.Drag(0, 100, 200);
        drag.Release(0, 100, 200, 1.1).ShouldBe(GestureResult.None);

        var hold = Gestures();
        hold.Press(0, 100, 200, 1);
        hold.Release(0, 100, 200, 3).ShouldBe(GestureResult.LongPress);
    }

    [Fact]
    public void Two_fingers_pinch_and_never_tap()
    {
        var gestures = Gestures();
        gestures.Press(0, 100, 100, 1);
        gestures.Press(1, 200, 100, 1);
        gestures.Drag(1, 300, 100).ShouldBe(GestureResult.Zoom); // apart: from 100 to 200
        gestures.ZoomFactor.ShouldBe(0.5);
        gestures.Drag(1, 200, 100).ShouldBe(GestureResult.Zoom); // together
        gestures.ZoomFactor.ShouldBe(2);
        gestures.Release(1, 200, 100, 1.1).ShouldBe(GestureResult.None);
        gestures.Release(0, 100, 100, 1.2).ShouldBe(GestureResult.None);

        gestures.Press(0, 100, 100, 2);
        gestures.Release(0, 100, 100, 2.1).ShouldBe(GestureResult.Tap); // ready again
    }

    [Theory]
    [InlineData(48, 160, 720, 720, 24)]     // the base density
    [InlineData(48, 480, 1080, 720, 48)]    // a 3x phone: 144 px wide, on a canvas stretched from 1080 to 720 rows
    [InlineData(48, 0, 720, 720, 24)]       // no dpi reported
    public void A_touch_target_stays_its_size_in_dp(double dp, double dpi, double window, double canvas, double radius) =>
        TouchTargets.RadiusInCanvasUnits(dp, dpi, window, canvas).ShouldBe(radius, 1e-9);

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.99, 0)]
    [InlineData(1.0, 1)]
    [InlineData(-0.01, -1)]
    public void A_ground_point_belongs_to_the_cell_it_is_in(double world, int cell) => TouchTargets.CellOf(world).ShouldBe(cell);

    // ---- rules read from the source ----

    [Fact]
    public void Core_never_touches_Godot()
    {
        foreach (var file in Sources("client/Core"))
            Regex.IsMatch(File.ReadAllText(file), @"using Godot|\bGodot\.[A-Z]").ShouldBeFalse(Path.GetFileName(file));
    }

    [Fact]
    public void No_content_id_or_asset_path_is_written_in_client_code()
    {
        var contentId = new Regex("\"(item|monster|skill|job|map|npc|quest|drop|status|exp|dialogue|model|scene|sfx|music|icon|tex|mat|vfx)\\.[a-z0-9_.]+\"");
        var assetPath = new Regex("\\.(glb|gltf|ogg|tscn|png)\"");
        foreach (var file in Sources("client"))
        {
            var text = File.ReadAllText(file);
            contentId.IsMatch(text).ShouldBeFalse(Path.GetFileName(file) + ": " + contentId.Match(text).Value);
            assetPath.IsMatch(text).ShouldBeFalse(Path.GetFileName(file) + ": " + assetPath.Match(text).Value);
        }
    }

    [Fact]
    public void The_client_never_computes_what_the_server_decides()
    {
        // Rule 1: Core takes no combat, drop, EXP or path code from Game.Shared; it only references the protocol, grid types and vocabulary.
        var forbidden = new[] { "Game.Shared.Combat", "Game.Shared.Formulas", "Game.Shared.Progression", "Game.Shared.Stats", "Pathfinder", "GridMover", "LineOfSight" };
        foreach (var file in Sources("client"))
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden) text.ShouldNotContain(word, Case.Sensitive, Path.GetFileName(file));
        }
    }

    [Fact]
    public void The_resume_token_never_reaches_a_disk_a_log_or_a_settings_file()
    {
        // In code: the machine holds it, and the session hands it to hello. Nothing else names it.
        var token = new Regex("\\bResumeToken\\b|\\b_token\\b");
        var holders = Sources("client").Where(f => token.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        // ByeRuns.cs (scripted runs only) hands it to a second test connection for the `replaced` run; it must never print it.
        holders.ShouldBe(["ByeRuns.cs", "ClientSession.cs", "ConnectionMachine.cs"]);
        var byeRuns = File.ReadAllText(Sources("client/Scene").Single(f => Path.GetFileName(f) == "ByeRuns.cs"));
        byeRuns.ShouldNotContain("{token", Case.Sensitive);
        byeRuns.ShouldNotContain("{Session.Connection.ResumeToken", Case.Sensitive);

        // Those two write nothing anywhere: no file API and no printing.
        var writer = new Regex("\\b(File|FileAccess|ConfigFile|DirAccess|StreamWriter|Console|GD|OS|ProjectSettings|DisplayServer)\\.");
        foreach (var file in Sources("client/Core"))
        {
            var match = writer.Match(File.ReadAllText(file));
            match.Success.ShouldBeFalse(Path.GetFileName(file) + ": " + match.Value);
        }

        // And the engine layer only opens files to read them.
        var fileWrites = new Regex("FileAccess\\.ModeFlags|ConfigFile|DirAccess|File\\.Write|StreamWriter");
        foreach (var file in Sources("client/Scene"))
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "UserPrefsStore.cs")
            {
                // The one exception (T-211): the player's UI-size choice, in the engine's per-user folder. It handles UserPreferences and nothing else.
                text.ShouldNotContain("Session", Case.Sensitive, "UserPrefsStore.cs");
                text.ShouldNotContain("oken", Case.Sensitive, "UserPrefsStore.cs");
                continue;
            }
            var match = fileWrites.Match(text);
            match.Success.ShouldBeFalse(Path.GetFileName(file) + ": " + match.Value);
        }
    }

    [Fact]
    public void The_client_is_not_in_the_solution()
    {
        File.ReadAllText(Path.Combine(Root, "Game.sln")).ShouldNotContain("GameClient");
    }

    [Fact]
    public void The_committed_reference_bundle_is_current()
    {
        var reference = File.ReadAllBytes(Path.Combine(Root, "content", "client-content.reference.json"));
        DisplayBundle.TryRead(reference, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        bundle!.ContentHash.ShouldBe(ClientBundleExporter.Hash(TestGrids.SampleContent));
    }
}
