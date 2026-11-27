using System.Diagnostics;
using Game.Client.Core;
using Game.Client.Core.Accounts;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted chat runs (T-901), against a server with a database and its admin API, two clients on one map, each with an account of its own:
/// <list type="bullet">
/// <item><c>--e2e-chat</c> (the speaker): enters, asks the script for the second client, learns its name from its first map line, talks on the map and whispers to it through the chat
/// panel, finds that a whisper after the block reads as "not online", says one more map line, then asks the script to take over (announce, read and close the report, mute it), and
/// talks until its line is refused as muted, with how long is left.</item>
/// <item><c>--e2e-chat-listen &lt;name&gt;</c> (the listener): enters, hears the speaker's map line and whisper, whispers back, reports the map line and blocks the speaker through the
/// panel's line actions, and when the script's announcement comes, checks that no line of the speaker reached it after the block.</item>
/// </list>
/// What is said carries the word <c>e2esaid</c>, which the script looks for in the server's log: it must not be there.
/// </summary>
public sealed class ChatRuns
{
    /// <summary>In every line the runs say: the script checks the server log has none.</summary>
    public const string Said = "e2esaid";

    private readonly Main _main;
    private readonly Func<Task> _shot;

    /// <summary>Prepares the runs.</summary>
    public ChatRuns(Main main, Func<Task> shot)
    {
        _main = main;
        _shot = shot;
    }

    private ClientSession Session => _main.Session!;

    private ChatPanel Panel => _main.Hud!.Chat!;

