using System.Diagnostics;
using Game.Client.Core;
using Game.Client.Core.Accounts;
using Game.Client.Core.Net;
using Game.Client.Core.Theme;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted runs for outfits (T-708). <c>--e2e-wardrobe</c>, against a server with a database and the wardrobe content copy (the first quest's turn-in is a talk): register, make a
/// character, earn the first quest's outfits, open the wardrobe from the bag and put one on, see it in the preview; a second client (<c>--e2e-wardrobe-watch</c>, started by
/// <c>run-e2e.ps1</c>) logs in beside it and sees it drawn; log out, come back, it is still on; take it off. Which outfit and slot come from the bundle, never from code.
/// </summary>
public sealed class WardrobeRuns
{
    private readonly Main _main;
    private readonly Func<string, Task> _shot;

    /// <summary>Prepares the runs. <paramref name="shot"/> saves the screenshot when one is asked for.</summary>
    public WardrobeRuns(Main main, Func<string, Task> shot)
    {
        _main = main;
        _shot = shot;
    }

    private ClientSession Session => _main.Session!;

    private WorldView World => Session.World;

    /// <summary>The first client's run.</summary>
    public async Task WearerAsync()
    {
        var hud = _main.Hud!;
        var accounts = Session.Accounts;
        var screen = hud.Accounts!;
        await FramesAsync(3);
        var tag = new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));
        screen.Username.Text = "e2e-" + tag;
        screen.Password.Text = Guid.NewGuid().ToString("N"); // thrown away with the run
        var password = screen.Password.Text;
        Press(screen.RegisterButton);
        await UntilAsync(() => accounts.Screen == AccountScreen.Characters && !accounts.Busy, 15, "the character list after registering");
        await LookRuns.CreateAsync(_main, "Dress" + tag);
        await EnterAsync(screen);
        Check(Session.Wardrobe.Known && Session.Wardrobe.Owned.Count == 0, "a new character owns no outfit");

        // Earn them: the quest giver's first quest gives the template's outfits.
        var giver = World.Entities.First(e => e.Kind == EntityKinds.Npc && Session.Quests.MarkerFor(e.DisplayKey) == QuestMarkers.Offer);
        await TalkAsync(giver);
        Press(hud.Dialogue!.ChoiceButtons[0].Button);   // the offer
        await UntilAsync(() => Session.Dialogue.Choices.Count > 0, 10, "the quest's offer");
        await FramesAsync(2);
        Press(hud.Dialogue.ChoiceButtons[0].Button);   // accepted
        await UntilAsync(() => Session.Quests.Active.Count == 1, 10, "the quest accepted");
        Press(hud.Dialogue.CloseButton);
        await UntilAsync(() => !Session.Dialogue.Open, 10, "the dialogue's end");
        await TalkAsync(giver);   // the turn-in
        await UntilAsync(() => Session.Wardrobe.Owned.Count > 0, 15, "the outfits of the quest");
        if (Session.Dialogue.Open) Press(hud.Dialogue.CloseButton);
        await UntilAsync(() => !Session.Dialogue.Open, 10, "the dialogue's end");
        Print($"wardrobe: earned {string.Join(", ", Session.Wardrobe.Owned)}; the flash said \"{Session.Ui.Flash}\"");

        // Put one on, through the bag and the wardrobe panel.
        var gender = World.Own!.Appearance!.Gender;
        var row = Session.Wardrobe.Rows(gender).Last();
        var outfit = row.Outfits.Last().Id;
        Press(hud.BagButton);
        await FramesAsync(3);
        Press(hud.Bag!.OutfitsButton);
        await FramesAsync(3);
        var panel = hud.Wardrobe!;
        Check(panel.Open && !hud.Bag.Open, "the Outfits button opens the wardrobe in the bag's place");
        Press(panel.OptionButton(row.Slot, outfit)!);
        await UntilAsync(() => Session.Wardrobe.Chosen(row.Slot) == outfit && World.Own!.Outfit.Any(s => s.Slot == row.Slot && s.Outfit == outfit), 10, "the outfit on");
        await UntilAsync(() => panel.Preview?.Settled != false && Drawn(panel.Preview?.Visual, outfit), 15, "the preview in the outfit");
        await UntilAsync(() => World.Own!.Visual is ModelVisual { Settled: true } own && Drawn(own, outfit), 15, "the own character in the outfit");
        Check(((ModelVisual)World.Own!.Visual!).Surfaces == 1, "still one surface (one draw call)");
        Check(panel.OptionButton(row.Slot, outfit)!.Text == UiText.Chosen(_main.Bundle!.NameOf(outfit)), "the chosen outfit is marked in words");
        var drawn = panel.Drawn;
        await SecondsAsync(1);
        Check(panel.Drawn == drawn, "nothing is redrawn while nothing changes");
        Check(panel.Preview is null || panel.Preview.Rendering, "the open preview renders");
        Print($"wardrobe: {outfit} shows in '{row.Slot}'; the preview and the world draw it as one surface");
        panel.Preview?.Table.Reset();
        await SecondsAsync(0.3);
        await _shot(string.Empty);
        Press(panel.CloseButton);
        await FramesAsync(3);
        Check(!panel.Open && (panel.Preview is null || !panel.Preview.Rendering), "closed, the preview renders nothing");

        // Another player sees it.
        Print("@ watch-outfit:" + row.Slot + "," + outfit);
        await UntilAsync(() => Others().Any(), 120, "the second client's character");
        await UntilAsync(() => !Others().Any(), 90, "the second client leaving");

        // Out and back in: it is still on. Then off.
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen && !accounts.Busy && accounts.Screen == AccountScreen.Characters, 15, "back at the character list");
        await FramesAsync(3);
        await EnterAsync(screen);
        await UntilAsync(() => World.Own!.Outfit.Any(s => s.Slot == row.Slot && s.Outfit == outfit), 10, "the outfit after signing in again");
        await UntilAsync(() => World.Own!.Visual is ModelVisual { Settled: true } again && Drawn(again, outfit), 15, "the own character in the outfit again");
        Check(Session.Wardrobe.Chosen(row.Slot) == outfit, "the wardrobe says so too");
        Print("wardrobe: after logging out and in again it is still on");
        Press(hud.BagButton);
        await FramesAsync(3);
        Press(hud.Bag.OutfitsButton);
        await FramesAsync(3);
        Press(panel.OptionButton(row.Slot, null)!);
        await UntilAsync(() => Session.Wardrobe.Chosen(row.Slot) is null && World.Own!.Outfit.Count == 0, 10, "the outfit off");
        await UntilAsync(() => World.Own!.Visual is ModelVisual { Settled: true } off && !Drawn(off, outfit), 15, "the own character in its gear");
        Press(panel.CloseButton);
        Print("wardrobe: taken off");
        _ = password;
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen && !accounts.Busy, 10, "bye logout");
    }

    /// <summary>The second client's run: <paramref name="wanted"/> is the slot and outfit it must see someone wear, as the first client's marker gave them.</summary>
    public async Task WatcherAsync(string wanted)
    {
        var parts = wanted.Split(',');
        Check(parts.Length == 2, "a slot and an outfit to look for");
        await FramesAsync(3);
        Session.Login(_main.Config.DevName, Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && World.Own is not null && _main.LoadedMap == World.MapId, 15, "entering the world");
        await UntilAsync(() => Others().Any(e => e.Outfit.Any(s => s.Slot == parts[0] && s.Outfit == parts[1])), 20, "the first client's character in its outfit");
        var entity = Others().First(e => e.Outfit.Any(s => s.Slot == parts[0] && s.Outfit == parts[1]));
        await UntilAsync(() => entity.Visual is ModelVisual { Settled: true } seen && Drawn(seen, parts[1]), 20, "the outfit drawn");
        Check(((ModelVisual)entity.Visual!).Surfaces == 1, "one surface");
        Print($"wardrobe: the second client draws {parts[1]} in '{parts[0]}'");
        await SecondsAsync(1);
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
    }

    /// <summary>True when a piece of <paramref name="outfit"/> is merged into the visual.</summary>
    private bool Drawn(ModelVisual? visual, string outfit)
    {
        if (visual is null || _main.Bundle!.Outfit(outfit) is not { } def) return false;
        var keys = def.Pieces.Select(p => p.ModelAssetKey).ToHashSet();
        return visual.Dressed.Any(p => p.Role == PartRole.Outfit && keys.Contains(p.Piece.VisualKey));
    }

    private async Task EnterAsync(AccountPanel screen)
    {
        await UntilAsync(() => screen.Rows.Count > 0 && screen.Rows[0].Play.IsVisibleInTree(), 10, "the character in the list");
        Press(screen.Rows[0].Play);
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && World.Own is not null && _main.LoadedMap == World.MapId && Session.Wardrobe.Known && Session.Quests.Known, 15, "entering the world");
        await UntilAsync(() => World.Own!.Visual is ModelVisual { Settled: true }, 15, "the own character drawn");
    }

    private async Task TalkAsync(EntityView npc)
    {
        var before = Session.Dialogue.Text;
        Session.TapNpc(npc.Id);   // what a tap on the NPC sends (the play run taps through the input path)
        await UntilAsync(() => Session.Dialogue.Open && Session.Dialogue.Text != before, 20, "the dialogue");
        await FramesAsync(2);
    }

    private IEnumerable<EntityView> Others() => World.Entities.Where(e => e.Kind == EntityKinds.Player && e.Id != World.OwnId);

    private static void Press(Button button)
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
