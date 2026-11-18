using System.Text;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using ClientSession = Game.Client.Core.ClientSession;

namespace Game.Tests.Client;

/// <summary>T-503 in the client: the stat book takes the server's numbers as they are, <c>raise_stat</c> carries only a stat id, and every reason has words.</summary>
public class StatClientTests
{
    private sealed class Socket : IClientSocket
    {
        public readonly Queue<SocketEvent> Events = new();
        public readonly List<string> Sent = new();

        public void Open(string url)
        {
        }

        public void Send(byte[] utf8) => Sent.Add(Encoding.UTF8.GetString(utf8));

        public void Close()
        {
        }

        public bool TryDequeue(out SocketEvent socketEvent) => Events.TryDequeue(out socketEvent);

        public void Push(ServerMessage message) => Events.Enqueue(new SocketEvent(SocketEventKind.Message, message));
    }

    private static (ClientSession Session, Socket Socket) Playing()
    {
        var socket = new Socket();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, ClientBundleFixture.Bundle(), socket, new Pcg32Random(1));
        session.Login("Ada", 0);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        socket.Push(new WelcomeMessage(1, 1, "map.starter_meadow", 10, null, "token"));
        socket.Push(new SnapshotMessage(1, "map.starter_meadow", 1,
            [new EntityRecord(1, EntityKinds.Player, "job.wanderer", new GridCell(5, 5), Directions.N, null, 0)], new VitalsRecord(40, 40, 10, 10)));
        session.Update(0, 0);
        socket.Sent.Clear();
        return (session, socket);
    }

    private static StatsMessage Message(int points) =>
        new(points, [new StatRecord("str", 3, 2), new StatRecord("agi", 1, 2), new StatRecord("luk", 99, 0)], [new DerivedRecord("atk", 12), new DerivedRecord("max_hp", 55)]);

    [Fact]
    public void The_stat_book_holds_exactly_what_the_server_sent_and_redraws_on_every_message()
    {
        var (session, socket) = Playing();
        session.Stats.Known.ShouldBeFalse();
        var version = session.Stats.Version;

        socket.Push(Message(4));
        session.Update(0.1, 0.1);

        var book = session.Stats;
        (book.Known, book.Points).ShouldBe((true, 4));
        book.Stats.Select(s => (s.Stat, s.Value, s.NextCost)).ShouldBe([("str", 3, 2), ("agi", 1, 2), ("luk", 99, 0)]);
        book.Derived.Select(d => (d.Stat, d.Value)).ShouldBe([("atk", 12), ("max_hp", 55)]);
        book.Version.ShouldBeGreaterThan(version);
        book.Affordable(book.Stats[0]).ShouldBeTrue();
        book.Affordable(book.Stats[2]).ShouldBeFalse();   // at the cap: no cost to pay
        socket.Push(Message(1));
        session.Update(0.2, 0.2);
        book.Affordable(book.Stats[0]).ShouldBeFalse();   // 2 points for the next step, 1 left

        session.Stats.Reset();
        book.Known.ShouldBeFalse();
    }

    [Fact]
    public void Raising_a_stat_sends_only_the_stat_the_server_prices_it()
    {
        var (session, socket) = Playing();
        session.RaiseStat("str");
        session.RaiseStat("agi", 3);
        socket.Sent.ShouldBe(["""{"type":"raise_stat","stat":"str","amount":1}""", """{"type":"raise_stat","stat":"agi","amount":3}"""]);
    }

    [Fact]
    public void A_refused_raise_says_why_on_the_flash_line_and_every_reason_has_words()
    {
        var (session, socket) = Playing();
        socket.Push(new RejectedMessage(MessageTypes.RaiseStat, StatReasons.NotEnoughPoints, null, Stat: "str"));
        session.Update(0.1, 0.1);
        session.Ui.Flash.ShouldBe(UiText.StatRefused(StatReasons.NotEnoughPoints));
        foreach (var term in StatReasons.All) UiText.StatRefused(term).ShouldNotBeEmpty(term);
        foreach (var term in new[] { ItemReasons.ResetInCombat, ItemReasons.ResetDead, ItemReasons.NothingToReset }) UiText.ItemRefused(term).ShouldNotBeEmpty(term);
        foreach (var stat in PrimaryStats.All.Concat(DerivedStats.Panel)) UiText.StatName(stat).ShouldNotBe(stat);
    }

    [Fact]
    public void The_bundle_marks_the_reset_items_that_ask_first()
    {
        var bundle = ClientBundleFixture.Bundle();
        bundle.Item("item.realign_tonic")!.ConfirmUse.ShouldBeTrue();
        bundle.Item("item.blank_page")!.ConfirmUse.ShouldBeTrue();
        bundle.Item("item.healing_draught")!.ConfirmUse.ShouldBeFalse();
    }
}