    /// <summary>The speaker.</summary>
    public async Task SpeakerAsync()
    {
        var (username, name) = await EnterAsync("Talk");
        Print($"chat: speaker {name} in the world as entity {Session.World.OwnId}; asking for the listener");
        Print("@ chat-second:" + name);

        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Map && !l.Mine && l.Text.Contains(Said)), 90, "the listener's first map line");
        var listener = Session.Chat.Lines.First(l => l.Channel == ChatChannels.Map && !l.Mine && l.Text.Contains(Said)).Name;
        Print($"chat: heard {listener} on the map");

        // Through the panel: a map line, then a whisper. Nothing shows until the server sends it back.
        Press(Panel.OpenButton);
        await FramesAsync(2);
        Check(Panel.Open, "the Chat button opens the panel");
        await TypeAndSendAsync(Said + " hi there", l => l.Channel == ChatChannels.Map && l.Mine && l.Text == Said + " hi there", "the own map line, from the server");
        Press(Panel.ChannelButton);
        Panel.WhisperField.Text = listener;
        Session.Chat.SetWhisperTo(listener);   // what typing in the field does
        await FramesAsync(2);
        Check(Session.Chat.Channel == ChatChannels.Whisper && Panel.WhisperField.Visible, "the switch goes to whisper and shows the name field");
        await TypeAndSendAsync(Said + " psst", l => l.Channel == ChatChannels.Whisper && l.Mine && l.To == listener, "the own whisper, from the server");
        Check(Panel.LogText.Contains(UiText.ChatSpeaker(ChatChannels.Whisper, name, listener) + ": " + Said + " psst"), "the log shows the whisper apart from its sender's name: " + Panel.LogText);

        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Whisper && l.Name == listener && l.Text == Said + " got it"), 30, "the listener's whisper");
        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Channel == ChatChannels.Map && l.Name == listener && l.Text == Said + " blocked"), 30, "the listener saying it blocked");
        Print("chat: the listener whispered back and says it blocked the speaker");

        // A whisper to someone who blocks reads as "not online"; a map line goes out (and does not reach the blocker: the listener checks that).
        ChatRefusedMessage? refused = null;
        void Refused(ChatRefusedMessage m) => refused = m;
        Session.ChatRefusedReceived += Refused;
        await TypeAndSendAsync(Said + " are you there", null, null);
        await UntilAsync(() => refused is not null, 15, "the refused whisper");
        Check(refused!.Reason == ChatReasons.NotOnline && refused.To == listener, $"the whisper to the blocker reads as not online ({refused.Reason})");
        Check(Panel.LogText.Contains(UiText.ChatRefused(ChatReasons.NotOnline, null, listener)), "and the log says so");
        Press(Panel.ChannelButton);
        await TypeAndSendAsync(Said + " after block", l => l.Channel == ChatChannels.Map && l.Mine && l.Text == Said + " after block", "the own map line after the block");
        await _shot();

        // The script announces (the listener's signal), reads and closes the report, and mutes this account. Talk until the mute holds.
        var announced = false;
        void Announced(AnnouncementMessage m) => announced = true;
        Session.AnnouncementReceived += Announced;
        Print("@ chat-sync:" + username);
        await UntilAsync(() => announced, 60, "the script's announcement");
        refused = null;
        var tries = 0;
        while (refused?.Reason != ChatReasons.Muted)
        {
            if (++tries > 30) throw new InvalidOperationException("never muted: " + refused?.Reason);
            refused = null;
            var before = Session.Chat.Lines.Count(l => l.Mine);
            Panel.TextField.Text = Said + " still here " + tries;
            Press(Panel.SendButton);
            await UntilAsync(() => refused is not null || Session.Chat.Lines.Count(l => l.Mine) > before, 15, "an answer to the line");
            if (refused?.Reason == ChatReasons.Muted) break;
            await SecondsAsync(2);
        }
        Check(refused.RetryAfterSeconds is > 0 and <= 300, "the mute says how long is left: " + refused.RetryAfterSeconds);
        Check(!Session.Chat.Lines.Any(l => l.Mine && l.Text == Said + " still here " + tries), "the muted line was never shown");
        Check(Panel.LogText.Contains(UiText.ChatRefused(ChatReasons.Muted, refused.RetryAfterSeconds, null)), "the log says the player is muted and for how long: " + Panel.LogText);
        Print($"chat: muted after {tries} line(s), {refused.RetryAfterSeconds} s left; the log says \"{UiText.ChatRefused(ChatReasons.Muted, refused.RetryAfterSeconds, null)}\"");
        Session.AnnouncementReceived -= Announced;
        Session.ChatRefusedReceived -= Refused;
        await LeaveAsync();
    }

    /// <summary>The listener: <paramref name="speaker"/> is the speaker's character name.</summary>
    public async Task ListenerAsync(string speaker)
    {
        var (_, name) = await EnterAsync("Ear");
        Print($"chat: listener {name} in the world; waiting for {speaker}");
        var attempts = 0;
        while (!Session.Chat.Lines.Any(l => l.Name == speaker && l.Channel == ChatChannels.Map))
        {
            if (++attempts > 20) throw new InvalidOperationException("the speaker never answered on the map");
            Session.Chat.SetChannel(ChatChannels.Map);
            Session.Say(Said + " hello from " + name);
            await SecondsAsync(3);
        }
        var heard = Session.Chat.Lines.First(l => l.Name == speaker && l.Channel == ChatChannels.Map);
        Check(heard.Text == Said + " hi there" && !heard.Mine && heard.Line > 0, "the speaker's map line, its name from the server: " + heard);
        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Name == speaker && l.Channel == ChatChannels.Whisper && l.To == name), 30, "the speaker's whisper");
        Print("chat: heard the speaker on the map and in a whisper");
        Session.Chat.SetWhisperTo(speaker);
        Check(Session.Say(Said + " got it"), "the whisper back went out");
        await UntilAsync(() => Session.Chat.Lines.Any(l => l.Mine && l.Channel == ChatChannels.Whisper), 15, "the own whisper back");

        // Report the map line and block the speaker through the panel's line actions (a tap on the name selects the line).
        Press(Panel.OpenButton);
        await FramesAsync(2);
        Check(Panel.SelectLast(l => l.Line == heard.Line), "the line is selected");
        var reported = false;
        void Reported(ChatReportedMessage m) => reported = m.Line == heard.Line;
        Session.ChatReportedReceived += Reported;
        Press(Panel.ReportButton);
        await UntilAsync(() => reported, 15, "the report stored");
        Session.ChatReportedReceived -= Reported;
        Check(Panel.LogText.Contains(UiText.ChatReported), "the log says the line was reported");
        Check(Panel.SelectLast(l => l.Name == speaker && l.Line > 0), "a line of the speaker is selected");
        Check(Panel.BlockButton.Text == UiText.ChatBlock, "the action says Block");
        Press(Panel.BlockButton);
        await UntilAsync(() => Session.Chat.IsBlocked(speaker), 15, "the block, from the server");
        var linesBefore = Session.Chat.Lines.Count(l => l.Name == speaker);
        Session.Chat.SetChannel(ChatChannels.Map);
        Check(Session.Say(Said + " blocked"), "the map line went out");
        Print($"chat: reported line {heard.Line} and blocked {speaker}");
        await _shot();

        var announced = false;
        void Announced(AnnouncementMessage m) => announced = true;
        Session.AnnouncementReceived += Announced;
        await UntilAsync(() => announced, 120, "the script's announcement");
        Session.AnnouncementReceived -= Announced;
        await FramesAsync(3);
        Check(Session.Chat.Lines.Count(l => l.Name == speaker) == linesBefore, "no line of the blocked speaker came after the block");
        Check(Session.Chat.Lines.Any(l => l.Channel == ChatChannels.System && l.Text.Length > 0), "the announcement is in the log too");
        Print($"chat: nothing of {speaker} arrived after the block; the announcement is in the log");
        await LeaveAsync();
    }

    private async Task<(string Username, string Name)> EnterAsync(string prefix)
    {
        var accounts = Session.Accounts;
        var screen = _main.Hud!.Accounts!;
        await FramesAsync(3);
        var tag = new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));
        var username = "e2e-chat-" + tag;
        screen.Username.Text = username;
        screen.Password.Text = Guid.NewGuid().ToString("N");   // thrown away with the run
        Press(screen.RegisterButton);
        await UntilAsync(() => accounts.Screen == AccountScreen.Characters && !accounts.Busy, 15, "the character list after registering");
        var name = prefix + tag;
        await LookRuns.CreateAsync(_main, name);
        await UntilAsync(() => screen.Rows.Count > 0 && screen.Rows[0].Play.IsVisibleInTree(), 10, "the character in the list");
        Press(screen.Rows[0].Play);
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && Session.World.Own is not null && _main.LoadedMap == Session.World.MapId && Session.Chat.MaxLength > 0, 20,
            "entering the world, with the chat state");
        await FramesAsync(3);
        Check(Panel.OpenButton.IsVisibleInTree(), "the chat strip shows while playing");
        return (username, name);
    }

    private async Task TypeAndSendAsync(string text, Func<Game.Client.Core.Chat.ChatEntry, bool>? back, string? what)
    {
        var shown = Session.Chat.Lines.Count;
        Panel.TextField.Text = text;
        Press(Panel.SendButton);
        Check(Panel.TextField.Text.Length == 0, "Send empties the field");
        Check(Session.Chat.Lines.Count == shown, "nothing shows before the server sends it back");
        if (back is not null) await UntilAsync(() => Session.Chat.Lines.Any(back), 15, what!);
    }

    private async Task LeaveAsync()
    {
        Session.Logout(Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.LoginScreen, 10, "bye logout");
        await FramesAsync(3);
        Check(Session.Chat.Lines.Count == 0, "no chat is left once signed out");
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
                throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}, notice {Session.Connection.Notice}; chat: " +
                                           string.Join(" | ", Session.Chat.Lines.Select(l => $"{l.Channel} {l.Name}: {l.Text}")));
            await FramesAsync(1);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
