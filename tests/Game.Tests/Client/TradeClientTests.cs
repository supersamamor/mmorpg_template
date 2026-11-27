using Game.Client.Core;
using Game.Client.Core.Trade;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Client;

/// <summary>T-903's client side without the engine: the book holds only what the server said, Confirm is offered only with both locked, and every term has words.</summary>
public sealed class TradeClientTests
{
    private static TradeMessage Trade(int version, bool mineLocked, bool theirsLocked, bool mineConfirmed = false) =>
        new(43, "Bo", version, new TradeSideRecord([new TradeItemRecord(1, "item.iron_blade", 1)], 0, mineLocked, mineConfirmed), new TradeSideRecord([], 250, theirsLocked, false));

    [Fact]
    public void The_book_shows_the_ask_then_the_trade_as_the_server_sent_it_and_how_it_ended()
    {
        var book = new TradeBook();
        book.Apply(new TradeAskedMessage(42, "Ada", 20), 100);
        book.Ask.ShouldBe(new TradeAskView(42, "Ada", 120));
        book.Expire(119.9);
        book.Ask.ShouldNotBeNull();
        book.Apply(Trade(1, false, false));
        (book.Ask, book.Open!.Version).ShouldBe((null, 1), "an open trade answers the ask");
        book.CanConfirm.ShouldBeFalse();
        book.Apply(Trade(2, true, false));
        book.CanConfirm.ShouldBeFalse("both must lock");
        book.Apply(Trade(3, true, true));
        book.CanConfirm.ShouldBeTrue();
        book.Apply(Trade(3, true, true, mineConfirmed: true));
        book.CanConfirm.ShouldBeFalse("confirmed already");
        book.Apply(new TradeClosedMessage(TradeEnds.Done, "Bo"));
        (book.Open, book.Ended).ShouldBe((null, UiText.TradeClosed(TradeEnds.Done, "Bo", false)));
        book.Notice.ShouldBe(book.Ended);

        book.Apply(new TradeAskedMessage(42, "Ada", 20), 200);
        book.Expire(220);
        book.Ask.ShouldBeNull();
        book.Apply(new TradeAskedMessage(42, "Ada", 20), 300);
        book.Apply(new TradeNoticeMessage(TradeNotices.Expired, "Ada"));
        book.Ask.ShouldBeNull();
        book.Apply(new TradeAskedMessage(42, "Ada", 20), 400);
        book.Apply(new TradeRefusedMessage(MessageTypes.TradeAnswer, TradeReasons.NoAsk));
        book.Ask.ShouldBeNull();
        book.Notice.ShouldBe(UiText.TradeRefused(TradeReasons.NoAsk, null));
        book.Apply(Trade(1, false, false));
        book.Clear();
        (book.Open, book.Ask, book.Ended).ShouldBe((null, null, null));
    }

    [Fact]
    public void Every_trade_term_has_its_own_words()
    {
        TradeReasons.All.Ordered.Select(r => UiText.TradeRefused(r, 10)).Distinct().Count().ShouldBe(TradeReasons.All.Count);
        TradeEnds.All.Ordered.Select(r => UiText.TradeClosed(r, "Bo", false)).Distinct().Count().ShouldBe(TradeEnds.All.Count);
        TradeEnds.All.Ordered.Select(r => UiText.TradeClosed(r, "Bo", true)).Distinct().Count().ShouldBe(TradeEnds.All.Count);
        TradeNotices.All.Ordered.Select(n => UiText.TradeNotice(n, "Bo")).Distinct().Count().ShouldBe(TradeNotices.All.Count);
        UiText.TradeLine("Iron Blade", 1, 4, 1).ShouldBe("Iron Blade +4 [1]");
        UiText.TradeLine("Healing Draught", 3, 0, 0).ShouldBe("Healing Draught x3");
        UiText.TradeState("Bo", true, true, false, true).ShouldContain("Bo confirmed");
    }
}
