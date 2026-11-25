using System.Diagnostics;
using Game.Server.Persistence;
using Game.Server.World;
using Game.Server.World.Saving;
using Game.Shared.Accounts;
using Game.Shared.Items;
using Game.Tests.Accounts;
using Game.Tests.Network;
using Xunit.Abstractions;

namespace Game.Tests.Saving;

/// <summary>T-702 cost: what a copy costs the tick, per character and for 100 at once, what the save system costs a tick with nothing due, and what 100 saves cost the database.</summary>
[Collection(TimingCollection.Name)]
public sealed class SavingBudgetTests(ITestOutputHelper output)
{
    private const int Players = 100;

    /// <summary>100 characters that own and have done something: 40 entries, 8 skills, 5 active and 30 completed quests.</summary>
    private static async Task<(SaveRig Rig, List<EntityId> Ids)> CrowdAsync(ICharacterStore store, Func<int, Guid> id)
    {
        var rig = SaveRig.Start(store);
        var ids = new List<EntityId>();
        for (var i = 0; i < Players; i++)
        {
            var entity = await rig.EnterAsync(id(i));
            ids.Add(entity);
            var bag = rig.Bag(entity);
            bag.SetEntries(Enumerable.Range(0, 40).Select(n => new InventoryEntry(rig.Saves.NextItemId(), "item.bone_dirk", 1, n == 0 ? "weapon" : null) { Refine = n % 4, Cards = n % 5 == 0 ? ["item.gelcap_card"] : [] }));
            bag.CooldownUntilMs["item.healing_draught"] = rig.Clock.NowMilliseconds + 1_000_000;
            var progress = rig.Progress(entity);
            foreach (var skill in rig.Content.Skills.All.Take(8)) progress.Skills[skill.Id] = 1;
            for (var q = 0; q < 30; q++) progress.CompletedQuests.Add("quest.done_" + q);
            for (var q = 0; q < 5; q++) progress.ActiveQuests["quest.active_" + q] = Game.Shared.Quests.QuestProgress.Load("quest.first_hunt", 0, [1, 2, 0], 0);
            foreach (var skill in rig.Content.Skills.All.Take(4)) rig.Get(entity).Cooldowns!.Start(skill.Id, 1_000_000);
        }
        await rig.SettledAsync();
        return (rig, ids);
    }

    [Fact]
    public async Task A_copy_costs_microseconds_and_a_hundred_fit_a_tick_many_times_over()
    {
        var store = new MemoryCharacterStore();
        var (rig, ids) = await CrowdAsync(store, i => new Guid(i + 1, 0, 0, new byte[8]));
        await using var _ = rig;
        var entities = ids.Select(rig.Get).ToList();
        for (var warm = 0; warm < 200; warm++) foreach (var entity in entities) rig.Keeper.Copy(entity, rig.Tickets[entity.Id]);

        var copies = new List<double>();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 20; round++)
            foreach (var entity in entities)
            {
                var start = Stopwatch.GetTimestamp();
                rig.Keeper.Copy(entity, rig.Tickets[entity.Id]);
                copies.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            }
        bytes = (GC.GetAllocatedBytesForCurrentThread() - bytes) / copies.Count;
        copies.Sort();

        // The whole step as the tick runs it: 100 due at once, copied and handed over; and then nothing due.
        var instance = rig.World.GetInstance(entities[0].MapId);
        var system = instance.Systems.OfType<SaveSystem>().Single();
        var field = typeof(MapInstance).GetField("_context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var context = (TickContext)field.GetValue(instance)!;
        store.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);   // a database that answers nothing meanwhile
        var due = new List<double>();
        var idle = new List<double>();
        for (var round = 0; round < 50; round++)
        {
            foreach (var id in ids) rig.Tickets[id].SaveSoon = true;
            var start = Stopwatch.GetTimestamp();
            system.Outbound(context);
            due.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            start = Stopwatch.GetTimestamp();
            system.Outbound(context);
            idle.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        }
        rig.Saves.Stats.Waiting.ShouldBe(Players);
        store.SaveGate.SetResult();
        await rig.SettledAsync();
        due.Sort();
        idle.Sort();
        output.WriteLine($"copy of one character (40 entries, 8 skills, 35 quests): p50 {copies[copies.Count / 2]:0.0} us, p99 {copies[copies.Count * 99 / 100]:0.0} us, {bytes} bytes");
        output.WriteLine($"{Players} due in one tick, the database answering nothing: p50 {due[25]:0} us, max {due[^1]:0} us; nothing due: p50 {idle[25]:0.0} us, max {idle[^1]:0.0} us");
        copies[copies.Count / 2].ShouldBeLessThan(100);
        due[25].ShouldBeLessThan(20_000);
        idle[25].ShouldBeLessThan(500);
    }
}

/// <summary>The database's share of <see cref="SavingBudgetTests"/>.</summary>
[Trait("Category", "Database")]
[Collection(TimingCollection.Name)]
public sealed class SavingDatabaseBudgetTests(DatabaseFixture fixture, ITestOutputHelper output) : IClassFixture<DatabaseFixture>
{
    [DatabaseFact]
    public async Task A_hundred_saves_take_a_moment()
    {
        var accounts = new PostgresAccountStore(fixture.Database ?? throw new InvalidOperationException("no database"));
        var store = new PostgresCharacterStore(fixture.Database);
        var account = Guid.NewGuid();
        await accounts.TryCreateAccountAsync(account, "budget", "hash", DateTime.UtcNow, default);
        var run = await store.StartRunAsync(default);
        long n = 0;
        var tickets = new List<CharacterTicket>();
        var states = new List<CharacterState>();
        for (var i = 0; i < 100; i++)
        {
            var name = "b" + string.Concat(i.ToString("000").Select(c => (char)('a' + (c - '0'))));
            var (_, character) = await accounts.CreateCharacterAsync(account, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.wanderer", 200, DateTime.UtcNow, default);
            var claim = await store.ClaimAsync(character!.Id, run, default);
            tickets.Add(new CharacterTicket(character.Id) { Claim = claim.Claim });
            states.Add(SavedContent.Full(() => ++n, DateTime.UtcNow) with
            {
                Items = Enumerable.Range(0, 40).Select(_ => new InventoryEntry(++n, "item.bone_dirk", 1) { Refine = 1 }).ToList(),
            });
        }

        async Task<double> RoundAsync(int writers)
        {
            var start = Stopwatch.GetTimestamp();
            await Parallel.ForEachAsync(Enumerable.Range(0, 100), new ParallelOptions { MaxDegreeOfParallelism = writers },
                async (i, _) => (await store.SaveAsync([new CharacterSave(tickets[i], ++tickets[i].TakenVersion, states[i], false, [])], default)).ShouldBeOfType<SaveResult.Saved>());
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var first = await RoundAsync(4);   // every row is new
        for (var i = 0; i < 100; i++) states[i] = states[i] with { BaseExp = 99, Items = [.. states[i].Items.Skip(1), states[i].Items[0] with { Refine = 2 }] };
        var later = await RoundAsync(4);   // one item and some numbers changed
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 100; i++) (await store.ClaimAsync(tickets[i].CharacterId, run, default)).State!.Items.Count.ShouldBe(40);
        var loads = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        output.WriteLine($"100 characters with 40 items each, 4 writers: first saves {first:0} ms, later saves (one item changed) {later:0} ms; 100 loads one after the other {loads:0} ms");
        later.ShouldBeLessThan(10_000);
    }
}
