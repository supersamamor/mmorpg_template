using Game.Server.Trade;
using Game.Server.World;
using Game.Server.World.Trade;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Chat;
using Game.Tests.Party;

namespace Game.Tests.Trade;

/// <summary>T-903's gate before the tick: the per-account ask limit, and a block or an absent player answered alike, as T-902 does.</summary>
public class TradeGateTests
{
    private readonly FakePartyDirectory _directory = new();
    private readonly List<IIntent> _queued = new();
    private readonly FakeTime _time = new() { Now = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero) };

    private TradeGate New(TradeOptions? options = null) => new(options ?? new TradeOptions(), _directory, _queued.Add, _time);

    private static TradeRefusedMessage? Refusal(Game.Server.Chat.ChatPeer peer) => ((ListSink)peer.Sink).Messages.OfType<TradeRefusedMessage>().LastOrDefault();

    [Fact]
    public void An_ask_to_a_player_goes_to_the_tick_and_one_to_nobody_or_to_someone_who_blocks_reads_as_not_online()
    {
        var gate = New(new TradeOptions { AskBurst = 100, AsksPerMinute = 100 });
        var (ada, bo, cy) = (_directory.Add(1, "Ada"), _directory.Add(2, "Bo"), _directory.Add(3, "Cy"));
        gate.Ask(ada, 2);
        _queued.ShouldBe([new TradeAsk(new EntityId(1), new EntityId(2))]);
        gate.Ask(ada, 99);
        Refusal(ada)!.Reason.ShouldBe(TradeReasons.NotOnline);
        _directory.BlockList.Add((cy.Character, ada.Character));
        gate.Ask(ada, 3);
        Refusal(ada).ShouldBe(new TradeRefusedMessage(MessageTypes.TradeAsk, TradeReasons.NotOnline), "a block does not show");
        _directory.Drop(bo);
        gate.Ask(ada, 2);
        Refusal(ada)!.Reason.ShouldBe(TradeReasons.NotOnline, "in its grace period");
        gate.Ask(ada, 1);
        Refusal(ada)!.Reason.ShouldBe(TradeReasons.InvalidTarget);
        _queued.Count.ShouldBe(1);
    }

    [Fact]
    public void Asks_are_limited_per_account_and_say_when_the_next_may_go()
    {
        var gate = New(new TradeOptions { AskBurst = 2, AsksPerMinute = 6 });
        var (ada, bo) = (_directory.Add(1, "Ada"), _directory.Add(2, "Bo"));
        gate.Ask(ada, 2);
        gate.Ask(ada, 2);
        gate.Ask(ada, 2);
        _queued.Count.ShouldBe(2);
        var refused = Refusal(ada)!;
        refused.Reason.ShouldBe(TradeReasons.RateLimited);
        refused.RetryAfterSeconds.ShouldBe(10);
        gate.Ask(bo, 1);
        _queued.Count.ShouldBe(3, "another account has its own");
        _time.Now = _time.Now.AddSeconds(10);
        gate.Ask(ada, 2);
        _queued.Count.ShouldBe(4);
    }

    [Fact]
    public void Trade_options_parse_and_refuse_unusable_values()
    {
        var parsed = TradeOptions.Parse(["--trade-ask-burst", "5"], name => name == "GAME_TRADE_ASKS_PER_MINUTE" ? "2.5" : null, out var error);
        error.ShouldBeNull();
        (parsed!.AskBurst, parsed.AsksPerMinute).ShouldBe((5, 2.5));
        TradeOptions.Parse(["--trade-nothing", "1"], _ => null, out error).ShouldBeNull();
        error!.ShouldContain("unknown trade option");
        new TradeOptions { AskBurst = 0 }.Problems().ShouldNotBeEmpty();
        new TradeOptions().Problems().ShouldBeEmpty();
    }
}
