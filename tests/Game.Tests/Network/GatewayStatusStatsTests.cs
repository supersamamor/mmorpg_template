using Game.Content.Loading;
using Game.Server.World;
using Game.Server.World.Inventory;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.World;

namespace Game.Tests.Network;

/// <summary>
/// T-406 (ADR-002 row 233): a status that changes a player's derived stats makes the server post the own <c>stats</c> message again (once per tick, after the
/// tick's systems ran) when it is applied and when it expires; a refresh that changes nothing the panel shows posts nothing.
/// </summary>
public class GatewayStatusStatsTests : IDisposable
{
    private readonly string _content = Path.Combine(Path.GetTempPath(), "status-stats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_content)) Directory.Delete(_content, recursive: true);
    }

    /// <summary>A copy of the content where Swift triples ASPD (the real +15 % never moves the floored panel value of a new character) and the elixir lasts 1.5 s.</summary>
    private Game.Content.Registry.ContentDatabase ShortSwift()
    {
        var source = Path.Combine(TestGrids.RepoRoot(), "content");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_content, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        Replace("statuses/swift.yaml", "op: percent, value: \"15\"", "op: percent, value: \"200\"");
        Replace("items/swift_elixir.yaml", "duration_ms: \"60000\"", "duration_ms: \"1500\"");
        Replace("items/swift_elixir.yaml", "cooldown_ms: 2000", "cooldown_ms: 100");
        var load = ContentLoader.Load(_content);
        load.Errors.ShouldBeEmpty(string.Join("\n", load.Errors));
        return load.Database!;
    }

    private void Replace(string file, string from, string to)
    {
        var path = Path.Combine(_content, file);
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        text.ShouldContain(from);
        File.WriteAllText(path, text.Replace(from, to));
    }

    [Fact]
    public async Task A_status_that_changes_derived_stats_reposts_stats_when_it_starts_and_ends_and_a_refresh_posts_nothing()
    {
        var content = ShortSwift();
        await using var rig = await GatewayRig.StartAsync(content: content);
        var me = await rig.JoinAsync("buffed");
        var first = await me.WaitForAsync<StatsMessage>();
        int Aspd(StatsMessage m) => m.Derived.Single(d => d.Stat == DerivedStats.Aspd).Value;
        var plain = Aspd(first);

        // Swift Elixirs straight into the bag (the fixture writes through the rules, as the inventory tests do; the player stands still).
        var inventory = InventoryContent.From(content);
        rig.Inventories.TryGet(new EntityId(me.Id), out var bag).ShouldBeTrue();
        long Give()
        {
            var delta = new InventoryDelta();
            inventory.Rules.TryAdd(bag, "item.swift_elixir", 1, int.MaxValue, rig.Inventories.NextEntryId, delta).ShouldBeNull();
            return delta.Upserted.Last().Id;
        }

        await me.SendAsync(new UseItemMessage(Give()));
        var buffed = await me.WaitForAsync<StatsMessage>(m => Aspd(m) > plain);
        buffed.Points.ShouldBe(first.Points);

        // Refreshed while it runs: the modifiers are the same, so nothing is posted. The next stats message is the one for the expiry.
        await Task.Delay(300);
        await me.SendAsync(new UseItemMessage(Give()));
        var expired = await Poll.UntilAsync(() => me.Messages.OfType<StatsMessage>().ToList(), all => all.Count >= 3, "the stats after Swift ran out");
        me.Messages.OfType<StatsMessage>().Select(Aspd).ShouldBe([plain, plain * 3, plain], "start and end each post once; the refresh posts nothing");
        expired[2].Points.ShouldBe(first.Points);
    }
}
