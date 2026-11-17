using System.Diagnostics;
using System.Globalization;
using Game.Client.Core;
using Game.Client.Core.Audio;
using Game.Client.Core.Bundle;
using Game.Client.Core.Combat;
using Game.Client.Core.Net;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted runs (docs/MOBILE_BUILD.md): <c>--e2e</c> plays login → map → walk → kill → drop → dropped socket → resume → warp → logout against a real
/// server, tapping through the same input path as a finger; <c>--e2e-hill</c> taps a cell on the hill and checks the server walks there; <c>--e2e-death</c> gets killed, drops the socket while dead and checks the death screen
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
        Array.IndexOf(args, "--e2e") >= 0 || Array.IndexOf(args, "--e2e-death") >= 0 || Array.IndexOf(args, "--e2e-warp") >= 0 || Array.IndexOf(args, "--e2e-hill") >= 0 ||
        Array.IndexOf(args, "--e2e-bye") >= 0 || Array.IndexOf(args, "--e2e-ui") >= 0 || Array.IndexOf(args, "--e2e-items") >= 0 || Array.IndexOf(args, "--measure") >= 0;

    /// <summary>For a refused bundle: save a picture of the refusal screen if asked, then quit.</summary>
    public static async void QuitAfterShot(Main main, string[] args, string what = "the bundle")
    {
        for (var i = 0; i < 10; i++) await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
        var at = Array.IndexOf(args, "--screenshot");
        if (at >= 0 && at + 1 < args.Length && DisplayServer.GetName() != "headless")
        {
            OS.LowProcessorUsageMode = false;
            await main.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            main.GetViewport().GetTexture().GetImage().SavePng(args[at + 1]);
        }
        Print($"refused: the client stopped at the refusal screen for {what}, as it must");
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
            else if (Array.IndexOf(_args, "--e2e-warp") >= 0) await WarpRunAsync();
            else if (Array.IndexOf(_args, "--e2e-hill") >= 0) await HillRunAsync();
            else if (Array.IndexOf(_args, "--e2e-items") >= 0) await ItemsRunAsync();
            else if (Array.IndexOf(_args, "--e2e-ui") >= 0) await new UiRuns(_main, _screenshot).RunAsync();
            else if (Value("--e2e-bye") is { } bye) await new ByeRuns(_main).RunAsync(bye);
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
        CheckAudioAfterFight();
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

        // Real content puts aggressive monsters between the start and the warp that a new character cannot pass; run-e2e.ps1 then runs the warp as its own run.
        if (Array.IndexOf(_args, "--e2e-skip-warp") >= 0) Print("warp: skipped here (its own run: -Run warp)");
        else await WarpAsync();

        Print("logout: sending logout");
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        Check(Session.Connection.Notice == ConnectionNotice.LoggedOut, "the bye was logout");
        Check(!Session.Connection.HasToken, "the token is gone");
        Print($"logout: bye logout, login screen shows \"{_main.Hud!.PanelText}\"");
    }

    /// <summary>
    /// T-401's run (on a content copy where every kill drops a weapon and a potion, and new characters start with nothing): kill, tap the drop to pick it up,
    /// open the bag and equip the weapon through its buttons, see the hits get stronger, and use a potion. Every step goes through the HUD and the input path.
    /// </summary>
    private async Task ItemsRunAsync()
    {
        await LoginAsync();
        await UntilAsync(() => Session.Inventory.Known, 10, "the own inventory after the snapshot");
        Print($"items: inventory rev {Session.Inventory.Rev}, {Session.Inventory.Entries.Count} entries, weight {Session.Inventory.Weight}/{Session.Inventory.WeightLimit}, " +
              $"own weapon {Own().Weapon}");
        Check(Own().Weapon == Game.Shared.Vocabulary.WeaponTypes.Unarmed, "the own record says unarmed");
        await FramesAsync(2);
        Check(Own().Visual is ModelVisual { PieceCount: 0 }, "nothing worn on the own character yet");
        var hits = new List<int>();
        var kinds = new List<(string Kind, int Amount)>();   // T-402: hits by monster kind, so the refine check compares like with like
        World.Combat.Impact += (in HitImpact impact) =>
        {
            if (impact.Attacker != World.OwnId || impact.Kind != HitKind.Hit) return;
            hits.Add(impact.Amount);
            kinds.Add((World.Find(impact.Target)?.DisplayKey ?? "?", impact.Amount));
        };
        var acquired = 0;
        var used = 0;
        var equipped = 0;
        var socketed = 0;
        Session.InventoryChanged += change =>
        {
            if (change.Cause == Game.Shared.Vocabulary.InventoryChanges.PickedUp) acquired++;
            if (change.Cause == Game.Shared.Vocabulary.InventoryChanges.Used) used++;
            if (change.Cause == Game.Shared.Vocabulary.InventoryChanges.Equipped) equipped++;
            if (change.Cause == Game.Shared.Vocabulary.InventoryChanges.CardInserted) socketed++;
        };
        var refines = new List<RefineResultMessage>();
        Session.RefineResultReceived += refines.Add;

        var open = NearestOpenCellOutsideSafeZones(CurrentMap(), Own().Cell);
        await TapCellAsync(open);
        await UntilAsync(() => !Own().IsWalking && Own().Cell.X == open.X && Own().Cell.Y == open.Y, 30, "leaving the safe zone");

        // Kill until a weapon lies on the ground, then pick up everything this client may take (a tap on each loot bag).
        string? weapon = null;
        for (var attempt = 0; attempt < 10 && weapon is null; attempt++)
        {
            await HuntOneAsync(90);
            await SecondsAsync(1);
            for (var pick = 0; pick < 6; pick++)
            {
                var drop = FindDrop();
                if (drop is null) break;
                var name = _main.Bundle!.NameOf(drop.DisplayKey);
                var before = Session.Inventory.Rev;
                Print($"items: tap {name} x{drop.Quantity} at {drop.Cell} (own {Own().Cell})");
                if (!OnScreen(drop))
                {
                    await TapCellAsync(drop.Cell);
                    await SecondsAsync(1);
                }
                var taken = false;
                for (var tap = 0; tap < 3 && !taken; tap++)
                {
                    await TapWorldAsync(new Vector3((float)drop.X, 0.15f, (float)drop.Z));
                    await FramesAsync(2);
                    Print($"items: after the tap: target {Session.Ui.Target?.ToString() ?? "none"}, marker {Session.Ui.Marker?.ToString() ?? "none"}, flash \"{Session.Ui.Flash}\"");
                    var clock = Stopwatch.StartNew();
                    while (clock.Elapsed.TotalSeconds < 8 && Session.Inventory.Rev == before && World.Find(drop.Id) is not null) await FrameAsync();
                    taken = Session.Inventory.Rev > before || World.Find(drop.Id) is null;
                }
                Check(taken, $"picking up {name}");
                await FramesAsync(2);
            }
            weapon = Session.Inventory.Entries.FirstOrDefault(e => e.Slot is null && _main.Bundle!.Item(e.Item)?.WeaponType is not null)?.Item;
        }
        Check(weapon is not null, "picked up a weapon");
        Check(acquired > 0, "the pick-up came back as inventory_changed picked_up");
        Print($"items: carrying {string.Join(", ", Session.Inventory.Entries.Select(e => e.Item + " x" + e.Quantity))}; flash \"{Session.Ui.Flash}\"");
        var unarmed = hits.ToList();

        // Open the bag with its button, select the weapon, press Equip.
        var hud = _main.Hud!;
        Press(hud.BagButton);
        await FramesAsync(3);
        Check(hud.Bag!.Open, "the bag opens");
        var weaponButton = hud.Bag.ItemButtons.First(b => Session.Inventory.Find(b.Entry)?.Item == weapon);
        Press(weaponButton.Button);
        await FramesAsync(2);
        Check(hud.Bag.Actions.Equip.Visible, "a selected weapon offers Equip");
        await ShotAsync("-bag");
        Press(hud.Bag.Actions.Equip);
        await UntilAsync(() => Session.Inventory.InSlot(Game.Shared.Vocabulary.EquipSlots.Weapon) is not null, 10, "the weapon in its slot");
        await UntilAsync(() => Own().Weapon != Game.Shared.Vocabulary.WeaponTypes.Unarmed, 10, "the own record shows the weapon");
        Check(equipped == 1, "one equipped change");
        Print($"items: wearing {weapon} ({Own().Weapon}); slot button \"{hud.Bag.SlotButtons[Game.Shared.Vocabulary.EquipSlots.Weapon].Text}\"");
        Press(hud.BagButton);
        await FramesAsync(2);
        // T-403: the look change re-dresses the own character: the weapon is on it, and its type picks the attack clip.
        await UntilAsync(() => Own().Visual is ModelVisual { PieceCount: > 0 } && _main.Entities!.Dressing == 0, 15, "the weapon on the own character");
        var dressed = (ModelVisual)Own().Visual!;
        Print($"items: own character wears {dressed.PieceCount} piece(s) after look {Own().LookVersion}; attack clip {dressed.ClipName(Game.Client.Core.Animation.AnimClip.Attack)}, " +
              $"idle clip {dressed.ClipName(Game.Client.Core.Animation.AnimClip.Idle)}; merged meshes built {_main.Entities!.Merged.Built}");
        await ShotAsync("-equipped");

        // Fight again: the same monsters take more damage per hit now.
        hits.Clear();
        kinds.Clear();   // from here on: hits with the weapon at +0, which the refine step compares against
        for (var attempt = 0; attempt < 4 && hits.Count < 3; attempt++) await HuntOneAsync(60);
        Check(unarmed.Count > 0 && hits.Count > 0, $"hits before ({unarmed.Count}) and after ({hits.Count}) equipping");
        var before2 = unarmed.Average();
        var after = hits.Average();
        Print($"items: average hit unarmed {before2:0.0} ({unarmed.Count} hits), with {weapon} {after:0.0} ({hits.Count} hits)");
        Check(after > before2, "the weapon makes the hits stronger");

        // Use a potion through the bag.
        var potion = Session.Inventory.Entries.FirstOrDefault(e => _main.Bundle!.Item(e.Item)?.Kind == Game.Shared.Vocabulary.ItemKinds.Consumable);
        if (potion is null)
        {
            for (var attempt = 0; attempt < 6 && potion is null; attempt++)
            {
                await HuntOneAsync(60);
                await SecondsAsync(1);
                if (FindDrop() is { } drop)
                {
                    await TapWorldAsync(new Vector3((float)drop.X, 0.15f, (float)drop.Z));
                    await SecondsAsync(3);
                }
                potion = Session.Inventory.Entries.FirstOrDefault(e => _main.Bundle!.Item(e.Item)?.Kind == Game.Shared.Vocabulary.ItemKinds.Consumable);
            }
        }
        Check(potion is not null, "carrying a potion");
        var quantity = potion!.Quantity;
        var hp = World.Vitals?.Hp;
        Press(hud.BagButton);
        await FramesAsync(3);
        Press(hud.Bag.ItemButtons.First(b => b.Entry == potion.Id).Button);
        await FramesAsync(2);
        Check(hud.Bag.Actions.Use.Visible, "a selected potion offers Use");
        Press(hud.Bag.Actions.Use);
        await UntilAsync(() => used > 0, 10, "the potion used");
        Print($"items: used {potion.Item}: {quantity} -> {Session.Inventory.Find(potion.Id)?.Quantity ?? 0}, HP {hp} -> {World.Vitals?.Hp}/{World.Vitals?.MaxHp}");
        Check((Session.Inventory.Find(potion.Id)?.Quantity ?? 0) == quantity - 1, "one potion used up");
        Press(hud.BagButton);
        await FramesAsync(2);
        await CardAndRefineAsync(hud, kinds, () => socketed, refines);

        var stats = _main.Audio!.Director.Stats;
        Print($"items: audio played {stats.Played}; rev {Session.Inventory.Rev}, stale {Session.Inventory.Stale}");
        Check(!Session.Inventory.Stale, "no change was missed");
    }

    /// <summary>
    /// T-402's step of the items run (the content copy's kills also drop armour with a card slot, a card for it and the refine material): put the armour on,
    /// socket the card into it through the bag (Set card, then the armour), see max HP go up; select the worn weapon by its slot, press Refine, read the offer
    /// from the bundle, confirm, see the success and +1 in the bag, and see the hits on the same kind of monster get stronger. Every step goes through the HUD.
    /// No content id is named here: the card is whatever card is carried, the armour whatever it is offered for, the material the bundle's first refine step.
    /// </summary>
    private async Task CardAndRefineAsync(Hud hud, List<(string Kind, int Amount)> kinds, Func<int> socketed, List<RefineResultMessage> refines)
    {
        var bundle = _main.Bundle!;
        var dirk = Session.Inventory.InSlot(EquipSlots.Weapon)!;
        Check(bundle.RefineStepTo(dirk.Refine + 1) is not null, "the bundle has a refine step for the weapon");
        var shardId = bundle.RefineStepTo(dirk.Refine + 1)!.Material;
        InventoryEntryRecord? Card() => Session.Inventory.Entries.FirstOrDefault(e => bundle.Item(e.Item)?.Kind == ItemKinds.Card);
        InventoryEntryRecord? Armour() => Card() is { } c
            ? Session.Inventory.CardTargets(bundle.Item(c.Item), bundle.Item).FirstOrDefault(e => e.Slot is null && bundle.Item(e.Item)?.WeaponType is null)
            : null;
        bool Ready() => Armour() is not null && Session.Inventory.Carried(shardId) >= bundle.RefineStepTo(dirk.Refine + 1)!.Quantity;
        for (var attempt = 0; attempt < 6 && !Ready(); attempt++)
        {
            await HuntOneAsync(60);
            await SecondsAsync(1);
            for (var pick = 0; pick < 6 && FindDrop() is { } drop; pick++)
            {
                var rev = Session.Inventory.Rev;
                if (!OnScreen(drop))
                {
                    await TapCellAsync(drop.Cell);
                    await SecondsAsync(1);
                }
                await TapWorldAsync(new Vector3((float)drop.X, 0.15f, (float)drop.Z));
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < 8 && Session.Inventory.Rev == rev && World.Find(drop.Id) is not null) await FrameAsync();
            }
        }
        Check(Ready(), "carrying armour, a card that fits it and the refine material");
        var bag = hud.Bag!;
        var card = Card()!;
        var vest = Armour()!;
        var vestId = vest.Item;
        var cardId = card.Item;
        Print($"items: card {cardId}, armour {vestId}, material {shardId} x{Session.Inventory.Carried(shardId)}");

        // Wear the armour (its DEF is not on the wire; the card's max HP is).
        Press(hud.BagButton);
        await FramesAsync(3);
        Press(bag.ItemButtons.First(b => b.Entry == vest.Id).Button);
        await FramesAsync(2);
        Press(bag.Actions.Equip);
        await UntilAsync(() => Session.Inventory.Find(vest.Id)?.Slot is not null, 10, "the armour on");
        var vestSlot = Session.Inventory.Find(vest.Id)!.Slot!;
        await SecondsAsync(0.5);
        var maxHp = World.Vitals!.MaxHp;

        // Socket the card: select it, Set card, the worn armour is offered, tap it.
        Press(bag.ItemButtons.First(b => b.Entry == card.Id).Button);
        await FramesAsync(2);
        Check(bag.CardAndRefine.InsertCard.Visible, "a selected card offers Set card");
        Press(bag.CardAndRefine.InsertCard);
        await FramesAsync(2);
        var targets = bag.CardTargetButtons.Where(b => b.Button.Visible).ToList();
        Print($"items: the card is offered for {string.Join(", ", targets.Select(t => "\"" + t.Button.Text + "\""))}; hint \"{bag.HintText}\"");
        Check(targets.Any(t => t.Entry == vest.Id), "the worn armour is offered for the card");
        Check(targets.All(t => t.Entry != dirk.Id), "the weapon is not (the card does not fit it)");
        Press(targets.First(t => t.Entry == vest.Id).Button);
        await UntilAsync(() => socketed() == 1, 10, "the card socketed");
        Check(Session.Inventory.Find(vest.Id)!.Cards?.Contains(cardId) == true, "the armour holds the card");
        Check(Session.Inventory.Find(card.Id) is null, "the card unit is used up");
        await UntilAsync(() => World.Vitals!.MaxHp > maxHp, 10, "max HP up from the card");
        Print($"items: socketed {cardId} into {vestId}: max HP {maxHp} -> {World.Vitals!.MaxHp}; flash \"{Session.Ui.Flash}\"; {vestSlot} slot \"{bag.SlotButtons[vestSlot].Text}\"");

        // Refine the worn weapon: tap its slot, Refine shows the bundle's next step, confirm.
        var before = kinds.ToList();
        Press(bag.SlotButtons[EquipSlots.Weapon]);
        await FramesAsync(2);
        Check(Session.Inventory.Selected == dirk.Id, "a tap on the weapon slot selects the weapon");
        Check(bag.Actions.Unequip.Visible && bag.CardAndRefine.Refine.Visible, "and offers Take off and Refine");
        Press(bag.CardAndRefine.Refine);
        await FramesAsync(2);
        var step = bundle.RefineStepTo(dirk.Refine + 1)!;
        Print($"items: refine offer \"{bag.HintText}\"");
        Check(bag.HintText.Contains(step.SuccessPercent + "%") && bag.HintText.Contains(bundle.NameOf(step.Material)), "the offer shows the bundle's chance and material");
        Check(bag.CardAndRefine.Confirm.Visible && !bag.Actions.Unequip.Visible, "the offer waits for a confirmation");
        Press(bag.CardAndRefine.Confirm);
        await UntilAsync(() => refines.Count == 1, 10, "the refine result");
        var result = refines[0];
        Print($"items: refine result {result.Result} +{result.Refine}; flash \"{Session.Ui.Flash}\"; weapon slot \"{bag.SlotButtons[EquipSlots.Weapon].Text}\"");
        Check(step.SuccessPercent < 100 || result.Result == RefineResults.Success, "a 100% step succeeds");
        Check(result.Result == RefineResults.Success && result.Refine == dirk.Refine + 1, "the refine succeeded (the run needs refine.yaml's first step at 100%)");
        await UntilAsync(() => Session.Inventory.Find(dirk.Id)?.Refine == dirk.Refine + 1, 10, "the bag shows the new level");
        await FramesAsync(3);
        Check(bag.SlotButtons[EquipSlots.Weapon].Text.Contains("+" + (dirk.Refine + 1)), "the weapon slot shows the new level");
        Check(bag.SlotButtons[vestSlot].Text.Contains(bundle.NameOf(cardId)), "the armour's slot shows the card");
        await ShotAsync("-bag-refined");
        Press(hud.BagButton);
        await FramesAsync(2);

        // The same kind of monster now takes more per hit (refine reaches physical_damage as refine * 2).
        var seen = before.Select(h => h.Kind).ToHashSet();
        kinds.Clear();
        for (var attempt = 0; attempt < 6 && !kinds.Any(h => seen.Contains(h.Kind)); attempt++) await HuntOneAsync(60);
        var kind = kinds.Select(h => h.Kind).FirstOrDefault(seen.Contains);
        Check(kind is not null, "hits on a kind of monster hit before the refine");
        var then = before.Where(h => h.Kind == kind).Average(h => h.Amount);
        var now = kinds.Where(h => h.Kind == kind).Average(h => h.Amount);
        Print($"items: average hit on {kind} with +{dirk.Refine} {then:0.0}, with +{dirk.Refine + 1} {now:0.0}");
        Check(now > then, "the refined weapon hits harder");
    }

    private static void Press(Button button) => button.EmitSignal(BaseButton.SignalName.Pressed);

    private async Task WarpRunAsync()
    {
        await LoginAsync();
        await WarpAsync();
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        Print("logout: bye logout");
    }

    /// <summary>
    /// A tap on the map's hill (T-220, ADR-002 row 148): walk to flat ground beside the highest cells, tap a raised cell on screen through the same input
    /// path as a finger, and check that the server moved the character to exactly that cell. The cell is chosen so that the old y = 0 plane pick would
    /// have chosen a different one, so the run fails if picking stops using the terrain heights.
    /// </summary>
    private async Task HillRunAsync()
    {
        await LoginAsync();
        var ground = _main.Map!.Ground;
        await UntilAsync(() => ground.MaxHeight > MinHillHeight, 10, "the map's ground heights");
        var map = CurrentMap();
        Print($"hill: ground heights {ground.MinHeight:0.00} to {ground.MaxHeight:0.00}");

        // The highest walkable cells, and the nearest flat cell beside them to stand on (the hill may start off screen).
        GridCell? peak = null;
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
                if (map.IsWalkable(x, y) && ground.At(x + 0.5, y + 0.5) >= ground.MaxHeight - 0.01f && (peak is null || GridDistanceSquared(new GridCell(x, y), Own().Cell) < GridDistanceSquared(peak.Value, Own().Cell)))
                    peak = new GridCell(x, y);
        Check(peak is not null, "the map has a hill");
        GridCell? stand = null;
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                var c = new GridCell(x, y);
                var d = GridDistanceSquared(c, peak!.Value);
                if (map.IsWalkable(x, y) && ground.At(x + 0.5, y + 0.5) < 0.01f && d >= 9 && (stand is null || d < GridDistanceSquared(stand.Value, peak.Value)))
                    stand = c;
            }
        Check(stand is not null, "flat ground beside the hill");
        Print($"hill: walking to {stand} beside the top at {peak}");
        Session.TapGround(stand!.Value, Clocks.View()); // may be off screen, so it is the intent a far tap would make
        await UntilAsync(() => Own().Cell.X == stand.Value.X && Own().Cell.Y == stand.Value.Y && !Own().IsWalking, 60, "walking beside the hill");
        await SecondsAsync(1); // the camera settles

        // A raised cell on screen whose tap a flat y = 0 pick would put in another cell.
        var camera = _main.Camera!.Camera;
        var visible = _main.GetViewport().GetVisibleRect();
        GridCell? target = null;
        GridCell flat = default;
        Vector2 point = default;
        float height = 0;
        // The y = 0 error is under half a cell here, so a cell's centre never crosses into another cell: points inside the cell are tried as well.
        for (var y = 0; y < map.Height && target is null; y++)
            for (var x = 0; x < map.Width && target is null; x++)
            {
                if (!map.IsWalkable(x, y) || ground.At(x + 0.5, y + 0.5) < MinHillHeight) continue;
                for (var i = 0; i < HillSamples.Length * HillSamples.Length && target is null; i++)
                {
                    float px = x + HillSamples[i % HillSamples.Length], pz = y + HillSamples[i / HillSamples.Length];
                    var h = ground.At(px, pz);
                    var world = new Vector3(px, h, pz);
                    if (camera.IsPositionBehind(world)) continue;
                    var screen = camera.UnprojectPosition(world);
                    if (!visible.Grow(-40).HasPoint(screen)) continue;
                    var origin = camera.ProjectRayOrigin(screen);
                    var normal = camera.ProjectRayNormal(screen);
                    if (normal.Y > -1e-4f) continue;
                    var onPlane = origin + normal * (-origin.Y / normal.Y);
                    var planeCell = new GridCell((int)Math.Floor(onPlane.X), (int)Math.Floor(onPlane.Z));
                    if (planeCell.X == x && planeCell.Y == y) continue;
                    (target, flat, point, height) = (new GridCell(x, y), planeCell, screen, h);
                }
            }
        Check(target is not null, "a raised cell on screen that the y = 0 plane would pick wrongly");
        Print($"hill: tap at screen {point} on cell {target} (height {height:0.00}); a y = 0 pick would have sent {flat}");

        _main.HandleInput(new InputEventScreenTouch { Index = 0, Position = point, Pressed = true });
        await FrameAsync();
        _main.HandleInput(new InputEventScreenTouch { Index = 0, Position = point, Pressed = false });
        Check(Session.Ui.Marker is { } m && m.X == target!.Value.X && m.Y == target.Value.Y, $"the tap marker is on {target}");
        await UntilAsync(() => Own().Cell.X == target!.Value.X && Own().Cell.Y == target.Value.Y && !Own().IsWalking, 30, $"the server walking the character to {target}");
        Print($"hill: the server received {target} and walked the character there; drawn at height {ground.At(Own().X, Own().Z):0.00}");

        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        Print("logout: bye logout");
    }

    /// <summary>
    /// Where the draw calls come from (T-220): each group is hidden for a few frames and the drop in each pass is printed. The groups are the
    /// entity visuals (models, rings), the HUD, the damage numbers, and the map scene; HP bars are one multimesh (one call) and effects one call each.
    /// </summary>
    private async Task BreakdownAsync()
    {
        async Task<(double Opaque, double Shadow, double Canvas)> Sample()
        {
            await FramesAsync(3);
            double o = 0, sh = 0, c = 0;
            for (var i = 0; i < 6; i++)
            {
                await FrameAsync();
                var d = PassDraws();
                (o, sh, c) = (o + d.Opaque, sh + d.Shadow, c + d.Canvas);
            }
            return (o / 6, sh / 6, c / 6);
        }
        async Task Without(string what, Action<bool> show)
        {
            var with = await Sample();
            show(false);
            var without = await Sample();
            show(true);
            Print($"measure: breakdown {what}: 3D {with.Opaque - without.Opaque:0.0}, shadow {with.Shadow - without.Shadow:0.0}, canvas {with.Canvas - without.Canvas:0.0} (on screen {CountOnScreen()})");
        }
        var entities = new List<Node3D>();
        foreach (var e in World.Entities)
            if (e.Visual is ModelVisual { Root.Visible: true } v) entities.Add(v.Root);
        var meshes = 0;
        var surfaces = 0;
        void CountMeshes(Node node)
        {
            if (node is MeshInstance3D { Mesh: not null } m && m.IsVisibleInTree()) { meshes++; surfaces += m.Mesh.GetSurfaceCount(); }
            foreach (var child in node.GetChildren()) CountMeshes(child);
        }
        var shapes = new Dictionary<string, int>();
        foreach (var n in entities)
        {
            var before = meshes;
            CountMeshes(n);
            if (meshes - before > 1)
            {
                var names = new List<string>();
                void Names(Node node)
                {
                    if (node is MeshInstance3D { Mesh: not null } m && m.IsVisibleInTree()) names.Add(m.Name);
                    foreach (var child in node.GetChildren()) Names(child);
                }
                Names(n);
                var shape = string.Join(",", names);
                shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
            }
        }
        foreach (var (shape, count) in shapes) Print($"measure: {count} visuals draw several meshes: {shape}");
        await Without($"entity visuals ({entities.Count} shown, {meshes} visible meshes, {surfaces} surfaces)", on => { foreach (var n in entities) n.Visible = on; });
        await Without("HUD", on => _main.Hud!.SetVisible(on));
        if (_main.FindChild("DamageNumbers", true, false) is CanvasLayer numbers) await Without($"damage numbers ({_main.Fx?.Shown} drawn)", on => numbers.Visible = on);
        if (_main.Map?.Scene is { } scene) await Without("map scene", on => scene.Visible = on);
    }

    /// <summary>What the players on screen wear (T-403): pieces per player, weapon types, and how many kinds of piece are loaded and shared.</summary>
    private void ReportGear()
    {
        int players = 0, pieces = 0;
        var weapons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in World.Entities)
        {
            if (e.Kind != EntityKinds.Player || e.Visual is not ModelVisual v) continue;
            players++;
            pieces += v.PieceCount;
            var w = e.Weapon ?? "none";
            weapons[w] = weapons.GetValueOrDefault(w) + 1;
        }
        Print($"measure: gear: {players} players in view wear {pieces} pieces ({(players > 0 ? (double)pieces / players : 0):0.0} each); weapon types {string.Join(", ", weapons.Select(p => p.Key + " x" + p.Value))}; " +
              $"{_main.Entities!.PieceKinds} piece kinds resolved, {_main.Entities.Store.LoadedPieces} piece files held; merged meshes cached {_main.Entities.Merged.Cached}, built {_main.Entities.Merged.Built}");
    }

    /// <summary>This frame's draw calls by pass (3D, shadow, canvas), with the entity counts that explain them.</summary>
    private (long Opaque, long Shadow, long Canvas, int OnScreen, int Lod0, int Lod1) PassDraws()
    {
        var vp = _main.GetViewport().GetViewportRid();
        long Info(RenderingServer.ViewportRenderInfoType type) => RenderingServer.ViewportGetRenderInfo(vp, type, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
        var (lod0, lod1, _) = _main.Presenter!.DetailCounts();
        return (Info(RenderingServer.ViewportRenderInfoType.Visible), Info(RenderingServer.ViewportRenderInfoType.Shadow), Info(RenderingServer.ViewportRenderInfoType.Canvas), CountOnScreen(), lod0, lod1);
    }

    /// <summary>Lowest height that counts as "on the hill" for the hill run.</summary>
    private const float MinHillHeight = 0.2f;

    /// <summary>Where inside a cell the hill run tries to tap (fractions of a cell), away from the edges.</summary>
    private static readonly float[] HillSamples = { 0.5f, 0.2f, 0.8f };

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
        if (Value("--measure-dress") is { } dress)
        {
            // T-403: every player drawn wearing these item ids (from the command line, never from code), so a crowd is measured fully equipped.
            _main.Presenter!.DressOverride = dress.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await FramesAsync(2); // the presenter hands the new look to the visuals on its next pass
            await UntilAsync(() => _main.Entities!.Dressing == 0, 30, "the worn pieces");
        }
        ReportGear();
        OS.LowProcessorUsageMode = false; // measure every frame, not only frames that drew
        await SecondsAsync(5); // shaders settle

        var impacts = 0;
        var dealt = 0;
        var taken = 0;
        var crits = 0;
        ImpactHandler count = (in HitImpact impact) =>
        {
            impacts++;
            if (impact.Side == HitSide.Dealt) dealt++;
            else if (impact.Side == HitSide.Taken) taken++;
            if (impact.Kind == HitKind.Crit) crits++;
        };
        World.Combat.Impact += count;
        var droppedBefore = World.Combat.Dropped;
        int maxNumbers = 0, maxEffects = 0, maxPending = 0, calmFrames = 0, fxFrames = 0;
        double calmDraws = 0, fxDraws = 0, maxShown = 0;
        _main.CountAllocations = true;
        if (Array.IndexOf(_args, "--fight") >= 0) _ = HuntOneAsync(60); // the player fights too, so dealt and taken numbers show
        var shot = false;
        var frames = 0;
        double worstMs = 0, maxTriangles = 0, maxDraws = 0, sumDraws = 0, sumTriangles = 0, sumOnScreen = 0;
        var peak = (Opaque: 0L, Shadow: 0L, Canvas: 0L, OnScreen: 0, Lod0: 0, Lod1: 0);
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
            if (draws > maxDraws) peak = PassDraws();
            maxDraws = Math.Max(maxDraws, draws);
            maxTriangles = Math.Max(maxTriangles, triangles);
            sumDraws += draws;
            sumTriangles += triangles;
            sumOnScreen += CountOnScreen();
            if (_main.Fx is { } fx)
            {
                var (numbers, effects) = fx.Active;
                maxNumbers = Math.Max(maxNumbers, numbers);
                maxEffects = Math.Max(maxEffects, effects);
                maxShown = Math.Max(maxShown, fx.Shown);
                if (fx.Shown == 0 && effects == 0)
                {
                    calmFrames++;
                    calmDraws += draws;
                }
                else
                {
                    fxFrames++;
                    fxDraws += draws;
                }
            }
            maxPending = Math.Max(maxPending, World.Combat.PendingCount);
            frames++;
            if (!shot && _screenshot is not null && clock.Elapsed.TotalSeconds > 3 && _main.Fx is { } shotFx && shotFx.ShownInside(0.05f) >= 3)
            {
                shot = true; // mid-fight, numbers and effects up (the pause shows in the worst frame time)
                var (n, e) = (shotFx.Shown, shotFx.Active.Effects);
                Print($"measure: screenshot with {n} numbers drawn and {e} effects playing; {shotFx.Describe()}");
                await ShotAsync();
            }
        }
        _main.CountAllocations = false;
        World.Combat.Impact -= count;
        var seconds = clock.Elapsed.TotalSeconds;
        var (quietBytes, quietFrames, busyBytes, busyFrames) = _main.Allocations;
        Print($"measure: cap {_main.Config.FpsCap}, {frames} frames in {seconds:0.0} s = {frames / seconds:0.0} fps, worst {worstMs:0.0} ms");
        Print($"measure: entities on screen avg {sumOnScreen / frames:0.0}; in view {World.Entities.Count}");
        Print($"measure: draw calls avg {sumDraws / frames:0.0}, max {maxDraws:0}; triangles avg {sumTriangles / frames:0}, max {maxTriangles:0}; ground {_main.Map!.Triangles}");
        Print($"measure: frame path allocated {quietBytes} bytes in {quietFrames} frames with no message; {busyBytes} bytes in {busyFrames} frames with messages");
        Print($"measure: combat {impacts} impacts in {seconds:0.0} s ({impacts / seconds:0.0}/s; {dealt} dealt, {taken} taken, {crits} crits), at most {maxNumbers} numbers ({maxShown} drawn), {maxEffects} effects and {maxPending} pending hits at once; {World.Combat.Dropped - droppedBefore} hits dropped, {Session.Numbers.DroppedForCap} numbers dropped for the cap, {_main.Fx?.Totals.Effects} effects started");
        await BreakdownAsync();
        Print($"measure: peak frame by pass: 3D {peak.Opaque}, shadow {peak.Shadow}, canvas (HUD, numbers) {peak.Canvas}; {peak.OnScreen} entities on screen, visuals at LOD0 {peak.Lod0}, LOD1 {peak.Lod1}");
        Print($"measure: draw calls avg {(calmFrames > 0 ? calmDraws / calmFrames : 0):0.0} in {calmFrames} frames with no number or effect on screen, {(fxFrames > 0 ? fxDraws / fxFrames : 0):0.0} in {fxFrames} frames with some");
        Print($"measure: static memory {OS.GetStaticMemoryUsage() / (1024 * 1024)} MB; video {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024 * 1024):0} MB; adapter {RenderingServer.GetVideoAdapterName()}");
        if (!shot) await ShotAsync();

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
        var crossfades = _main.Audio!.Director.Stats.Crossfades;
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
        var audio = _main.Audio!;
        var crossfadesBefore = crossfades;
        Print($"warp: now on {next.Id} \"{next.Name}\" {next.Width}x{next.Height}, ground rebuilt ({triangles} -> {_main.Map.Triangles} triangles), {World.Entities.Count} entities, own at {Own().Cell}");
        await SecondsAsync(Config.AudioCrossfadeSeconds + 0.5);
        Check(audio.Director.Stats.Crossfades >= crossfadesBefore + 2, "the warp crossfaded the music and the ambience");
        Check(audio.Director.Playing(AudioDirector.MusicChannel) == next.MusicKey, "the new map's music is the one playing");
        Check(audio.Director.FadingOut(AudioDirector.MusicChannel) is null, "the old music has faded out and let go");
        Print($"warp: audio crossfaded to {next.MusicKey} and {next.AmbienceKey}; {audio.Director.Stats.Crossfades} crossfades so far");
    }

    private ClientConfig Config => _main.Config;

    /// <summary>After a fight the sound system must have played, the map's music must be on, and the pool must have held its cap.</summary>
    private void CheckAudioAfterFight()
    {
        var audio = _main.Audio!;
        var stats = audio.Director.Stats;
        Print($"audio: played {stats.Played}, skipped: cooldown {stats.Cooldown}, distance {stats.Distance}, full {stats.Full}, muted {stats.Muted}, paused {stats.Paused}; stolen {stats.Stolen}, silent {stats.Silent}, load failures {stats.LoadFailed}, crossfades {stats.Crossfades}");
        Print($"audio: music {audio.Director.Playing(AudioDirector.MusicChannel)}, ambience {audio.Director.Playing(AudioDirector.AmbientChannel)}; engine voices playing {audio.Sink.PlayingVoices()} of {Config.AudioMaxVoices}; effects {audio.Sink.LoadedEffects} files, {audio.Sink.Budget.Used / 1024.0 / 1024.0:0.00} MB of {audio.Sink.Budget.Limit / 1024 / 1024} MB");
        Check(stats.Played > 0, "the fight made sound");
        Check(stats.LoadFailed == 0, "every sound file loaded");
        Check(audio.Director.Playing(AudioDirector.MusicChannel) == CurrentMap().MusicKey && CurrentMap().MusicKey is not null, "the map's music is playing");
        Check(audio.Sink.PlayingVoices() <= Config.AudioMaxVoices, "no more voices than the cap");
        Check(audio.Sink.Budget.Used <= audio.Sink.Budget.Limit, "effects stay within the memory budget");
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

    private async Task ShotAsync(string suffix)
    {
        if (_screenshot is null || DisplayServer.GetName() == "headless") return;
        OS.LowProcessorUsageMode = false;
        await FramesAsync(3);
        await _main.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        var path = System.IO.Path.ChangeExtension(_screenshot, null) + suffix + System.IO.Path.GetExtension(_screenshot);
        var error = _main.GetViewport().GetTexture().GetImage().SavePng(path);
        Print($"screenshot: {path} ({error}), {_main.GetWindow().Size.X}x{_main.GetWindow().Size.Y}");
        OS.LowProcessorUsageMode = true;
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
