using System.Text;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Content.ClientExport;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;
using ClientSession = Game.Client.Core.ClientSession;

namespace Game.Tests.Client;

/// <summary>The client's half of dialogue (T-601): what it sends, what it holds of what the server sent, and the words it uses.</summary>
public class DialogueClientTests
{
    private sealed class ScriptedSocket : IClientSocket
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

    private static DisplayBundle Bundle()
    {
        var file = ClientBundleExporter.Export(TestGrids.SampleContent);
        DisplayBundle.TryRead(file.FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        return bundle!;
    }

    private static (ClientSession Session, ScriptedSocket Socket) Playing()
    {
        var socket = new ScriptedSocket();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, Bundle(), socket, new Pcg32Random(1));
        session.Login("Ada", 0);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        socket.Push(new WelcomeMessage(1, 1, "map.starter_meadow", 10, null, "secret-token"));
        var all = new List<EntityRecord>
        {
            new(1, EntityKinds.Player, "job.wanderer", new GridCell(12, 12), Directions.N, null, 0),
            new(12, EntityKinds.Npc, "npc.guide", new GridCell(11, 10), Directions.S, null, 0),
        };
        socket.Push(new SnapshotMessage(1, "map.starter_meadow", 1, all, new VitalsRecord(40, 40, 10, 10)));
        session.Update(0, 0);
        session.Connection.Phase.ShouldBe(ConnectionPhase.Online);
        socket.Sent.Clear();
        return (session, socket);
    }

    private static DialogueMessage Node(string text, params (int Index, string Text)[] choices) =>
        new(12, "npc.guide", null, text, choices.Select(c => new DialogueChoiceRecord(c.Index, c.Text)).ToList(), More: false);

    [Fact]
    public void Tapping_an_npc_talks_and_the_panels_buttons_choose_and_close()
    {
        var (session, socket) = Playing();

        session.TapNpc(12);
        session.ChooseDialogue(2);
        session.CloseDialogue();

        socket.Sent.ShouldBe(["""{"type":"talk","target":12}""", """{"type":"dialogue_choice","index":2}""", """{"type":"dialogue_close"}"""]);
    }

    [Fact]
    public void Nothing_goes_out_before_the_welcome_or_while_dead()
    {
        var socket = new ScriptedSocket();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, Bundle(), socket, new Pcg32Random(1));
        session.TapNpc(12);
        session.ChooseDialogue(0);
        session.CloseDialogue();
        socket.Sent.ShouldBeEmpty();

        var (playing, sent) = Playing();
        sent.Push(new YouDiedMessage(null));
        playing.Update(1, 1);
        playing.TapNpc(12);
        sent.Sent.ShouldBeEmpty();
    }

    [Fact]
    public void The_view_holds_the_node_the_server_sent_and_closes_when_it_ends_the_dialogue()
    {
        var (session, socket) = Playing();
        var start = session.Dialogue.Version;

        socket.Push(Node("Hello.", (0, "Hi"), (2, "Bye")));
        session.Update(1, 1);

        var view = session.Dialogue;
        (view.Open, view.Npc, view.Speaker, view.Text).ShouldBe((true, 12L, "npc.guide", "Hello."));
        view.Choices.Select(c => (c.Index, c.Text)).ShouldBe([(0, "Hi"), (2, "Bye")]);
        view.Version.ShouldBeGreaterThan(start);

        socket.Push(new DialogueEndedMessage(DialogueEndReasons.Closed));
        session.Update(2, 2);
        view.Open.ShouldBeFalse();
    }

    [Fact]
    public void A_finished_graph_leaves_its_service_open_and_any_other_ending_closes_it()
    {
        var (session, socket) = Playing();
        ServiceOpenedMessage? raised = null;
        session.ServiceOpened += m => raised = m;

        socket.Push(Node("Open the forge?", (0, "Yes")));
        socket.Push(new ServiceOpenedMessage(ServiceIds.Refine, 12));
        socket.Push(new DialogueEndedMessage(DialogueEndReasons.Finished));
        session.Update(1, 1);
        session.Dialogue.Open.ShouldBeFalse();
        session.Dialogue.Service.ShouldBe(ServiceIds.Refine);
        raised.ShouldBe(new ServiceOpenedMessage(ServiceIds.Refine, 12));

        socket.Push(new DialogueEndedMessage(DialogueEndReasons.MovedAway));
        session.Update(2, 2);
        session.Dialogue.Service.ShouldBeNull();
    }

    [Fact]
    public void A_refine_refused_for_the_service_closes_it_and_says_why()
    {
        var (session, socket) = Playing();
        socket.Push(new ServiceOpenedMessage(ServiceIds.Refine, 12));
        session.Update(1, 1);

        socket.Push(new RejectedMessage(MessageTypes.Refine, ItemReasons.ServiceRequired, null, Entry: 5));
        session.Update(2, 2);

        session.Dialogue.Service.ShouldBeNull();
        session.Ui.Flash.ShouldBe(UiText.ItemRefused(ItemReasons.ServiceRequired));
        session.Ui.Flash.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_refused_talk_or_choice_is_said_on_the_flash_line()
    {
        var (session, socket) = Playing();

        socket.Push(new RejectedMessage(MessageTypes.Talk, DialogueReasons.NoDialogue, null, 12));
        session.Update(1, 1);
        session.Ui.Flash.ShouldBe(UiText.DialogueRefused(DialogueReasons.NoDialogue));

        socket.Push(new RejectedMessage(MessageTypes.DialogueChoice, DialogueReasons.InventoryFull, null));
        session.Update(2, 2);
        session.Ui.Flash.ShouldBe(UiText.DialogueRefused(DialogueReasons.InventoryFull));
    }

    [Fact]
    public void Every_dialogue_refusal_that_needs_a_word_has_one()
    {
        foreach (var reason in DialogueReasons.All.Ordered.Where(r => r is not (DialogueReasons.Dead or DialogueReasons.Cancelled)))
            UiText.DialogueRefused(reason).ShouldNotBeEmpty(reason);
        UiText.DialogueRefused(DialogueReasons.Cancelled).ShouldBeEmpty();
    }

    [Fact]
    public void The_bundle_names_an_npc_for_the_panel_and_carries_no_dialogue_graph()
    {
        var bundle = Bundle();

        var guide = bundle.Npc("npc.guide").ShouldNotBeNull();
        (guide.Name, guide.Title, guide.Portrait).ShouldBe(("Odalys", "Vale Guide", "portrait.npc.guide"));
        bundle.Npc("npc.nobody").ShouldBeNull();
        bundle.Npc(null).ShouldBeNull();
        var json = Encoding.UTF8.GetString(ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes);
        json.ShouldNotContain("dialogues");
        json.ShouldNotContain("start_quest");
    }
}
