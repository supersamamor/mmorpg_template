using Game.Server.Accounts;
using Game.Server.World;
using Game.Server.World.Autopilot;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Quests;
using Game.Server.World.Saving;
using Game.Shared.Protocol;
using Game.Shared.Skills;
using Game.Shared.World;
using Game.Tests.Accounts;
using Game.Tests.Network;
using Game.Tests.World;

namespace Game.Tests.Saving;

/// <summary>T-702 above the database: when copies are taken and what is in them, the order saves land in, failures, logins and shutdown.</summary>
public class CharacterSavingTests
{
    private const string Meadow = "map.starter_meadow";
    private const string Hollow = "map.thistle_hollow";
    internal static readonly Guid Ada = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Cat = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static GridCell HollowCell => TestGrids.SampleContent.GetGrid(Meadow).Warps.First(w => w.TargetMapId == Hollow).TargetCell;

    // ---- what is saved ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_character_gets_its_start_items_once_and_comes_back_as_it_left()
    {
        var store = new MemoryCharacterStore();
        await using (var first = SaveRig.Start(store))
        {
            var id = await first.EnterAsync(Ada);
            first.Count(id, "item.healing_draught").ShouldBe(3);
            await first.SettledAsync();
            store.StateOf(Ada).ShouldNotBeNull("saved soon after entering");
            first.Send(new MoveTo(id, new GridCell(15, 12)));
            first.TickUntil(() => first.Get(id).Cell.Equals(new GridCell(15, 12)), "the walk");
            first.Progress(id).BaseExp = 17;
            first.Bag(id).Currency = 55;
            await first.LeaveAsync(id);
            first.Progressions.Count.ShouldBe(0, "and the server forgets it");
        }
        await using var second = SaveRig.Start(store);
        var again = await second.EnterAsync(Ada);
        second.Get(again).Cell.ShouldBe(new GridCell(15, 12));
        second.Progress(again).BaseExp.ShouldBe(17);
        second.Bag(again).Currency.ShouldBe(55);
        second.Count(again, "item.healing_draught").ShouldBe(3, "the start items are given once");
        second.Get(again).Weapon.ShouldNotBeNull("what it wore is worn");
        second.Get(again).Look!.Worn.Count.ShouldBe(store.StateOf(Ada)!.Items.Count(i => i.Slot is not null));
    }

