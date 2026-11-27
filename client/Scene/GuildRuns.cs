using System.Diagnostics;
using Game.Client.Core;
using Game.Client.Core.Accounts;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted guild runs (T-904), against a server with a database, two clients on one map, each with an account of its own:
/// <list type="bullet">
/// <item><c>--e2e-guild</c> (the founder): enters, founds a guild through the guild panel (a name, the last emblem of the picker), asks the script for the second client, learns its name from
/// its first map line, invites it by name through the panel, talks with it on the guild channel, writes the notice, promotes it with the row's Up, and sees it leave.</item>
/// <item><c>--e2e-guild-join &lt;founder&gt;</c> (the member): enters, says a map line, joins through the invite prompt, answers on the guild channel, sees the notice and its new rank,
/// then leaves through the panel.</item>
/// </list>
/// What is said carries the word <c>e2esaid</c>, which the script looks for in the server's log: it must not be there.
/// </summary>
public sealed class GuildRuns
{
    private const string Said = ChatRuns.Said;
    private const string Notice = "e2esaid meet at the well";

    private readonly Main _main;
    private readonly Func<Task> _shot;

    /// <summary>Prepares the runs.</summary>
    public GuildRuns(Main main, Func<Task> shot)
    {
        _main = main;
        _shot = shot;
    }

    private ClientSession Session => _main.Session!;

    private GuildPanel Panel => _main.Hud!.Guild!;

