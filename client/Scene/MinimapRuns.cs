using System.Diagnostics;
using Game.Client.Core;
using Game.Client.Core.Net;
using Game.Client.Core.Ui;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.World;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted minimap runs (T-812), two dev-login clients on one map against a real server (<c>run-e2e.ps1 -Run minimap</c>):
/// <list type="bullet">
/// <item><c>--e2e-minimap &lt;ally&gt;</c> (the walker): enters with the minimap on, sees the corner map with its own mark, walks a few cells and sees its mark move that far across
/// the map's picture; asks the script for the second client, invites it, and sees it on the corner map and the full map as a party mark where the server says it is; windowed, a
/// picture of each; leaves the party.</item>
/// <item><c>--e2e-minimap-join &lt;walker&gt;</c> (the ally): enters, answers the invite with Join, stays until the party ends.</item>
/// </list>
/// </summary>
public sealed class MinimapRuns
{
    private readonly Main _main;
    private readonly Func<string, Task> _shot;

    /// <summary>Prepares the runs; <paramref name="shot"/> saves a picture with a suffix when the run has <c>--screenshot</c> and a window.</summary>
    public MinimapRuns(Main main, Func<string, Task> shot)
    {
        _main = main;
        _shot = shot;
    }

    private ClientSession Session => _main.Session!;

    private WorldView World => Session.World;

