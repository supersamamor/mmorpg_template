using System.Diagnostics;
using Game.Client.Core;
using Game.Client.Core.Audio;
using Game.Shared.Vocabulary;
using Game.Client.Core.Net;
using Game.Client.Core.Ui;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted run for the UI shell (T-211), against a real local server: every screen shape and UI scale lays out without anything cut off or smaller than a
/// finger, touch and mouse gestures reach the right place (UI taps never reach the world; a long-press shows the info panel and does not tap; a pinch zooms and
/// does not tap), the hotbar gives feedback and sends nothing, the size button stores its choice, and what the UI costs (§9). Started by
/// <c>run-e2e.ps1 -Run ui</c>.
/// </summary>
public sealed class UiRuns
{
    private const string LongName = "Extraordinarily Long Monster Name";

    private bool _chatSizesShown;

    private readonly Main _main;
    private readonly string? _screenshot;

    /// <summary>Prepares the run.</summary>
    public UiRuns(Main main, string? screenshot)
    {
        _main = main;
        _screenshot = screenshot;
    }

    private ClientSession Session => _main.Session!;

    private Hud Hud => _main.Hud!;

    private ClientConfig Config => _main.Config;

    /// <summary>Runs the whole script; throws on the first difference.</summary>
    public async Task RunAsync()
    {
        Print($"ui: platform {ScreenProbe.Platform}, window {_main.GetWindow().Size.X}x{_main.GetWindow().Size.Y}, canvas {Hud.Metrics.CanvasWidth:0}x{Hud.Metrics.CanvasHeight:0}, {Hud.Metrics.UnitsPerDp:0.00} units per dp, scale {Hud.Scale}, safe {Describe(Hud.Metrics.Safe)}, debug notch {(Config.DebugNotch ? "on" : "off")}");
        var originalScale = Hud.Scale;

        await LoginAsync();
        await LayoutMatrixAsync("playing");
        Hud.SampleAnnouncement = true;   // T-806: the longest announcement, where the flash line is while it shows
        await LayoutMatrixAsync("announcement shown");
        Hud.SampleAnnouncement = false;
        Hud.SampleChatLines = true;   // T-901: the last chat lines, where the flash line is while none shows
        await LayoutMatrixAsync("chat lines shown");
        Hud.SampleChatLines = false;
        await PanelsLayoutAsync();
        Hud.SetScale(originalScale, false);
        Hud.ForceRefresh();
        await FramesAsync(3);

        await PinchAndWheelAsync();
        await UiTouchesAsync();
        await LongPressAsync();
        await HotbarAsync();
        await ScaleButtonAsync(originalScale);
        await SoundPanelAsync();
        await BagPanelAsync();
        await ShopPanelAsync();
        await TapAsync();
        await MeasureAsync();
        await ShotAsync();

        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        await FramesAsync(3);
        Check(Hud.Accounts!.Open && Hud.Accounts.SignInButton.IsVisibleInTree(), "the sign-in screen shows");
        await AccountScreensAsync();
        Hud.ShowRefusal(UiText.CleartextRefused);
        await LayoutMatrixAsync("longest refusal text");
        Hud.SetScale(originalScale, false);
    }

