using Game.Server.World;
using Game.Server.World.Dialogue;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Autopilot;

namespace Game.Tests.Network;

/// <summary>
/// The quest autopilot over a real gateway (T-604): start, status, stop and refusals on the wire, the player's own messages ending a run, and no play without a player (disconnect,
/// takeover, logout). In the timing collection: it drops connections and advances the gateway's clock.
/// </summary>
[Collection(TimingCollection.Name)]
public class GatewayAutopilotTests
{
    private const string Far = "quest.reach_far";   // two maps away: a run that lasts as long as a test needs

    private static Task<GatewayRig> StartAsync(Func<Game.Shared.Definitions.AutopilotBalanceDef, Game.Shared.Definitions.AutopilotBalanceDef>? autopilot = null) =>
        GatewayRig.StartAsync(o => o with { IdleTimeoutMs = 10 * o.GracePeriodMs }, content: AutopilotContentKit.Build(autopilot, balance: b => b));

    /// <summary>Joins and puts quests in the log, slowed down so a journey takes long.</summary>
    private static async Task<(TestClient Client, EntityId Id)> JoinAsync(GatewayRig rig, params string[] quests)
    {
        var client = await rig.JoinAsync("pilot");
        var id = new EntityId(client.Id);
        foreach (var quest in quests) rig.World.GetInstance(AutopilotContentKit.Alpha).Events.Publish(new QuestStartRequested(id, quest, "npc.test"));
        foreach (var quest in quests) await client.WaitForAsync<QuestMessage>(m => m.Quest == quest && m.Change == QuestChanges.Accepted);
        return (client, id);
    }

    private static Task<AutopilotMessage> StatusAsync(TestClient client, Func<AutopilotMessage, bool> match) => client.WaitForAsync(match);

    [Fact]
    public async Task A_run_is_started_reported_on_change_and_finishes_over_the_wire()
    {
        await using var rig = await StartAsync();
        var (client, id) = await JoinAsync(rig, "quest.talk", "quest.reach");
        rig.Progressions.TryGet(id, out var state).ShouldBeTrue();
        state.Skills["skill.second_wind"] = 1;
        client.Messages.OfType<AutopilotMessage>().ShouldBeEmpty();   // nothing to say at a join

        await client.SendAsync(new StartAutopilotMessage(["quest.talk", "quest.reach"], [new SkillLevelRecord("skill.second_wind", 1)]));
        await StatusAsync(client, m => m.Mode == AutopilotModes.Running && m.Quest == "quest.talk");
        await client.WaitForAsync<QuestMessage>(m => m.Quest == "quest.talk" && m.Change == QuestChanges.Completed);
        await StatusAsync(client, m => m.Mode == AutopilotModes.Running && m is { Quest: "quest.reach", Doing: AutopilotActivities.Travelling });
        (await StatusAsync(client, m => m.Mode == AutopilotModes.Off)).ShouldBe(new AutopilotMessage(AutopilotModes.Off, null, null, AutopilotStops.Finished));
        await client.WaitForAsync<QuestMessage>(m => m.Quest == "quest.reach" && m.Change == QuestChanges.Completed);

        var told = client.Messages.OfType<AutopilotMessage>().ToList();
        told.Count.ShouldBeLessThan(8);   // on change, never per tick
        for (var i = 1; i < told.Count; i++) told[i].ShouldNotBe(told[i - 1]);
    }

