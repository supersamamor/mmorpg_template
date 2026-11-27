using Game.Client.Core;
using Game.Client.Core.Chat;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Client;

/// <summary>T-901's client side without the engine: the chat book (what the log holds, what may go out, what is cleared) and the words of the chat.</summary>
public sealed class ChatClientTests
{
    [Fact]
    public void A_line_shows_only_when_the_server_sends_it_and_one_s_own_is_marked()
    {
        var chat = new ChatBook { OwnId = 42 };
        chat.Apply(new ChatStateMessage(120, []));
        chat.Outgoing("  hello  ", out var why).ShouldBe("hello");
        why.ShouldBeNull();
        chat.Lines.ShouldBeEmpty("going out shows nothing");
        chat.Apply(new ChatLineMessage(7, ChatChannels.Map, 42, "Ada", "hello"));
        chat.Apply(new ChatLineMessage(8, ChatChannels.Map, 9, "Bo", "hi"));
        chat.Lines.Select(l => (l.Line, l.Mine)).ShouldBe([(7L, true), (8L, false)]);
        chat.Unread.ShouldBeTrue();
        var version = chat.Version;
        chat.MarkRead();
        (chat.Unread, chat.Version > version).ShouldBe((false, true));
    }

    [Fact]
    public void What_cannot_go_out_is_said_in_the_log_without_asking_the_server()
    {
        var chat = new ChatBook();
        chat.Apply(new ChatStateMessage(5, []));
        chat.Outgoing("   ", out var why).ShouldBeNull();
        why.ShouldBeNull("nothing typed: nothing to say");
        chat.Outgoing("123456", out why).ShouldBeNull();
        why.ShouldBe(UiText.ChatRefused(ChatReasons.TooLong, null, null));
        chat.Outgoing("12345", out _).ShouldBe("12345");
        chat.Outgoing("1234\U0001F600", out why).ShouldBeNull("the server counts UTF-16 units, so does the client");
        chat.SetChannel(ChatChannels.Whisper);
        chat.Outgoing("hi", out why).ShouldBeNull();
        why.ShouldBe(UiText.ChatNoWhisperTarget);
        chat.SetWhisperTo("  Bo ");
        (chat.WhisperTo, chat.Outgoing("hi", out _)).ShouldBe(("Bo", "hi"));
        chat.SetWhisperTo(new string('x', Game.Shared.Accounts.CharacterNameRules.LongestAllowed + 1));
        chat.Outgoing("hi", out why).ShouldBeNull("no name is that long, and the message must stay under the gateway's limit");
        chat.SetChannel("party");
        chat.Channel.ShouldBe(ChatChannels.Whisper, "only map and whisper are chosen here");
    }

    [Fact]
    public void The_log_keeps_the_last_lines_and_nothing_once_signed_out()
    {
        var chat = new ChatBook { OwnId = 1 };
        for (var i = 1; i <= ChatBook.MaxLines + 10; i++) chat.Apply(new ChatLineMessage(i, ChatChannels.Map, 2, "Bo", "line " + i));
        chat.Lines.Count.ShouldBe(ChatBook.MaxLines);
        chat.Lines[0].Line.ShouldBe(11);
        chat.Apply(new ChatStateMessage(120, ["Bo"]));
        chat.IsBlocked("bo").ShouldBeTrue();
        chat.Clear();
        (chat.Lines.Count, chat.Blocked.Count, chat.MaxLength, chat.OwnId, chat.Channel).ShouldBe((0, 0, 0, 0L, ChatChannels.Map));
    }

    [Fact]
    public void Refusals_reports_and_announcements_are_server_lines_in_the_log()
    {
        var chat = new ChatBook();
        chat.Apply(new ChatRefusedMessage(MessageTypes.Chat, ChatReasons.Muted, 4 * 60, ChatChannels.Map));
        chat.Apply(new ChatRefusedMessage(MessageTypes.Chat, ChatReasons.NotOnline, null, ChatChannels.Whisper, "Bo"));
        chat.Apply(new ChatReportedMessage(3));
        chat.Announcement("Noon.");
        chat.Lines.ShouldAllBe(l => l.Channel == ChatChannels.System && l.Line == 0 && l.Name == "");
        chat.Lines.Select(l => l.Text).ShouldBe(["A game master muted you. You can talk again in 4 minutes.", "Bo is not online.", UiText.ChatReported, "Noon."]);
    }

    [Fact]
    public void Every_chat_reason_has_words_and_the_speaker_is_only_the_server_s_name()
    {
        foreach (var reason in ChatReasons.All) UiText.ChatRefused(reason, 30, "Bo").Length.ShouldBeGreaterThan(0, reason);
        UiText.ChatRefused(ChatReasons.RateLimited, 30, null).ShouldBe("You are talking too fast. Wait 30 seconds.");
        UiText.ChatRefused("something_newer", null, null).ShouldBe(UiText.ChatRefused(ChatReasons.Unavailable, null, null));
        UiText.ChatSpeaker(ChatChannels.Map, "Ada", null).ShouldBe("Ada");
        UiText.ChatSpeaker(ChatChannels.Whisper, "Ada", "Bo").ShouldBe("Ada > Bo");
        UiText.ChatSpeaker(ChatChannels.System, "", null).ShouldBe("Server");
        foreach (var channel in ChatChannels.All) UiText.ChatChannel(channel).Length.ShouldBeGreaterThan(0);
    }
}