    /// <summary>The founder.</summary>
    public async Task FounderAsync()
    {
        var name = await EnterAsync("Fnd");
        var title = "Oak " + char.ToUpperInvariant(Tag()[0]) + Tag()[..6];
        Press(_main.Hud!.Chat!.OpenButton);
        await FramesAsync(2);
        Press(_main.Hud!.Chat!.GuildButton);
        await FramesAsync(2);
        Check(Panel.Open && Panel.FoundButton.IsVisibleInTree(), "the Guild button opens the founding form");
        Check(Panel.EmblemButtons.Count >= 2, "the theme's emblems are in the picker");
        Press(Panel.EmblemButtons[^1]);
        var emblem = Panel.ChosenEmblem!;
        Panel.NameField.Text = title;
        Press(Panel.FoundButton);
        await UntilAsync(() => Session.Guild.InGuild, 30, "the guild founded");
        Check(Session.Guild.State.Name == title && Session.Guild.State.Emblem == emblem && Session.Guild.Leading, "founded with its name and emblem, led by the founder: " + Session.Guild.State);
        await FramesAsync(2);
        Check(Panel.Text.Contains(title), "the panel shows the guild: " + Panel.Text);
        Print($"guild: {name} founded \"{title}\" with {emblem}; asking for the member");
        Print("@ guild-second:" + name);

        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Map && !l.Mine && l.Text.Contains(Said)), 90, "the member's first map line");
        var member = Session.Chat.Lines.First(l => l.Channel == ChatChannels.Map && !l.Mine && l.Text.Contains(Said)).Name;
        Panel.NameField.Text = member;
        Press(Panel.InviteButton);
        await UntilAsync(() => Session.Guild.State.Members.Count == 2, 60, "the member joined");
        Print($"guild: {member} joined");

        Session.Chat.SetChannel(ChatChannels.Guild);
        Check(Session.Say(Said + " guild hello"), "the guild line went out");
        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Guild && l.Name == member && l.Text == Said + " guild back"), 30, "the member's guild line");
        Print("guild: talked on the guild channel");

        Panel.NoticeField.Text = Notice;
        Press(Panel.NoticeSaveButton);
        await UntilAsync(() => Session.Guild.State.Notice == Notice, 15, "the notice written");
        await FramesAsync(2);
        Press(Panel.PromoteButtonOf(member) ?? throw new InvalidOperationException("no Up on the member's row: " + Panel.Text));
        await UntilAsync(() => Session.Guild.State.Members.Any(m => m.Name == member && m.Rank == 1), 15, "the member promoted");
        Print("guild: wrote the notice and promoted the member to " + Session.Guild.State.Ranks[1].Name);
        await _shot();

        await UntilAsync(() => Session.Guild.State.Members.Count == 1, 60, "the member left");
        Check(Session.Guild.Leading, "the founder still leads");
        Print("guild: the member left; the guild has its founder");
        Session.LeaveGuild();   // the last member: the guild ends and its name is free
        await UntilAsync(() => !Session.Guild.InGuild, 15, "the guild disbanded");
        await LeaveAsync();
    }

    /// <summary>The member: <paramref name="founder"/> is the founder's character name.</summary>
    public async Task MemberAsync(string founder)
    {
        var name = await EnterAsync("Mbr");
        Print($"guild: member {name} in the world; waiting for {founder}'s invite");
        var attempts = 0;
        while (Session.Guild.Invite is null)
        {
            if (++attempts > 20) throw new InvalidOperationException("no guild invite came");
            Session.Chat.SetChannel(ChatChannels.Map);
            Session.Say(Said + " hello from " + name);
            await SecondsAsync(3);
        }
        await FramesAsync(2);
        Check(Panel.PromptShown, "the invite prompt shows");
        Check(Session.Guild.Invite!.Name == founder, "the invite names the founder");
        Press(Panel.AcceptButton);
        await UntilAsync(() => Session.Guild.InGuild && Session.Guild.State.Members.Count == 2, 30, "in the guild");
        Check(Session.Guild.OwnRank == Session.Guild.State.Ranks.Count - 1, "new members join at the last rank");
        Check(Session.Guild.State.Members.Single(m => m.Name == founder).Online, "the founder shows online");

        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Guild && l.Name == founder && l.Text == Said + " guild hello"), 30, "the founder's guild line");
        Session.Chat.SetChannel(ChatChannels.Guild);
        Check(Session.Say(Said + " guild back"), "the guild line back went out");

        await UntilAsync(() => Session.Guild.State.Notice == Notice, 30, "the notice");
        await UntilAsync(() => Session.Guild.OwnRank == 1, 30, "the promotion");
        Print("guild: the member sees the notice and its new rank " + Session.Guild.State.Ranks[1].Name);
        Press(_main.Hud!.Chat!.OpenButton);
        await FramesAsync(2);
        Press(_main.Hud!.Chat!.GuildButton);
        await FramesAsync(2);
        Check(Panel.Text.Contains("meet at the well"), "the panel shows the notice: " + Panel.Text);
        Press(Panel.LeaveButton);
        await UntilAsync(() => !Session.Guild.InGuild, 15, "out of the guild");
        await LeaveAsync();
    }

    private static string Tag() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));

    private async Task<string> EnterAsync(string prefix)
    {
        var accounts = Session.Accounts;
        var screen = _main.Hud!.Accounts!;
        await FramesAsync(3);
        var tag = Tag();
        var username = "e2e-guild-" + tag;
        screen.Username.Text = username;
        screen.Password.Text = Guid.NewGuid().ToString("N");   // thrown away with the run
        Press(screen.RegisterButton);
        await UntilAsync(() => accounts.Screen == AccountScreen.Characters && !accounts.Busy, 15, "the character list after registering");
        var name = prefix + tag;
        await LookRuns.CreateAsync(_main, name);
        await UntilAsync(() => screen.Rows.Count > 0 && screen.Rows[0].Play.IsVisibleInTree(), 10, "the character in the list");
        Press(screen.Rows[0].Play);
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && Session.World.Own is not null && _main.LoadedMap == Session.World.MapId && Session.Chat.MaxLength > 0, 20,
            "entering the world");
        await FramesAsync(3);
        return name;
    }

    private async Task LeaveAsync()
    {
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        await FramesAsync(3);
        Check(!Session.Guild.InGuild, "no guild is left once signed out");
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
                throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}; guild: {Session.Guild.Notice} {Session.Guild.State}");
            await FramesAsync(1);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