    [Fact]
    public async Task Stop_and_a_pause_and_every_kind_of_refusal_reach_the_owner()
    {
        await using var rig = await StartAsync(a => a with { MaxQuests = 2 });
        var (client, _) = await JoinAsync(rig, Far, "quest.equip");

        await client.SendAsync(new StartAutopilotMessage(["quest.none"], []));
        (await client.WaitForAsync<RejectedMessage>(r => r.Reason == AutopilotReasons.UnknownQuest)).ShouldBe(
            new RejectedMessage(MessageTypes.StartAutopilot, AutopilotReasons.UnknownQuest, null, Quest: "quest.none"));
        await client.SendAsync(new StartAutopilotMessage([Far], [new SkillLevelRecord("skill.ember_bolt", 1)]));
        (await client.WaitForAsync<RejectedMessage>(r => r.Reason == AutopilotReasons.NotLearned)).ShouldBe(
            new RejectedMessage(MessageTypes.StartAutopilot, AutopilotReasons.NotLearned, null, Skill: "skill.ember_bolt"));
        await client.SendAsync(new StartAutopilotMessage([], []));
        await client.WaitForAsync<RejectedMessage>(r => r.Reason == AutopilotReasons.EmptyQueue);
        await client.SendAsync(new StartAutopilotMessage([Far, "quest.equip", "quest.kill"], []));
        await client.WaitForAsync<RejectedMessage>(r => r.Reason == AutopilotReasons.TooManyQuests);

        await client.SendAsync(new StartAutopilotMessage(["quest.equip"], []));
        (await StatusAsync(client, m => m.Mode == AutopilotModes.Paused)).ShouldBe(new AutopilotMessage(AutopilotModes.Paused, "quest.equip", null, AutopilotPauses.NeedsEquip));
        await client.SendAsync(new StartAutopilotMessage([Far], []));
        await StatusAsync(client, m => m.Mode == AutopilotModes.Running && m.Quest == Far);
        await client.SendAsync(new StopAutopilotMessage());
        (await StatusAsync(client, m => m.Mode == AutopilotModes.Off)).Reason.ShouldBe(AutopilotStops.Stopped);
    }

