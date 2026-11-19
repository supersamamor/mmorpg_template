using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Network;

/// <summary>Talking to NPCs over a real gateway (T-601): the real content, the real pipeline, new characters starting beside Odalys in the starter meadow's safe zone.</summary>
public class GatewayDialogueTests
{
    private static async Task<long> SeeNpcAsync(TestClient client, string npcId) =>
        await Poll.UntilAsync(() => client.SeenIds.FirstOrDefault(id => client.Seen(id) is { Kind: EntityKinds.Npc } r && r.DisplayKey == npcId), id => id != 0, "the NPC " + npcId);

    [Fact]
    public async Task Talk_choose_and_close_over_the_wire()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var client = await rig.JoinAsync("talker");
        var guide = await SeeNpcAsync(client, "npc.guide");

        await client.SendAsync(new TalkMessage(guide));
        var first = await client.WaitForAsync<DialogueMessage>();
        first.Npc.ShouldBe(guide);
        first.Speaker.ShouldBe("npc.guide");
        first.Text.ShouldNotBeEmpty();
        // T-602: the quest choices follow the quest log. A new character sees the hunt's offer, the services and goodbye; tracking, thanks and Gear Up are hidden.
        first.Choices.Count.ShouldBe(3);
        first.Choices[0].Text.ShouldContain("help with");
        first.Choices.Select(c => c.Index).ShouldBe([0, 5, 6]);   // the original indices: what dialogue_choice sends back

        var more = first.Choices.Single(c => c.Text.Contains("something else", StringComparison.Ordinal));
        await client.SendAsync(new DialogueChoiceMessage(more.Index));
        var services = await client.WaitForAsync<DialogueMessage>(m => m.Text != first.Text);
        services.Choices.Select(c => c.Text).ShouldContain(t => t.Contains("home", StringComparison.Ordinal));
        services.Choices.Count.ShouldBe(4);   // a new character is below level 10: both free resets are shown

        await client.SendAsync(new DialogueCloseMessage());
        (await client.WaitForAsync<DialogueEndedMessage>()).Reason.ShouldBe(DialogueEndReasons.Closed);
    }

    [Fact]
    public async Task A_finished_dialogue_says_so_and_a_choice_with_no_dialogue_is_rejected()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var client = await rig.JoinAsync("finisher");
        var guide = await SeeNpcAsync(client, "npc.guide");

        await client.SendAsync(new DialogueChoiceMessage(0));
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.DialogueChoice)).Reason.ShouldBe(DialogueReasons.NoDialogue);

        await client.SendAsync(new TalkMessage(guide));
        var first = await client.WaitForAsync<DialogueMessage>();
        await client.SendAsync(new DialogueChoiceMessage(first.Choices.Single(c => c.Text.Contains("help with", StringComparison.Ordinal)).Index));
        var offer = await client.WaitForAsync<DialogueMessage>(m => m.Text != first.Text);
        var accept = offer.Choices.Single(c => c.Text.Contains("hunt some slimes", StringComparison.Ordinal));
        await client.SendAsync(new DialogueChoiceMessage(accept.Index));
        var accepted = await client.WaitForAsync<DialogueMessage>(m => m.Text.StartsWith("Good luck", StringComparison.Ordinal));
        accepted.Choices.ShouldBeEmpty();
        accepted.More.ShouldBeFalse();
        // T-602: the start_quest action started First Hunt; its owner is told with the step's objectives.
        var quest = await client.WaitForAsync<QuestMessage>(m => m.Change == QuestChanges.Accepted);
        quest.Quest.ShouldBe("quest.first_hunt");
        quest.Entry!.Objectives.Select(o => o.Type).ShouldBe([QuestObjectiveTypes.Kill, QuestObjectiveTypes.Collect, QuestObjectiveTypes.Talk]);

        await client.SendAsync(new DialogueChoiceMessage(0));
        (await client.WaitForAsync<DialogueEndedMessage>()).Reason.ShouldBe(DialogueEndReasons.Finished);
    }

    [Fact]
    public async Task A_talk_to_something_unseen_or_not_an_npc_is_refused_by_the_gateway_itself()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var client = await rig.JoinAsync("refused");
        await SeeNpcAsync(client, "npc.guide");

        await client.SendAsync(new TalkMessage(424242));
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.Talk)).ShouldBe(new RejectedMessage(MessageTypes.Talk, DialogueReasons.TargetGone, null, 424242));
        await client.SendAsync(new TalkMessage(client.Id));
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.Talk && r.Target == client.Id)).Reason.ShouldBe(DialogueReasons.InvalidTarget);
    }

    [Fact]
    public async Task Refining_needs_the_smith_whose_service_opens_over_the_wire()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var client = await rig.JoinAsync("refiner");
        var inventory = await client.WaitForAsync<InventoryMessage>();
        var dirk = inventory.Entries.Single(e => e.Item == "item.bone_dirk").Id;

        await client.SendAsync(new RefineMessage(dirk));
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.Refine)).Reason.ShouldBe(ItemReasons.ServiceRequired);

        var smith = await SeeNpcAsync(client, "npc.smith");
        await client.SendAsync(new TalkMessage(smith));   // the server walks the character to the smith first
        var greeting = await client.WaitForAsync<DialogueMessage>();
        greeting.Speaker.ShouldBe("npc.smith");
        await client.SendAsync(new DialogueChoiceMessage(greeting.Choices.Single(c => c.Text.Contains("Refine", StringComparison.Ordinal)).Index));
        var opened = await client.WaitForAsync<ServiceOpenedMessage>();
        (opened.Service, opened.Npc).ShouldBe((ServiceIds.Refine, smith));

        await client.SendAsync(new RefineMessage(dirk));   // no shards in a new character's bag: refused for the material, not for the service
        (await client.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.Refine && r.Reason != ItemReasons.ServiceRequired)).Reason.ShouldBe(ItemReasons.MissingMaterial);
    }
}