    /// <summary>
    /// The stats panel and the quest log (T-406), each filled with its longest words at every screen shape and scale; the long quest tracker is part of every
    /// matrix (<see cref="Scene.Hud.ShowSamples"/>), where <see cref="Scene.Hud.FindProblems"/> also checks that it overlaps nothing.
    /// </summary>
    private async Task PanelsLayoutAsync()
    {
        var scaleBefore = Hud.Scale;
        var stats = Hud.Stats!;
        stats.ShowSamples();
        await FramesAsync(3);
        await LayoutMatrixAsync("stats panel open");
        stats.ClearSamples();
        var quests = Hud.Quests!;
        quests.ShowSamples(LongName);
        await FramesAsync(3);
        await LayoutMatrixAsync("quest log open");
        quests.ClearSamples();
        // T-901: the chat panel with its longest lines and the line actions, with the on-screen keyboard up (a landscape phone keyboard takes about half the height or more;
        // 0.6 is the worst case planned for), and without it.
        var chat = Hud.Chat!;
        chat.ShowSamples(LongName);
        chat.SimulatedKeyboard = 0.6f;
        await FramesAsync(3);
        await LayoutMatrixAsync("chat open, keyboard up");
        chat.SimulatedKeyboard = null;
        await LayoutMatrixAsync("chat open");
        Check(Hud.Blocks(chat.SendButton.GetGlobalRect().GetCenter()), "a touch on the chat panel never reaches the world");
        chat.ClearSamples();
        // T-902: the party panel with a full party of the longest names and the leader's buttons on every row, the invite prompt over it, and four marks at the screen's edges.
        var party = Hud.Party!;
        party.ShowSamples();
        await FramesAsync(3);
        await LayoutMatrixAsync("party panel, invite prompt and marks");
        Check(Hud.Blocks(party.LeaveButton.GetGlobalRect().GetCenter()), "a touch on the party panel never reaches the world");
        Check(Hud.Blocks(party.AcceptButton.GetGlobalRect().GetCenter()), "a touch on the invite prompt never reaches the world");
        party.ClearSamples();
        // T-904: the guild panel with the longest guild, rank and member names, a notice of the longest length, the leader's buttons on every row and the invite prompt over it;
        // then the founding form with the emblem picker.
        var guild = Hud.Guild!;
        guild.ShowSamples();
        await FramesAsync(3);
        await LayoutMatrixAsync("guild panel and invite prompt");
        Check(Hud.Blocks(guild.LeaveButton.GetGlobalRect().GetCenter()), "a touch on the guild panel never reaches the world");
        Check(Hud.Blocks(guild.AcceptButton.GetGlobalRect().GetCenter()), "a touch on the guild invite prompt never reaches the world");
        guild.ShowFoundingSample();
        await FramesAsync(3);
        await LayoutMatrixAsync("guild founding with the emblem picker");
        Check(guild.EmblemButtons.Count > 0 && Hud.Blocks(guild.EmblemButtons[^1].GetGlobalRect().GetCenter()), "the emblem picker shows and takes its touches");
        guild.ClearSamples();
        // T-905: the guild storage window full (the size's entries of the longest lines, the largest currency) with the quantity picker, then its log of the longest lines.
        var storage = Hud.GuildStorage!;
        storage.ShowSamples(log: false);
        await FramesAsync(3);
        await LayoutMatrixAsync("guild storage with the quantity picker");
        Check(Hud.Blocks(storage.MoveButton.GetGlobalRect().GetCenter()), "a touch on the storage window never reaches the world");
        storage.ShowSamples(log: true);
        await FramesAsync(3);
        await LayoutMatrixAsync("guild storage log");
        Check(storage.LogTab.IsVisibleInTree() && Hud.Blocks(storage.LogTab.GetGlobalRect().GetCenter()), "the log tab shows and takes its touches");
        storage.ClearSamples();
        // T-903: the trade window with both offers full of the longest lines, the longest name and state, the currency field and the footer, the ask prompt over it; then the bag's
        // list of what can be offered with the quantity picker in the columns' place.
        var trade = Hud.Trade!;
        trade.ShowSamples(picker: false);
        await FramesAsync(3);
        await LayoutMatrixAsync("trade window and ask prompt");
        Check(Hud.Blocks(trade.ConfirmButton.GetGlobalRect().GetCenter()), "a touch on the trade window never reaches the world");
        Check(Hud.Blocks(trade.AcceptButton.GetGlobalRect().GetCenter()), "a touch on the ask prompt never reaches the world");
        trade.ShowSamples(picker: true);
        await FramesAsync(3);
        await LayoutMatrixAsync("trade window, picking with the quantity picker");
        trade.ClearSamples();
        var wardrobe = Hud.Wardrobe!;   // T-708: five slots of the longest names, the switch and the line under them
        wardrobe.ShowSamples(LongName);
        await FramesAsync(3);
        await LayoutMatrixAsync("wardrobe open");
        Check(Hud.Blocks(wardrobe.HideButton.GetGlobalRect().GetCenter()), "a touch on the wardrobe never reaches the world");
        wardrobe.ClearSamples();
        Hud.SetScale(scaleBefore, false);
        Hud.ForceRefresh();
        await FramesAsync(3);
    }

