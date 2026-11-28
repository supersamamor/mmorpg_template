using System.Text;
using Game.Client.Core;
using Game.Client.Core.Autopilot;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Client.Core.Quests;
using Game.Content.ClientExport;
using Game.Server.Net;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;
using ClientSession = Game.Client.Core.ClientSession;

namespace Game.Tests.Client;

/// <summary>
/// The quest autopilot's client side (T-605): the ticks and their order, the messages that go out and come in, the flash lines for a refusal or an end, and the rule that the client
/// sends nothing of its own that would count as manual input while a run is on. The server decides every run; these tests only watch what the client holds and sends.
/// </summary>
public sealed class AutopilotClientTests
{
    private const string FirstHunt = "quest.first_hunt";
    private const string GearUp = "quest.gear_up";
    private const string Third = "quest.third";

    private sealed class ScriptedSocket : IClientSocket
    {
        public readonly Queue<SocketEvent> Events = new();
        public readonly List<string> Sent = new();

        public void Open(string url) { }

        public void Send(byte[] utf8) => Sent.Add(Encoding.UTF8.GetString(utf8));

        public void Close() { }

        public bool TryDequeue(out SocketEvent socketEvent) => Events.TryDequeue(out socketEvent);

        public void Push(ServerMessage message) => Events.Enqueue(new SocketEvent(SocketEventKind.Message, message));
    }

    private static DisplayBundle Bundle()
    {
        DisplayBundle.TryRead(ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        return bundle!;
    }

    private static QuestEntryRecord Entry(string id, params QuestObjectiveRecord[] objectives) => new(id, 0, objectives);

    private static (ClientSession Session, ScriptedSocket Socket) Playing(params string[] active)
    {
        var socket = new ScriptedSocket();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, Bundle(), socket, new Pcg32Random(1));
        session.Login("Ada", 0);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        socket.Push(new WelcomeMessage(1, 1, "map.starter_meadow", 10, null, "token"));
        socket.Push(new SnapshotMessage(1, "map.starter_meadow", 1,
            [new EntityRecord(1, EntityKinds.Player, "job.wanderer", new GridCell(5, 5), Directions.N, null, 0)], new VitalsRecord(40, 40, 10, 10)));
        socket.Push(new QuestLogMessage(active.Select(id => Entry(id)).ToList(), [], [], []));
        session.Update(0, 0);
        session.Connection.Phase.ShouldBe(ConnectionPhase.Online);
        socket.Sent.Clear();
        return (session, socket);
    }

