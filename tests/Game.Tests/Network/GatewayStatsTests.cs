using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Network;

/// <summary>
/// T-503: stat points over the wire, end to end on the real stack: the own <c>stats</c> message after the snapshot, a <c>raise_stat</c> with the server's cost
/// and the derived stats following, every refusal as <c>rejected</c> with the stat, and a stat reset item used from the bag giving the points back.
/// </summary>
public class GatewayStatsTests
{
    [Fact]
    public async Task A_player_spends_stat_points_is_refused_and_takes_them_back_with_a_reset_item()
    {
        await using var rig = await GatewayRig.StartAsync();
        var me = await rig.JoinAsync("spender");
        var first = await me.WaitForAsync<StatsMessage>();
        first.Points.ShouldBe(10);
        first.Stats.Select(s => s.Stat).ShouldBe(PrimaryStats.All.Ordered);
        first.Stats.ShouldAllBe(s => s.Value == 1 && s.NextCost == 2);
        first.Derived.Select(d => d.Stat).ShouldBe(DerivedStats.Panel.Ordered);
        var atk = first.Derived.Single(d => d.Stat == DerivedStats.Atk).Value;

        // refused: nothing about the stat is invented by the client
        await me.SendAsync(new RaiseStatMessage("stat.luck", 1));
        (await me.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.RaiseStat)).ShouldBe(
            new RejectedMessage(MessageTypes.RaiseStat, StatReasons.UnknownStat, null, Stat: "stat.luck"));
        await me.SendAsync(new RaiseStatMessage(PrimaryStats.Str, 6));
        (await me.WaitForAsync<RejectedMessage>(r => r.Reason == StatReasons.NotEnoughPoints)).Stat.ShouldBe(PrimaryStats.Str);

        // raised: the server's cost, and the derived stats follow
        await me.SendAsync(new RaiseStatMessage(PrimaryStats.Str, 3));
        var raised = await me.WaitForAsync<StatsMessage>(m => m.Points == 4);
        raised.Stats.Single(s => s.Stat == PrimaryStats.Str).Value.ShouldBe(4);
        raised.Derived.Single(d => d.Stat == DerivedStats.Atk).Value.ShouldBe(atk + 3);

        // a new character is below the free level: the reset happens and the item stays
        var inventory = await me.WaitForAsync<InventoryMessage>();
        var reset = inventory.Entries.Single(e => e.Item == "item.realign_tonic");
        await me.SendAsync(new UseItemMessage(reset.Id));
        var back = await Poll.UntilAsync(() => me.Messages.OfType<StatsMessage>().Last(), m => m.Points == 10, "the stats after the reset");   // the newest one: the first also had 10
        back.Stats.ShouldAllBe(s => s.Value == 1);
        back.Derived.Single(d => d.Stat == DerivedStats.Atk).Value.ShouldBe(atk);
        rig.Progressions.TryGet(new EntityId(me.Id), out var state).ShouldBeTrue();
        state.StatPoints.ShouldBe(10);
    }
}