    /// <summary>The walker.</summary>
    public async Task WalkerAsync(string ally)
    {
        await EnterAsync();
        var hud = _main.Hud!;
        var minimap = hud.Minimap!;
        await UntilAsync(() => minimap.CornerShown, 5, "the corner map");
        Check(hud.MinimapWanted && minimap.HasPicture, "the minimap is on and the map has its picture");
        Check(hud.MinimapOverlaps().Count == 0, "the corner map lies over nothing: " + string.Join(", ", hud.MinimapOverlaps()));
        var map = CurrentMap();
        var cellPx = minimap.CornerFrame.CellPx;
        Print($"minimap: corner map {minimap.Corner.Size.X / hud.UnitsPerDp:0} dp, {cellPx:0.0} px a cell, {minimap.Marks.Count} marks on {map.Id}");

        // Walk: the mark moves across the picture as far as the player walked (the frame may scroll with the player, so the picture's own coordinates are compared).
        var start = Own().Cell;
        var before = OnPicture(minimap, MinimapMarkKind.Own, 0) ?? throw new InvalidOperationException("no own mark on the corner map");
        var drawsBefore = minimap.Draws.Corner;
        var goal = start;
        foreach (var (dx, dy) in new[] { (4, 0), (-4, 0), (0, 4), (0, -4), (3, 3), (-3, -3) })
            if (map.IsWalkable(start.X + dx, start.Y + dy))
            {
                goal = new GridCell(start.X + dx, start.Y + dy);
                break;
            }
        Check(!goal.Equals(start), "a walkable cell four away");
        Session.TapGround(goal, Clocks.View());
        await UntilAsync(() => Own().Cell.Equals(goal) && !Own().IsWalking, 15, "the walk to " + goal);
        await FramesAsync(3);
        var after = OnPicture(minimap, MinimapMarkKind.Own, 0) ?? throw new InvalidOperationException("no own mark after the walk");
        var moved = after - before;
        var expected = new Vector2(goal.X - start.X, goal.Y - start.Y) * (float)cellPx;
        Check((moved - expected).Length() < cellPx * 0.6, $"the player's mark moved {moved} on the map's picture for a walk of {expected} ({start} -> {goal})");
        Check(minimap.Draws.Corner > drawsBefore, "the corner map drew the walk");
        Print($"minimap: walked {start} -> {goal}; the mark moved {moved.X:0.0},{moved.Y:0.0} px on the picture (expected {expected.X:0.0},{expected.Y:0.0}); {minimap.Draws.Corner - drawsBefore} redraws");

        // The ally: invited by name, and then on the minimap where the server says it is.
        Print("@ minimap-second:" + _main.Config.DevName);
        var sent = false;
        void Notice(PartyNoticeMessage n) => sent |= n.Notice == Game.Shared.Vocabulary.PartyNotices.InviteSent;
        Session.PartyNoticeReceived += Notice;
        for (var tries = 0; !sent; tries++)
        {
            if (tries > 40) throw new TimeoutException("the ally never came online");
            Session.InviteToParty(ally);
            await SecondsAsync(1.5);
        }
        Session.PartyNoticeReceived -= Notice;
        await UntilAsync(() => Session.Party.InParty && Session.Party.Members.Count == 2, 30, "the ally joining");
        var member = Session.Party.Members.First(m => !m.Mine);
        await UntilAsync(() => minimap.Marks.Any(m => m.Kind == MinimapMarkKind.Party && m.Id == member.Id), 15, "the ally's mark");
        var mark = minimap.Marks.First(m => m.Kind == MinimapMarkKind.Party && m.Id == member.Id);
        var seen = World.Find(member.Id);
        var where = seen is not null ? (seen.X, seen.Z) : Session.Party.Members.First(m => m.Id == member.Id).Cell is { } c ? (c.X + 0.5, c.Y + 0.5) : (double.NaN, double.NaN);
        Check(Math.Abs(mark.X - where.Item1) < 0.01 && Math.Abs(mark.Z - where.Item2) < 0.01, $"the ally's mark is where the {(seen is not null ? "world view" : "server's party_members")} says ({mark.X},{mark.Z})");
        Check(minimap.CornerShows(member.Id), "and on the corner map, so it needs no edge mark");
        await FramesAsync(3);
        Check(hud.Party!.Marks.Count == 0, "no edge mark for a member the corner map shows: " + string.Join(", ", hud.Party.Marks));
        Print($"minimap: the ally ({member.Name}, #{member.Id}) is a party mark at {mark.X:0.0},{mark.Z:0.0} ({(seen is not null ? "in view" : "from party_members")})");
        await _shot("-minimap");
        if (DisplayServer.GetName() != "headless")
        {
            // §9 with a renderer: the frame's draw calls with the corner map and without (the switch), the party standing still.
            OS.LowProcessorUsageMode = false;
            var on = await DrawCallsAsync(3);
            hud.SetMinimapWanted(false);
            await FramesAsync(3);
            var off = await DrawCallsAsync(3);
            hud.SetMinimapWanted(true);
            await FramesAsync(3);
            OS.LowProcessorUsageMode = true;
            Print($"minimap: draw calls avg {on.Avg:0.0} (max {on.Max:0}) with the corner map, {off.Avg:0.0} (max {off.Max:0}) without; {on.Fps:0.0} fps");
        }

        // The full map: the same marks, opened from the chat panel's Map button.
        Press(hud.Chat!.OpenButton);
        await FramesAsync(2);
        Press(hud.Chat.MapButton);
        await FramesAsync(4);
        Check(minimap.Open && !hud.Chat.Open, "the chat panel's Map button opens the full map");
        Check(hud.FindProblems().Count == 0, "the full map fits: " + string.Join(", ", hud.FindProblems()));
        await _shot("-full-map");
        Press(minimap.CloseButton);
        await FramesAsync(2);
        Check(!minimap.Open, "Close closes it");

        Session.LeaveParty();
        await UntilAsync(() => !Session.Party.InParty, 15, "leaving the party");
        await FramesAsync(3);
        Check(!minimap.Marks.Any(m => m.Kind == MinimapMarkKind.Party), "out of the party: no party mark");
        Print("minimap: left the party");
        await LeaveAsync();
    }

    /// <summary>The ally.</summary>
    public async Task AllyAsync(string walker)
    {
        await EnterAsync();
        Print($"minimap: ally {_main.Config.DevName} in the world as {World.OwnId}; waiting for {walker}'s invite");
        await UntilAsync(() => Session.Party.Invite?.Name == walker, 90, "the invite");
        Session.AnswerParty(true);
        await UntilAsync(() => Session.Party.InParty, 15, "joining");
        Print("minimap: joined " + walker);
        await UntilAsync(() => !Session.Party.InParty, 120, "the party ending");
        Print("minimap: the party ended");
        await LeaveAsync();
    }

    /// <summary>A mark's place on the map's picture (canvas units from the picture's north-west corner), whatever part of the map the corner shows.</summary>
    private static Vector2? OnPicture(MinimapView minimap, MinimapMarkKind kind, long id)
    {
        if (minimap.CornerMarkOf(kind, id) is not { } at) return null;
        var frame = minimap.CornerFrame;
        return at + new Vector2((float)(frame.Left * frame.CellPx), (float)(frame.Top * frame.CellPx));
    }

    private EntityView Own() => World.Own ?? throw new InvalidOperationException("not in the world");

    private Game.Client.Core.Bundle.MapData CurrentMap() =>
        World.MapId is { } id && _main.Bundle!.TryGetMap(id, out var map) && map is not null ? map : throw new InvalidOperationException("no map " + World.MapId);

    private async Task EnterAsync()
    {
        await FramesAsync(3);
        Session.Login(_main.Config.DevName, Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && World.Own is not null && _main.LoadedMap == World.MapId, 20, "entering the world");
        await FramesAsync(3);
    }

    private async Task LeaveAsync()
    {
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        await FramesAsync(3);
    }

    private static void Press(Button button)
    {
        Check(button.IsVisibleInTree() && !button.Disabled, $"the button \"{button.Text}\" can be pressed");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private async Task FramesAsync(int count)
    {
        for (var i = 0; i < count; i++) await _main.ToSignal(_main.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task<(double Avg, double Max, double Fps)> DrawCallsAsync(double seconds)
    {
        double sum = 0, max = 0;
        var frames = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            await FramesAsync(1);
            var draws = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            sum += draws;
            max = Math.Max(max, draws);
            frames++;
        }
        return (frames > 0 ? sum / frames : 0, max, frames / clock.Elapsed.TotalSeconds);
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
                throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}; flash {Session.Ui.Flash}");
            await FramesAsync(1);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