    /// <summary>
    /// The sign-in and character-select screens (T-701), each with the longest words it can show, at every screen shape and scale. Every button and field in them
    /// is at least the touch target: the same check as the rest of the HUD (<c>FindProblems</c>).
    /// </summary>
    private async Task AccountScreensAsync()
    {
        var accounts = Hud.Accounts!;
        accounts.ShowSamples(Game.Client.Core.Accounts.AccountScreen.SignIn, LongName);
        await FramesAsync(3);
        Check(accounts.Password.Secret, "the password field hides what is typed");
        Check(Hud.Blocks(accounts.SignInButton.GetGlobalRect().GetCenter()), "a touch on the sign-in screen never reaches the world");
        await LayoutMatrixAsync("sign-in screen");
        accounts.ShowSamples(Game.Client.Core.Accounts.AccountScreen.Characters, LongName);
        await FramesAsync(3);
        Check(accounts.Rows.Count == 3 && accounts.NewButton.IsVisibleInTree(), "the character screen shows its slots and the new-character button");
        Check(accounts.Pictures.Count == 3 && accounts.Pictures.All(p => p.Looks is not null), "every character in the list has its picture");
        await LayoutMatrixAsync("character select");
        accounts.ShowSamples(Game.Client.Core.Accounts.AccountScreen.Password, LongName);
        await FramesAsync(3);
        var passwords = accounts.PasswordScreen;
        Check(passwords.Current.Secret && passwords.New.Secret && passwords.Repeat.Secret && passwords.Change.IsVisibleInTree(), "the password screen hides what is typed and shows its buttons");
        await LayoutMatrixAsync("password screen");
        accounts.ShowSamples(Game.Client.Core.Accounts.AccountScreen.DeleteAccount, LongName);
        await FramesAsync(3);
        Check(accounts.DeletionScreen.Password.Secret && accounts.DeletionScreen.Confirm.IsVisibleInTree(), "the deletion screen hides what is typed and shows its buttons");
        await LayoutMatrixAsync("deletion screen");
        accounts.ClearSamples();
        accounts.ShowCreationSamples(LongName);
        await FramesAsync(3);
        var creation = accounts.Creation!;
        Check(creation.Open && creation.CreateButton.IsVisibleInTree() && creation.OptionButton("sample5") is not null, "the creation screen shows its choices and its buttons");
        Check(Hud.Blocks(creation.BackButton.GetGlobalRect().GetCenter()) && (creation.Preview is null || Hud.Blocks(creation.Preview.Frame.GetGlobalRect().GetCenter())), "a touch on the creation screen never reaches the world");
        await LayoutMatrixAsync("character creation");
        accounts.ClearSamples();
        await FramesAsync(3);
        Print("accounts: the sign-in, character-select, password, deletion and creation screens fit every screen shape and scale, every button and field at least the touch target");
    }

    /// <summary>The sound settings: the panel fits at every screen shape, the buttons change the mixer and the engine's buses, and the choice is stored.</summary>
    private async Task SoundPanelAsync()
    {
        var audio = _main.Audio!;
        var mixer = audio.Director.Mixer;
        Hud.SetSoundPanel(true);
        await FramesAsync(2);
        Check(Hud.SoundPanelShown, "the sound panel opens");
        var scaleBefore = Hud.Scale;
        await LayoutMatrixAsync("sound panel open");
        Hud.SetScale(scaleBefore, false); // the matrix leaves the last fake screen laid out: back to the real one
        Hud.ForceRefresh();
        await FramesAsync(3);
        var buttons = Hud.SoundButtons; // the four buses in order, then mute
        var sfx = BusMixer.IndexOf(AudioBuses.Sfx);
        var before = mixer.Volume(AudioBuses.Sfx);
        buttons[sfx].EmitSignal(BaseButton.SignalName.Pressed);
        await FramesAsync(3);
        Check(Math.Abs(mixer.Volume(AudioBuses.Sfx) - Hud.NextVolume(before)) < 1e-9, $"the effects button goes {before} -> {Hud.NextVolume(before)}");
        var busIndex = AudioServer.GetBusIndex(AudioBuses.Sfx);
        Check(Math.Abs(AudioServer.GetBusVolumeDb(busIndex) - BusMixer.ToDb(mixer.Volume(AudioBuses.Sfx))) < 0.01, "and the engine bus follows");
        Check(Math.Abs(UserPrefsStore.Load().Volumes[sfx] - mixer.Volume(AudioBuses.Sfx)) < 1e-6, "and the choice is stored");
        buttons[4].EmitSignal(BaseButton.SignalName.Pressed);
        await FramesAsync(3);
        Check(mixer.Muted && AudioServer.IsBusMute(busIndex) && AudioServer.IsBusMute(AudioServer.GetBusIndex(AudioBuses.Music)), "mute silences every bus");
        Check(UserPrefsStore.Load().Muted, "and is stored");
        buttons[4].EmitSignal(BaseButton.SignalName.Pressed);
        await FramesAsync(3);
        Check(!mixer.Muted, "unmuting brings the sound back");
        Check(Hud.FindProblems().Count == 0, "and the panel still fits");
        Print($"sound: panel fits every screen; effects volume {before} -> {mixer.Volume(AudioBuses.Sfx)} reached the engine bus and the preferences; mute and unmute worked");
        Hud.SetSoundPanel(false);
        await FramesAsync(2);
    }

