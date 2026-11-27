using System.Diagnostics;
using System.Globalization;
using Game.Client.Core;
using Game.Client.Core.Net;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted party runs (T-902), two dev-login clients on one map against a real server:
/// <list type="bullet">
/// <item><c>--e2e-party &lt;ally&gt; &lt;base&gt; &lt;job&gt;</c> (the leader): enters, asks the script for the second client, invites it by name through the party panel (opened from the chat
/// panel) until the invite goes out, waits for it to join, says a line on the party channel through the chat panel and hears the answer, waits until the ally (who follows it by the
/// cell the server reports) is near, kills a Gel Slime alone, checks its own EXP gain is <c>base</c> and <c>job</c>, and leaves the party with the panel's Leave.</item>
/// <item><c>--e2e-party-join &lt;leader&gt; &lt;base&gt; &lt;job&gt;</c> (the ally): enters, answers the invite prompt with Join, follows the leader by the cell the server reports, answers
/// on the party channel, checks its EXP gain from the leader's kill (it never hit), and sees the party end when the leader leaves.</item>
/// </list>
/// The script works the expected numbers out of <c>content/</c> (the monster's EXP and <c>party_exp</c>); the clients decide nothing.
/// </summary>
public sealed class PartyRuns
{
    private const string Said = "e2esaid";

    private readonly Main _main;
    private readonly string _monster;

    /// <summary>Prepares the runs; <paramref name="monster"/> is the monster id the leader hunts (the script's <c>--e2e-monster</c>, never written here).</summary>
    public PartyRuns(Main main, string monster)
    {
        _main = main;
        _monster = monster;
    }

    private ClientSession Session => _main.Session!;

    private WorldView World => Session.World;

