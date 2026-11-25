using System.Diagnostics;
using Game.Client.Core;
using Game.Client.Core.Accounts;
using Game.Client.Core.Net;
using Game.Client.Core.Theme;
using Game.Client.Core.World;
using Game.Shared.ClientContent;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted runs for character looks (T-706). <c>--e2e-appearance</c>, against a server with a database: register, make a character with looks other than the
/// defaults through the creation screen, enter the world and see it drawn with those ids; then a second client (<c>--e2e-appearance-watch</c>, started by
/// <c>run-e2e.ps1</c>) logs in beside it and sees them too. <c>--measure-looks</c>, after <c>--measure N</c>'s wait for a crowd: what the looks cost (§9).
/// </summary>
public sealed class LookRuns
{
    private readonly Main _main;
    private readonly Func<string, Task> _shot;
    private readonly Func<int> _onScreen;

    /// <summary>Prepares the runs. <paramref name="shot"/> saves the screenshot when one is asked for; <paramref name="onScreen"/> counts the entities on screen.</summary>
    public LookRuns(Main main, Func<string, Task> shot, Func<int>? onScreen = null)
    {
        _onScreen = onScreen ?? (() => 0);
        _main = main;
        _shot = shot;
    }

    private ClientSession Session => _main.Session!;

    private WorldView World => Session.World;

    /// <summary>Creates a character through the creation screen from the character list, with the looks chosen there now, and waits for it to be listed.</summary>
    public static async Task CreateAsync(Main main, string name)
    {
        var accounts = main.Session!.Accounts;
        var screen = main.Hud!.Accounts!;
        var before = accounts.Characters.Count;
        var runs = new LookRuns(main, _ => Task.CompletedTask);
        if (accounts.Screen == AccountScreen.Characters) runs.Press(screen.NewButton);
        await runs.UntilAsync(() => screen.Creation is { Open: true }, 5, "the creation screen");
        runs.Type(name);
        runs.Press(screen.Creation!.CreateButton);
        await runs.UntilAsync(() => accounts.Characters.Count == before + 1 && !accounts.Busy && accounts.Screen == AccountScreen.Characters, 10, "the new character");
        await runs.FramesAsync(3);
    }