    /// <summary>
    /// The bag (T-401): it fits every screen shape and scale filled with the longest words, it opens with its button, it lists the real inventory, a tap selects
    /// an item and shows its actions, and what it costs to draw while open.
    /// </summary>
    private async Task BagPanelAsync()
    {
        var bag = Hud.Bag!;
        var scaleBefore = Hud.Scale;
        bag.ShowSamples(LongName);
        await FramesAsync(3);
        await LayoutMatrixAsync("bag open");
        bag.ClearSamples();
        Hud.SetScale(scaleBefore, false);
        Hud.ForceRefresh();
        await FramesAsync(3);

        await UntilAsync(() => Session.Inventory.Known, 10, "the own inventory");
        Hud.BagButton.EmitSignal(BaseButton.SignalName.Pressed);
        await FramesAsync(3);
        Check(bag.Open, "the bag button opens the bag");
        var carried = Session.Inventory.Entries.Count(e => e.Slot is null);
        Check(bag.ItemButtons.Count(b => b.Button.Visible) == carried, $"one button per carried entry ({carried})");
        foreach (var (slot, button) in bag.SlotButtons)
            if (Session.Inventory.InSlot(slot) is { } worn) Check(button.Text.Contains(_main.Bundle!.NameOf(worn.Item)), $"the {slot} slot names what is worn");
        if (bag.ItemButtons.FirstOrDefault() is { Button: { } first, Entry: var entry })
        {
            first.EmitSignal(BaseButton.SignalName.Pressed);
            await FramesAsync(2);
            Check(Session.Inventory.Selected == entry, "a tap selects the item");
            Check(bag.Actions.Drop.Visible, "and offers its actions");
            first.EmitSignal(BaseButton.SignalName.Pressed);
            await FramesAsync(2);
            Check(Session.Inventory.Selected is null, "a second tap clears the selection");
        }
        Check(Hud.FindProblems().Count == 0, "the open bag fits this screen");
        Check(Hud.Blocks(bag.ItemButtons.Count > 0 ? bag.ItemButtons[0].Button.GetGlobalRect().GetCenter() : Hud.BagButton.GetGlobalRect().GetCenter()), "a touch on the bag never reaches the world");

        OS.LowProcessorUsageMode = false;
        await FramesAsync(5);
        double sum = 0, max = 0;
        var frames = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 2)
        {
            await FramesAsync(1);
            var draws = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            sum += draws;
            max = Math.Max(max, draws);
            frames++;
        }
        var nodes = Hud.NodeCount;
        if (_screenshot is not null && DisplayServer.GetName() != "headless")
            await SaveAsync(System.IO.Path.ChangeExtension(_screenshot, null) + "-bag" + System.IO.Path.GetExtension(_screenshot), "bag open");
        OS.LowProcessorUsageMode = true;
        Hud.BagButton.EmitSignal(BaseButton.SignalName.Pressed);
        await FramesAsync(2);
        Check(!bag.Open, "the bag button closes it again");
        Print($"bag: fits every screen; {carried} carried entries shown; with the bag open draw calls avg {(frames > 0 ? sum / frames : 0):0.0}, max {max:0}; {nodes} UI nodes");
    }

    /// <summary>
    /// The shop panel (T-603): filled with the longest words (a long item name, the largest prices, the picker) it fits every screen shape and scale, and every button in it
    /// (rows, tabs, picker, Buy, Close) is at least the touch target: the same check as the rest of the HUD (<c>FindProblems</c>).
    /// </summary>
    private async Task ShopPanelAsync()
    {
        var shop = Hud.Shop!;
        var scaleBefore = Hud.Scale;
        shop.ShowSamples(LongName);
        await FramesAsync(3);
        await LayoutMatrixAsync("shop open");
        Check(shop.Open && shop.Rows.Count >= 12, "the shop panel shows its sample rows");
        Check(Hud.Blocks(shop.Rows[0].Button.GetGlobalRect().GetCenter()), "a touch on a shop row never reaches the world");
        shop.ClearSamples();
        Hud.SetScale(scaleBefore, false);
        Hud.ForceRefresh();
        await FramesAsync(3);
        Check(!shop.Open, "the sample shop closes again");
        Print("shop: the panel fits every screen shape and scale with the longest words, every button at least the touch target");
    }

    // ---- layout ----

    /// <summary>Phones as the layout sees them: a small 16:9, the common 19.5:9, a large one, and an iPhone (44 pt).</summary>
    private static readonly (string Name, double WidthDp, double HeightDp, double CanvasRows, UiPlatform Platform)[] Screens =
    [
        ("640x360 dp (16:9)", 640, 360, 720, UiPlatform.Android),
        ("780x360 dp (19.5:9)", 780, 360, 720, UiPlatform.Android),
        ("891x411 dp (19.5:9)", 891, 411, 720, UiPlatform.Android),
        ("844x390 pt (iPhone)", 844, 390, 720, UiPlatform.Ios),
        ("1280x720 desktop", 1280, 720, 720, UiPlatform.Desktop),
        ("1560x720 desktop", 1560, 720, 720, UiPlatform.Desktop),
    ];

    private async Task LayoutMatrixAsync(string what)
    {
        var scales = new List<double>();
        for (var i = 0; i < UiScale.StepCount(Config.UiScaleMin, Config.UiScaleMax, Config.UiScaleStep); i++) scales.Add(Config.UiScaleMin + i * Config.UiScaleStep);
        var combinations = 0;
        var failures = new List<string>();
        foreach (var screen in Screens)
            foreach (var notch in new[] { false, true })
                foreach (var scale in scales)
                {
                    var unitsPerDp = screen.CanvasRows / screen.HeightDp;
                    var insets = notch ? new Insets(Config.DebugNotchLeftDp, Config.DebugNotchTopDp, Config.DebugNotchRightDp, Config.DebugNotchBottomDp).Scaled(unitsPerDp) : default;
                    var metrics = new ScreenMetrics(screen.WidthDp * unitsPerDp, screen.HeightDp * unitsPerDp, unitsPerDp, screen.Platform, insets);
                    Hud.ApplyMetrics(metrics, scale);
                    Hud.ShowSamples(LongName);
                    // The first screen comes after the window's own (a tiny one when headless), and the containers need a few frames to wrap text at the new width.
                    await FramesAsync(combinations == 0 ? 6 : 3);
                    combinations++;
                    foreach (var problem in Hud.FindProblems()) failures.Add($"{what}, {screen.Name}, notch {(notch ? "on" : "off")}, scale {scale}: {problem}");
                    if (failures.Count > 0 && Hud.Chat is { Open: true } open && !_chatSizesShown)
                    {
                        _chatSizesShown = true;
                        Print($"layout: chat panel parts at {screen.Name}, scale {scale}: {open.Sizes}");
                    }
                    // The quest tracker, long and one-line, must lie over no other HUD button or the target frame (T-406, T-408): a failure, not a note.
                    foreach (var name in Hud.TrackerOverlaps()) failures.Add($"{what}, {screen.Name}, notch {(notch ? "on" : "off")}, scale {scale}: the long quest tracker covers {name}");
                    // The autopilot indicator (T-605), paused with its longest line, must lie over nothing; the plain running one is shorter and trimmed the same way.
                    foreach (var name in Hud.AutopilotOverlaps()) failures.Add($"{what}, {screen.Name}, notch {(notch ? "on" : "off")}, scale {scale}: the autopilot indicator covers {name}");
                    // T-901: the chat strip lies over no other part of the HUD, and with the keyboard up the open panel's field and buttons stay above it.
                    foreach (var name in Hud.ChatStripOverlaps()) failures.Add($"{what}, {screen.Name}, notch {(notch ? "on" : "off")}, scale {scale}: the chat strip: {name}");
                    if (Hud.Chat is { Open: true, SimulatedKeyboard: { } share } chat)
                        foreach (var problem in chat.KeyboardProblems((float)(metrics.CanvasHeight * (1 - share))))
                            failures.Add($"{what}, {screen.Name}, notch {(notch ? "on" : "off")}, scale {scale}: with the keyboard up, {problem}");
                    var longText = Hud.Tracker.Text;
                    Hud.Tracker.Text = UiText.QuestsButton;
                    await FramesAsync(2);
                    foreach (var name in Hud.TrackerOverlaps()) failures.Add($"{what}, {screen.Name}, notch {(notch ? "on" : "off")}, scale {scale}: the one-line quest tracker covers {name}");
                    Hud.Tracker.Text = longText;
                }
        Print($"layout: {what}: {combinations} combinations ({Screens.Length} screens x notch on/off x {scales.Count} scales), {failures.Count} problems");
        foreach (var line in failures.Take(12)) Print("layout: PROBLEM " + line);
        Check(failures.Count == 0, $"{what}: nothing cut off, outside the safe area, or under the touch minimum ({failures.Count} problems)");
    }

    // ---- input ----

    private async Task PinchAndWheelAsync()
    {
        Check(Session.Ui.Marker is null && Session.Ui.Target is null, "starts with no marker and no target");
        var camera = _main.Camera!;
        var start = camera.Distance;
        var center = _main.GetViewport().GetVisibleRect().Size / 2;
        var dp = (float)Hud.UnitsPerDp; // distances in dp, as the thresholds are
        Touch(0, center + new Vector2(-12 * dp, 0), true);
        Touch(1, center + new Vector2(12 * dp, 0), true);
        for (var i = 1; i <= 10; i++) Drag(1, center + new Vector2((12 + 1.5f * i) * dp, 0));
        var zoomedIn = camera.Distance;
        Check(zoomedIn < start, $"fingers moving apart bring the camera closer ({start:0.0} -> {zoomedIn:0.0})");
        for (var i = 10; i >= 0; i--) Drag(1, center + new Vector2((12 + 1.5f * i - 7) * dp, 0));
        Touch(1, center + new Vector2(5 * dp, 0), false);
        Touch(0, center + new Vector2(-12 * dp, 0), false);
        var zoomedOut = camera.Distance;
        Check(zoomedOut > zoomedIn, $"fingers moving together take it away again ({zoomedIn:0.0} -> {zoomedOut:0.0})");
        Check(zoomedIn >= Config.CameraDistanceMin - 1e-3 && zoomedOut <= Config.CameraDistanceMax + 1e-3, "a pinch stays inside the zoom clamp");
        await FramesAsync(2);
        Check(Session.Ui.Marker is null && Session.Ui.Target is null, "a pinch fires no tap");

        for (var i = 0; i < 40; i++) Wheel(MouseButton.WheelUp);
        Check(Math.Abs(camera.Distance - Config.CameraDistanceMin) < 1e-3, $"the wheel stops at the closest zoom ({camera.Distance:0.00})");
        for (var i = 0; i < 80; i++) Wheel(MouseButton.WheelDown);
        Check(Math.Abs(camera.Distance - Config.CameraDistanceMax) < 1e-3, $"and at the farthest ({camera.Distance:0.00})");
        camera.Zoom(Config.CameraDistance / camera.Distance);
        Print($"input: pinch {start:0.0} -> {zoomedIn:0.0} -> {zoomedOut:0.0}, wheel clamps at {Config.CameraDistanceMin}..{Config.CameraDistanceMax}; no tap from either");
    }

    private async Task UiTouchesAsync()
    {
        var version = Session.Ui.Version;
        var targets = new List<Button> { Hud.UiSizeButton };
        targets.AddRange(Hud.HotbarSlots);
        foreach (var button in targets)
        {
            var at = button.GetGlobalRect().GetCenter();
            Check(Hud.Blocks(at), $"a touch on {button.Text.Replace("\n", " ")} is blocked from the world");
            Touch(0, at, true);
            await FramesAsync(1);
            Touch(0, at, false);
        }
        await FramesAsync(2);
        Check(Session.Ui.Marker is null && Session.Ui.Target is null && Session.Ui.Version == version, "taps on the UI never reach the world");
        Check(!Hud.Blocks(_main.GetViewport().GetVisibleRect().Size / 2), "the middle of the screen belongs to the world");
        Print($"input: {targets.Count} touches on UI controls: all blocked, none reached the world");
    }

    private async Task LongPressAsync()
    {
        var own = Session.World.Own!;
        var at = _main.Camera!.Camera.UnprojectPosition(new Vector3((float)own.X, 0.45f, (float)own.Z));
        Check(!Hud.Blocks(at), "the player is not under a control");
        Touch(0, at, true);
        await SecondsAsync(Config.LongPressSeconds + 0.3);
        Check(Hud.InfoShown, "holding still shows the info panel");
        var expected = Session.World.Own is { } e ? _main.Bundle!.NameOf(e.DisplayKey) : string.Empty;
        Check(Hud.InfoText.StartsWith(expected, StringComparison.Ordinal), $"the panel names what is under the finger (\"{Hud.InfoText}\", expected \"{expected}\")");
        Touch(0, at, false);
        await FramesAsync(2);
        Check(Session.Ui.Marker is null && Session.Ui.Target is null, "lifting after a long-press fires no tap");
        Check(Hud.InfoShown, "and the panel stays");

        // A touch elsewhere dismisses it, and is a tap of its own.
        var elsewhere = at + new Vector2(0, 160);
        Touch(0, elsewhere, true);
        Check(!Hud.InfoShown, "a new touch hides the panel");
        Touch(0, elsewhere, false);
        await FramesAsync(1);
        Check(Session.Ui.Marker is not null, "and that touch is an ordinary tap");
        Print($"input: long-press on the player showed \"{Hud.InfoText}\"; the lift fired no tap; the next touch hid the panel and tapped");
        await UntilAsync(() => Session.Ui.Marker is null, 10, "the tap's walk ending");
    }

    private async Task HotbarAsync()
    {
        var slots = Hud.HotbarSlots;
        Check(slots.Count == Config.HotbarSlots, $"the hotbar has the configured {Config.HotbarSlots} slots");
        var before = Hud.HotbarPresses;
        slots[1].EmitSignal(BaseButton.SignalName.Pressed);
        await FramesAsync(2);
        Check(Hud.HotbarPresses == before + 1 && Hud.LastHotbarPress == HotbarPress.Empty, "pressing a slot reaches the model, which says it is empty");
        Check(Session.Ui.Marker is null && Session.Ui.Target is null, "and sends no intent");
        var minimum = TouchTargetRules.MinTarget(Hud.Metrics.Platform) * Hud.Metrics.UnitsPerDp - 0.6;
        foreach (var slot in slots) Check(slot.Size.X >= minimum && slot.Size.Y >= minimum, $"slot {slot.Text} is at least {TouchTargetRules.MinTarget(Hud.Metrics.Platform)} dp");

        if (DisplayServer.GetName() != "headless")
        {
            // A real mouse click on a slot, through the engine's own input, in window pixels.
            var canvas = slots[2].GetGlobalRect().GetCenter();
            var pixel = canvas * (Vector2)_main.GetWindow().Size / _main.GetViewport().GetVisibleRect().Size;
            foreach (var pressed in new[] { true, false })
            {
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = pixel, GlobalPosition = pixel });
                Input.FlushBufferedEvents();
                await FramesAsync(2);
            }
            Check(Hud.HotbarPresses == before + 2, "a real mouse click on a slot presses it");
            Check(Session.Ui.Marker is null && Session.Ui.Target is null, "and does not tap the ground behind it");
            Print($"hotbar: {slots.Count} slots, empty; a real click at window pixel {pixel} pressed slot 3 and the world saw nothing");
        }
        else
        {
            Print($"hotbar: {slots.Count} slots, empty; a press reached the model and sent nothing (the real click needs a window: skipped headless)");
        }
    }

    private async Task ScaleButtonAsync(double originalScale)
    {
        var scale = Hud.Scale;
        Hud.UiSizeButton.EmitSignal(BaseButton.SignalName.Pressed);
        await FramesAsync(2);
        var next = UiScale.Next(scale, Config.UiScaleMin, Config.UiScaleMax, Config.UiScaleStep);
        Check(Math.Abs(Hud.Scale - next) < 1e-9, $"the size button goes {scale} -> {next}");
        Check(Hud.FindProblems().Count == 0, "and the layout still fits");
        Check(Math.Abs(UserPrefsStore.Load().EffectiveUiScale(Config) - next) < 1e-9, "the choice is stored in the user's preferences");
        Hud.SetScale(originalScale, true);
        await FramesAsync(2);
        Print($"scale: size button {scale} -> {next}, stored; put back to {originalScale}");
    }

    private async Task TapAsync()
    {
        var own = Session.World.Own!;
        var cell = new Game.Shared.World.GridCell(own.Cell.X + 2, own.Cell.Y);
        var at = _main.Camera!.Camera.UnprojectPosition(new Vector3(cell.X + 0.5f, 0, cell.Y + 0.5f));
        if (Hud.Blocks(at)) at = _main.GetViewport().GetVisibleRect().Size / 2;
        Touch(0, at, true);
        await FramesAsync(1);
        Touch(0, at, false);
        await FramesAsync(1);
        Check(Session.Ui.Marker is not null, "a plain tap on the ground still moves");
        Print("input: a plain tap on the ground still sends a move");
    }

    // ---- cost ----

    private async Task MeasureAsync()
    {
        OS.LowProcessorUsageMode = false;
        await SecondsAsync(2);
        _main.CountAllocations = true;
        var frames = 0;
        double sumWith = 0, maxWith = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 4)
        {
            await FramesAsync(1);
            var draws = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            sumWith += draws;
            maxWith = Math.Max(maxWith, draws);
            frames++;
        }
        _main.CountAllocations = false;
        var seconds = clock.Elapsed.TotalSeconds;
        var fps = frames / seconds;
        var (quietBytes, quietFrames, _, _) = _main.Allocations;

        Hud.SetVisible(false);
        await FramesAsync(3);
        double sumWithout = 0;
        var framesWithout = 0;
        clock.Restart();
        while (clock.Elapsed.TotalSeconds < 2)
        {
            await FramesAsync(1);
            sumWithout += Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            framesWithout++;
        }
        Hud.SetVisible(true);
        await FramesAsync(2);
        OS.LowProcessorUsageMode = true;

        Print($"measure: cap {Config.FpsCap}, {frames} frames in {seconds:0.0} s = {fps:0.0} fps with the HUD showing");
        Print($"measure: draw calls with the HUD avg {sumWith / frames:0.0}, max {maxWith:0}; without {sumWithout / framesWithout:0.0}; the UI adds {sumWith / frames - sumWithout / framesWithout:0.0}");
        Print($"measure: the frame path allocated {quietBytes} bytes in {quietFrames} frames with no message ({(quietFrames > 0 ? quietBytes / (double)quietFrames : 0):0.0} bytes per frame, HUD included)");
        Print($"measure: {Hud.NodeCount} UI nodes");
    }

    private async Task ShotAsync()
    {
        if (_screenshot is null) return;
        if (DisplayServer.GetName() == "headless")
        {
            Print("screenshot: headless, skipped");
            return;
        }
        // Two pictures: the plain HUD, and the HUD stressed with the longest words it can be asked to show (the same text the layout check uses).
        Hud.ForceRefresh();
        Hud.HideInfo();
        OS.LowProcessorUsageMode = false;
        await FramesAsync(3);
        await SaveAsync(_screenshot, "plain");
        Hud.ShowSamples(LongName);
        await FramesAsync(4);
        await SaveAsync(System.IO.Path.ChangeExtension(_screenshot, null) + "-long-text" + System.IO.Path.GetExtension(_screenshot), "longest text");
        // And at the largest UI scale (T-408: on a 360 dp tall window the left column must stay clear of the Skills button there).
        var scaleBefore = Hud.Scale;
        Hud.SetScale(Config.UiScaleMax, false);
        Hud.ShowSamples(LongName);
        await FramesAsync(4);
        await SaveAsync(System.IO.Path.ChangeExtension(_screenshot, null) + "-max-scale" + System.IO.Path.GetExtension(_screenshot), $"longest text at UI scale {Config.UiScaleMax}");
        Hud.SetScale(scaleBefore, false);
        Hud.ForceRefresh();
        Hud.HideInfo();
        OS.LowProcessorUsageMode = true;
    }

    private async Task SaveAsync(string path, string what)
    {
        await _main.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        await _main.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        var error = _main.GetViewport().GetTexture().GetImage().SavePng(path);
        Print($"screenshot: {path} ({error}), {what}, {_main.GetWindow().Size.X}x{_main.GetWindow().Size.Y}, debug notch {(Config.DebugNotch ? "on" : "off")}");
    }

    // ---- helpers ----

    private async Task LoginAsync()
    {
        await FramesAsync(1);
        Check(Session.Connection.Phase == ConnectionPhase.LoginScreen, "starts on the login screen");
        Session.Login(Config.DevName, Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online, 15, "welcome");
        await UntilAsync(() => Session.World.Own is not null && _main.LoadedMap == Session.World.MapId, 15, "first snapshot and map");
        await FramesAsync(5);
        Check(!Hud.PanelButtonShown, "the login panel is gone once playing");
        Print($"login: online on {Session.World.MapId}");
    }

    private void Touch(int index, Vector2 position, bool pressed) => _main.HandleInput(new InputEventScreenTouch { Index = index, Position = position, Pressed = pressed });

    private void Drag(int index, Vector2 position) => _main.HandleInput(new InputEventScreenDrag { Index = index, Position = position });

    private void Wheel(MouseButton button) => _main.HandleInput(new InputEventMouseButton { ButtonIndex = button, Pressed = true });

    private static string Describe((double X, double Y, double Width, double Height) rect) => $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0})";

    private async Task FramesAsync(int count)
    {
        for (var i = 0; i < count; i++) await _main.ToSignal(_main.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task SecondsAsync(double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds) await FramesAsync(1);
    }

    private async Task UntilAsync(Func<bool> condition, double seconds, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}, notice {Session.Connection.Notice}");
            await FramesAsync(1);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