    [Fact]
    public async Task Every_field_survives_entering_and_leaving()
    {
        var store = new MemoryCharacterStore();
        var time = new FakeTime();
        long n = 1_000;
        var saved = SavedContent.Full(() => ++n, time.Now.UtcDateTime);
        // What the world itself changes within a few ticks is asked for on its own below: owed rewards are paid, HP and SP regenerate, times run.
        saved = saved with { OwedRewards = [], SettledTurnIns = [7] };
        store.Put(Ada, saved);
        await using var rig = SaveRig.Start(store, time: time);
        var id = await rig.EnterAsync(Ada);
        var entity = rig.Get(id);
        entity.DefinitionId.ShouldBe("job.warden");
        entity.Stats!.Base.BaseLevel.ShouldBe(14);
        entity.Stats.Base["str"].ShouldBe(12);
        (entity.Vitals!.Hp, entity.Vitals.Sp).ShouldBe((31, 7), "not full, as a new character's");
        entity.Cooldowns!.LeftMs("skill.twin_strike").ShouldBeInRange(8_000, 9_000);
        entity.AutopilotUsage!.UsedMs.ShouldBe(123_456);
        await rig.LeaveAsync(id);

        var back = SavedContent.Fields(store.StateOf(Ada)!);
        var before = SavedContent.Fields(saved);
        string[] timed = [nameof(CharacterState.SkillCooldowns), nameof(CharacterState.ItemCooldowns), nameof(CharacterState.AutopilotDayStartedAt)];
        foreach (var (name, value) in before)
            if (!timed.Contains(name)) back[name].ShouldBe(value, name);
        var state = store.StateOf(Ada)!;
        state.SkillCooldowns["skill.twin_strike"].ShouldBeInRange(8_000, 9_000);
        state.ItemCooldowns["item.healing_draught"].ShouldBeLessThanOrEqualTo(2_500);
        state.AutopilotDayStartedAt!.Value.ShouldBe(saved.AutopilotDayStartedAt!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task HP_above_the_bare_maximum_survives_because_equipment_and_passives_count()
    {
        var store = new MemoryCharacterStore();
        long n = 1_000;
        var saved = SavedContent.Full(() => ++n, DateTime.UtcNow) with
        {
            Skills = new Dictionary<string, int> { ["skill.tough_body"] = 5 },
            Items = [new Game.Shared.Items.InventoryEntry(++n, "item.padded_vest", 1, "body") { Cards = ["item.gelcap_card"] }],
            Hp = 1, OwedRewards = [],
        };
        int max;
        store.Put(Ada, saved);
        await using (var rig = SaveRig.Start(store))
        {
            var id = await rig.EnterAsync(Ada);
            max = rig.Get(id).Vitals!.MaxHp;
            var bare = new Game.Server.Net.NewCharacters(rig.Content).CreateStats(rig.Progress(id));
            bare.GetInt(Game.Shared.Stats.StatIndex.IndexOf("max_hp")).ShouldBeLessThan(max);
        }
        store.Put(Bob, saved with { Hp = max, Items = [saved.Items[0] with { Id = ++n }] });
        await using var second = SaveRig.Start(store);
        second.Get(await second.EnterAsync(Bob)).Vitals!.Hp.ShouldBe(max);
    }

    [Fact]
    public async Task Cooldowns_the_job_and_the_autopilots_time_are_not_reset_by_signing_in_again()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var id = await rig.EnterAsync(Ada);
        rig.Get(id).Cooldowns!.Start("skill.twin_strike", 60_000);
        rig.Get(id).AutopilotUsage = AutopilotUsage.Restore(90_000, 1_000, rig.Clock.NowMilliseconds);
        rig.Bag(id).CooldownUntilMs["item.healing_draught"] = rig.Clock.NowMilliseconds + 60_000;
        await rig.LeaveAsync(id);
        rig.Time.Now += TimeSpan.FromSeconds(20);
        id = await rig.EnterAsync(Ada);
        rig.Get(id).Cooldowns!.LeftMs("skill.twin_strike").ShouldBeInRange(35_000, 40_000);
        (rig.Bag(id).CooldownUntilMs["item.healing_draught"] - rig.Clock.NowMilliseconds).ShouldBeInRange(35_000, 40_000);
        rig.Get(id).AutopilotUsage!.UsedMs.ShouldBe(90_000, "ADR-002 row 293");
        rig.Get(id).Autopilot.ShouldBeNull("a run itself is not saved");
        await rig.LeaveAsync(id);
        rig.Time.Now += TimeSpan.FromHours(25);
        id = await rig.EnterAsync(Ada);
        rig.Get(id).AutopilotUsage!.Roll(rig.Clock.NowMilliseconds);
        rig.Get(id).AutopilotUsage!.UsedMs.ShouldBe(0, "a new day");
    }

    [Fact]
    public async Task A_character_that_died_comes_back_at_its_save_point_alive()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var id = await rig.EnterAsync(Ada);
        rig.SavePoints.Set(id, new SavePoint(Meadow, new GridCell(13, 13)));
        rig.Get(id).Dead = true;
        rig.Get(id).Vitals!.Hp = 0;
        await rig.LeaveAsync(id);
        store.StateOf(Ada)!.Hp.ShouldBe(0);
        id = await rig.EnterAsync(Ada);
        var entity = rig.Get(id);
        entity.Dead.ShouldBeFalse();
        entity.Cell.ShouldBe(new GridCell(13, 13));
        entity.Vitals!.Hp.ShouldBe(Math.Max(1, entity.Vitals.MaxHp * rig.Content.Balance.Death.RespawnHpPercent / 100));
    }

