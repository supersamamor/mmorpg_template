using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Network;

/// <summary>Quests over a real gateway (T-602): the quest log after the snapshot, a start through dialogue, abandon and its refusal, and the currency field.</summary>
public class GatewayQuestTests
{
    [Fact]
    public async Task The_log_arrives_with_the_snapshot_and_starts_and_abandons_go_over_the_wire()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var client = await rig.JoinAsync("quester");

        var log = await client.WaitForAsync<QuestLogMessage>();
        log.Active.ShouldBeEmpty();
        log.Completed.ShouldBeEmpty();
        log.Offer.ShouldBe(["npc.guide"]);   // First Hunt; the trial needs base level 5 and Gear Up the hunt
        log.TurnIn.ShouldBeEmpty();
        (await client.WaitForAsync<InventoryMessage>()).Currency.ShouldBe(0);

        var guide = await Poll.UntilAsync(() => client.SeenIds.FirstOrDefault(id => client.Seen(id) is { Kind: EntityKinds.Npc } r && r.DisplayKey == "npc.guide"), id => id != 0, "the guide");
        await client.SendAsync(new TalkMessage(guide));
        var greet = await client.WaitForAsync<DialogueMessage>();
        await client.SendAsync(new DialogueChoiceMessage(greet.Choices.Single(c => c.Text.Contains("help with", StringComparison.Ordinal)).Index));
        var offer = await client.WaitForAsync<DialogueMessage>(m => m.Text.Contains("gel slimes", StringComparison.Ordinal));
        await client.SendAsync(new DialogueChoiceMessage(offer.Choices.Single(c => c.Text.Contains("hunt some slimes", StringComparison.Ordinal)).Index));

        var accepted = await client.WaitForAsync<QuestMessage>(m => m.Change == QuestChanges.Accepted);
        accepted.Entry!.Objectives.Select(o => (o.Type, o.Target, o.Have, o.Need)).ShouldBe(
            [(QuestObjectiveTypes.Kill, "monster.gel_slime", 0, 5), (QuestObjectiveTypes.Collect, "item.gel_glob", 0, 3), (QuestObjectiveTypes.Talk, "npc.guide", 0, 1)]);
        (await client.WaitForAsync<QuestLogMessage>(m => m.Offer.Count == 0)).Active.Single().Quest.ShouldBe("quest.first_hunt");   // the offer marker went

        await client.SendAsync(new AbandonQuestMessage("quest.first_hunt"));
        await client.WaitForAsync<QuestMessage>(m => m.Change == QuestChanges.Abandoned && m.Entry is null);
        await client.SendAsync(new AbandonQuestMessage("quest.first_hunt"));
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.AbandonQuest))
            .ShouldBe(new RejectedMessage(MessageTypes.AbandonQuest, QuestReasons.NotActive, null, Quest: "quest.first_hunt"));
    }
}
