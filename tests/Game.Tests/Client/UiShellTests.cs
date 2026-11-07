using System.Text.RegularExpressions;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Input;
using Game.Client.Core.Ui;
using Game.Client.Core.World;
using Game.Content.ClientExport;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Client;

/// <summary>The mobile UI shell's rules: touch targets, UI scale, safe area, hotbar, gestures and the info panel's words (T-211, ARCHITECTURE §7.3–7.4).</summary>
public class UiShellTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Game.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static ClientConfig Shipped()
    {
        var config = new ClientConfig();
        config.ApplyJson(File.ReadAllBytes(Path.Combine(Root, "client", "client_settings.json")));
        return config;
    }

    // ---- touch targets ----

    [Theory]
    [InlineData(UiPlatform.Android, 48)]
    [InlineData(UiPlatform.Ios, 44)]
    [InlineData(UiPlatform.Desktop, 48)]
    public void The_smallest_touch_target_depends_on_the_platform(UiPlatform platform, double minimum) => TouchTargetRules.MinTarget(platform).ShouldBe(minimum);

    [Theory]
    [InlineData("Android", UiPlatform.Android)]
    [InlineData("iOS", UiPlatform.Ios)]
    [InlineData("Windows", UiPlatform.Desktop)]
    [InlineData("Linux", UiPlatform.Desktop)]
    [InlineData("", UiPlatform.Desktop)]
    public void The_platform_comes_from_the_os_name(string name, UiPlatform platform) => TouchTargetRules.PlatformOf(name).ShouldBe(platform);

    [Theory]
    [InlineData(UiPlatform.Android, 420, 1080, 720, 1.75)]    // 2.625 pixels per dp, shown on a 720-row canvas from 1080 rows: 0.667 units per pixel
    [InlineData(UiPlatform.Android, 160, 720, 720, 1)]
    [InlineData(UiPlatform.Android, 0, 720, 720, 1)]          // no density reported: the base
    [InlineData(UiPlatform.Ios, 326, 1080, 720, 1.3333333333)] // 326 ppi / 163 = 2 pixels per pt
    [InlineData(UiPlatform.Desktop, 96, 720, 720, 1)]         // a desktop monitor never counts below the base density
    [InlineData(UiPlatform.Desktop, 192, 1440, 720, 0.6)]
    [InlineData(UiPlatform.Android, 420, 0, 0, 2.625)]        // sizes not known yet: no stretch
    public void A_dp_is_worked_out_from_density_and_stretch(UiPlatform platform, double dpi, double windowRows, double canvasRows, double expected) =>
        TouchTargetRules.UnitsPerDp(platform, dpi, windowRows, canvasRows).ShouldBe(expected, 1e-6);

    [Theory]
    [InlineData(UiPlatform.Android, 0.75)]
    [InlineData(UiPlatform.Android, 1)]
    [InlineData(UiPlatform.Android, 1.5)]
    [InlineData(UiPlatform.Ios, 0.75)]
    [InlineData(UiPlatform.Ios, 1.5)]
    public void A_button_is_never_smaller_than_the_minimum_at_any_ui_scale(UiPlatform platform, double scale)
    {
        // A 48 dp button asked for at scale 0.75 would be 36 dp: it stays at the platform's minimum instead.
        TouchTargetRules.Meets(platform, TouchTargetRules.TargetDp(platform, 48, scale)).ShouldBeTrue();
        TouchTargetRules.Meets(platform, TouchTargetRules.TargetDp(platform, 20, scale)).ShouldBeTrue();
        TouchTargetRules.Meets(platform, 47.9).ShouldBe(platform == UiPlatform.Ios);
        TouchTargetRules.Meets(platform, 43.9).ShouldBeFalse();
    }

    [Fact]
    public void Picking_is_at_least_as_generous_as_the_ui_targets()
    {
        foreach (var platform in new[] { UiPlatform.Android, UiPlatform.Ios, UiPlatform.Desktop })
        {
            // Half a minimum target, even when the configured size is smaller, or zero.
            TouchTargetRules.PickRadius(platform, 0, 1.75).ShouldBe(TouchTargetRules.MinTarget(platform) * 1.75 / 2, 1e-9);
            TouchTargetRules.PickRadius(platform, 30, 2).ShouldBe(TouchTargetRules.MinTarget(platform));
            TouchTargetRules.PickRadius(platform, 96, 1).ShouldBe(48, 1e-9);
        }
        // The shipped size is the Android minimum, so picking was never made less generous.
        Shipped().TouchTargetDp.ShouldBeGreaterThanOrEqualTo(TouchTargetRules.AndroidMinDp);
    }

    // ---- UI scale ----

    [Theory]
    [InlineData(1, 0.75, 1.5, 1)]
    [InlineData(0.1, 0.75, 1.5, 0.75)]
    [InlineData(9, 0.75, 1.5, 1.5)]
    [InlineData(double.NaN, 0.75, 1.5, 1)]
    [InlineData(double.PositiveInfinity, 0.75, 1.5, 1)]
    [InlineData(2, 1.5, 0.75, 1.5)]   // a range given the wrong way round is swapped
    public void The_ui_scale_is_clamped_to_the_configured_range(double value, double min, double max, double expected) =>
        UiScale.Clamp(value, min, max, 1).ShouldBe(expected);

    [Fact]
    public void The_size_button_cycles_through_the_steps_and_wraps()
    {
        UiScale.StepCount(0.75, 1.5, 0.25).ShouldBe(4);
        UiScale.Next(0.75, 0.75, 1.5, 0.25).ShouldBe(1);
        UiScale.Next(1, 0.75, 1.5, 0.25).ShouldBe(1.25);
        UiScale.Next(1.25, 0.75, 1.5, 0.25).ShouldBe(1.5);
        UiScale.Next(1.5, 0.75, 1.5, 0.25).ShouldBe(0.75);
        UiScale.Next(1.1, 0.75, 1.5, 0.25).ShouldBe(1.25);  // between steps
        UiScale.Next(3, 0.75, 1.5, 0.25).ShouldBe(0.75);
        UiScale.Next(1, 1, 1, 0.25).ShouldBe(1);            // a single step
        UiScale.Next(1, 0.75, 1.5, 0).ShouldBe(0.75);       // a step of nothing cannot walk
    }

    [Fact]
    public void The_stored_preference_is_clamped_and_never_throws()
    {
        var config = new ClientConfig();
        UserPreferences.Parse("{\"ui_scale\":1.25}"u8).EffectiveUiScale(config).ShouldBe(1.25);
        UserPreferences.Parse("{\"ui_scale\":7}"u8).EffectiveUiScale(config).ShouldBe(config.UiScaleMax);
        UserPreferences.Parse("{\"ui_scale\":-2}"u8).EffectiveUiScale(config).ShouldBe(config.UiScaleDefault);
        foreach (var damaged in new[] { "", "not json", "[]", "{", "{\"ui_scale\":\"big\"}", "{\"ui_scale\":", "{\"other\":{\"a\":[1]},\"ui_scale\":1.5}" })
        {
            var result = UserPreferences.Parse(System.Text.Encoding.UTF8.GetBytes(damaged));
            result.EffectiveUiScale(config).ShouldBeInRange(config.UiScaleMin, config.UiScaleMax, damaged);
        }
        UserPreferences.Parse("{\"other\":{\"a\":[1]},\"ui_scale\":1.5}"u8).UiScale.ShouldBe(1.5);

        var saved = new UserPreferences { UiScale = 1.25 };
        UserPreferences.Parse(saved.ToJson()).UiScale.ShouldBe(1.25);
    }

    [Fact]
    public void The_shipped_ui_scale_range_has_a_default_inside_it_and_steps_that_land_on_the_ends()
    {
        var config = Shipped();
        config.UiScaleDefault.ShouldBeInRange(config.UiScaleMin, config.UiScaleMax);
        var last = config.UiScaleMin + (UiScale.StepCount(config.UiScaleMin, config.UiScaleMax, config.UiScaleStep) - 1) * config.UiScaleStep;
        last.ShouldBe(config.UiScaleMax, 1e-9);
    }

    // ---- safe area ----

    [Fact]
    public void Insets_come_from_the_safe_rectangle_the_os_reports()
    {
        // A 2340x1080 landscape phone with a camera cut-out on the left, rounded corners on the right and a home indicator.
        var insets = SafeAreaMath.FromRects(2340, 1080, 132, 0, 2184, 1050);
        insets.Left.ShouldBe(132);
        insets.Top.ShouldBe(0);
        insets.Right.ShouldBe(24);
        insets.Bottom.ShouldBe(30);

        SafeAreaMath.FromRects(2340, 1080, 0, 0, 0, 0).Same(default).ShouldBeTrue();  // nothing reported
        SafeAreaMath.FromRects(2340, 1080, 0, 0, 2340, 1080).Same(default).ShouldBeTrue();
        SafeAreaMath.FromRects(100, 100, -5, -5, 120, 120).Same(default).ShouldBeTrue(); // a rectangle larger than the screen gives no negative inset
    }

    [Fact]
    public void The_safe_rectangle_follows_the_insets_and_rotation()
    {
        var left = new Insets(60, 0, 20, 20);
        var rect = SafeAreaMath.SafeRect(1560, 720, left);
        (rect.X, rect.Y, rect.Width, rect.Height).ShouldBe((60.0, 0.0, 1480.0, 700.0));

        // Turned the other way up, the cut-out is on the right: the rectangle moves with it.
        var turned = SafeAreaMath.SafeRect(1560, 720, new Insets(20, 0, 60, 20));
        (turned.X, turned.Width).ShouldBe((20.0, 1480.0));

        // Insets that leave nothing are a corrupt report: the whole canvas is used.
        SafeAreaMath.SafeRect(1560, 720, new Insets(900, 0, 900, 0)).ShouldBe((0.0, 0.0, 1560.0, 720.0));
        SafeAreaMath.SafeRect(1560, 720, default).ShouldBe((0.0, 0.0, 1560.0, 720.0));
    }

    [Fact]
    public void Two_obscured_areas_count_as_the_larger_on_each_side()
    {
        var both = new Insets(10, 0, 30, 5).Max(new Insets(48, 3, 24, 0));
        (both.Left, both.Top, both.Right, both.Bottom).ShouldBe((48.0, 3.0, 30.0, 5.0));
        new Insets(-4, 0, 0, 0).Left.ShouldBe(0);
    }

    [Fact]
    public void The_simulated_notch_is_in_dp_and_off_by_default()
    {
        var config = new ClientConfig();
        SafeAreaMath.DebugNotch(config, 2).Same(default).ShouldBeTrue();
        config.ApplyArgs(["--set", "debug_notch=1"]);
        var notch = SafeAreaMath.DebugNotch(config, 2);
        (notch.Left, notch.Top, notch.Right, notch.Bottom).ShouldBe((96.0, 0.0, 48.0, 42.0));

        var metrics = new ScreenMetrics(1560, 720, 2, UiPlatform.Android, notch);
        metrics.Safe.Width.ShouldBe(1560 - 96 - 48);
        metrics.SafeWidthDp.ShouldBe((1560 - 96 - 48) / 2.0);
        metrics.SafeHeightDp.ShouldBe((720 - 42) / 2.0);
    }

    [Fact]
    public void The_notch_is_never_on_in_the_shipped_settings()
    {
        Shipped().DebugNotch.ShouldBeFalse();
    }

    // ---- hotbar ----

    private sealed class FakeHotbar : IHotbarContent, IHotbarActivator
    {
        public readonly HotbarSlotView[] Slots;
        public readonly List<int> Activated = [];
        public int Version { get; set; }

        public FakeHotbar(params HotbarSlotView[] slots) => Slots = slots;

        public HotbarSlotView Slot(int index) => Slots[index];

        public void Activate(int index) => Activated.Add(index);
    }

    [Fact]
    public void Every_slot_starts_empty_and_a_press_does_nothing_but_give_feedback()
    {
        var hotbar = new HotbarModel(6);
        hotbar.SlotCount.ShouldBe(6);
        for (var i = 0; i < 6; i++)
        {
            hotbar.Slot(i).IsEmpty.ShouldBeTrue();
            hotbar.Press(i).ShouldBe(HotbarPress.Empty);
        }
        hotbar.Press(-1).ShouldBe(HotbarPress.OutOfRange);
        hotbar.Press(6).ShouldBe(HotbarPress.OutOfRange);
        hotbar.Slot(99).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void The_seam_binds_content_and_an_activator_and_only_usable_slots_activate()
    {
        var hotbar = new HotbarModel(3);
        var fake = new FakeHotbar(new HotbarSlotView("skill.x", 0, true), default, new HotbarSlotView("skill.y", 0.5, false));
        var before = hotbar.Version;
        hotbar.Bind(fake, fake);
        hotbar.Version.ShouldNotBe(before);

        hotbar.Press(0).ShouldBe(HotbarPress.Activated);
        hotbar.Press(1).ShouldBe(HotbarPress.Empty);
        hotbar.Press(2).ShouldBe(HotbarPress.Unusable);
        fake.Activated.ShouldBe([0]);

        // A change the content reports changes the version, so the view redraws exactly then.
        var seen = hotbar.Version;
        fake.Version++;
        hotbar.Version.ShouldNotBe(seen);

        hotbar.Bind(null, null);
        hotbar.Press(0).ShouldBe(HotbarPress.Empty);
        fake.Activated.Count.ShouldBe(1);
    }

    [Fact]
    public void A_filled_slot_without_an_activator_does_not_throw()
    {
        var hotbar = new HotbarModel(1);
        hotbar.Bind(new FakeHotbar(new HotbarSlotView("skill.x", 0, true)), null);
        hotbar.Press(0).ShouldBe(HotbarPress.Activated);
    }

    [Fact]
    public void A_session_has_an_empty_hotbar_of_the_configured_size()
    {
        var config = new ClientConfig { HotbarSlots = 4 };
        new HotbarModel(config.HotbarSlots).SlotCount.ShouldBe(4);
        new HotbarModel(-3).SlotCount.ShouldBe(0);
        Shipped().HotbarSlots.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_hotbar_row_fits_or_shrinks_to_the_minimum_or_wraps()
    {
        // Plenty of room: one row at the wanted size.
        var wide = HotbarLayout.Compute(6, 1, 56, 48, 8, 800);
        (wide.Columns, wide.Rows, wide.SlotDp).ShouldBe((6, 1, 56.0));

        // A little short: the slots shrink, but not below the minimum.
        var snug = HotbarLayout.Compute(6, 1, 56, 48, 8, 6 * 50 + 5 * 8);
        (snug.Columns, snug.Rows).ShouldBe((6, 1));
        snug.SlotDp.ShouldBe(50, 1e-9);

        // Too short even at the minimum: more rows.
        var narrow = HotbarLayout.Compute(6, 1, 56, 48, 8, 300);
        narrow.Rows.ShouldBeGreaterThan(1);
        narrow.SlotDp.ShouldBeGreaterThanOrEqualTo(48);
        (narrow.Columns * narrow.SlotDp + (narrow.Columns - 1) * 8).ShouldBeLessThanOrEqualTo(300);
        narrow.Columns.ShouldBe((6 + narrow.Rows - 1) / narrow.Rows);

        // The wanted rows are kept; no slots gives an empty grid; hopeless widths end with the minimum rather than loop.
        HotbarLayout.Compute(6, 2, 56, 48, 8, 800).Rows.ShouldBe(2);
        HotbarLayout.Compute(0, 1, 56, 48, 8, 800).Columns.ShouldBe(0);
        HotbarLayout.Compute(5, 1, 56, 48, 8, 10).SlotDp.ShouldBe(48);
    }

    [Theory]
    [InlineData(640, 360)]   // a small 16:9 phone
    [InlineData(780, 360)]   // a common 19.5:9 phone
    [InlineData(891, 411)]
    public void The_shipped_hotbar_fits_every_phone_at_every_ui_scale_even_with_a_notch(double widthDp, double heightDp)
    {
        var config = Shipped();
        var notch = SafeAreaMath.DebugNotch(new ClientConfig { DebugNotch = true }, 1);
        var metrics = new ScreenMetrics(widthDp, heightDp, 1, UiPlatform.Android, notch);
        var step = config.UiScaleMin;
        while (step <= config.UiScaleMax + 1e-9)
        {
            var grid = HotbarLayout.Compute(config.HotbarSlots, config.HotbarRows, config.HotbarSlotDp * step, TouchTargetRules.MinTarget(UiPlatform.Android),
                config.HotbarGapDp * step, metrics.SafeWidthDp - 16 * step);
            grid.SlotDp.ShouldBeGreaterThanOrEqualTo(48);
            var width = grid.Columns * grid.SlotDp + (grid.Columns - 1) * config.HotbarGapDp * step;
            width.ShouldBeLessThanOrEqualTo(metrics.SafeWidthDp - 16 * step + 1e-9, $"{widthDp}x{heightDp} dp at scale {step}");
            var height = grid.Rows * grid.SlotDp + (grid.Rows - 1) * config.HotbarGapDp * step;
            height.ShouldBeLessThan(metrics.SafeHeightDp / 2, $"{widthDp}x{heightDp} dp at scale {step}: the hotbar must leave room to play");
            step += config.UiScaleStep;
        }
    }

    // ---- gestures ----

    private static TapGestures Gestures(Action<ClientConfig>? tweak = null)
    {
        var config = new ClientConfig();
        tweak?.Invoke(config);
        return new TapGestures(config);
    }

    [Fact]
    public void A_press_held_still_becomes_a_long_press_once_and_never_a_tap()
    {
        var g = Gestures();
        g.Press(0, 300, 200, 10);
        g.Waiting.ShouldBeTrue();
        g.Tick(10.2).ShouldBe(GestureResult.None);
        g.Tick(10.5).ShouldBe(GestureResult.LongPress);
        (g.LongPressX, g.LongPressY).ShouldBe((300.0, 200.0));
        g.Waiting.ShouldBeFalse();
        g.Tick(10.6).ShouldBe(GestureResult.None);                 // once
        g.Release(0, 301, 200, 11).ShouldBe(GestureResult.None);   // and the lift is no tap

        // Ready again.
        g.Press(0, 5, 5, 12);
        g.Release(0, 5, 5, 12.1).ShouldBe(GestureResult.Tap);
    }

    [Fact]
    public void A_long_press_is_found_on_release_if_no_frame_noticed_it()
    {
        var g = Gestures();
        g.Press(0, 100, 100, 1);
        g.Release(0, 100, 100, 1.8).ShouldBe(GestureResult.LongPress);
        (g.LongPressX, g.LongPressY).ShouldBe((100.0, 100.0));
    }

    [Fact]
    public void The_long_press_time_is_the_dividing_line_between_tap_and_long_press()
    {
        Gestures().Also(g => g.Press(0, 0, 0, 0)).Release(0, 0, 0, 0.49).ShouldBe(GestureResult.Tap);
        Gestures().Also(g => g.Press(0, 0, 0, 0)).Release(0, 0, 0, 0.5).ShouldBe(GestureResult.LongPress);
        Gestures(c => c.LongPressSeconds = 0.8).Also(g => g.Press(0, 0, 0, 0)).Release(0, 0, 0, 0.7).ShouldBe(GestureResult.Tap);
    }

    [Fact]
    public void A_drag_is_neither_a_tap_nor_a_long_press()
    {
        var g = Gestures();
        g.Press(0, 100, 100, 0);
        g.Drag(0, 100 + 13, 100).ShouldBe(GestureResult.None);     // just past 12 dp at 1 unit per dp
        g.Waiting.ShouldBeFalse();
        g.Tick(2).ShouldBe(GestureResult.None);
        g.Release(0, 113, 100, 2.1).ShouldBe(GestureResult.None);

        // Dragging back to where it began does not make it a tap again.
        g.Press(0, 100, 100, 3);
        g.Drag(0, 200, 100);
        g.Drag(0, 100, 100);
        g.Release(0, 100, 100, 3.1).ShouldBe(GestureResult.None);
    }

    [Theory]
    [InlineData(1.0, 11.9, GestureResult.Tap)]
    [InlineData(1.0, 12.1, GestureResult.None)]
    [InlineData(2.0, 23.9, GestureResult.Tap)]    // a denser screen has more units per dp: the same finger movement in dp
    [InlineData(2.0, 24.1, GestureResult.None)]
    [InlineData(0.5, 5.9, GestureResult.Tap)]
    [InlineData(0.5, 6.1, GestureResult.None)]
    public void The_drag_threshold_is_in_dp(double unitsPerDp, double travel, GestureResult expected)
    {
        var g = Gestures();
        g.UnitsPerDp = unitsPerDp;
        g.Press(0, 0, 0, 0);
        g.Drag(0, travel, 0);
        g.Release(0, travel, 0, 0.1).ShouldBe(expected);
    }

    [Fact]
    public void A_pinch_needs_the_fingers_to_move_past_a_dead_zone_and_never_taps()
    {
        var g = Gestures();
        g.Press(0, 100, 100, 0);
        g.Press(1, 200, 100, 0);
        g.Waiting.ShouldBeFalse();
        g.Tick(5).ShouldBe(GestureResult.None);                       // two fingers down never long-press

        g.Drag(1, 205, 100).ShouldBe(GestureResult.None);             // 5 apart: jitter
        g.Drag(1, 207.5, 100).ShouldBe(GestureResult.None);           // 7.5: still inside 8
        g.Drag(1, 209, 100).ShouldBe(GestureResult.Zoom);             // 9: a pinch
        g.ZoomFactor.ShouldBe(100.0 / 109, 1e-9);                     // from the distance when the second finger went down
        g.Drag(1, 218, 100).ShouldBe(GestureResult.Zoom);             // no dead zone once started
        g.ZoomFactor.ShouldBe(109.0 / 118, 1e-9);
        g.Release(1, 218, 100, 0.5).ShouldBe(GestureResult.None);
        g.Release(0, 100, 100, 0.6).ShouldBe(GestureResult.None);     // not a tap, though both fingers were quick and close
    }

    [Fact]
    public void The_pinch_dead_zone_is_in_dp()
    {
        var g = Gestures();
        g.UnitsPerDp = 2;
        g.Press(0, 0, 0, 0);
        g.Press(1, 200, 0, 0);
        g.Drag(1, 215, 0).ShouldBe(GestureResult.None);               // 15 units = 7.5 dp
        g.Drag(1, 217, 0).ShouldBe(GestureResult.Zoom);               // 17 units = 8.5 dp
    }

    [Fact]
    public void The_finger_left_after_a_pinch_cannot_tap_or_long_press()
    {
        var g = Gestures();
        g.Press(0, 100, 100, 0);
        g.Press(1, 200, 100, 0);
        g.Release(0, 100, 100, 0.1).ShouldBe(GestureResult.None);
        g.Waiting.ShouldBeFalse();
        g.Tick(5).ShouldBe(GestureResult.None);
        g.Release(1, 200, 100, 5.1).ShouldBe(GestureResult.None);

        // And a pinch that began after a long-press is a pinch, not a second long-press.
        var h = Gestures();
        h.Press(0, 10, 10, 0);
        h.Tick(0.6).ShouldBe(GestureResult.LongPress);
        h.Press(1, 90, 10, 0.7);
        h.Drag(1, 150, 10).ShouldBe(GestureResult.Zoom);
        h.Release(1, 150, 10, 0.8);
        h.Release(0, 10, 10, 0.9).ShouldBe(GestureResult.None);
    }

    [Fact]
    public void Strangers_and_a_third_finger_are_ignored()
    {
        var g = Gestures();
        g.Drag(3, 5, 5).ShouldBe(GestureResult.None);
        g.Release(3, 5, 5, 1).ShouldBe(GestureResult.None);
        g.Press(0, 0, 0, 0);
        g.Press(1, 100, 0, 0);
        g.Press(2, 50, 50, 0).ShouldBe(GestureResult.None);
        g.Drag(2, 80, 80).ShouldBe(GestureResult.None);
    }

    [Fact]
    public void A_mouse_is_touch_index_zero_and_taps_the_same_way()
    {
        var g = Gestures();
        g.Press(0, 640, 360, 1);
        g.Release(0, 640, 360, 1.05).ShouldBe(GestureResult.Tap);
        (g.TapX, g.TapY).ShouldBe((640.0, 360.0));
    }

    // ---- the info panel's words ----

    private static DisplayBundle Bundle()
    {
        DisplayBundle.TryRead(ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        return bundle!;
    }

    private static EntityView Entity(EntityRecord record)
    {
        var world = new WorldView(new ClientConfig());
        world.ApplyWelcome(new WelcomeMessage(1, 1, "map.a", 10));
        world.ApplySnapshot(new SnapshotMessage(0, "map.a", 1, [record]), 0);
        return world.Find(record.Id)!;
    }

    [Fact]
    public void The_info_panel_says_what_the_bundle_says()
    {
        var bundle = Bundle();
        var monster = Entity(new EntityRecord(5, EntityKinds.Monster, "monster.gel_slime", new GridCell(1, 1), Directions.S, null, 0));
        EntityInfo.Title(monster, bundle).ShouldBe(TestGrids.SampleContent.Monsters.Get("monster.gel_slime").Name);
        EntityInfo.Subtitle(monster).ShouldBe(UiText.EntityKind(EntityKinds.Monster, false));

        var pile = Entity(new EntityRecord(6, EntityKinds.Item, "item.gel_glob", new GridCell(1, 1), Directions.S, null, 0, Quantity: 3, Mine: true));
        EntityInfo.Title(pile, bundle).ShouldBe(bundle.NameOf("item.gel_glob") + " x3");
        var single = Entity(new EntityRecord(7, EntityKinds.Item, "item.gel_glob", new GridCell(1, 1), Directions.S, null, 0, Quantity: 1, Mine: true));
        EntityInfo.Title(single, bundle).ShouldBe(bundle.NameOf("item.gel_glob"));

        var dead = Entity(new EntityRecord(8, EntityKinds.Monster, "monster.gel_slime", new GridCell(1, 1), Directions.S, null, 0, Dead: true));
        EntityInfo.Subtitle(dead).ShouldContain("defeated");
    }

    [Fact]
    public void Every_entity_kind_has_a_label_and_unknown_ones_have_none()
    {
        foreach (var kind in new[] { EntityKinds.Player, EntityKinds.Monster, EntityKinds.Npc, EntityKinds.Item })
            UiText.EntityKind(kind, false).ShouldNotBeEmpty(kind);
        UiText.EntityKind("dragon", false).ShouldBeEmpty();
        UiText.EntityKind("dragon", true).ShouldBeEmpty();
    }

    // ---- UI text (ADR-002 row 120) ----

    [Fact]
    public void The_scene_layer_writes_no_words_of_its_own()
    {
        // Every string a player reads comes from UiText or the bundle. A literal assigned to a Text property in client/Scene would be a second table.
        var literal = new Regex("""\b(Text|TooltipText|PlaceholderText|Title)\s*=\s*\$?"[^"]""");
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "client", "Scene"), "*.cs", SearchOption.AllDirectories))
            literal.IsMatch(File.ReadAllText(file)).ShouldBeFalse(Path.GetFileName(file) + ": " + literal.Match(File.ReadAllText(file)).Value);
    }

    [Fact]
    public void The_new_settings_read_from_the_file_and_the_command_line_and_are_bounded()
    {
        var config = new ClientConfig();
        config.ApplyJson("""{"hotbar_slots":8,"ui_scale_max":2,"long_press_seconds":0.4,"debug_notch":true}"""u8);
        config.HotbarSlots.ShouldBe(8);
        config.UiScaleMax.ShouldBe(2);
        config.LongPressSeconds.ShouldBe(0.4);
        config.DebugNotch.ShouldBeTrue();

        config.ApplyArgs(["--set", "hotbar_slots=99", "--set", "hotbar_rows=0"]);
        config.HotbarSlots.ShouldBe(12);
        config.HotbarRows.ShouldBe(1);
        config.ApplyArgs(["--set", "hotbar_slots=-4"]);
        config.HotbarSlots.ShouldBe(0);
    }
}

internal static class TestExtensions
{
    /// <summary>Runs <paramref name="action"/> on the value and returns it, so a setup step and the call under test can sit in one expression.</summary>
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