    private static T LastSent<T>(ScriptedSocket socket) where T : ClientMessage =>
        ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(socket.Sent.Last())).Message.ShouldBeOfType<T>();

    private static void Hear(ClientSession session, ScriptedSocket socket, ServerMessage message, double now = 1)
    {
        socket.Push(message);
        session.Update(now, now);
    }

    // ---- ticks and order ----

    [Fact]
    public void A_tick_goes_to_the_end_of_the_run_and_a_second_toggle_takes_it_away()
    {
        var (session, _) = Playing(FirstHunt, GearUp);
        var model = session.Autopilot;
        model.Toggle(GearUp, session.Quests).ShouldBeTrue();
        model.Toggle(FirstHunt, session.Quests).ShouldBeTrue();
        model.Ticked.ShouldBe([GearUp, FirstHunt]);
        model.OrderOf(GearUp).ShouldBe(1);
        model.OrderOf(FirstHunt).ShouldBe(2);
        model.Toggle(GearUp, session.Quests).ShouldBeTrue();
        model.Ticked.ShouldBe([FirstHunt]);
        model.OrderOf(GearUp).ShouldBe(0);
        model.IsTicked(GearUp).ShouldBeFalse();
    }

    [Fact]
    public void A_quest_the_character_does_not_have_cannot_be_ticked()
    {
        var (session, _) = Playing(FirstHunt);
        var version = session.Autopilot.Version;
        session.Autopilot.Toggle(Third, session.Quests).ShouldBeFalse();
        session.Autopilot.Ticked.ShouldBeEmpty();
        session.Autopilot.Version.ShouldBe(version);
    }

    [Fact]
    public void The_order_buttons_swap_one_place_and_stop_at_the_ends()
    {
        var (session, _) = Playing(FirstHunt, GearUp, Third);
        var model = session.Autopilot;
        foreach (var id in new[] { FirstHunt, GearUp, Third }) model.Toggle(id, session.Quests);
        model.MoveEarlier(FirstHunt).ShouldBeFalse();   // already first
        model.MoveLater(Third).ShouldBeFalse();          // already last
        model.MoveLater(FirstHunt).ShouldBeTrue();
        model.Ticked.ShouldBe([GearUp, FirstHunt, Third]);
        model.MoveEarlier(Third).ShouldBeTrue();
        model.Ticked.ShouldBe([GearUp, Third, FirstHunt]);
        model.MoveLater("quest.not_ticked").ShouldBeFalse();
    }

    [Fact]
    public void Ticks_of_quests_that_are_completed_or_abandoned_go_away()
    {
        var (session, socket) = Playing(FirstHunt, GearUp, Third);
        foreach (var id in new[] { FirstHunt, GearUp, Third }) session.Autopilot.Toggle(id, session.Quests);

        Hear(session, socket, new QuestMessage(QuestChanges.Completed, GearUp, null));
        session.Autopilot.Ticked.ShouldBe([FirstHunt, Third]);
        Hear(session, socket, new QuestMessage(QuestChanges.Abandoned, FirstHunt, null));
        session.Autopilot.Ticked.ShouldBe([Third]);

        // A whole new log (after a snapshot) drops what is no longer active, too.
        Hear(session, socket, new QuestLogMessage([Entry(FirstHunt)], [], [], []));
        session.Autopilot.Ticked.ShouldBeEmpty();
    }

    [Fact]
    public void Ticks_are_sent_nowhere_until_the_start()
    {
        var (session, socket) = Playing(FirstHunt);
        session.Autopilot.Toggle(FirstHunt, session.Quests);
        session.Autopilot.MoveLater(FirstHunt);
        session.Update(2, 2);
        socket.Sent.ShouldBeEmpty();
    }

    // ---- start and stop ----

    [Fact]
    public void Start_sends_the_ticked_quests_in_order_and_the_bar_skills_in_slot_order()
    {
        var (session, socket) = Playing(FirstHunt, GearUp);
        Hear(session, socket, new SkillsMessage("job.wanderer", 0, [new SkillLevelRecord("skill.second_wind", 1), new SkillLevelRecord("skill.ember_bolt", 2), new SkillLevelRecord("skill.tough_body", 1)], [], []));
        session.Autopilot.Toggle(GearUp, session.Quests);
        session.Autopilot.Toggle(FirstHunt, session.Quests);

        session.StartAutopilot().ShouldBeTrue();

        var start = LastSent<StartAutopilotMessage>(socket);
        start.Quests.ShouldBe([GearUp, FirstHunt]);
        start.Skills.Select(s => s.Skill).ToArray().ShouldBe(session.Skills.ActiveSkills.Take(session.Hotbar.SlotCount).ToArray());
        start.Skills.Select(s => s.Skill).ShouldNotContain("skill.tough_body");   // passive: never on the bar
        start.Skills.All(s => s.Level == 0).ShouldBeTrue();                       // 0 = the learned level
        socket.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public void Start_with_nothing_ticked_dead_or_offline_sends_nothing()
    {
        var (session, socket) = Playing(FirstHunt);
        session.StartAutopilot().ShouldBeFalse();   // nothing ticked
        session.Autopilot.Toggle(FirstHunt, session.Quests);
        Hear(session, socket, new YouDiedMessage(null));
        session.Ui.Dead.ShouldBeTrue();
        session.StartAutopilot().ShouldBeFalse();
        socket.Sent.ShouldBeEmpty();
    }

    [Fact]
    public void Start_fits_the_gateways_message_cap_with_the_most_the_server_takes()
    {
        var socket = new ScriptedSocket();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true, HotbarSlots = 12 }, Bundle(), socket, new Pcg32Random(1));
        var quests = Enumerable.Range(0, 10).Select(i => "quest." + new string((char)('a' + i), 19)).ToArray();   // ids of 25 characters, the most ADR-002 row 292 allows for
        var skills = Enumerable.Range(0, 12).Select(i => new SkillLevelRecord("skill." + new string((char)('a' + i), 19), 0)).ToArray();
        var bytes = ProtocolJson.ToBytes(new StartAutopilotMessage(quests, skills));
        bytes.Length.ShouldBeLessThan(1024);
        session.Hotbar.SlotCount.ShouldBe(12);
    }

    [Fact]
    public void Stop_sends_only_while_a_run_is_on()
    {
        var (session, socket) = Playing(FirstHunt);
        session.StopAutopilot().ShouldBeFalse();
        socket.Sent.ShouldBeEmpty();

        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Hunting, null));
        session.StopAutopilot().ShouldBeTrue();
        LastSent<StopAutopilotMessage>(socket);

        Hear(session, socket, new AutopilotMessage(AutopilotModes.Paused, FirstHunt, null, AutopilotPauses.NeedsLevel));
        socket.Sent.Clear();
        session.StopAutopilot().ShouldBeTrue();   // a paused run is stopped the same way
    }

    // ---- what the server says ----

    [Fact]
    public void The_state_is_only_what_the_server_said()
    {
        var (session, socket) = Playing(FirstHunt);
        var model = session.Autopilot;
        model.On.ShouldBeFalse();   // a fresh login gets no message: off

        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Travelling, null));
        (model.Running, model.Quest, model.Doing).ShouldBe((true, FirstHunt, AutopilotActivities.Travelling));

        Hear(session, socket, new AutopilotMessage(AutopilotModes.Paused, FirstHunt, null, AutopilotPauses.NeedsItemUse));
        (model.Paused, model.Doing, model.Reason).ShouldBe((true, null, AutopilotPauses.NeedsItemUse));

        Hear(session, socket, new AutopilotMessage(AutopilotModes.Off, null, null, AutopilotStops.Finished));
        (model.On, model.Quest, model.Reason).ShouldBe((false, null, AutopilotStops.Finished));
        socket.Sent.ShouldBeEmpty();   // hearing a state sends nothing
    }

    [Fact]
    public void A_run_that_ends_says_why_on_the_flash_line()
    {
        foreach (var reason in new[]
                 {
                     AutopilotStops.Finished, AutopilotStops.Stopped, AutopilotStops.ManualInput, AutopilotStops.Died, AutopilotStops.Logout, AutopilotStops.Disconnected,
                     AutopilotStops.BagFull, AutopilotStops.TimeLimit, AutopilotStops.QuestAbandoned,
                 })
        {
            var (session, socket) = Playing(FirstHunt);
            Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Hunting, null));
            session.Ui.Flash.ShouldBe(string.Empty);
            Hear(session, socket, new AutopilotMessage(AutopilotModes.Off, null, null, reason));
            session.Ui.Flash.ShouldBe(UiText.AutopilotEnded(reason));
            session.Ui.Flash.ShouldNotBeEmpty();
        }
        UiText.AutopilotEnded(AutopilotStops.ManualInput).ShouldContain("took over");
        new[] { AutopilotStops.Finished, AutopilotStops.Stopped, AutopilotStops.ManualInput, AutopilotStops.Died, AutopilotStops.Logout, AutopilotStops.BagFull, AutopilotStops.TimeLimit, AutopilotStops.QuestAbandoned }
            .Select(UiText.AutopilotEnded).Distinct().Count().ShouldBeGreaterThan(6);   // each reason has its own words
    }

    [Fact]
    public void An_off_that_follows_off_says_nothing()
    {
        var (session, socket) = Playing(FirstHunt);
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Off, null, null, AutopilotStops.Disconnected));   // what a resumed session is told
        session.Ui.Flash.ShouldBe(string.Empty);
    }

    [Fact]
    public void A_paused_run_names_what_the_player_can_do()
    {
        var (session, socket) = Playing();
        Hear(session, socket, new QuestLogMessage([Entry(FirstHunt, new QuestObjectiveRecord(QuestObjectiveTypes.Level, null, 3, 10), new QuestObjectiveRecord(QuestObjectiveTypes.Kill, "monster.gel_slime", 0, 5))], [], [], []));
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Paused, FirstHunt, null, AutopilotPauses.NeedsLevel));
        var detail = session.Autopilot.PauseDetail(Bundle(), session.Quests);
        detail.ShouldBe("10");
        UiText.AutopilotPause(AutopilotPauses.NeedsLevel, detail).ShouldBe("Reach level 10 to go on.");

        // Without a quest to read it from, the line still says what to do.
        UiText.AutopilotPause(AutopilotPauses.NeedsLevel, null).ShouldBe("Raise your level to go on.");
        foreach (var reason in new[]
                 {
                     AutopilotPauses.NeedsLevel, AutopilotPauses.NeedsJob, AutopilotPauses.NeedsEquip, AutopilotPauses.NeedsItemUse, AutopilotPauses.NoSource, AutopilotPauses.Unreachable,
                     AutopilotPauses.NoProgress, AutopilotPauses.NoSafeChoice,
                 })
            UiText.AutopilotPause(reason, null).ShouldNotBe(UiText.AutopilotPause("something_new", null));
    }

    [Fact]
    public void Every_refusal_has_its_own_words_and_a_refused_start_changes_nothing()
    {
        var (session, socket) = Playing(FirstHunt);
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Hunting, null));
        var reasons = new[]
        {
            AutopilotReasons.Disabled, AutopilotReasons.Dead, AutopilotReasons.TimeLimit, AutopilotReasons.EmptyQueue, AutopilotReasons.TooManyQuests, AutopilotReasons.UnknownQuest,
            AutopilotReasons.DuplicateQuest, AutopilotReasons.NotActive, AutopilotReasons.TooManySkills, AutopilotReasons.UnknownSkill, AutopilotReasons.NotLearned,
            AutopilotReasons.PassiveSkill, AutopilotReasons.LevelTooHigh,
        };
        foreach (var reason in reasons)
        {
            Hear(session, socket, new RejectedMessage(MessageTypes.StartAutopilot, reason, null, Quest: FirstHunt));
            session.Ui.Flash.ShouldBe(UiText.AutopilotRefused(reason, Bundle().Quest(FirstHunt)?.Name ?? FirstHunt));
            session.Ui.Flash.ShouldNotBe(UiText.AutopilotRefused("something_new"));
            session.Autopilot.Running.ShouldBeTrue();   // the run in progress goes on
        }
        reasons.Select(r => UiText.AutopilotRefused(r)).Distinct().Count().ShouldBeGreaterThan(9);
        Hear(session, socket, new RejectedMessage(MessageTypes.StartAutopilot, AutopilotReasons.NotLearned, null, Skill: "skill.ember_bolt"));
        session.Ui.Flash.ShouldContain(Bundle().Skill("skill.ember_bolt")!.Name);
    }

    [Fact]
    public void A_new_login_forgets_the_ticks_and_the_run()
    {
        var (session, socket) = Playing(FirstHunt);
        session.Autopilot.Toggle(FirstHunt, session.Quests);
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Hunting, null));

        session.Logout(2);
        Hear(session, socket, new ByeMessage(ByeReasons.Logout), 3);
        session.Autopilot.On.ShouldBeFalse();
        session.Autopilot.Ticked.ShouldBeEmpty();
    }

    [Fact]
    public void A_stopped_session_forgets_the_run_when_the_player_goes_back_to_the_login_screen()
    {
        var (session, socket) = Playing(FirstHunt);
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Hunting, null));
        Hear(session, socket, new ByeMessage(ByeReasons.Replaced), 2);
        session.Update(3, 3);
        session.Autopilot.On.ShouldBeFalse();
    }

    // ---- no manual input of the client's own ----

    [Fact]
    public void While_a_run_is_on_the_client_sends_nothing_by_itself()
    {
        var (session, socket) = Playing(FirstHunt);
        session.Autopilot.Toggle(FirstHunt, session.Quests);
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Talking, null), 1);

        // The turn-in's dialogue opens and ends on the server's word; the client closes nothing, answers nothing, and stays quiet through snapshots, deltas and many frames.
        Hear(session, socket, new DialogueMessage(5, "npc.guide", null, "Well done.", [], false), 1.1);
        session.Dialogue.Open.ShouldBeTrue();
        for (var frame = 0; frame < 120; frame++) session.Update(1.2 + frame * 0.1, 1.2 + frame * 0.1);
        Hear(session, socket, new DialogueEndedMessage(DialogueEndReasons.Finished), 14);
        Hear(session, socket, new QuestMessage(QuestChanges.Completed, FirstHunt, null), 14.1);
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Hunting, null), 14.2);
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Off, null, null, AutopilotStops.Finished), 14.3);

        socket.Sent.Select(json => ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(json)).Message!.Type)
            .Where(type => type != MessageTypes.Ping)   // a ping does not end a run
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_manual_intents_are_the_ones_the_server_counts_and_none_is_sent_by_a_state_change()
    {
        // Every ClientSession method that sends something is a player's press. The ones that end a run are named here so that a new one has to be thought about: the autopilot
        // flash and the indicator's tap only ever send stop_autopilot.
        var (session, socket) = Playing(FirstHunt);
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Hunting, null));
        session.TapGround(new GridCell(6, 6), 2);
        LastSent<MoveMessage>(socket);    // a manual order: the server ends the run with manual_input, and the client says so when it hears it
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Off, null, null, AutopilotStops.ManualInput), 3);
        session.Ui.Flash.ShouldBe(UiText.AutopilotEnded(AutopilotStops.ManualInput));
    }

    // ---- auto-battle (T-811) ----

    [Fact]
    public void Auto_sends_a_battle_start_with_the_bar_and_the_fight_choice_and_no_quests()
    {
        var (session, socket) = Playing(FirstHunt);
        Hear(session, socket, new SkillsMessage("job.wanderer", 0, [new SkillLevelRecord("skill.second_wind", 1), new SkillLevelRecord("skill.tough_body", 1)], [], []));
        session.Autopilot.Toggle(FirstHunt, session.Quests);   // ticks are the quest run's: auto-battle ignores them

        session.StartAutoBattle().ShouldBeTrue();
        var start = LastSent<StartAutopilotMessage>(socket);
        start.Quests.ShouldBeEmpty();
        start.Battle.ShouldBe(AutoBattleFilters.Nearby);
        start.Skills.Select(s => s.Skill).ShouldBe(["skill.second_wind"]);
        session.Autopilot.On.ShouldBeFalse();   // only the server's word turns it on

        var version = session.Autopilot.Version;
        session.Autopilot.CycleBattleFilter();
        session.Autopilot.Version.ShouldBeGreaterThan(version);
        session.Autopilot.BattleFilter.ShouldBe(AutoBattleFilters.Attackers);
        session.Autopilot.CycleBattleFilter();
        session.Autopilot.CycleBattleFilter();
        session.Autopilot.BattleFilter.ShouldBe(AutoBattleFilters.Nearby);   // round again
        session.Autopilot.CycleBattleFilter();
        session.StartAutoBattle().ShouldBeTrue();
        LastSent<StartAutopilotMessage>(socket).Battle.ShouldBe(AutoBattleFilters.Attackers);
    }

    [Fact]
    public void A_status_without_a_quest_is_auto_battle_and_its_end_is_told_as_such()
    {
        var (session, socket) = Playing(FirstHunt);
        session.StartAutoBattle();
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, null, AutopilotActivities.Fighting, null));
        session.Autopilot.Battle.ShouldBeTrue();
        session.Autopilot.Doing.ShouldBe(AutopilotActivities.Fighting);
        UiText.AutoBattleRunning(session.Autopilot.Doing).ShouldBe("Stop auto-battle: fighting");
        UiText.AutoBattleRunning(AutopilotActivities.Returning).ShouldBe("Stop auto-battle: returning");
        UiText.AutoBattleRunning(AutopilotActivities.Waiting).ShouldBe("Stop auto-battle: waiting");

        session.StopAutopilot().ShouldBeTrue();
        LastSent<StopAutopilotMessage>(socket);
        foreach (var stop in AutopilotStops.All)
        {
            Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, null, AutopilotActivities.Waiting, null), 2);
            Hear(session, socket, new AutopilotMessage(AutopilotModes.Off, null, null, stop), 3);
            session.Ui.Flash.ShouldBe(UiText.AutoBattleEnded(stop));
        }
        UiText.AutoBattleEnded(AutopilotStops.LowHp).ShouldNotBe(UiText.AutoBattleEnded("something_new"));
        UiText.AutoBattleEnded(AutopilotStops.LeftArea).ShouldNotBe(UiText.AutoBattleEnded("something_new"));
        UiText.AutoBattleEnded(AutopilotStops.NothingToFight).ShouldNotBe(UiText.AutoBattleEnded("something_new"));

        // A quest run afterwards is a quest run again.
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Running, FirstHunt, AutopilotActivities.Hunting, null), 4);
        session.Autopilot.Battle.ShouldBeFalse();
        Hear(session, socket, new AutopilotMessage(AutopilotModes.Off, null, null, AutopilotStops.Stopped), 5);
        session.Ui.Flash.ShouldBe(UiText.AutopilotEnded(AutopilotStops.Stopped));
    }

    [Fact]
    public void A_refused_battle_start_says_why_in_auto_battles_words()
    {
        var (session, socket) = Playing(FirstHunt);
        session.StartAutoBattle();
        foreach (var reason in AutoBattleReasons.All.Concat([AutopilotReasons.Dead, AutopilotReasons.TimeLimit]))
        {
            Hear(session, socket, new RejectedMessage(MessageTypes.StartAutopilot, reason, null));
            session.Ui.Flash.ShouldBe(UiText.AutoBattleRefused(reason));
        }
        UiText.AutoBattleRefused(AutoBattleReasons.BattleDisabled).ShouldNotBe(UiText.AutopilotRefused(AutopilotReasons.Disabled));
        Hear(session, socket, new RejectedMessage(MessageTypes.StartAutopilot, AutopilotReasons.NotLearned, null, Skill: "skill.ember_bolt"));
        session.Ui.Flash.ShouldContain(Bundle().Skill("skill.ember_bolt")!.Name);

        // After a quest start, a refusal is the quest autopilot's again.
        session.Autopilot.Toggle(FirstHunt, session.Quests);
        session.StartAutopilot().ShouldBeTrue();
        Hear(session, socket, new RejectedMessage(MessageTypes.StartAutopilot, AutopilotReasons.Dead, null));
        session.Ui.Flash.ShouldBe(UiText.AutopilotRefused(AutopilotReasons.Dead));
    }

    [Fact]
    public void The_fight_choices_have_short_words_of_their_own()
    {
        AutoBattleFilters.All.Select(UiText.AutoBattleFilter).Distinct().Count().ShouldBe(AutoBattleFilters.All.Count);
        AutoBattleFilters.All.Select(UiText.AutoBattleFilter).ShouldAllBe(text => text.Length <= UiText.AutoBattleLongestFilter.Length && text.Length <= 10);
    }
}