    /// <summary>The leader.</summary>
    public async Task LeaderAsync(string ally, long baseGain, long jobGain)
    {
        await EnterAsync();
        Print($"party: leader {_main.Config.DevName} in the world as {World.OwnId}; asking for {ally}");
        Print("@ party-second:" + _main.Config.DevName);
        var hud = _main.Hud!;
        Press(hud.Chat!.OpenButton);
        await FramesAsync(2);
        Press(hud.Chat.PartyButton);
        await FramesAsync(2);
        var panel = hud.Party!;
        Check(panel.Open && !hud.Chat.Open, "the chat panel's Party button opens the party panel");

        // Invite by name until the ally is online.
        var sent = false;
        void Notice(PartyNoticeMessage n) => sent |= n.Notice == PartyNotices.InviteSent;
        Session.PartyNoticeReceived += Notice;
        for (var tries = 0; !sent; tries++)
        {
            if (tries > 40) throw new TimeoutException("the ally never came online");
            panel.NameField.Text = ally;
            Press(panel.InviteButton);
            await SecondsAsync(1.5);
        }
        Session.PartyNoticeReceived -= Notice;
        await UntilAsync(() => Session.Party.InParty && Session.Party.Members.Count == 2, 30, "the ally joining");
        Check(Session.Party.Leading, "the inviter leads");
        await FramesAsync(2);
        Check(panel.Text.Contains(ally), "the panel lists the ally: " + panel.Text);
        Print("party: formed: " + panel.Text);
        Press(FindClose(panel));

        // Party chat through the chat panel: map, whisper, party.
        var chat = hud.Chat;
        Press(chat.OpenButton);
        await FramesAsync(2);
        Press(chat.ChannelButton);
        Press(chat.ChannelButton);
        Check(Session.Chat.Channel == ChatChannels.Party, "the channel switch reaches the party channel: " + Session.Chat.Channel);
        chat.TextField.Text = Said + " party hello";
        Press(chat.SendButton);
        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Party && l.Mine && l.Text == Said + " party hello"), 15, "the own party line, from the server");
        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Party && l.Name == ally && l.Text == Said + " party ok"), 30, "the ally's party line");
        Print("party: talked on the party channel");
        chat.SetOpen(false);

        // The kill: the ally near, the leader alone on a slime.
        var before = World.Exp ?? throw new InvalidOperationException("no exp yet");
        var killed = false;
        var clock = Stopwatch.StartNew();
        while (!killed)
        {
            if (clock.Elapsed.TotalSeconds > 180) throw new TimeoutException("no slime killed with the ally near");
            var allyAt = AllyCell(ally);
            var monster = NearestSlime();
            if (monster is null)
            {
                Session.TapGround(StepToward(new GridCell(CurrentMap().Width / 2, CurrentMap().Height / 2)), Clocks.View());
                await SecondsAsync(2);
                continue;
            }
            if (allyAt is not { } near || GridDistance.Chebyshev(near, Own().Cell) > 4)
            {
                await SecondsAsync(1);   // the ally follows by the cell the server sends it
                continue;
            }
            Session.TapEntity(monster.Id);
            await SecondsAsync(0.5);
            if (Session.Ui.Target != monster.Id)
            {
                Session.TapGround(StepToward(monster.Cell), Clocks.View());   // out of the safe zone first
                await SecondsAsync(1.5);
                continue;
            }
            var fight = Stopwatch.StartNew();
            while (Session.Ui.Target == monster.Id && !Session.Ui.Dead && fight.Elapsed.TotalSeconds < 30) await FramesAsync(1);
            killed = World.Find(monster.Id) is { Dead: true } || World.Find(monster.Id) is null;
            if (killed) Print($"party: killed slime #{monster.Id} at {monster.Cell}; ally at {AllyCell(ally)}, own {Own().Cell}");
        }
        await CheckGainAsync(before, baseGain, jobGain, "leader");

        await SecondsAsync(2);   // the ally's EXP came in the same tick; give its client time to see it
        var opened = hud.Chat;
        Press(opened.OpenButton);
        await FramesAsync(2);
        Press(opened.PartyButton);
        await FramesAsync(2);
        Press(panel.LeaveButton);
        await UntilAsync(() => !Session.Party.InParty, 15, "leaving the party");
        Print("party: left");
        await LeaveAsync();
    }

    /// <summary>The ally.</summary>
    public async Task AllyAsync(string leader, long baseGain, long jobGain)
    {
        await EnterAsync();
        Print($"party: ally {_main.Config.DevName} in the world as {World.OwnId}; waiting for {leader}'s invite");
        var panel = _main.Hud!.Party!;
        await UntilAsync(() => panel.PromptShown && Session.Party.Invite?.Name == leader, 90, "the invite prompt");
        Press(panel.AcceptButton);
        await UntilAsync(() => Session.Party.InParty && Session.Party.Members.Count == 2, 15, "joining");
        Check(Session.Party.Members[0].Name == leader && Session.Party.Members[0].Leader, "the inviter leads, listed first");
        Print("party: joined " + leader);

        var follow = true;
        var gained = false;
        var before = World.Exp ?? throw new InvalidOperationException("no exp yet");
        var answered = false;
        var clock = Stopwatch.StartNew();
        while (follow)
        {
            if (clock.Elapsed.TotalSeconds > 240) throw new TimeoutException("the party never ended");
            if (!answered && Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Party && l.Name == leader && l.Text == Said + " party hello"))
            {
                Session.Chat.SetChannel(ChatChannels.Party);
                Check(Session.Say(Said + " party ok"), "the party line goes out");
                answered = true;
            }
            if (!gained && World.Exp is { } now && (now.BaseExp != before.BaseExp || now.JobExp != before.JobExp || now.BaseLevel != before.BaseLevel))
            {
                await CheckGainAsync(before, baseGain, jobGain, "ally");
                gained = true;
            }
            if (!Session.Party.InParty)
            {
                follow = false;
                break;
            }
            if (Session.Party.Members.FirstOrDefault(m => !m.Mine) is { Known: true, Away: false, Cell: { } cell } m2 && m2.Map == World.MapId &&
                GridDistance.Chebyshev(cell, Own().Cell) > 2)
                Session.TapGround(cell, Clocks.View());
            await SecondsAsync(1);
        }
        Check(answered, "the ally heard the leader on the party channel");
        Check(gained, "the ally got EXP from the leader's kill before the party ended");
        Check(Session.Party.Members.Count == 0, "the party of one ended");
        Print("party: the party ended (" + Session.Ui.Flash + ")");
        await LeaveAsync();
    }

    private async Task CheckGainAsync(ExpMessage before, long baseGain, long jobGain, string who)
    {
        await UntilAsync(() => World.Exp is { } e && (e.BaseExp != before.BaseExp || e.JobExp != before.JobExp || e.BaseLevel != before.BaseLevel), 15, "the EXP of the kill");
        await SecondsAsync(0.5);   // one exp message carries both
        var after = World.Exp!;
        Check(after.BaseLevel == before.BaseLevel && after.JobLevel == before.JobLevel, $"{who}: no level changed, so the gain can be read off ({before.BaseLevel}/{before.JobLevel} -> {after.BaseLevel}/{after.JobLevel})");
        var gotBase = after.BaseExp - before.BaseExp;
        var gotJob = after.JobExp - before.JobExp;
        Print($"party: {who} gained {gotBase} base and {gotJob} job EXP (expected {baseGain} and {jobGain})");
        Check(gotBase == baseGain && gotJob == jobGain, $"{who}'s EXP is the formula's: {gotBase}/{gotJob}, not {baseGain}/{jobGain}");
    }

    private GridCell? AllyCell(string ally) =>
        Session.Party.Members.FirstOrDefault(m => m.Name == ally) is { Known: true, Away: false, Cell: { } cell } m && m.Map == World.MapId ? cell : null;

    private EntityView? NearestSlime()
    {
        var own = Own().Cell;
        EntityView? best = null;
        foreach (var e in World.Entities)
        {
            if (e.Kind != EntityKinds.Monster || e.Dead || e.DisplayKey != _monster) continue;
            if (best is null || GridDistance.Chebyshev(e.Cell, own) < GridDistance.Chebyshev(best.Cell, own)) best = e;
        }
        return best;
    }

    private EntityView Own() => World.Own ?? throw new InvalidOperationException("not in the world");

    private Game.Client.Core.Bundle.MapData CurrentMap() =>
        World.MapId is { } id && _main.Bundle!.TryGetMap(id, out var map) && map is not null ? map : throw new InvalidOperationException("no map " + World.MapId);

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

    private static Button FindClose(PartyPanel panel) => panel.CloseButton;

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
        Check(!Session.Party.InParty && Session.Party.Invite is null, "no party is left once signed out");
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
                throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}; party {Session.Party.PartyId} " +
                                           string.Join(", ", Session.Party.Members.Select(m => $"{m.Name} {m.Map} {m.Cell}")) + "; flash " + Session.Ui.Flash);
            await FramesAsync(1);
        }
    }

    /// <summary>The numbers after <paramref name="flag"/>: its value and the two gains.</summary>
    public static (string Name, long Base, long Job)? Args(string[] args, string flag)
    {
        var at = Array.IndexOf(args, flag);
        if (at < 0 || at + 3 >= args.Length) return null;
        return (args[at + 1], long.Parse(args[at + 2], CultureInfo.InvariantCulture), long.Parse(args[at + 3], CultureInfo.InvariantCulture));
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
