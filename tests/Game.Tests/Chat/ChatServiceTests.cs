using Game.Server.Admin;
using Game.Server.Chat;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Server;

namespace Game.Tests.Chat;

/// <summary>
/// T-901's chat service on its own: who hears a line (map, another map, whisper, nobody online, blocked), the seam for party and guild with a double, the mute through the real
/// <see cref="ModerationChecks"/> and its cache, the rate limit at its edges, reports (the server's copy of the line, never the client's), and that nothing a player wrote is logged.
/// </summary>
public sealed class ChatServiceTests
{
    private readonly FakeDirectory _directory = new();
    private readonly MemoryChatStore _store = new();
    private readonly FakeModeration _moderation = new();
    private readonly FakeTime _time = new() { Now = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero) };
    private readonly ListLog _log = new();
    private readonly ChatService _chat;
    private readonly ModerationChecks _checks;

    public ChatServiceTests()
    {
        _checks = new ModerationChecks(_moderation, _time);
        _chat = new ChatService(new ChatOptions(), _directory, _store, _checks.SpeakAsync, _time, _log, _store);
    }

    private ChatService With(ChatOptions options) => new(options, _directory, _store, _checks.SpeakAsync, _time, _log, _store);

    private static ListSink Sink(ChatPeer peer) => (ListSink)peer.Sink;

    private Task Say(ChatPeer who, string text, string channel = ChatChannels.Map, string? to = null, ChatService? chat = null) =>
        (chat ?? _chat).HandleAsync(who, new ChatMessage(channel, to, text));

    private (ChatPeer Ada, ChatPeer Bo, ChatPeer Cy, ChatPeer Far) Four()
    {
        var ada = _directory.Add(1, "Ada", "map.meadow");
        var bo = _directory.Add(2, "Bo", "map.meadow");
        var cy = _directory.Add(3, "Cy", "map.meadow");
        var far = _directory.Add(4, "Far", "map.cave");
        foreach (var p in _directory.Peers) _store.Know(p);
        return (ada, bo, cy, far);
    }

    // ---- routing ----

    [Fact]
    public async Task A_map_line_reaches_everyone_on_the_map_the_sender_too_and_nobody_on_another_map()
    {
        var (ada, bo, cy, far) = Four();
        await Say(ada, "  hello   meadow ");
        foreach (var peer in new[] { ada, bo, cy })
        {
            var line = Sink(peer).Lines.ShouldHaveSingleItem();
            (line.Channel, line.From, line.Name, line.Text, line.To).ShouldBe((ChatChannels.Map, 1L, "Ada", "hello meadow", (string?)null));
        }
        Sink(far).Messages.ShouldBeEmpty();
        Sink(ada).Lines[0].Line.ShouldBe(Sink(bo).Lines[0].Line, "one line, one number");
    }

    [Fact]
    public async Task A_whisper_reaches_the_one_named_in_any_letter_case_on_any_map_and_its_sender()
    {
        var (ada, bo, _, far) = Four();
        await Say(ada, "psst", ChatChannels.Whisper, "  fAR ");
        var line = Sink(far).Lines.ShouldHaveSingleItem();
        (line.Channel, line.Name, line.To, line.Text).ShouldBe((ChatChannels.Whisper, "Ada", "Far", "psst"));
        Sink(ada).Lines.ShouldHaveSingleItem().ShouldBe(line);
        Sink(bo).Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_whisper_to_nobody_online_says_only_that()
    {
        var (ada, bo, _, _) = Four();
        await Say(ada, "anyone?", ChatChannels.Whisper, "Ghost");
        var refused = Sink(ada).Refusals.ShouldHaveSingleItem();
        (refused.Intent, refused.Reason, refused.To, refused.Channel).ShouldBe((MessageTypes.Chat, ChatReasons.NotOnline, "Ghost", ChatChannels.Whisper));
        Sink(ada).Lines.ShouldBeEmpty("never echoed: the server did not take it");
        Sink(bo).Messages.ShouldBeEmpty();

        await Say(ada, "me?", ChatChannels.Whisper, "ada");
        Sink(ada).Refusals[^1].Reason.ShouldBe(ChatReasons.InvalidTarget);
        await Say(ada, "", ChatChannels.Whisper, null);
        Sink(ada).Refusals[^1].Reason.ShouldBe(ChatReasons.Empty);
    }

    [Theory]
    [InlineData(ChatChannels.System)]
    [InlineData("party")]
    [InlineData("")]
    public async Task System_and_unknown_channels_are_refused(string channel)
    {
        var (ada, bo, _, _) = Four();
        await Say(ada, "hi", channel);
        Sink(ada).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.InvalidChannel);
        Sink(bo).Messages.ShouldBeEmpty();
    }

    // ---- the seam ----

    /// <summary>A channel whose members something else knows: what a party (T-902) or a guild (T-904) will register.</summary>
    private sealed class Members(params long[] ids) : IChannelMembers
    {
        public int Asked;

        public IReadOnlyCollection<EntityId> MembersFor(ChatPeer speaker)
        {
            Asked++;
            return ids.Contains(speaker.Character.Value) ? ids.Select(i => new EntityId(i)).ToList() : [];
        }
    }

    [Fact]
    public async Task A_registered_channel_goes_to_the_members_its_owner_names_with_every_rule_of_chat()
    {
        var (ada, bo, cy, far) = Four();
        var members = new Members(1, 4, 99);   // 99 does not play now: skipped
        _chat.Register("test_group", members);
        await Say(ada, "to my group", "test_group");
        foreach (var peer in new[] { ada, far }) Sink(peer).Lines.ShouldHaveSingleItem().Channel.ShouldBe("test_group");
        Sink(bo).Messages.ShouldBeEmpty();
        Sink(cy).Messages.ShouldBeEmpty();

        await Say(bo, "let me in", "test_group");
        Sink(bo).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.InvalidChannel, "not a member");

        _moderation.Mutes[ada.AccountId!.Value] = _time.Now.UtcDateTime.AddMinutes(5);
        _checks.Forget(ada.AccountId.Value);
        await Say(ada, "muted here too", "test_group");
        Sink(ada).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.Muted);
        Sink(far).Lines.Count.ShouldBe(1);

        Should.Throw<ArgumentException>(() => _chat.Register(ChatChannels.Map, members));
        Should.Throw<ArgumentException>(() => _chat.Register("test_group", members));
    }

    // ---- mute ----

    [Fact]
    public async Task A_muted_sender_is_told_how_long_is_left_and_the_line_goes_nowhere_with_one_read_per_cache_period()
    {
        var (ada, bo, _, _) = Four();
        _moderation.Mutes[ada.AccountId!.Value] = _time.Now.UtcDateTime.AddMinutes(4).AddSeconds(1);
        for (var i = 0; i < 4; i++) await Say(ada, "line " + i);
        var refusals = Sink(ada).Refusals;
        refusals.Count.ShouldBe(4);
        refusals.ShouldAllBe(r => r.Reason == ChatReasons.Muted && r.RetryAfterSeconds == 241);
        Sink(ada).Lines.ShouldBeEmpty();
        Sink(bo).Messages.ShouldBeEmpty();
        _moderation.Reads.ShouldBe(1, "the cache answers a burst");

        _time.Now += TimeSpan.FromMilliseconds(ModerationChecks.CacheMs);
        await Say(ada, "again");
        _moderation.Reads.ShouldBe(2);

        _time.Now += TimeSpan.FromMinutes(5);   // the mute is over
        await Say(ada, "free");
        Sink(bo).Lines.ShouldHaveSingleItem().Text.ShouldBe("free");
    }

    [Fact]
    public async Task Speaking_reads_the_database_only_through_the_mute_cache()
    {
        var (ada, bo, _, _) = Four();
        await _chat.LoadBlocksAsync(bo.AccountId!.Value, default);
        var reads = _store.BlockReads;
        for (var i = 0; i < 5; i++) await Say(ada, "line " + i);
        _moderation.Reads.ShouldBe(1);
        _store.BlockReads.ShouldBe(reads, "block lists are in memory while their accounts play");
    }

    [Fact]
    public async Task When_the_mute_cannot_be_checked_nothing_is_said()
    {
        var (ada, bo, _, _) = Four();
        _moderation.Down = true;
        await Say(ada, "hello");
        Sink(ada).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.Unavailable);
        Sink(bo).Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dev_login_has_no_account_to_mute_and_is_counted_by_its_entity()
    {
        var dev = _directory.Add(9, "Dev", "map.meadow", account: false);
        var bo = _directory.Add(2, "Bo", "map.meadow");
        await Say(dev, "hi");
        Sink(bo).Lines.ShouldHaveSingleItem();
        _moderation.Reads.ShouldBe(0);
        await _chat.HandleAsync(dev, new BlockMessage("Bo"));
        Sink(dev).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.Unavailable);
    }

    // ---- rate ----

    [Fact]
    public async Task The_rate_limit_lets_the_burst_through_refuses_the_next_and_gives_one_back_per_refill_interval()
    {
        var (ada, bo, _, _) = Four();
        var chat = With(new ChatOptions { Burst = 3, LinesPerMinute = 6 });   // one back every 10 s
        for (var i = 0; i < 3; i++) await Say(ada, "line " + i, chat: chat);
        Sink(bo).Lines.Count.ShouldBe(3);
        await Say(ada, "one too many", chat: chat);
        var refused = Sink(ada).Refusals.ShouldHaveSingleItem();
        (refused.Reason, refused.RetryAfterSeconds).ShouldBe((ChatReasons.RateLimited, (long?)10));
        Sink(bo).Lines.Count.ShouldBe(3);

        _time.Now += TimeSpan.FromSeconds(9.9);
        await Say(ada, "still too soon", chat: chat);
        (Sink(ada).Refusals[^1].Reason, Sink(ada).Refusals[^1].RetryAfterSeconds).ShouldBe((ChatReasons.RateLimited, (long?)1));
        _time.Now += TimeSpan.FromSeconds(0.1);
        await Say(ada, "now", chat: chat);
        Sink(bo).Lines[^1].Text.ShouldBe("now");
        await Say(ada, "and not again", chat: chat);
        Sink(bo).Lines.Count.ShouldBe(4);
    }

    [Fact]
    public async Task The_rate_limit_is_the_account_s_not_the_connection_s()
    {
        var (ada, bo, _, _) = Four();
        var chat = With(new ChatOptions { Burst = 2, LinesPerMinute = 1 });
        await Say(ada, "1", chat: chat);
        await Say(ada, "2", chat: chat);
        // The same account on a new connection (a resume, a new sign-in): a new peer, the same account.
        var again = ada with { Sink = new ListSink() };
        await Say(again, "3", chat: chat);
        Sink(again).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.RateLimited);
        await Say(bo, "another account has its own", chat: chat);
        Sink(bo).Lines[^1].Text.ShouldBe("another account has its own");
    }

    // ---- blocks ----

    [Fact]
    public async Task A_blocked_character_s_lines_no_longer_reach_the_blocker_and_its_whispers_read_as_not_online()
    {
        var (ada, bo, cy, _) = Four();
        await _chat.LoadBlocksAsync(bo.AccountId!.Value, default);
        await _chat.HandleAsync(bo, new BlockMessage("ADA"));
        var state = Sink(bo).Messages.OfType<ChatStateMessage>().ShouldHaveSingleItem();
        state.Blocked.ShouldBe(["Ada"]);
        state.MaxLength.ShouldBe(new ChatOptions().MaxLength);

        await Say(ada, "can you hear me");
        Sink(bo).Lines.ShouldBeEmpty();
        Sink(cy).Lines.ShouldHaveSingleItem();
        Sink(ada).Lines.ShouldHaveSingleItem("the sender is not told");

        await Say(ada, "psst", ChatChannels.Whisper, "Bo");
        Sink(ada).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.NotOnline, "the same answer as nobody online");
        Sink(bo).Lines.ShouldBeEmpty();

        await Say(bo, "I still talk to you", ChatChannels.Whisper, "Ada");
        Sink(ada).Lines[^1].Text.ShouldBe("I still talk to you", "a block is one way");

        await _chat.HandleAsync(bo, new BlockMessage("Ada"));
        Sink(bo).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.AlreadyBlocked);
        await _chat.HandleAsync(bo, new UnblockMessage("ada"));
        Sink(bo).Messages.OfType<ChatStateMessage>().Last().Blocked.ShouldBeEmpty();
        await Say(ada, "back");
        Sink(bo).Lines.Where(l => l.From == ada.Character.Value).ShouldHaveSingleItem().Text.ShouldBe("back");
        await _chat.HandleAsync(bo, new UnblockMessage("Ada"));
        Sink(bo).Refusals[^1].Reason.ShouldBe(ChatReasons.NotBlocked);
    }

    [Fact]
    public async Task Blocks_need_a_known_other_character_and_stop_at_the_limit()
    {
        var (ada, bo, cy, far) = Four();
        var chat = With(new ChatOptions { BlockLimit = 1, Burst = 10 });
        await chat.HandleAsync(ada, new BlockMessage("Nobody"));
        Sink(ada).Refusals[^1].Reason.ShouldBe(ChatReasons.UnknownCharacter);
        await chat.HandleAsync(ada, new BlockMessage("Ada"));
        Sink(ada).Refusals[^1].Reason.ShouldBe(ChatReasons.InvalidTarget);
        await chat.HandleAsync(ada, new BlockMessage("Bo"));
        await chat.HandleAsync(ada, new BlockMessage("Cy"));
        Sink(ada).Refusals[^1].Reason.ShouldBe(ChatReasons.BlockLimit);
        // An offline character is found by its stored name.
        _directory.Peers.Remove(far);
        await chat.HandleAsync(bo, new BlockMessage("far"));
        Sink(bo).Messages.OfType<ChatStateMessage>().Last().Blocked.ShouldBe(["Far"]);
    }

    [Fact]
    public async Task The_block_list_is_read_once_at_sign_in_and_sent_after_every_welcome()
    {
        var (ada, bo, _, _) = Four();
        await _chat.LoadBlocksAsync(bo.AccountId!.Value, default);
        await _chat.HandleAsync(bo, new BlockMessage("Ada"));
        var other = With(new ChatOptions());   // another server process, or this one after a restart
        await other.LoadBlocksAsync(bo.AccountId.Value, default);
        other.StateFor(bo.AccountId).Blocked.ShouldBe(["Ada"]);
        await Say(ada, "hello", chat: other);
        Sink(bo).Lines.ShouldBeEmpty();
        other.StateFor(null).Blocked.ShouldBeEmpty("a dev login blocks nobody");

        _store.Down = true;
        var third = With(new ChatOptions());
        await third.LoadBlocksAsync(bo.AccountId.Value, default);   // never throws: the player plays, and it is logged
        _log.Lines.ShouldContain(l => l.StartsWith("WARN [chat]") && l.Contains("block list"));
    }

    // ---- reports ----

    [Fact]
    public async Task A_report_stores_the_server_s_copy_of_a_line_the_reporter_received_with_ids_and_times()
    {
        var (ada, bo, cy, far) = Four();
        await Say(ada, "something \u202Enasty");
        var line = Sink(bo).Lines.Single();
        _time.Now += TimeSpan.FromSeconds(30);
        await _chat.HandleAsync(bo, new ReportMessage(line.Line));
        Sink(bo).Messages.OfType<ChatReportedMessage>().ShouldHaveSingleItem().Line.ShouldBe(line.Line);
        var report = _store.Reports.ShouldHaveSingleItem();
        (report.Text, report.Channel).ShouldBe(("something nasty", ChatChannels.Map), "what the server sent, cleaned; never anything the client says");
        (report.SenderAccountId, report.SenderCharacterId, report.ReporterAccountId, report.ReporterCharacterId)
            .ShouldBe((ada.AccountId!.Value, ada.CharacterId!.Value, bo.AccountId!.Value, bo.CharacterId!.Value));
        report.SaidAt.ShouldBe(_time.Now.UtcDateTime.AddSeconds(-30));
        report.ReportedAt.ShouldBe(_time.Now.UtcDateTime);

        await _chat.HandleAsync(bo, new ReportMessage(line.Line));
        Sink(bo).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.AlreadyReported);
        await _chat.HandleAsync(far, new ReportMessage(line.Line));
        Sink(far).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.UnknownLine, "it never reached that player");
        await _chat.HandleAsync(ada, new ReportMessage(line.Line));
        Sink(ada).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.UnknownLine, "one's own line");
        await _chat.HandleAsync(cy, new ReportMessage(line.Line + 1000));
        Sink(cy).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.UnknownLine);
        _store.Reports.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_line_can_be_reported_only_within_the_window_and_among_the_reporter_s_last_lines()
    {
        var (ada, bo, cy, _) = Four();
        var chat = With(new ChatOptions { ReportWindowSeconds = 60, ReportableLinesPerPlayer = 3, Burst = 100 });
        await Say(ada, "old", chat: chat);
        var old = Sink(bo).Lines.Single().Line;
        _time.Now += TimeSpan.FromSeconds(61);
        await chat.HandleAsync(bo, new ReportMessage(old));
        Sink(bo).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.UnknownLine);

        await Say(ada, "first", chat: chat);
        var first = Sink(cy).Lines.Last().Line;
        for (var i = 0; i < 3; i++) await Say(ada, "more " + i, chat: chat);
        await chat.HandleAsync(cy, new ReportMessage(first));
        Sink(cy).Refusals.ShouldHaveSingleItem().Reason.ShouldBe(ChatReasons.UnknownLine, "it fell out of the last three");
        await chat.HandleAsync(cy, new ReportMessage(Sink(cy).Lines.Last().Line));
        Sink(cy).Messages.OfType<ChatReportedMessage>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Recent_lines_stay_within_their_cap()
    {
        var (ada, _, _, _) = Four();
        var chat = With(new ChatOptions { RecentLinesMax = 5, Burst = 100 });
        for (var i = 0; i < 20; i++) await Say(ada, "x" + i, chat: chat);
        chat.Recent.Count.ShouldBe(5);
    }

    // ---- logs ----

    [Fact]
    public async Task Nothing_a_player_wrote_is_logged()
    {
        var (ada, bo, _, _) = Four();
        await Say(ada, "SECRET-map-words");
        await Say(ada, "SECRET-whisper-words", ChatChannels.Whisper, "Bo");
        await Say(ada, "SECRET-to-nobody", ChatChannels.Whisper, "SECRET-name");
        await _chat.HandleAsync(bo, new ReportMessage(Sink(bo).Lines[0].Line));
        await _chat.HandleAsync(bo, new BlockMessage("SECRET-block"));
        _store.Down = true;
        await _chat.HandleAsync(bo, new ReportMessage(Sink(bo).Lines[1].Line));
        _log.Lines.ShouldNotBeEmpty();
        _log.Lines.ShouldNotContain(l => l.Contains("SECRET"));
    }
}
