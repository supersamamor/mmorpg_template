using System.Diagnostics;
using System.Globalization;
using Game.Client.Core;
using Game.Client.Core.Audio;
using Game.Client.Core.Bundle;
using Game.Client.Core.Combat;
using Game.Client.Core.Net;
using Game.Client.Core.Skills;
using Game.Client.Core.Ui;
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
        Array.IndexOf(args, "--e2e-bye") >= 0 || Array.IndexOf(args, "--e2e-ui") >= 0 || Array.IndexOf(args, "--e2e-items") >= 0 || Array.IndexOf(args, "--e2e-statuses") >= 0 || Array.IndexOf(args, "--e2e-skills") >= 0 || Array.IndexOf(args, "--e2e-stats") >= 0 || Array.IndexOf(args, "--e2e-dialogue") >= 0 || Array.IndexOf(args, "--e2e-quests") >= 0 ||
        Array.IndexOf(args, "--measure") >= 0;

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
            else if (Array.IndexOf(_args, "--e2e-statuses") >= 0) await StatusesRunAsync();
            else if (Array.IndexOf(_args, "--e2e-skills") >= 0) await SkillsRunAsync();
            else if (Array.IndexOf(_args, "--e2e-stats") >= 0) await StatsRunAsync();
            else if (Array.IndexOf(_args, "--e2e-dialogue") >= 0) await DialogueRunAsync();
            else if (Array.IndexOf(_args, "--e2e-quests") >= 0) await QuestsRunAsync();
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
    /// <summary>
    /// T-501's run (on a content copy where new characters start with a Swift Elixir and the weak slimes cast Iron Guard on themselves when they fight):
    /// use the elixir through the bag and see Swift in the HUD, then attack a slime until it shows Iron Skin, as a status in the world view and a
    /// marker above it. Every step goes through the HUD and the input path; the screenshot (windowed) shows both.
    /// </summary>
    private async Task StatusesRunAsync()
    {
        await LoginAsync();
        await UntilAsync(() => Session.Inventory.Known, 10, "the own inventory after the snapshot");
        var hud = _main.Hud!;
        // The run's content copy starts a character with one consumable only, one that applies a status (no ids in client code: the first consumable).
        var elixir = Session.Inventory.Entries.FirstOrDefault(e => _main.Bundle!.Item(e.Item)?.Kind == Game.Shared.Vocabulary.ItemKinds.Consumable);
        Check(elixir is not null, "carrying the start consumable");
        Press(hud.BagButton);
        await FramesAsync(3);
        Press(hud.Bag!.ItemButtons.First(b => b.Entry == elixir!.Id).Button);
        await FramesAsync(2);
        Check(hud.Bag.Actions.Use.Visible, "a selected elixir offers Use");
        Press(hud.Bag.Actions.Use);
        await UntilAsync(() => Own().Statuses.Count > 0, 10, "a status on the own character");
        Press(hud.BagButton);
        await FramesAsync(3);
        var own = Own().Statuses[0];
        Print($"statuses: own {string.Join(", ", Own().Statuses.Select(s => s.Id + " x" + s.Stacks + " " + s.SecondsLeft(Clocks.View()) + "s" + (s.Debuff ? " debuff" : "")))}; " +
              $"HUD [{string.Join("; ", hud.StatusTexts)}]");
        Check(hud.StatusTexts.Any(t => t.StartsWith(own.Id, StringComparison.Ordinal)), "the HUD shows the own status");
        Check(own.SecondsLeft(Clocks.View()) > 0, "time left on the own status");

        var open = NearestOpenCellOutsideSafeZones(CurrentMap(), Own().Cell);
        await TapCellAsync(open);
        await UntilAsync(() => !Own().IsWalking && Own().Cell.X == open.X && Own().Cell.Y == open.Y, 30, "leaving the safe zone");
        EntityView? buffed = null;
        var clock = Stopwatch.StartNew();
        while (buffed is null && clock.Elapsed.TotalSeconds < 120)
        {
            var monster = NearestMonster();
            if (monster is null)
            {
                await WalkTowardMapMiddleAsync();
                continue;
            }
            if (!OnScreen(monster))
            {
                await TapCellAsync(StepToward(monster.Cell));
                await SecondsAsync(1);
                continue;
            }
            await TapWorldAsync(new Vector3((float)monster.X, 0.45f, (float)monster.Z));
            var fight = Stopwatch.StartNew();
            while (fight.Elapsed.TotalSeconds < 15 && World.Find(monster.Id) is { Dead: false } m && m.Statuses.Count == 0) await FrameAsync();
            if (World.Find(monster.Id) is { Dead: false, Statuses.Count: > 0 } found) buffed = found;
            else await SecondsAsync(0.5);
        }
        Check(buffed is not null, "a monster with a status in view");
        // A new move order ends the auto-attack, so the slime (a tough one in this copy) stays alive for the picture.
        var here = Own().Cell;
        var away = new GridCell(here.X + 2, here.Y);
        foreach (var (dx, dy) in new[] { (2, 0), (-2, 0), (0, 2), (0, -2) })
            if (CurrentMap().IsWalkable(here.X + dx, here.Y + dy)) { away = new GridCell(here.X + dx, here.Y + dy); break; }
        await TapCellAsync(away);
        await SecondsAsync(1.5);
        Print($"statuses: monster #{buffed!.Id} {_main.Bundle!.NameOf(buffed.DisplayKey)} has {string.Join(", ", buffed.Statuses.Select(s => s.Id + (s.Debuff ? " debuff" : "")))}; " +
              $"markers drawn {_main.Presenter!.StatusMarkCount}; effects started {_main.Fx?.Totals.Effects}");
        Check(_main.Presenter!.StatusMarkCount >= 1, "a status marker above the monster");
        await ShotAsync();
        await LookShotsAsync(buffed);
    }

    /// <summary>
    /// T-222's readability pictures (windowed with <c>--screenshot</c> only): the fight with damage numbers and the status marker, then every job's model beside the
    /// own character, each at the default zoom and at the closest. The gallery's visuals are drawn by this client alone (nothing is sent) and go back to the pool.
    /// </summary>
    private async Task LookShotsAsync(EntityView monster)
    {
        if (_screenshot is null || DisplayServer.GetName() == "headless") return;
        var camera = _main.Camera!;
        var zoom = camera.Distance;
        await TapWorldAsync(new Vector3((float)monster.X, 0.45f, (float)monster.Z));
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 10 && (_main.Fx?.Shown ?? 0) < 2) await FrameAsync();
        await ShotAsync("-fight");
        camera.Zoom(0.01);
        clock.Restart();
        while (clock.Elapsed.TotalSeconds < 6 && (_main.Fx?.Shown ?? 0) < 1) await FrameAsync();
        await ShotAsync("-fight-close");
        camera.Zoom(zoom / camera.Distance);
        var away = NearestOpenCellOutsideSafeZones(CurrentMap(), Own().Cell);
        await TapCellAsync(away); // stop fighting, so the gallery is calm
        await SecondsAsync(1.5);

        var entities = _main.Entities!;
        var gallery = new List<ModelVisual>();
        var jobs = _main.Bundle!.JobIds;
        for (var i = 0; i < jobs.Count; i++)
        {
            if (entities.Acquire(EntityKinds.Player, jobs[i]) is not ModelVisual visual) continue;
            var x = (float)Own().X + 1.6f * (i - (jobs.Count - 1) / 2f);
            var z = (float)Own().Z + 1.4f;
            visual.SetPose(x, _main.Map!.Ground.At(x, z), z, Mathf.Pi);
            visual.SetLook(default);
            visual.SetOutline(true);
            gallery.Add(visual);
        }
        await UntilAsync(() => gallery.All(v => v.Ready), 10, "the gallery models");
        Print($"look: gallery of {gallery.Count} jobs ({string.Join(", ", jobs.Select(j => _main.Bundle.NameOf(j)))}) beside the own character; {_main.Presenter!.OutlinedCount} outlined in the world");
        await ShotAsync("-jobs");
        // What an outline costs: the same frame with the gallery's outlines off, then on again.
        OS.LowProcessorUsageMode = false;
        await FramesAsync(5);
        var withOutlines = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        foreach (var visual in gallery) visual.SetOutline(false);
        await FramesAsync(5);
        var without = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        foreach (var visual in gallery) visual.SetOutline(true);
        await FramesAsync(5);
        Print($"look: draw calls with the {gallery.Count} gallery outlines {withOutlines:0}, without {without:0}: {(withOutlines - without) / Math.Max(1, gallery.Count):0.0} per outline");
        camera.Zoom(0.01);
        await ShotAsync("-jobs-close");
        camera.Zoom(zoom / camera.Distance);
        foreach (var visual in gallery) entities.Release(visual);
    }

    /// <summary>
    /// T-502's run (on a content copy with weak monsters that give a lot of EXP, cast Mana Font when they fight, and 3 skill points a job level): fight until the
    /// level-ups bring skill points and a job to change to, watching a monster's cast bar; open the skill panel through the HUD, learn a skill of the first job,
    /// change job, learn a ranged attack of the new job, then press its skill bar slot, tap a monster, and see the own cast bar, the hit and the cooldown sweep.
    /// Every step goes through the HUD and the input path; no skill or job id is in this code.
    /// </summary>
    private async Task SkillsRunAsync()
    {
        await LoginAsync();
        var hud = _main.Hud!;
        var book = Session.Skills;
        await UntilAsync(() => book.Known, 10, "the own skills after the snapshot");
        Check(book.Points == 0 && book.ChangeableJobs.Count == 0 && book.ActiveSkills.Count == 0, "a new character: no points, no job to change to, nothing on the skill bar");
        var firstJob = book.JobId;

        var open = NearestOpenCellOutsideSafeZones(CurrentMap(), Own().Cell);
        await TapCellAsync(open);
        await UntilAsync(() => !Own().IsWalking && Own().Cell.X == open.X && Own().Cell.Y == open.Y, 30, "leaving the safe zone");
        var sawMonsterBar = false;
        var clock = Stopwatch.StartNew();
        while ((book.Points == 0 || book.ChangeableJobs.Count == 0) && clock.Elapsed.TotalSeconds < 150)
        {
            var monster = NearestMonster();
            if (monster is null)
            {
                await WalkTowardMapMiddleAsync();
                continue;
            }
            if (!OnScreen(monster))
            {
                await TapCellAsync(StepToward(monster.Cell));
                await SecondsAsync(1);
                continue;
            }
            await TapWorldAsync(new Vector3((float)monster.X, 0.45f, (float)monster.Z));
            var fight = Stopwatch.StartNew();
            while (fight.Elapsed.TotalSeconds < 20 && World.Find(monster.Id) is { Dead: false })
            {
                if (!sawMonsterBar && World.Casts.Any(c => c.Caster != World.OwnId && c.Fraction(Clocks.View()) > 0.45) && _main.Presenter!.CastBarCount > 0)   // half full for the picture
                {
                    sawMonsterBar = true;
                    Print($"skills: a cast bar over monster #{monster.Id} ({_main.Bundle!.NameOf(monster.DisplayKey)}): {World.Casts.Count} open, {_main.Presenter.CastBarCount} drawn");
                    await ShotAsync("-cast");
                }
                await FrameAsync();
            }
            await SecondsAsync(0.4);
        }
        Check(book.Points > 0 && book.ChangeableJobs.Count > 0, $"skill points and a job to change to after the fights (points {book.Points}, jobs {book.ChangeableJobs.Count})");
        Check(sawMonsterBar, "a cast bar drawn over a casting monster");
        Print($"skills: {book.Points} points, may change to {string.Join(", ", book.ChangeableJobs)}");

        // The panel: learn something of the first job's tree.
        Press(hud.SkillsButton);
        await FramesAsync(4);
        var panel = hud.Skills!;
        Check(panel.Open && panel.RowButtons.Count > 0, "the skill panel opens with the tree's rows");
        var first = book.Rows.First(r => r.State == SkillState.Learnable);
        var points = book.Points;
        Press(panel.RowButtons.First(r => r.Skill == first.SkillId).Button);
        await FramesAsync(3);
        Check(panel.LearnButton.Visible, "a learnable row offers Learn");
        Print($"skills: tree layout: {panel.Layout}");
        await ShotAsync("-tree");
        Press(panel.LearnButton);
        await UntilAsync(() => book.Level(first.SkillId) == 1 && book.Points == points - 1, 10, "the learned skill and one point less");
        Print($"skills: learned {_main.Bundle!.NameOf(first.SkillId)}; {panel.StatusText}");

        // Job change through the panel.
        var job = book.ChangeableJobs[0];
        await FramesAsync(3);
        Check(panel.JobButtons.Any(b => b.Job == job), "the panel offers the job change");
        Press(panel.JobButtons.First(b => b.Job == job).Button);
        await UntilAsync(() => book.JobId == job, 10, "the new job");
        await FramesAsync(3);
        Check(book.Rows.Any(r => r.JobId == job), "the tree now has the new job's rows");
        Check(book.Level(first.SkillId) == 1, "the learned skill stays after the job change");
        Check(Own().DisplayKey == job, "the character is seen with the new job");

        // Learn a ranged attack of the new job and put it on a slot.
        SkillRow? attack = null;
        foreach (var row in book.Rows)
            if (row.JobId == job && row.State == SkillState.Learnable && _main.Bundle!.Skill(row.SkillId) is { Passive: false, Ground: false, Friendly: false, Range: > 1 }) { attack = row; break; }
        Check(attack is not null, "a learnable ranged attack in the new job");
        await FramesAsync(2);
        Press(panel.RowButtons.First(r => r.Skill == attack!.SkillId).Button);
        await FramesAsync(2);
        Press(panel.LearnButton);
        await UntilAsync(() => book.Level(attack!.SkillId) == 1, 10, "the attack skill learned");
        Press(hud.SkillsButton);
        await FramesAsync(3);
        Check(!panel.Open, "the skills button closes the panel");
        var slot = book.ActiveSkills.ToList().IndexOf(attack!.SkillId);
        Check(slot >= 0 && hud.HotbarSlots[slot].Text.Contains(_main.Bundle!.NameOf(attack.SkillId), StringComparison.Ordinal), "the skill is on the skill bar");

        // Cast it: press the slot, tap a monster. A pick-up still under way (loot from the fights) walks the character and cancels a cast, so each attempt
        // first stands still with no target, and an attempt that never reaches a cooldown is simply made again.
        var hitsBefore = 0;
        World.Combat.Impact += (in HitImpact impact) =>
        {
            if (impact.Attacker == World.OwnId && impact.Kind != HitKind.Status) hitsBefore++;
        };
        var sawOwnBar = false;
        var sweep = false;
        EntityView? target = null;
        for (var attempt = 1; attempt <= 5 && !sweep; attempt++)
        {
            var here = Own().Cell;
            var away = here;
            foreach (var (dx, dy) in new[] { (2, 0), (-2, 0), (0, 2), (0, -2) })
                if (CurrentMap().IsWalkable(here.X + dx, here.Y + dy)) { away = new GridCell(here.X + dx, here.Y + dy); break; }
            await TapCellAsync(away);   // a move order ends any auto-attack or pick-up still running and clears the target
            await UntilAsync(() => !Own().IsWalking && Session.Ui.Target is null, 10, "standing still with no target");
            await SecondsAsync(1.5);
            target = null;
            var search = Stopwatch.StartNew();
            while (target is null && search.Elapsed.TotalSeconds < 60)
            {
                target = NearestMonster();
                if (target is null) { await WalkTowardMapMiddleAsync(); continue; }
                if (!OnScreen(target)) { await TapCellAsync(StepToward(target.Cell)); await SecondsAsync(1); target = null; }
            }
            Check(target is not null, "a monster on screen to cast at");
            Press(hud.HotbarSlots[slot]);
            await FramesAsync(2);
            Check(Session.Ui.PendingSkill == attack.SkillId && Session.Ui.Flash.Length > 0,
                $"the press makes the skill wait for a target and says so (pending {Session.Ui.PendingSkill}, flash '{Session.Ui.Flash}', target {Session.Ui.Target}, press {hud.LastHotbarPress}, slot {slot})");
            var tapping = TapWorldAsync(new Vector3((float)target!.X, 0.45f, (float)target.Z));   // watched while it runs: the cast may be over before the tap helper returns
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 6 && !sweep)
            {
                if (World.Casts.Any(c => c.Caster == World.OwnId) && _main.Presenter!.CastBarCount > 0) sawOwnBar = true;
                if (book.AnyCooldown && book.CooldownFraction(attack.SkillId) > 0.3)
                {
                    sweep = true;
                    await FramesAsync(1);
                    Print($"skills: cooldown running on {_main.Bundle.NameOf(attack.SkillId)}: {book.CooldownLeft(attack.SkillId):0.00} s left; slot text [{hud.HotbarSlots[slot].Text.Replace((char)10, '|')}]");
                    Check(!((IHotbarContent)book).Slot(slot).Usable, "a skill on cooldown is not usable");
                    await ShotAsync("-cooldown");
                }
                await FrameAsync();
            }
            await tapping;
            if (!sweep) Print($"skills: attempt {attempt} did not reach a cooldown (flash '{Session.Ui.Flash}', own cell {Own().Cell}, monster #{target.Id} at {World.Find(target.Id)?.Cell}, sp {World.Vitals?.Sp}); trying again");
        }
        Check(sawOwnBar, "the own cast bar was drawn while casting");
        Check(sweep, "the skill went on cooldown");
        var presses = hud.HotbarPresses;
        Press(hud.HotbarSlots[slot]);
        Check(hud.LastHotbarPress == HotbarPress.Unusable && hud.HotbarPresses == presses + 1, "a press during the cooldown is refused by the bar and sends nothing");
        await UntilAsync(() => !book.AnyCooldown, 10, "the cooldown to run out");
        Check(((IHotbarContent)book).Slot(slot).Usable, "the skill is usable again");
        await UntilAsync(() => hitsBefore > 0, 5, "the skill hit its target");
        Print($"skills: own cast bar seen {sawOwnBar}; hits by me {hitsBefore}; first job {firstJob}, now {book.JobId}");
    }

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
        // T-405: the frame times from the press of Equip until the weapon is on the character. The bag item's model was loaded ahead of time (gear_preload_limit above 0),
        // so no frame in this window should be the one that loads and merges it; run with --set gear_preload_limit=0 for the "before" figure.
        var calm = new List<double>();
        var calmLast = Time.GetTicksUsec();
        for (var i = 0; i < 30; i++)
        {
            await FrameAsync();
            var t = Time.GetTicksUsec();
            calm.Add((t - calmLast) / 1000.0);
            calmLast = t;
        }
        calm.Sort();
        var preloaded = _main.Entities!.Store.LoadedPieces;
        var pending = _main.Entities.GearPending;
        var lookBefore = Own().LookVersion;
        var loadMsBefore = _main.Entities.Store.MainThreadMs;
        var mergeMsBefore = _main.Entities.MergeMainThreadMs;
        Press(hud.Bag.Actions.Equip);
        double equipWorst = 0;
        var equipFrames = 0;
        var frameLog = new List<string>();
        var equipLast = Time.GetTicksUsec();
        while (equipFrames < 600 && !(Session.Inventory.InSlot(Game.Shared.Vocabulary.EquipSlots.Weapon) is not null && Own().Visual is ModelVisual { PieceCount: > 0 } && _main.Entities.Dressing == 0))
        {
            await FrameAsync();
            var now = Time.GetTicksUsec();
            equipWorst = Math.Max(equipWorst, (now - equipLast) / 1000.0);
            frameLog.Add($"{(now - equipLast) / 1000.0:0.0}{(Own().LookVersion != lookBefore ? "*" : "")}");
            equipLast = now;
            equipFrames++;
        }
        Print($"items: equip frames (ms, * once the new look arrived): {string.Join(" ", frameLog)}; the 30 frames before: median {calm[calm.Count / 2]:0.0}, worst {calm[^1]:0.0}");
        Print($"items: equip frames: worst {equipWorst:0.0} ms over {equipFrames} frames from the press until dressed; {preloaded} piece(s) preloaded and {pending} still loading at the press (gear_preload_limit {_main.Config.GearPreloadLimit}); main-thread model work from the press until dressed: {_main.Entities.Store.MainThreadMs - loadMsBefore:0.00} ms; the frame that put the merged mesh on {ModelVisual.LastDressMs:0.00} ms, merge work on the main thread {_main.Entities.MergeMainThreadMs - mergeMsBefore:0.00} ms in all");
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

    /// <summary>
    /// T-503's run (real content: a new character starts with 10 stat points, a stat reset and a skill reset): open the stats panel through the skill panel, spend
    /// stat points with the + buttons (the points go down by the server's costs and the derived stats follow), close it, open the bag, select the stat reset,
    /// see the confirmation, confirm, and see the points and stats come back; then the skill reset, refused by the server because nothing was learned (it
    /// stays in the bag). Below level 10 a reset is free, so the items stay. No stat, item or cost is named here: the stats are the rows the server sent and
    /// the reset items the ones the bundle says to confirm.
    /// </summary>
    private async Task StatsRunAsync()
    {
        await LoginAsync();
        var hud = _main.Hud!;
        var book = Session.Stats;
        await UntilAsync(() => book.Known, 10, "the own stats after the snapshot");
        var startPoints = book.Points;
        var startStats = book.Stats.Select(s => s.Value).ToArray();
        var startDerived = book.Derived.Select(d => d.Value).ToArray();
        Check(startPoints > 0 && book.Stats.Count > 0 && book.Derived.Count > 0, "a new character has stat points, stats and derived stats");
        Check(book.Stats.All(s => s.NextCost > 0), "the server names a cost for every stat's next point");
        Print($"stats: start {startPoints} points, stats [{string.Join(", ", book.Stats.Select(s => s.Stat + " " + s.Value + " (+" + s.NextCost + ")"))}]");

        Press(hud.SkillsButton);
        await FramesAsync(3);
        Press(hud.Skills!.StatsButton);
        await FramesAsync(3);
        var panel = hud.Stats!;
        Check(panel.Open && !hud.Skills.Open, "the Stats button closes the skills and opens the stats panel");
        Print($"stats: panel {panel.Text}");
        Print($"stats: layout: {panel.Layout}");
        await ShotAsync("-stats");

        var first = panel.RaiseButtons[0];
        var cost = book.Stats[0].NextCost;
        Press(first.Button);
        await UntilAsync(() => book.Points == startPoints - cost, 10, "the points spent by the server's cost");
        Check(book.Stats[0].Value == startStats[0] + 1, "the raised stat is one higher");
        var second = panel.RaiseButtons[2];
        var cost2 = book.Stats[2].NextCost;
        Press(second.Button);
        await UntilAsync(() => book.Points == startPoints - cost - cost2, 10, "the second raise");
        var raisedDerived = book.Derived.Select(d => d.Value).ToArray();
        Check(!raisedDerived.SequenceEqual(startDerived), "the derived stats followed the raised stats");
        await FramesAsync(3);
        Print($"stats: raised {panel.Text}");
        Check(panel.Text.Contains(UiText.StatPoints(book.Points), StringComparison.Ordinal), "the panel shows the points the server sent");

        // Close it; then the bag: a reset item asks first.
        Press(panel.CloseButton);
        await FramesAsync(3);
        Check(!panel.Open, "Close closes the stats panel");

        var bundle = _main.Bundle!;
        var resets = Session.Inventory.Entries.Where(e => bundle.Item(e.Item) is { ConfirmUse: true }).ToList();
        Check(resets.Count == 2, "a new character carries both reset items, found by the bundle's confirm flag");
        Press(hud.BagButton);
        await FramesAsync(3);
        // Use each in turn: the stat reset changes the points; the skill reset has nothing to take back, so the server refuses it and says why.
        var refusals = 0;
        foreach (var entry in resets)
        {
            var before = book.Points;
            var flashBefore = Session.Ui.Flash;
            Press(hud.Bag!.ItemButtons.First(b => b.Entry == entry.Id).Button);
            await FramesAsync(2);
            Press(hud.Bag.Actions.Use);
            await FramesAsync(3);
            Check(hud.Bag.HintText.Contains(UiText.ConfirmUse, StringComparison.Ordinal), "using a reset item asks first");
            Print($"stats: confirm: {hud.Bag.HintText}");
            await ShotAsync("-confirm");
            Press(hud.Bag.CardAndRefine.Confirm);
            await FramesAsync(10);
            await Task.Delay(500);
            Print($"stats: used {bundle.NameOf(entry.Item)}: points {before} -> {book.Points}; flash '{Session.Ui.Flash}'");
            Check(Session.Inventory.Find(entry.Id) is not null, "a free reset (below level 10) keeps the item, and so does a refusal");
            if (book.Points == before) refusals += Session.Ui.Flash.Length > 0 ? 1 : 0;
        }
        Check(refusals == 1, "the reset with nothing to take back was refused with a reason on the flash line");
        Check(book.Points == startPoints, "the stat reset gave exactly the spent points back");
        Check(book.Stats.Select(s => s.Value).SequenceEqual(startStats), "every stat is back at its start value");
        Check(book.Derived.Select(d => d.Value).SequenceEqual(startDerived), "the derived stats are back too");
        Press(hud.BagButton);
        await FramesAsync(2);
        Print("stats: points back, stats back, both items kept");
    }

    /// <summary>
    /// T-601's run (real content): the guide Odalys stands in the safe zone. Tap her through the input path (the server walks the character up), see the dialogue
    /// panel with her name, title and portrait and the choices the server shows, pick the one that leads to her services, see the next node, pick the save point,
    /// see the confirming node, finish with its button, then talk again and close with the Close button. Every node, choice and reply is the server's; the run only
    /// presses what the panel shows.
    /// </summary>
    private async Task DialogueRunAsync()
    {
        await LoginAsync();
        var hud = _main.Hud!;
        var dialogue = Session.Dialogue;
        var panel = hud.Dialogue!;
        var bundle = _main.Bundle!;

        EntityView? Guide() => World.Entities.FirstOrDefault(e => e.Kind == EntityKinds.Npc && bundle.NameOf(e.DisplayKey) == "Odalys");
        await UntilAsync(() => Guide() is not null, 10, "the guide in view");
        var guide = Guide()!;
        Print($"dialogue: {bundle.NameOf(guide.DisplayKey)} #{guide.Id} at {guide.Cell}, own {Own().Cell}");
        Check(!panel.Open, "no dialogue panel before talking");

        // Walk away first, so the talk has to walk the character back into range.
        var map = CurrentMap();
        GridCell? far = null;
        for (var dy = 0; dy <= 3 && far is null; dy++)   // five cells east, inside the safe zone, so no monster is near
            far = map.IsWalkable(guide.Cell.X + 5, guide.Cell.Y + dy) ? new GridCell(guide.Cell.X + 5, guide.Cell.Y + dy) : null;
        Check(far is not null, "a walkable cell five cells from the guide");
        await TapCellAsync(far!.Value);
        await UntilAsync(() => Own().Cell.X == far.Value.X && Own().Cell.Y == far.Value.Y && !Own().IsWalking, 30, "walking away from the guide");
        Print($"dialogue: walked away to {Own().Cell}");

        await TapWorldAsync(new Vector3((float)guide.X, 0.8f, (float)guide.Z));
        await UntilAsync(() => dialogue.Open, 20, "the dialogue after the walk up");
        await FramesAsync(3);
        var own = Own().Cell;
        Check(Math.Max(Math.Abs(own.X - guide.Cell.X), Math.Abs(own.Y - guide.Cell.Y)) <= 2, "the server walked the character into talking range first");
        Check(panel.Open && panel.Text.StartsWith("Odalys | Vale Guide | ", StringComparison.Ordinal), "the panel shows the speaker's name and title");
        Check(dialogue.Choices.Count >= 3, "the server shows the first node's choices");
        Print($"dialogue: panel {panel.Text}");
        Print($"dialogue: layout {panel.Layout}; portrait {(panel.ShowsPortrait ? "texture" : "letter fallback")}");
        await ShotAsync("-dialogue");

        int Find(string text) => dialogue.Choices.ToList().FindIndex(c => c.Text.Contains(text, StringComparison.Ordinal));
        string Node() => dialogue.Text;
        var first = Node();
        var more = Find("something else");
        Check(more >= 0, "the first node has the choice that leads to the services");
        Press(panel.ChoiceButtons[more].Button);
        await UntilAsync(() => Node() != first, 10, "the next node");
        await FramesAsync(3);
        Print($"dialogue: next {panel.Text}");
        Check(dialogue.Open && dialogue.Choices.Count >= 2, "the next node shows its choices (the level-gated reset ones too: a new character is below level 10)");

        var home = Find("home");
        Check(home >= 0, "the services node has the save point choice");
        var services = Node();
        Press(panel.ChoiceButtons[home].Button);
        await UntilAsync(() => Node() != services, 10, "the confirming node");
        await FramesAsync(3);
        Print($"dialogue: after the save point {panel.Text}");
        Check(dialogue.Choices.Count == 0 && !dialogue.More, "the last node has no choices and ends the dialogue");
        Check(panel.ChoiceButtons.Count == 1, "the panel offers one button to finish");
        Press(panel.ChoiceButtons[0].Button);
        await UntilAsync(() => !dialogue.Open && !panel.Open, 10, "the panel closing when the server ends the dialogue");

        // Again, and close it with the Close button.
        await TapWorldAsync(new Vector3((float)guide.X, 0.8f, (float)guide.Z));
        await UntilAsync(() => dialogue.Open, 20, "the dialogue again");
        Press(panel.CloseButton);
        await UntilAsync(() => !dialogue.Open && !panel.Open, 10, "Close ending the dialogue");
        Print("dialogue: walked up, talked, chose, saw the next nodes, finished, talked again, closed");
        await ShopStepAsync();
    }

    /// <summary>
    /// T-603: the merchant's shop end to end, on the real server. Talk to Brannoc (the server walks the character up), pick "Show me your wares", see the shop panel open with
    /// the stock and the balance; sell the three starting Healing Draughts (the balance and the bag change), buy an Antidote with the proceeds (they change again), and close.
    /// A new character has no currency, so the sale pays for the purchase: 3 x 12 = 36, less the Antidote's 30, leaves 6.
    /// </summary>
    private async Task ShopStepAsync()
    {
        var hud = _main.Hud!;
        var shop = hud.Shop!;
        var book = Session.Shop;
        var bundle = _main.Bundle!;
        var currency = bundle.CurrencyName;
        Check(!shop.Open, "no shop panel before talking to the merchant");
        EntityView? Merchant() => World.Entities.FirstOrDefault(e => e.Kind == EntityKinds.Npc && bundle.NameOf(e.DisplayKey) == "Brannoc");
        await UntilAsync(() => Merchant() is not null, 10, "the merchant in view");
        var merchant = Merchant()!;
        await TapWorldAsync(new Vector3((float)merchant.X, 0.8f, (float)merchant.Z));
        await UntilAsync(() => Session.Dialogue.Open, 20, "the merchant's dialogue");
        Check(hud.Dialogue!.Text.StartsWith("Brannoc | Meadow Merchant | ", StringComparison.Ordinal), "the merchant greets the player by name and title");
        var wares = Session.Dialogue.Choices.ToList().FindIndex(c => c.Text.Contains("wares", StringComparison.Ordinal));
        Check(wares >= 0, "the greeting offers the wares");
        Press(hud.Dialogue.ChoiceButtons[wares].Button);
        await UntilAsync(() => book.Open && shop.Open, 15, "the shop panel opening");
        await UntilAsync(() => !Session.Dialogue.Open && !hud.Dialogue.Open, 10, "the dialogue panel closing while the shop stays");
        await FramesAsync(3);
        Check(shop.Open && book.Stock.Count >= 10, $"the shop lists its stock ({book.Stock.Count} items)");
        Check(shop.Rows.Count == book.Stock.Count, "one row per stock item on the Buy tab");
        Check(shop.Text.StartsWith(UiText.ShopHeader(currency, Session.Inventory.Currency), StringComparison.Ordinal), $"the balance is shown ({shop.Text})");
        Check(hud.FindProblems().Count == 0, "the open shop fits this screen with every touch target at least the minimum");
        Check(hud.Blocks(shop.Rows[0].Button.GetGlobalRect().GetCenter()), "a touch on the shop never reaches the world");
        Print($"shop: opened; {shop.Text}; {shop.Layout}");
        await ShotAsync("-shop");

        var poorBefore = Session.Inventory.Currency;
        var antidote = book.Stock.First(s => bundle.NameOf(s.Item) == "Antidote");   // the cheap potion: a new character's sale pays for it
        shop.Rows.First(r => r.Item == antidote.Item).Button.EmitSignal(BaseButton.SignalName.Pressed);
        await FramesAsync(2);
        Press(shop.Picker.Action);   // the balance is 0: the server refuses and the flash says why, the panel stays
        await UntilAsync(() => Session.Ui.Flash == UiText.ItemRefused(ItemReasons.CannotAfford), 10, "the refusal for a purchase it cannot afford");
        Check(shop.Open && Session.Inventory.Currency == poorBefore, "a refused purchase changes nothing and the panel stays");

        // Sell the three Healing Draughts.
        Press(shop.Tabs.Sell);
        await FramesAsync(3);
        var draughts = Session.Inventory.Entries.First(e => bundle.NameOf(e.Item) == "Healing Draught" && e.Slot is null);
        var pays = book.SellPriceOf(draughts.Item) ?? 0;
        var row = shop.Rows.First(r => r.Entry == draughts.Id);
        Check(pays > 0 && row.Button.Text.Contains(pays.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), $"the sell tab shows what the shop pays ({row.Button.Text})");
        Press(row.Button);
        await FramesAsync(2);
        Press(shop.Picker.Most);
        await FramesAsync(2);
        Check(book.Quantity == draughts.Quantity, $"Max picks the whole stack ({book.Quantity})");
        var rev = Session.Inventory.Rev;
        Press(shop.Picker.Action);
        await UntilAsync(() => Session.Inventory.Rev > rev, 10, "the sale's inventory change");
        Check(Session.Inventory.Currency == pays * draughts.Quantity, $"the balance rose by the sale ({Session.Inventory.Currency})");
        Check(Session.Inventory.Carried(draughts.Item) == 0, "and the draughts left the bag");
        await FramesAsync(3);
        Check(shop.Text.StartsWith(UiText.ShopHeader(currency, Session.Inventory.Currency), StringComparison.Ordinal), "the panel shows the new balance");

        // Buy an Antidote with it.
        Press(shop.Tabs.Buy);
        await FramesAsync(3);
        Press(shop.Rows.First(r => r.Item == antidote.Item).Button);
        await FramesAsync(2);
        var before = Session.Inventory.Currency;
        rev = Session.Inventory.Rev;
        Press(shop.Picker.Action);
        await UntilAsync(() => Session.Inventory.Rev > rev, 10, "the purchase's inventory change");
        Check(Session.Inventory.Currency == before - antidote.Price, $"the balance fell by the price ({Session.Inventory.Currency})");
        Check(Session.Inventory.Carried(antidote.Item) == 1, "and the Antidote is in the bag");
        Print($"shop: sold {draughts.Quantity} draughts, bought an antidote; balance {Session.Inventory.Currency}");

        Press(shop.CloseButton);
        await UntilAsync(() => !book.Open && !shop.Open, 10, "Close ending the shop");
        Print("shop: talked to the merchant, opened the shop, was refused for lack of money, sold, bought, closed");
    }

    /// <summary>
    /// T-602: First Hunt end to end. The guide's "!" is drawn; the dialogue offers the hunt and the tracker shows it; slimes are killed and their globs picked up
    /// until the tracker counts 5/5 and 3/3 (the server's counters) and the "!" turns green; the quest log lists it; walking back and talking turns it in: the
    /// globs go, the reward draughts and the currency arrive and the log moves it to completed.
    /// </summary>
    private async Task QuestsRunAsync()
    {
        // No content id is written here (a client rule): the guide is the NPC the server marks with an offer, the quest is the one its dialogue starts, and the
        // objectives' targets come from the server's quest messages.
        await LoginAsync();
        var hud = _main.Hud!;
        var quests = Session.Quests;
        var dialogue = Session.Dialogue;
        var panel = hud.Dialogue!;
        var bundle = _main.Bundle!;
        var presenter = _main.Presenter!;
        await UntilAsync(() => quests.Known && Session.Inventory.Known, 10, "the quest log and the bag after the snapshot");
        EntityView? Marked() => World.Entities.FirstOrDefault(e => e.Kind == EntityKinds.Npc && quests.MarkerFor(e.DisplayKey) == QuestMarkers.Offer);
        await UntilAsync(() => Marked() is not null && presenter.QuestMarkCount > 0, 10, "an NPC with a quest to offer, and its marker drawn");
        var guide = Marked()!;
        var guideKey = guide.DisplayKey;
        Print($"quests: log known, tracker \"{hud.Tracker.Text}\", marker over {bundle.NameOf(guideKey)}: {quests.MarkerFor(guideKey)}, {presenter.QuestMarkCount} marker(s) drawn");
        Check(hud.Tracker.Text == UiText.QuestsButton, "the tracker is the Quests button while nothing is active");

        // Accept it through the dialogue: the first choice leads to the offer, whose first choice accepts.
        await TapWorldAsync(new Vector3((float)guide.X, 0.8f, (float)guide.Z));
        await UntilAsync(() => dialogue.Open, 20, "the guide's dialogue");
        await FramesAsync(2);
        var greeting = dialogue.Text;
        Press(panel.ChoiceButtons[0].Button);
        await UntilAsync(() => dialogue.Text != greeting && dialogue.Choices.Count > 0, 10, "the quest's offer");
        Print($"quests: offer \"{panel.Text}\"");
        Press(panel.ChoiceButtons[0].Button);
        await UntilAsync(() => quests.Active.Count == 1, 10, "the quest accepted");
        var questId = quests.Active[0].Quest;
        var quest = bundle.Quest(questId)!;
        await UntilAsync(() => dialogue.Open && dialogue.Choices.Count == 0, 10, "the accepted node");
        Press(panel.ChoiceButtons[0].Button);
        await UntilAsync(() => !dialogue.Open, 10, "the dialogue's end");
        await FramesAsync(3);
        Print($"quests: accepted {quest.Name}; flash \"{Session.Ui.Flash}\"; tracker \"{hud.Tracker.Text.Replace('\n', '|')}\"");
        Check(quest.Giver == guideKey, "the quest is the marked NPC's");
        Check(hud.Tracker.Text.StartsWith(quest.Name, StringComparison.Ordinal), "the tracker follows the new quest");
        Check(quests.MarkerFor(guideKey) is null, "no marker while nothing is to offer or turn in");

        // Kill and collect: the server counts, the tracker shows its counters.
        Game.Shared.Protocol.QuestObjectiveRecord Objective(string type) => quests.Find(questId)!.Objectives.First(o => o.Type == type);
        var killTarget = Objective(QuestObjectiveTypes.Kill).Target;
        var collectTarget = Objective(QuestObjectiveTypes.Collect).Target;
        Check(_monster is null || _monster == killTarget, "the hunted monster is the quest's");
        var open = NearestOpenCellOutsideSafeZones(CurrentMap(), Own().Cell);
        await TapCellAsync(open);
        await UntilAsync(() => !Own().IsWalking && Own().Cell.X == open.X && Own().Cell.Y == open.Y, 30, "leaving the safe zone");
        var shown = string.Empty;
        for (var attempt = 0; attempt < 20 && !(Objective(QuestObjectiveTypes.Kill).Met && Objective(QuestObjectiveTypes.Collect).Met); attempt++)
        {
            if (!Objective(QuestObjectiveTypes.Kill).Met) await HuntOneAsync(60);
            await SecondsAsync(1);
            for (var pick = 0; pick < 4; pick++)
            {
                var drop = FindDrop();
                if (drop is null) break;
                var before = Session.Inventory.Rev;
                if (!OnScreen(drop))
                {
                    await TapCellAsync(drop.Cell);
                    await SecondsAsync(1);
                }
                await TapWorldAsync(new Vector3((float)drop.X, 0.15f, (float)drop.Z));
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < 8 && Session.Inventory.Rev == before && World.Find(drop.Id) is not null) await FrameAsync();
                await FramesAsync(2);
            }
            if (hud.Tracker.Text != shown)
            {
                shown = hud.Tracker.Text;
                Print($"quests: tracker \"{shown.Replace('\n', '|')}\"");
            }
        }
        var kills = Objective(QuestObjectiveTypes.Kill);
        Check(kills.Met, "the server counted the kills");
        Check(Objective(QuestObjectiveTypes.Collect).Met, "the server counted the items in the bag");
        Check(hud.Tracker.Text.Contains(UiText.QuestObjective(QuestObjectiveTypes.Kill, bundle.NameOf(killTarget), kills.Need, kills.Need), StringComparison.Ordinal),
            "the tracker shows the full kill count");
        await UntilAsync(() => quests.MarkerFor(guideKey) == QuestMarkers.TurnIn, 5, "the turn-in marker over the guide");
        Print($"quests: ready to turn in; marker over the guide: {quests.MarkerFor(guideKey)}");
        await ShotAsync("-tracker");

        // The quest log, opened from the tracker.
        Press(hud.Tracker);
        await FramesAsync(3);
        Check(hud.Quests!.Open, "the tracker opens the quest log");
        Print($"quests: log \"{hud.Quests.Text}\"");
        Check(hud.Quests.Text.Contains(quest.Step(0), StringComparison.Ordinal), "the log shows the step's text from the bundle");
        await ShotAsync("-questlog");
        Press(hud.Quests.CloseButton);
        await FramesAsync(2);

        // Walk back and talk: the turn-in.
        var need = Objective(QuestObjectiveTypes.Collect).Need;
        int Held(string? item) => Session.Inventory.Entries.Where(e => e.Item == item && e.Slot is null).Sum(e => e.Quantity);
        var carried = Session.Inventory.Entries.Where(e => e.Slot is null).GroupBy(e => e.Item).ToDictionary(g => g.Key, g => g.Sum(e => e.Quantity));
        var collected = Held(collectTarget);
        await TapWorldAsync(new Vector3((float)guide.X, 0.8f, (float)guide.Z));
        await UntilAsync(() => quests.Completed.Contains(questId), 60, "the turn-in by talking");
        await UntilAsync(() => Session.Inventory.Currency > 0, 10, "the currency reward");
        await FramesAsync(3);
        var gained = Session.Inventory.Entries.Where(e => e.Slot is null).GroupBy(e => e.Item)
            .Select(g => (Item: g.Key, More: g.Sum(e => e.Quantity) - carried.GetValueOrDefault(g.Key))).Where(x => x.More > 0).ToList();
        Print($"quests: completed; flash \"{Session.Ui.Flash}\"; {bundle.NameOf(collectTarget)} {collected} -> {Held(collectTarget)}; gained " +
              $"{string.Join(", ", gained.Select(g => bundle.NameOf(g.Item) + " +" + g.More))}; {UiText.Currency(bundle.CurrencyName, Session.Inventory.Currency)}; tracker \"{hud.Tracker.Text}\"");
        Check(Held(collectTarget) == collected - need, "the collected items were taken at the turn-in");
        Check(gained.Count > 0, "the item rewards arrived");
        Check(Session.Inventory.Currency > 0, "the currency reward is in the bag");
        Check(quests.Active.Count == 0 && hud.Tracker.Text == UiText.QuestsButton, "the tracker is the Quests button again");
        if (dialogue.Open) Print($"quests: dialogue now \"{panel.Text}\"");
        Press(hud.Tracker);
        await FramesAsync(3);
        Print($"quests: log \"{hud.Quests.Text}\"");
        Check(hud.Quests.Text.Contains(quest.Name, StringComparison.Ordinal), "the log lists the completed quest");
        Press(hud.Quests.CloseButton);
        Print("quests: offered, accepted, counted, tracked, logged, turned in and rewarded");
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
            // Three with/without pairs and the median of each pass (T-406): one HUD run once read 61 canvas draws where the others read 32 to 34, because a single pair
            // can catch a frame with a transient (a floating number, an icon appearing) on one side only.
            var opaque = new List<double>();
            var shadow = new List<double>();
            var canvas = new List<double>();
            for (var pair = 0; pair < 3; pair++)
            {
                var with = await Sample();
                show(false);
                var without = await Sample();
                show(true);
                opaque.Add(with.Opaque - without.Opaque);
                shadow.Add(with.Shadow - without.Shadow);
                canvas.Add(with.Canvas - without.Canvas);
            }
            double Median(List<double> values) { values.Sort(); return values[values.Count / 2]; }
            Print($"measure: breakdown {what}: 3D {Median(opaque):0.0}, shadow {Median(shadow):0.0}, canvas {Median(canvas):0.0} (median of 3; canvas samples {string.Join(", ", canvas.Select(c => c.ToString("0.0")))}; on screen {CountOnScreen()})");
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
        await Without($"entity visuals ({entities.Count} shown, {meshes} visible meshes, {surfaces} surfaces)", on => { foreach (var n in entities) if (GodotObject.IsInstanceValid(n)) n.Visible = on; }); // a visual can be freed while the frames are sampled (T-405, ADR-002 row 208)
        await Without("HUD", on => { if (GodotObject.IsInstanceValid(_main)) _main.Hud!.SetVisible(on); });
        if (_main.FindChild("DamageNumbers", true, false) is CanvasLayer numbers) await Without($"damage numbers ({_main.Fx?.Shown} drawn)", on => { if (GodotObject.IsInstanceValid(numbers)) numbers.Visible = on; });
        if (_main.Map?.Scene is { } scene) await Without("map scene", on => { if (GodotObject.IsInstanceValid(scene)) scene.Visible = on; });
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
        int maxStatused = 0, maxDebuffed = 0, maxMarks = 0, maxCastBars = 0, maxOutlined = 0;   // T-501, T-502, T-222
        var synthCasts = Array.IndexOf(_args, "--measure-casts") >= 0;   // T-502, measuring only: every entity in view is shown casting, to count the cast bars' cost
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
            if (frames % 10 == 0)
            {
                maxStatused = Math.Max(maxStatused, World.Entities.Count(e => e.Statuses.Count > 0));
                maxDebuffed = Math.Max(maxDebuffed, World.Entities.Count(e => e.HasDebuff));
                maxMarks = Math.Max(maxMarks, _main.Presenter?.StatusMarkCount ?? 0);
                maxCastBars = Math.Max(maxCastBars, _main.Presenter?.CastBarCount ?? 0);
                maxOutlined = Math.Max(maxOutlined, _main.Presenter?.OutlinedCount ?? 0);
                if (synthCasts && frames % 20 == 0)
                {
                    var open = World.Entities.Where(e => !e.Dead).Select(e => new CastRecord(e.Id, "measure", 1500)).ToList();
                    World.ApplyDelta(new DeltaMessage(World.Tick, [], [], [], null, null, open), Clocks.View());
                }
            }
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
        Print($"measure: look (T-222): {(_main.Look?.Enabled == true ? "toon" : "plain")}, at most {maxOutlined} outlined at once (distance {_main.Config.OutlineDistanceCells:0.#} cells, nearest {_main.Config.OutlineMaxCount}), {_main.Look?.Materials ?? 0} toon materials");
        Print($"measure: cast bars (T-502): at most {maxCastBars} drawn at once{(synthCasts ? " (a synthetic cast on every entity in view)" : string.Empty)}");
        Print($"measure: statuses (T-501): at most {maxStatused} entities in view with statuses ({maxDebuffed} with a debuff) and {maxMarks} markers at once; at the end " +
              $"{World.Entities.Count(e => e.Statuses.Count > 0)} with statuses, own [{string.Join(", ", World.Own?.Statuses.Select(st => st.Id) ?? [])}]");
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
        var zoom = camera.Distance;
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
        var nextReport = 15.0;
        while (!condition())
        {
            // A slow wait says where it is (T-406: a measuring client once sat in Reconnecting on a loaded machine and nothing in the log said so).
            if (seconds > nextReport && clock.Elapsed.TotalSeconds > nextReport)
            {
                Print($"still waiting for {what} after {nextReport:0} s: phase {Session.Connection.Phase}, notice {Session.Connection.Notice}, {World.Entities.Count} entities in view");
                nextReport += 15;
            }
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