    [Fact]
    public async Task A_dev_login_character_is_never_saved()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var start = new Game.Server.Net.NewCharacters(rig.Content);
        var id = rig.World.Spawn(EntityKind.Player, start.MapId, start.Cell, start.Facing, e =>
        {
            e.Stats = start.CreateStats();
            e.DefinitionId = start.JobId;
        });
        rig.Ticks(3);
        rig.World.Warp(id, Hollow, HollowCell);
        rig.Ticks(3);
        rig.World.Despawn(id);
        rig.Ticks(1);
        rig.World.LocationOf(id).ShouldBeNull("and leaves at once, as before");
        store.Groups.ShouldBeEmpty();
        store.Claims.ShouldBe(0);
    }

    // ---- when -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task It_saves_on_the_interval_and_on_a_map_change()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store, SaveRig.Fast with { IntervalMs = 40 });
        var id = await rig.EnterAsync(Ada);
        await rig.SettledAsync();
        var version = store.VersionOf(Ada);
        await Poll.UntilAsync(() =>
        {
            rig.Ticks();
            return store.VersionOf(Ada);
        }, v => v >= version + 3, "interval saves");

        await using var slow = SaveRig.Start(new MemoryCharacterStore());
        var other = await slow.EnterAsync(Bob);
        await slow.SettledAsync();
        slow.World.Warp(other, Hollow, HollowCell);
        slow.Ticks(4);
        await slow.SettledAsync();
        var copy = slow.Tickets[other].LandedVersion;
        copy.ShouldBe(2, "one on entering, one on the map change");
    }

    [Fact]
    public async Task A_character_removed_while_it_warps_is_saved_on_the_map_it_was_going_to()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var id = await rig.EnterAsync(Ada);
        rig.Progress(id).BaseExp = 12;
        rig.World.Warp(id, Hollow, HollowCell);
        rig.World.GetInstance(Meadow).Tick();   // it has left the meadow and is on its way
        rig.World.LocationOf(id).ShouldBe(Hollow);
        await rig.LeaveAsync(id);
        (store.StateOf(Ada)!.MapId, store.StateOf(Ada)!.BaseExp).ShouldBe((Hollow, 12L));
        rig.Progressions.Count.ShouldBe(0);
        rig.Inventories.Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_character_that_never_entered_is_given_up_unchanged()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var entered = await rig.Saves.EnterAsync(Ada, default);
        rig.Saves.Holds(Ada).ShouldBeTrue();
        store.IsHeld(Ada).ShouldBeTrue();
        var start = new Game.Server.Net.NewCharacters(rig.Content);
        var id = rig.World.Spawn(EntityKind.Player, start.MapId, start.Cell, start.Facing, _ => throw new InvalidOperationException("must not run"), entered.Ticket);
        rig.World.Despawn(id);   // before its instance ticked
        rig.Ticks(2);
        await entered.Ticket!.Gone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Saves.Holds(Ada).ShouldBeFalse();
        store.IsHeld(Ada).ShouldBeFalse();
        store.StateOf(Ada).ShouldBeNull();
        (await rig.EnterAsync(Ada)).Value.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task An_initializer_that_throws_saves_nothing_over_what_there_is()
    {
        var store = new MemoryCharacterStore();
        long n = 1_000;
        store.Put(Ada, SavedContent.Full(() => ++n, DateTime.UtcNow));
        await using var rig = SaveRig.Start(store);
        var entered = await rig.Saves.EnterAsync(Ada, default);
        var id = rig.World.Spawn(EntityKind.Player, Meadow, new GridCell(12, 12), "s", e =>
        {
            e.Stats = new Game.Server.Net.NewCharacters(rig.Content).CreateStats();
            throw new InvalidOperationException("test");
        }, entered.Ticket);
        rig.Ticks(5);
        rig.World.Despawn(id);
        rig.Ticks(3);
        await entered.Ticket!.Gone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        store.StateOf(Ada)!.BaseLevel.ShouldBe(14);
        store.VersionOf(Ada).ShouldBe(1);
    }

    [Fact]
    public async Task A_copy_and_a_leave_wait_for_what_spans_ticks()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var id = await rig.EnterAsync(Ada);
        await rig.SettledAsync();
        rig.Get(id).Vitals!.Hp = 1;
        var ticket = rig.Tickets[id];
        var version = ticket.TakenVersion;
        ticket.SaveSoon = true;
        rig.Send(new UseItem(id, rig.Bag(id).Entries.First(e => e.ItemId == "item.healing_draught").Id));
        rig.Ticks(1);
        rig.Of<ItemUsed>().Count.ShouldBe(1);
        rig.Get(id).Vitals!.Hp.ShouldBe(1, "used up, and its effect comes in the next tick");
        ticket.TakenVersion.ShouldBe(version, "so the copy waits");
        rig.Ticks(1);
        ticket.TakenVersion.ShouldBe(version + 1);
        rig.Get(id).Vitals!.Hp.ShouldBeGreaterThan(1);

        rig.Bag(id).CooldownUntilMs.Clear();
        rig.Get(id).Vitals!.Hp = 1;
        rig.Send(new UseItem(id, rig.Bag(id).Entries.First(e => e.ItemId == "item.healing_draught").Id));
        await rig.LeaveAsync(id);   // asked to leave in the same tick
        store.StateOf(Ada)!.Hp.ShouldBeGreaterThan(1, "the draught it drank on leaving did its work");
        store.StateOf(Ada)!.Items.Single(i => i.ItemId == "item.healing_draught").Quantity.ShouldBe(1);
    }

    // ---- quest turn-ins: rows 257 and 264 -----------------------------------------------------------------------------------------------

    private static async Task<EntityId> WithFirstHuntDoneAsync(SaveRig rig, Guid character)
    {
        var id = await rig.EnterAsync(character);
        rig.Progress(id).ActiveQuests["quest.first_hunt"] = Game.Shared.Quests.QuestProgress.Load("quest.first_hunt", 0, [5, 3, 0], 0);
        rig.Put(id, "item.gel_glob", 3);
        return id;
    }

    private static NpcTalked Talk(SaveRig rig, EntityId id) =>
        new(id, rig.On(id).Entities.First(e => e.DefinitionId == "npc.guide").Id, "npc.guide");

    /// <summary>The turn-in begins; after <paramref name="ticks"/> more ticks something happens to the character. Its whole reward arrives, once, and the globs go once.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task A_warp_at_any_tick_of_a_turn_in_neither_loses_nor_repeats_it(int ticks)
    {
        await using var rig = SaveRig.Start(new MemoryCharacterStore());
        var id = await WithFirstHuntDoneAsync(rig, Ada);
        rig.On(id).Events.Publish(Talk(rig, id));
        rig.Ticks(ticks);
        rig.World.Warp(id, Hollow, HollowCell);
        rig.Ticks(10);
        rig.World.LocationOf(id).ShouldBe(Hollow);
        if (ticks == 0)
        {
            // It left in the tick it talked, so the talk never counted: everything is as it was.
            rig.Progress(id).ActiveQuests["quest.first_hunt"].Counters.ShouldBe([5, 3, 0]);
            rig.Count(id, "item.gel_glob").ShouldBe(3);
            rig.Bag(id).Currency.ShouldBe(0);
            return;
        }
        ExpectPaidOnce(rig, id);
    }

    /// <summary>The same with a crash: whatever tick is the last one saved, loading it settles the turn-in exactly once.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task A_save_at_any_tick_of_a_turn_in_neither_loses_nor_repeats_it(int ticks)
    {
        var store = new MemoryCharacterStore();
        CharacterState state;
        await using (var rig = SaveRig.Start(store))
        {
            var id = await WithFirstHuntDoneAsync(rig, Ada);
            rig.On(id).Events.Publish(Talk(rig, id));
            rig.Ticks(ticks);
            state = rig.Keeper.Copy(rig.Get(id), rig.Tickets[id])!;   // as a copy the waiting rule could not prevent
        }
        var later = new MemoryCharacterStore();
        later.Put(Ada, state);
        await using var second = SaveRig.Start(later);
        var again = await second.EnterAsync(Ada);
        second.Ticks(10);
        if (second.Progress(again).ActiveQuests.TryGetValue("quest.first_hunt", out var still))
        {
            still.Counters[2].ShouldBe(0, "copied before the talk counted: it is asked to talk again");
            second.Count(again, "item.gel_glob").ShouldBe(3);
            second.Bag(again).Currency.ShouldBe(0);
            second.On(again).Events.Publish(Talk(second, again));
            second.Ticks(10);
        }
        ExpectPaidOnce(second, again);
    }

    private static void ExpectPaidOnce(SaveRig rig, EntityId id)
    {
        var progress = rig.Progress(id);
        progress.ActiveQuests.ShouldBeEmpty();
        progress.CompletedQuests.ShouldBe(["quest.first_hunt"]);
        rig.Count(id, "item.gel_glob").ShouldBe(0);
        rig.Count(id, "item.healing_draught").ShouldBe(3 + 5);
        rig.Bag(id).Currency.ShouldBe(150);
        progress.OwedRewards.ShouldBeEmpty();
        (progress.BaseLevel > 1 || progress.BaseExp == 40).ShouldBeTrue("the EXP was paid");
        var curve = rig.Content.ExpTables.Get("exp.base").Exp;
        (curve.Take(progress.BaseLevel - 1).Sum() + progress.BaseExp).ShouldBe(40, "once");
    }

    [Fact]
    public async Task Abandoning_a_quest_whose_turn_in_is_being_paid_does_not_keep_both()
    {
        await using var rig = SaveRig.Start(new MemoryCharacterStore());
        for (var ticks = 0; ticks < 4; ticks++)
        {
            var id = await WithFirstHuntDoneAsync(rig, new Guid(ticks + 1, 0, 0, new byte[8]));
            rig.On(id).Events.Publish(Talk(rig, id));
            rig.Ticks(ticks);
            rig.Send(new AbandonQuest(id, "quest.first_hunt"));
            rig.Ticks(10);
            rig.Progress(id).ActiveQuests.ShouldBeEmpty();
            var paid = rig.Bag(id).Currency == 150;
            rig.Progress(id).CompletedQuests.Contains("quest.first_hunt").ShouldBe(paid, $"after {ticks} ticks");
            rig.Count(id, "item.gel_glob").ShouldBe(paid ? 0 : 3);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Abandoning_on_arrival_does_not_keep_both_either(int ticks)
    {
        await using var rig = SaveRig.Start(new MemoryCharacterStore());
        var id = await WithFirstHuntDoneAsync(rig, Ada);
        rig.On(id).Events.Publish(Talk(rig, id));
        rig.Ticks(ticks);
        rig.World.Warp(id, Hollow, HollowCell);
        for (var i = 0; i < 4; i++)
        {
            rig.Ticks(1);
            rig.World.TryEnqueue(new AbandonQuest(id, "quest.first_hunt"));
        }
        rig.Ticks(10);
        var paid = rig.Bag(id).Currency == 150;
        rig.Progress(id).ActiveQuests.ShouldBeEmpty();
        rig.Progress(id).CompletedQuests.Contains("quest.first_hunt").ShouldBe(paid);
        rig.Count(id, "item.gel_glob").ShouldBe(paid ? 0 : 3);
    }

    [Fact]
    public async Task Paid_turn_in_numbers_are_forgotten_when_their_quests_have_gone_on()
    {
        await using var rig = SaveRig.Start(new MemoryCharacterStore());
        var id = await WithFirstHuntDoneAsync(rig, Ada);
        rig.On(id).Events.Publish(Talk(rig, id));
        rig.Ticks(10);
        var first = rig.Bag(id).SettledTurnIns.Single();
        rig.Progress(id).CompletedQuests.Clear();
        rig.Progress(id).ActiveQuests["quest.first_hunt"] = Game.Shared.Quests.QuestProgress.Load("quest.first_hunt", 0, [5, 3, 0], 0);
        rig.Put(id, "item.gel_glob", 3);
        rig.On(id).Events.Publish(Talk(rig, id));
        rig.Ticks(10);
        rig.Bag(id).SettledTurnIns.Single().ShouldBe(first + 1, "a number is never used twice, and the old one is gone");
        rig.Bag(id).Currency.ShouldBe(300);
    }

    // ---- items between characters: the order ----------------------------------------------------------------------------------------------

    private static async Task<(EntityId Ada, EntityId Bob, long Entry)> AboutToHandOverAsync(SaveRig rig)
    {
        var ada = await rig.EnterAsync(Ada);
        var bob = await rig.EnterAsync(Bob);
        var entry = rig.Put(ada, "item.iron_blade");
        await rig.SaveAsync(ada);
        await rig.SettledAsync();
        return (ada, bob, entry);
    }

    private static void HandOver(SaveRig rig, EntityId from, EntityId to, long entry)
    {
        rig.Send(new DropItem(from, entry, 1));
        rig.Ticks(1);
        var item = rig.On(from).Entities.Last(e => e.Kind == EntityKind.Item);
        rig.Send(new PickUp(to, item.Id));
        rig.TickUntil(() => rig.Bag(to).Find(entry) is not null, "the pick-up", 30);
    }

    [Fact]
    public async Task Who_picked_an_item_up_is_never_saved_before_who_put_it_down()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var (ada, bob, entry) = await AboutToHandOverAsync(rig);
        store.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HandOver(rig, ada, bob, entry);
        rig.Bag(bob).Find(entry)!.ItemId.ShouldBe("item.iron_blade", "the same permanent id");
        rig.Tickets[bob].SaveSoon = true;
        rig.Ticks(3);
        store.Groups.Clear();
        store.SaveGate.SetResult();
        await rig.SettledAsync();
        var order = store.Groups.SelectMany((g, i) => g.Select(s => (Group: i, s.Character))).ToList();
        order.First(o => o.Character == Ada).Group.ShouldBeLessThanOrEqualTo(order.First(o => o.Character == Bob).Group);
        store.StateOf(Ada)!.Items.ShouldNotContain(i => i.Id == entry);
        store.StateOf(Bob)!.Items.ShouldContain(i => i.Id == entry);
        rig.Tickets[bob].Links.Keys.ShouldContain(rig.Tickets[ada]);
        await rig.SaveAsync(bob);
        rig.Tickets[bob].Links.ShouldBeEmpty("landed links are dropped");
    }

    [Fact]
    public async Task Two_who_swapped_items_are_saved_together_or_not_at_all()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var (ada, bob, entry) = await AboutToHandOverAsync(rig);
        var other = rig.Put(bob, "item.wooden_buckler");
        await rig.SaveAsync(bob);
        store.Away = true;
        // What a trade does (T-903): in one tick, each gives and gets.
        rig.On(ada).Events.Publish(new Noted(() =>
        {
            var a = rig.Bag(ada).Find(entry)!;
            var b = rig.Bag(bob).Find(other)!;
            rig.Bag(ada).SetEntries(rig.Bag(ada).Entries.Where(e => e.Id != entry).Append(b).ToList());
            rig.Bag(bob).SetEntries(rig.Bag(bob).Entries.Where(e => e.Id != other).Append(a).ToList());
            var (fromAda, fromBob) = (rig.Tickets[ada].PutDown(), rig.Tickets[bob].PutDown());
            rig.Tickets[bob].DependOn(fromAda);
            rig.Tickets[ada].DependOn(fromBob);
        }));
        rig.On(ada).Events.Subscribe<Noted>(n => n.Run());
        rig.Ticks(3);
        await Poll.UntilAsync(() => rig.Saves.Stats.Failures, f => f > 0, "a failure");
        store.Groups.Clear();
        store.Away = false;
        await rig.SettledAsync();
        store.Groups.Count.ShouldBe(1);
        store.Groups[0].Select(s => s.Character).ShouldBe([Ada, Bob], ignoreOrder: true);
    }

    private sealed record Noted(Action Run) : Game.Server.Events.IGameEvent
    {
        public string Name => "TestNoted";
    }

    [Fact]
    public async Task A_chain_of_three_lands_giver_first()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var (ada, bob, entry) = await AboutToHandOverAsync(rig);
        var cat = await rig.EnterAsync(Cat);
        await rig.SettledAsync();
        store.Away = true;
        HandOver(rig, ada, bob, entry);
        HandOver(rig, bob, cat, entry);
        rig.Tickets[cat].SaveSoon = true;
        rig.Ticks(2);
        store.Groups.Clear();
        store.Away = false;
        await rig.SettledAsync();
        var landed = new HashSet<Guid>();
        foreach (var group in store.Groups)
        {
            foreach (var (character, _) in group) landed.Add(character);
            if (landed.Contains(Cat)) landed.ShouldContain(Bob);
            if (landed.Contains(Bob)) landed.ShouldContain(Ada);
        }
        landed.Count.ShouldBe(3);
        store.StateOf(Cat)!.Items.ShouldContain(i => i.Id == entry);
    }

    [Fact]
    public async Task Stacks_count_too_and_picking_up_your_own_needs_nothing()
    {
        await using var rig = SaveRig.Start(new MemoryCharacterStore());
        var ada = await rig.EnterAsync(Ada);
        var bob = await rig.EnterAsync(Bob);
        var draughts = rig.Bag(ada).Entries.First(e => e.ItemId == "item.healing_draught").Id;
        rig.Send(new DropItem(ada, draughts, 2));
        rig.Ticks(1);
        var item = rig.On(ada).Entities.Last(e => e.Kind == EntityKind.Item).Id;
        rig.Send(new PickUp(ada, item));
        rig.TickUntil(() => rig.Count(ada, "item.healing_draught") == 3, "taking it back");
        rig.Tickets[ada].Links.ShouldBeEmpty();
        rig.Send(new DropItem(ada, draughts, 2));
        rig.Ticks(1);
        item = rig.On(ada).Entities.Last(e => e.Kind == EntityKind.Item).Id;
        rig.Send(new PickUp(bob, item));
        rig.TickUntil(() => rig.Count(bob, "item.healing_draught") == 5, "bob takes it");
        rig.Tickets[bob].Links.Keys.ShouldBe([rig.Tickets[ada]]);
    }

    [Fact]
    public async Task A_character_lost_to_this_server_takes_those_with_it_who_hold_what_it_gave_away()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var (ada, bob, entry) = await AboutToHandOverAsync(rig);
        var lost = new List<Guid>();
        rig.Saves.Lost += t => { lock (lost) lost.Add(t.CharacterId); };
        store.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);   // nothing is found out before he has it
        store.Steal(Ada);   // another server claimed her: her drop never happened there
        HandOver(rig, ada, bob, entry);
        rig.Tickets[bob].SaveSoon = true;
        rig.Ticks(3);
        store.SaveGate.SetResult();
        await Poll.UntilAsync(() => { lock (lost) return lost.Count; }, n => n == 2, "both lost");
        rig.TickUntil(() => rig.World.LocationOf(ada) is null && rig.World.LocationOf(bob) is null, "both leave");
        store.StateOf(Ada)!.Items.ShouldContain(i => i.Id == entry, "she still has it");
        store.StateOf(Bob)!.Items.ShouldNotContain(i => i.Id == entry, "he never got it");
        rig.Saves.Stats.Lost.ShouldBe(2);
        rig.Saves.Holds(Bob).ShouldBeFalse();
        var again = await rig.EnterAsync(Bob);
        rig.Bag(again).Find(entry).ShouldBeNull();
    }

    [Fact]
    public async Task What_a_lost_character_put_down_cannot_be_picked_up()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var (ada, bob, entry) = await AboutToHandOverAsync(rig);
        store.Steal(Ada);
        rig.Send(new DropItem(ada, entry, 1));
        rig.Ticks(1);
        var item = rig.On(ada).Entities.Last(e => e.Kind == EntityKind.Item).Id;
        await Poll.UntilAsync(() => rig.Tickets[ada].IsLost, l => l, "lost");
        rig.Send(new PickUp(bob, item));
        rig.Ticks(3);
        rig.Bag(bob).Find(entry).ShouldBeNull();
        rig.On(bob).TryGetEntity(item, out _).ShouldBeFalse();
        rig.Of<ItemRefused>().Last().Reason.ShouldBe("item_gone");
        await rig.SaveAsync(bob);
        rig.Tickets[bob].IsLost.ShouldBeFalse();
    }

    [Fact]
    public async Task Duplicated_items_are_reported_and_not_saved()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var (ada, bob, entry) = await AboutToHandOverAsync(rig);
        // A bug: the same entry in two bags, without a link.
        rig.Bag(bob).SetEntries(rig.Bag(bob).Entries.Append(rig.Bag(ada).Find(entry)!).ToList());
        rig.Tickets[bob].SaveSoon = true;
        rig.Ticks(2);
        await Poll.UntilAsync(() => rig.Tickets[bob].IsLost, l => l, "bob is lost");
        rig.Log.Lines.ShouldContain(l => l.Contains("DUPLICATED ITEMS: " + entry));
        store.StateOf(Bob)!.Items.ShouldNotContain(i => i.Id == entry);
        rig.Tickets[ada].IsLost.ShouldBeFalse();
    }

    // ---- item ids -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_restart_repeats_an_item_id()
    {
        var store = new MemoryCharacterStore();
        var seen = new HashSet<long>();
        for (var run = 0; run < 3; run++)
        {
            await using var rig = SaveRig.Start(store);
            await Parallel.ForEachAsync(Enumerable.Range(0, 1_000), (_, _) =>
            {
                var id = rig.Saves.NextItemId();
                lock (seen) seen.Add(id).ShouldBeTrue();
                return ValueTask.CompletedTask;
            });
            (rig.Saves.NextItemId() >> CharacterSaves.ItemIdRunShift).ShouldBe(rig.Saves.Run);
        }
        seen.Count.ShouldBe(3_000);
    }

    // ---- failures -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_load_refuses_the_login_and_changes_nothing()
    {
        var store = new MemoryCharacterStore();
        long n = 1_000;
        store.Put(Ada, SavedContent.Full(() => ++n, DateTime.UtcNow));
        await using var rig = SaveRig.Start(store);
        store.Away = true;
        (await rig.Saves.EnterAsync(Ada, default)).Refusal.ShouldBe(ByeReasons.LoadFailed);
        store.Away = false;
        store.ClaimsFail = true;   // an unreadable state, not a database that is away
        (await rig.Saves.EnterAsync(Ada, default)).Refusal.ShouldBe(ByeReasons.LoadFailed);
        (await rig.Saves.EnterAsync(Bob, default)).Refusal.ShouldBe(ByeReasons.LoadFailed, "never read as a new character");
        store.ClaimsFail = false;
        rig.Saves.Holds(Ada).ShouldBeFalse();
        (store.VersionOf(Ada), store.StateOf(Ada)!.BaseLevel).ShouldBe((1L, 14));
        store.StateOf(Bob).ShouldBeNull();
        store.Deleted.Add(Cat);
        (await rig.Saves.EnterAsync(Cat, default)).Refusal.ShouldBe(ByeReasons.LoginRefused, "row 289: checked as it enters");
        (await rig.EnterAsync(Ada)).Value.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_failed_save_is_tried_again_while_the_character_plays_and_the_operator_is_told_when_it_is_at_risk()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var id = await rig.EnterAsync(Ada);
        await rig.SettledAsync();
        store.Away = true;
        rig.Progress(id).BaseExp = 5;
        rig.Tickets[id].SaveSoon = true;
        rig.Ticks(2);
        await Poll.UntilAsync(() => rig.Saves.Stats.AtRisk, n => n == 1, "at risk");
        rig.Log.Lines.Count(l => l.StartsWith("ERROR") && l.Contains("IS AT RISK")).ShouldBe(1, "said once");
        rig.Log.Lines.Count(l => l.Contains(": save ") && l.Contains("failed (test: away)")).ShouldBe(1);
        rig.Send(new MoveTo(id, new GridCell(13, 12)));
        rig.TickUntil(() => rig.Get(id).Cell.Equals(new GridCell(13, 12)), "it plays on");
        rig.Progress(id).BaseExp = 6;
        rig.Tickets[id].SaveSoon = true;
        rig.Ticks(2);
        store.Away = false;
        await rig.SettledAsync();
        store.StateOf(Ada)!.BaseExp.ShouldBe(6, "the newest copy, not the first one that failed");
        rig.Saves.Stats.AtRisk.ShouldBe(0);
        rig.Log.Lines.ShouldContain(l => l.Contains("no longer at risk"));
    }

    [Fact]
    public async Task A_character_whose_last_save_has_not_landed_cannot_be_entered_again()
    {
        var store = new MemoryCharacterStore();
        await using var rig = SaveRig.Start(store);
        var id = await rig.EnterAsync(Ada);
        await rig.SettledAsync();
        rig.Bag(id).Currency = 77;
        store.Away = true;
        rig.World.Despawn(id);
        rig.TickUntil(() => rig.World.LocationOf(id) is null, "leaving");
        rig.Saves.Holds(Ada).ShouldBeTrue("held until it lands, so it cannot be deleted either");
        (await rig.Saves.EnterAsync(Ada, default)).Refusal.ShouldBe(ByeReasons.SavePending);
        store.Claims.ShouldBe(1, "the old state was not even read");
        store.Away = false;
        var again = await rig.EnterAsync(Ada);   // waits for it
        rig.Bag(again).Currency.ShouldBe(77);
    }

    [Fact]
    public async Task Another_live_server_keeps_a_character_and_a_dead_one_does_not()
    {
        var store = new MemoryCharacterStore();
        await using var first = SaveRig.Start(store);
        var id = await first.EnterAsync(Ada);
        await first.SettledAsync();
        await using var second = SaveRig.Start(store);
        (await second.Saves.EnterAsync(Ada, default)).Refusal.ShouldBe(ByeReasons.SavePending);
        await store.EndRunAsync(first.Saves.Run, default);   // its lease is over
        var taken = await second.EnterAsync(Ada);
        second.Bag(taken).Currency = 9;
        first.Bag(id).Currency = 1_000_000;
        first.Tickets[id].SaveSoon = true;
        first.Ticks(2);
        await Poll.UntilAsync(() => first.Tickets[id].IsLost, l => l, "the late server has lost it");
        await second.SaveAsync(taken);
        store.StateOf(Ada)!.Currency.ShouldBe(9);
    }

    [Fact]
    public async Task Logins_of_one_character_at_once_enter_it_one_after_the_other()
    {
        var store = new MemoryCharacterStore { ClaimGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var rig = SaveRig.Start(store, SaveRig.Fast with { LoginWaitMs = 5_000 });
        using var cancel = new CancellationTokenSource();
        var first = rig.Saves.EnterAsync(Ada, cancel.Token);
        var second = rig.Saves.EnterAsync(Ada, default);
        await Task.Delay(50);
        store.Claims.ShouldBe(1, "the second waits");
        await cancel.CancelAsync();   // the first connection drops
        store.ClaimGate.SetResult();
        await Should.ThrowAsync<OperationCanceledException>(() => first);
        var entered = await second;
        entered.Refusal.ShouldBeNull();
        store.Claims.ShouldBe(2);
        rig.Saves.Holds(Ada).ShouldBeTrue();
    }

    // ---- shutdown -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_shutdown_waits_for_every_save_and_ends_the_run()
    {
        var store = new MemoryCharacterStore();
        var rig = SaveRig.Start(store, SaveRig.Fast with { SpacingMs = 60_000 });   // the last save does not wait for the spacing
        var ids = new List<EntityId>();
        for (var i = 1; i <= 20; i++) ids.Add(await rig.EnterAsync(new Guid(i, 0, 0, new byte[8])));
        foreach (var id in ids) rig.Bag(id).Currency = 100 + id.Value;
        foreach (var id in ids) rig.World.Despawn(id);
        using var stop = new CancellationTokenSource();
        var ticking = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                rig.Ticks();
                await Task.Delay(5);
            }
        });
        var report = await rig.Saves.DrainAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await ticking;
        report.Failed.ShouldBeEmpty();
        for (var i = 0; i < 20; i++) store.StateOf(new Guid(i + 1, 0, 0, new byte[8]))!.Currency.ShouldBe(100 + ids[i].Value);
        (await rig.Saves.EnterAsync(Ada, default)).Refusal.ShouldBe(ByeReasons.Shutdown);
    }

    // ---- never on a tick thread -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Loading_is_refused_on_a_tick_thread()
    {
        await using var rig = SaveRig.Start(new MemoryCharacterStore());
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            Game.Server.Hosting.TickThread.Enter();
            try
            {
                rig.Saves.EnterAsync(Ada, default).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException e)
            {
                caught = e;
            }
        });
        thread.Start();
        thread.Join();
        caught.ShouldNotBeNull().Message.ShouldContain("tick thread");
    }
}

/// <summary>
/// The one test here that measures wall time (T-710): beside the rest of the suite the thread pool ran its short waits seconds late (3.9 s against the bound of 3 s, once), which says nothing
/// about the shutdown.
/// </summary>
[Collection(TimingCollection.Name)]
public class CharacterSavingShutdownTimeTests
{
    [Fact]
    public async Task A_shutdown_without_a_database_is_bounded_and_says_who_was_not_saved()
    {
        var store = new MemoryCharacterStore();
        var rig = SaveRig.Start(store);
        var id = await rig.EnterAsync(CharacterSavingTests.Ada);
        await rig.SettledAsync();
        rig.Bag(id).Currency = 5;
        store.Away = true;
        rig.World.Despawn(id);
        rig.Ticks(5);
        var started = Environment.TickCount64;
        var report = await rig.Saves.DrainAsync(TimeSpan.FromMilliseconds(300));
        (Environment.TickCount64 - started).ShouldBeLessThan(3_000);
        report.Failed.ShouldBe([CharacterSavingTests.Ada]);
        rig.Log.Lines.ShouldContain(l => l.Contains("NOT SAVED at shutdown"));
        store.StateOf(CharacterSavingTests.Ada)!.Currency.ShouldBe(0, "back to its last save");
    }
}