    /// <summary>The first client's run.</summary>
    public async Task CreatorAsync()
    {
        var accounts = Session.Accounts;
        var screen = _main.Hud!.Accounts!;
        await FramesAsync(3);
        var tag = new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));
        Fill(screen.Username, "e2e-" + tag);
        Fill(screen.Password, Guid.NewGuid().ToString("N")); // thrown away with the run
        Press(screen.RegisterButton);
        await UntilAsync(() => accounts.Screen == AccountScreen.Characters && !accounts.Busy, 15, "the character list after registering");
        await FramesAsync(3);
        Press(screen.NewButton);
        await UntilAsync(() => screen.Creation is { Open: true }, 5, "the creation screen");
        var creation = screen.Creation!;
        var creator = accounts.Creator!;
        var defaults = creator.Look!;
        await UntilAsync(() => creation.Preview is { Settled: true }, 10, "the preview");
        CheckDrawn(creation.Preview!.Visual!, defaults, "the preview of the defaults");
        Check(creation.Preview.Rendering, "the preview renders while the screen is open");
        var start = creation.Preview.Table.Degrees;
        await SecondsAsync(0.5);
        Check(Math.Abs(creation.Preview.Table.Degrees - start) > 1, "the preview turns by itself");
        await DragAsync(creation);

        // Every choice by its own button, none of them the default.
        var gender = creator.Genders.First(g => g.Gender != defaults.Gender);
        Press(creation.OptionButton(gender.Gender)!);
        Press(creation.OptionButton(Last(creator.Faces, creator.Face, o => o.Id))!);
        Press(creation.OptionButton(Last(creator.Hairstyles, creator.Hair, o => o.Id))!);
        Press(creation.OptionButton(Last(creator.SkinTones, defaults.Skin, o => o.Id))!);
        Press(creation.OptionButton(Last(creator.HairColors, defaults.HairColor, o => o.Id))!);
        var chosen = creator.Look!;
        Check(chosen.Gender != defaults.Gender && chosen.Face != defaults.Face && chosen.Hair != defaults.Hair && chosen.Skin != defaults.Skin && chosen.HairColor != defaults.HairColor,
            $"every part differs from the default ({chosen})");
        await UntilAsync(() => creation.Preview.Settled, 10, "the preview of the choices");
        CheckDrawn(creation.Preview.Visual!, chosen, "the preview of the choices");
        Print($"appearance: chosen {Describe(chosen)}; the preview draws it as one surface");

        // A refusal is shown and loses nothing; so does Back.
        Type("1");
        Press(creation.CreateButton);
        await UntilAsync(() => !accounts.Busy && accounts.Notice == AccountNotice.Refused, 10, "the refusal of a bad name");
        await FramesAsync(2);
        Check(accounts.Error == AccountErrors.InvalidName && creation.Open && creation.Notice == UiText.Account(AccountNotice.Refused, AccountErrors.InvalidName, 0), $"the refusal is shown, was \"{creation.Notice}\"");
        Check(creator.Look!.Equals(chosen), "a refusal keeps the choices");
        Type("Look" + tag);
        Press(creation.BackButton);
        await FramesAsync(3);
        Check(!creation.Open && creation.Preview.Visual is null && !creation.Preview.Rendering, "Back leads to the character list, and the preview is freed and renders nothing");
        Press(screen.NewButton);
        await FramesAsync(3);
        Check(creation.Open && creator.Look!.Equals(chosen) && creation.NameField.Text == "Look" + tag, "Back kept what was chosen");
        await UntilAsync(() => creation.Preview.Settled, 10, "the preview again");
        creation.Preview.Table.Reset();
        await SecondsAsync(0.3);
        await _shot(string.Empty);

        Press(creation.CreateButton);
        await UntilAsync(() => accounts.Characters.Count == 1 && !accounts.Busy && accounts.Screen == AccountScreen.Characters, 10, "the new character");
        await FramesAsync(3);
        Check(chosen.Equals(accounts.Characters[0].Appearance), $"the server kept the choices, was {accounts.Characters[0].Appearance}");
        await UntilAsync(() => screen.Pictures.Count == 1 && screen.Pictures[0].Settled, 10, "the picture in the character list");
        CheckDrawn(screen.Pictures[0].Visual!, chosen, "the picture in the character list");
        Check(!screen.Pictures[0].Rendering, "the picture does not render every frame");
        Check(creator.Look!.Equals(defaults) && creator.Name.Length == 0, "the next new character starts from the defaults");
        Print("appearance: created; the character list shows it");
        await SecondsAsync(0.3);
        await _shot("-list");

        Press(screen.Rows[0].Play);
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && World.Own is not null && _main.LoadedMap == World.MapId, 15, "entering the world");
        await UntilAsync(() => World.Own?.Visual is ModelVisual { Settled: true }, 10, "the own character drawn");
        CheckDrawn((ModelVisual)World.Own!.Visual!, chosen, "the own character in the world");
        Check(screen.Pictures.All(p => p.Visual is null), "the account screens' characters are freed while playing");
        Print($"appearance: in the world as {Describe(World.Own.Appearance!)}");

        Print("@ watch:" + string.Join(",", chosen.Gender, chosen.Skin, chosen.Face, chosen.Hair, chosen.HairColor));
        await UntilAsync(() => OthersInView().Any(), 120, "the second client's character");
        await UntilAsync(() => OthersInView().FirstOrDefault()?.Visual is ModelVisual { Settled: true }, 20, "the second client's character drawn");
        var other = OthersInView().First();
        CheckDrawn((ModelVisual)other.Visual!, defaults, "the second client's character");
        Print($"appearance: the second client's character is drawn as {Describe(other.Appearance!)}");
        await _shot("-world");
        await UntilAsync(() => !OthersInView().Any(), 90, "the second client leaving");
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen && !accounts.Busy, 10, "bye logout");
    }

    /// <summary>The second client's run: <paramref name="looks"/> are the ids it must see someone with, as the first client's marker gave them.</summary>
    public async Task WatcherAsync(string looks)
    {
        var ids = looks.Split(',');
        Check(ids.Length == 5, "five ids to look for");
        var wanted = new AppearanceRecord(ids[0], ids[1], ids[2], ids[3], ids[4]);
        await FramesAsync(3);
        Session.Login(_main.Config.DevName, Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && World.Own is not null && _main.LoadedMap == World.MapId, 15, "entering the world");
        await UntilAsync(() => OthersInView().Any(e => wanted.Equals(e.Appearance)), 20, "the first client's character with its looks");
        var entity = OthersInView().First(e => wanted.Equals(e.Appearance));
        await UntilAsync(() => entity.Visual is ModelVisual { Settled: true }, 20, "the first client's character drawn");
        CheckDrawn((ModelVisual)entity.Visual!, wanted, "the first client's character");
        Print($"appearance: the second client draws {Describe(wanted)}");
        await SecondsAsync(2); // the first client sees this one too
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
    }

    /// <summary>
    /// What looks cost in a crowd (§9): the crowd by its job models as before T-706, by its own looks (a crowd of bots has the defaults), by looks that are all different,
    /// and those in the items given (all dressed the same). Everything in one run on one crowd, so the figures compare.
    /// </summary>
    public async Task MeasureAsync(int wanted, IReadOnlyList<string>? dress)
    {
        var presenter = _main.Presenter!;
        var entities = _main.Entities!;
        OS.LowProcessorUsageMode = false;
        await SecondsAsync(5); // shaders settle
        var options = _main.Bundle!.Appearance!;
        Print($"measure: looks offered: {Variety(options)} ({string.Join(" + ", options.Genders.Select(g => $"{g.Faces.Count} faces x {g.Hairstyles.Count} hairstyles"))}, x {options.SkinTones.Count} skin tones x {options.HairColors.Count} hair colours); cache limit {_main.Config.LookCacheLimit} looks");

        async Task Phase(string what, Action change)
        {
            var built = entities.Merged.Built;
            var main = entities.MergeMainThreadMs;
            var created = entities.Created;
            change();
            var clock = Stopwatch.StartNew();
            double worstChange = 0, last = 0;
            do
            {
                await FramesAsync(1);
                worstChange = Math.Max(worstChange, clock.Elapsed.TotalMilliseconds - last);
                last = clock.Elapsed.TotalMilliseconds;
            }
            while ((entities.Waiting > 0 || entities.Dressing > 0 || clock.Elapsed.TotalSeconds < 0.2) && clock.Elapsed.TotalSeconds < 30);
            var settled = clock.Elapsed.TotalSeconds;
            await SecondsAsync(1.5);

            double sumDraws = 0, maxDraws = 0, maxTriangles = 0, sumOn = 0, sumScreen = 0, worst = 0, sumProcess = 0;
            var frames = 0;
            var maxScreen = 0;
            var screenAtMax = 0;
            clock.Restart();
            last = 0;
            while (clock.Elapsed.TotalSeconds < 10)
            {
                await FramesAsync(1);
                worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds - last);
                last = clock.Elapsed.TotalMilliseconds;
                var draws = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
                sumDraws += draws;
                var screen = _onScreen();
                sumScreen += screen;
                maxScreen = Math.Max(maxScreen, screen);
                if (draws > maxDraws) (maxDraws, screenAtMax) = (draws, screen);
                maxTriangles = Math.Max(maxTriangles, Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame));
                sumProcess += Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
                sumOn += World.Entities.Count(Players);
                frames++;
            }
            var visuals = World.Entities.Where(Players).Select(e => e.Visual).OfType<ModelVisual>().ToList();
            var different = World.Entities.Where(Players).Select(e => e.Visual).OfType<ModelVisual>().Where(v => v.Character is not null).Select(v => v.Character!.Ids).Distinct().Count();
            Print($"measure: {what}: {sumOn / frames:0.0} players in view, {sumScreen / frames:0.0} entities on screen (at most {maxScreen}), {different} different looks, surfaces per player at most {visuals.Select(v => v.Surfaces).DefaultIfEmpty().Max()}");
            Print($"measure: {what}: draw calls avg {sumDraws / frames:0.0}, max {maxDraws:0} (with {screenAtMax} on screen); triangles max {maxTriangles:0}; {frames / clock.Elapsed.TotalSeconds:0.0} fps, worst frame {worst:0.0} ms, process {sumProcess / frames:0.00} ms a frame");
            Print($"measure: {what}: change settled in {settled:0.00} s (worst frame {worstChange:0.0} ms, {entities.Created - created} instances and dressings, {entities.Merged.Built - built} merges, {entities.MergeMainThreadMs - main:0.0} ms of them on the main thread); " +
                  $"{entities.Merged.Cached} merged characters cached; static memory {OS.GetStaticMemoryUsage() / 1048576.0:0.0} MB, video {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:0.0} MB");
        }

        await Phase("before (job models)", () =>
        {
            _main.Config.DrawAppearance = false;
            presenter.Redraw();
        });
        await Phase("own looks (all default)", () =>
        {
            _main.Config.DrawAppearance = true;
            presenter.Redraw();
        });
        await Phase("all different", () =>
        {
            presenter.LooksOverride = e => Nth(options, e.Id);
            presenter.Redraw();
        });
        if (dress is { Count: > 0 })
            await Phase("all different, dressed", () => presenter.DressOverride = dress);
        // T-708: the same crowd, also in every outfit slot the bundle's outfits have for each one's gender. They replace the gear of those slots in the same one merge.
        var outfits = _main.Bundle!.Outfits;
        if (outfits.Count > 0)
            await Phase(dress is { Count: > 0 } ? "all different, dressed, in outfits" : "all different, in outfits", () =>
            {
                var byGender = options.Genders.ToDictionary(g => g.Gender, g => (IReadOnlyList<ShownOutfit>)OutfitSlots.All.Ordered
                    .Select(slot => outfits.FirstOrDefault(o => Game.Client.Core.Outfits.OutfitPieces.Find(o, slot, g.Gender) is not null) is { } o ? new ShownOutfit(slot, o.Id) : (ShownOutfit?)null)
                    .Where(s => s is not null).Select(s => s!.Value).ToList());
                presenter.OutfitOverride = e => byGender[Nth(options, e.Id).Gender];
                presenter.Redraw();
            });
        Print($"measure: {entities.LookKinds} looks remembered; {_main.Look?.Materials ?? 0} toon materials; adapter {RenderingServer.GetVideoAdapterName()}");
        await _shot(string.Empty);
    }

    private static bool Players(EntityView e) => e.Kind == EntityKinds.Player;

    private static int Variety(ClientAppearance a) => a.Genders.Sum(g => g.Faces.Count * g.Hairstyles.Count) * a.SkinTones.Count * a.HairColors.Count;

    /// <summary>A look by number: neighbouring numbers differ in every part they can.</summary>
    private static AppearanceRecord Nth(ClientAppearance a, long id)
    {
        var n = (int)(id % 100_000);
        var gender = a.Genders[n % a.Genders.Count];
        var m = n / a.Genders.Count;
        return new AppearanceRecord(gender.Gender, a.SkinTones[m % a.SkinTones.Count].Id, gender.Faces[m / 2 % gender.Faces.Count].Id, gender.Hairstyles[m % gender.Hairstyles.Count].Id,
            a.HairColors[(m + m / a.SkinTones.Count) % a.HairColors.Count].Id);
    }

    private IEnumerable<EntityView> OthersInView() => World.Entities.Where(e => e.Kind == EntityKinds.Player && e.Id != World.OwnId);

    /// <summary>Checks that a visual is drawn with these ids: the gender's body, one surface, the face and hairstyle of the ids (unless hidden), and the colours baked in.</summary>
    private void CheckDrawn(ModelVisual visual, AppearanceRecord looks, string what)
    {
        Check(visual.Character is not null, $"{what} is drawn by its looks");
        var look = visual.Character!;
        Check(look.Ids.Equals(looks), $"{what} is drawn as {Describe(looks)}, was {Describe(look.Ids)}");
        var appearance = _main.Bundle!.Appearance!;
        var gender = appearance.Genders.First(g => g.Gender == looks.Gender);
        Check(look.Body.Key == gender.BodyModelAssetKey && visual.PoolKey == look.Body.PoolKey, $"{what} stands on its gender's body");
        Check(visual.Dressed.Any(p => p.Role == PartRole.Face && p.Piece.VisualKey == gender.Faces.First(f => f.Id == looks.Face).ModelAssetKey), $"{what} has its face");
        var hidden = visual.Dressed.Any(p => p.Piece.HidesHair);
        Check(hidden || visual.Dressed.Any(p => p.Role == PartRole.Hair && p.Piece.VisualKey == gender.Hairstyles.First(h => h.Id == looks.Hair).ModelAssetKey), $"{what} has its hairstyle");
        Check(visual.Surfaces == 1, $"{what} is one surface (one draw call), was {visual.Surfaces}");
        var drawn = visual.DrawnColors();
        foreach (var (name, hex) in new[] { ("skin", appearance.SkinTones.First(c => c.Id == looks.Skin).Color), ("hair", appearance.HairColors.First(c => c.Id == looks.HairColor).Color) })
        {
            var want = new Color(hex).SrgbToLinear();
            Check(drawn.Any(c => Math.Abs(c.R - want.R) < 0.006f && Math.Abs(c.G - want.G) < 0.006f && Math.Abs(c.B - want.B) < 0.006f), $"{what} has its {name} colour {hex} baked into the mesh");
        }
    }

    /// <summary>A finger drags across the preview: it turns by that and stops turning by itself while it is held.</summary>
    private async Task DragAsync(CreationPanel creation)
    {
        var frame = creation.Preview!.Frame;
        var table = creation.Preview.Table;
        var half = frame.Size.X / 2;
        void Mouse(InputEvent e) => frame.EmitSignal(Control.SignalName.GuiInput, e);
        Mouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        var before = table.Degrees;
        Mouse(new InputEventMouseMotion { Relative = new Vector2(half, 0), ButtonMask = MouseButtonMask.Left });
        await FramesAsync(5);
        var turned = (table.Degrees - before + 360) % 360;
        Check(Math.Abs(turned - (_main.Config.PreviewDragDegrees / 2 + 360) % 360) < 0.5, $"a drag across half the preview turns it {_main.Config.PreviewDragDegrees / 2:0} degrees and holds it there, turned {turned:0.0}");
        Mouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        await SecondsAsync(0.3);
        Check(Math.Abs(table.Degrees - before - turned) > 0.5, "and it turns by itself again once the finger lifts");
        Print($"appearance: the preview turns by itself and by drag ({turned:0} degrees for half its width)");
    }

    private static string Last<T>(IReadOnlyList<T> options, string not, Func<T, string> idOf) => idOf(options.Last(o => idOf(o) != not));

    private static string Describe(AppearanceRecord a) => $"{a.Gender}, {a.Skin}, {a.Face}, {a.Hair}, {a.HairColor}";

    private static void Fill(LineEdit field, string value) => field.Text = value;

    private void Type(string name)
    {
        var field = _main.Hud!.Accounts!.Creation!.NameField;
        Fill(field, name);
        field.EmitSignal(LineEdit.SignalName.TextChanged, name);
    }

    private void Press(Button button)
    {
        Check(button.IsVisibleInTree() && !button.Disabled, $"the button \"{button.Text}\" can be pressed");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

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
            if (clock.Elapsed.TotalSeconds > seconds)
                throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}, notice {Session.Connection.Notice}, account {Session.Accounts.Notice} {Session.Accounts.Error}");
            await FramesAsync(1);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