    [Fact]
    public async Task A_disabled_autopilot_refuses()
    {
        await using var rig = await StartAsync(a => a with { Enabled = false });
        var (client, _) = await JoinAsync(rig, Far);
        await client.SendAsync(new StartAutopilotMessage([Far], []));
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.StartAutopilot)).Reason.ShouldBe(AutopilotReasons.Disabled);
    }

    [Fact]
    public async Task Every_order_of_the_players_own_ends_a_run()
    {
        await using var rig = await StartAsync();
        var (client, _) = await JoinAsync(rig, Far);
        var keeper = (await Poll.UntilAsync(() => client.View.FirstOrDefault(r => r.DisplayKey == AutopilotContentKit.Keeper), r => r is not null, "the keeper"))!.Id;
        var monster = (await Poll.UntilAsync(() => client.View.FirstOrDefault(r => r.Kind == EntityKinds.Monster), r => r is not null, "a monster"))!.Id;
        ClientMessage[] orders =
        [
            new MoveMessage(new GridCell(2, 2)), new MoveMessage(new GridCell(-5, 2)) /* out of bounds: refused, and still the player's */, new StopMessage(), new AttackMessage(monster),
            new AttackMessage(424242), new StopAttackMessage(), new RespawnMessage(), new PickUpMessage(424242), new UseItemMessage(424242), new EquipMessage(424242),
            new UnequipMessage(EquipSlots.Shield), new DropItemMessage(424242, 1), new InsertCardMessage(424242, 424243), new RefineMessage(424242), new UseSkillMessage("skill.none"),
            new TalkMessage(keeper), new DialogueChoiceMessage(0), new DialogueCloseMessage(), new ShopBuyMessage("item.none", 1), new ShopSellMessage(424242, 1),
        ];
        for (var i = 0; i < orders.Length; i++)
        {
            rig.Clock.Advance(10_000);   // the rate limit refills
            await client.SendAsync(new StartAutopilotMessage([Far], []));
            await Poll.UntilAsync(() => client.Messages.OfType<AutopilotMessage>().LastOrDefault()?.Mode, m => m == AutopilotModes.Running, "running before " + orders[i]);
            await client.SendAsync(orders[i]);
            var off = await Poll.UntilAsync(() => client.Messages.OfType<AutopilotMessage>().Where(m => m.Mode == AutopilotModes.Off).ToList(), l => l.Count == i + 1, "off after " + orders[i]);
            off[i].Reason.ShouldBe(AutopilotStops.ManualInput, orders[i].ToString());
        }
    }

    [Fact]
    public async Task Messages_that_are_no_orders_to_the_character_leave_it_running()
    {
        await using var rig = await StartAsync();
        var (client, _) = await JoinAsync(rig, Far, "quest.kill");
        await client.SendAsync(new StartAutopilotMessage([Far], []));
        await StatusAsync(client, m => m.Mode == AutopilotModes.Running);
        await client.SendAsync(new PingMessage(1));
        await client.SendAsync(new LearnSkillMessage("skill.second_wind"));
        await client.SendAsync(new RaiseStatMessage("str", 1));
        await client.SendAsync(new ChangeJobMessage("job.warden"));
        await client.SendAsync(new AbandonQuestMessage("quest.kill"));
        await client.WaitForAsync<QuestMessage>(m => m.Change == QuestChanges.Abandoned);
        await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.ChangeJob);
        client.Messages.OfType<AutopilotMessage>().ShouldNotContain(m => m.Mode != AutopilotModes.Running);
    }

    [Fact]
    public async Task A_dropped_connection_ends_it_and_a_resume_says_so_and_does_not_restart_it()
    {
        await using var rig = await StartAsync();
        var (client, id) = await JoinAsync(rig, Far);
        await client.SendAsync(new StartAutopilotMessage([Far], []));
        await StatusAsync(client, m => m.Mode == AutopilotModes.Running);
        var token = client.Token;
        client.Dispose();
        await Poll.UntilAsync(() => rig.Gateway.CharactersInGrace, n => n == 1, "grace period");
        await Task.Delay(400);   // a few ticks: the stop has been carried out
        var parked = Where(rig, id);
        await Task.Delay(600);
        Where(rig, id).ShouldBe(parked);   // it stands still for the whole grace period

        var back = await rig.ResumeAsync(token);
        await back.WaitForAsync<SnapshotMessage>();
        (await back.WaitForAsync<AutopilotMessage>()).ShouldBe(new AutopilotMessage(AutopilotModes.Off, null, null, AutopilotStops.Disconnected));
        await Task.Delay(400);
        back.Messages.OfType<AutopilotMessage>().Count().ShouldBe(1);
        Where(rig, id).ShouldBe(parked);
    }

    [Fact]
    public async Task A_session_that_takes_a_played_character_over_ends_its_run()
    {
        await using var rig = await StartAsync();
        var (client, _) = await JoinAsync(rig, Far);
        await client.SendAsync(new StartAutopilotMessage([Far], []));
        await StatusAsync(client, m => m.Mode == AutopilotModes.Running);
        var second = await rig.ResumeAsync(client.Token);
        (await second.WaitForAsync<AutopilotMessage>(m => m.Mode == AutopilotModes.Off)).Reason.ShouldBe(AutopilotStops.Disconnected);
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Replaced);
    }

    [Fact]
    public async Task Logout_ends_it_with_the_character()
    {
        await using var rig = await StartAsync();
        var (client, id) = await JoinAsync(rig, Far);
        await client.SendAsync(new StartAutopilotMessage([Far], []));
        await StatusAsync(client, m => m.Mode == AutopilotModes.Running);
        await client.SendAsync(new LogoutMessage());
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Logout);
        await Poll.UntilAsync(() => rig.World.LocationOf(id), l => l is null, "gone");
        await Task.Delay(300);
        rig.Log.Lines.ShouldNotContain(l => l.StartsWith("ERROR"));
    }

    [Fact]
    public async Task Nothing_about_monsters_or_spawns_is_in_the_status()
    {
        await using var rig = await StartAsync();
        var (client, _) = await JoinAsync(rig, "quest.kill");
        await client.SendAsync(new StartAutopilotMessage(["quest.kill"], []));
        await StatusAsync(client, m => m.Mode == AutopilotModes.Off);
        foreach (var message in client.Messages.OfType<AutopilotMessage>())
        {
            AutopilotModes.All.Contains(message.Mode).ShouldBeTrue();
            if (message.Doing is not null) AutopilotActivities.All.Contains(message.Doing).ShouldBeTrue();
            if (message.Reason is not null) (AutopilotPauses.All.Contains(message.Reason) || AutopilotStops.All.Contains(message.Reason)).ShouldBeTrue();
            (message.Quest is null || message.Quest == "quest.kill").ShouldBeTrue();
        }
    }

    private static (string?, GridCell) Where(GatewayRig rig, EntityId id)
    {
        var map = rig.World.LocationOf(id);
        return (map, rig.World.GetInstance(map!).TryGetEntity(id, out var e) ? e.Cell : default);
    }
}
