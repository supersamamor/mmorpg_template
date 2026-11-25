using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Saving;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Quests;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Saving;

/// <summary>T-702: a saved character against content that has changed. Nothing disappears silently, nothing throws, and every rule leaves a note.</summary>
public class CharacterLoaderTests
{
    private static readonly DateTime At = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private long _last = 100;

    private long NewId() => ++_last;

    private CharacterState Full() => SavedContent.Full(NewId, At);

    private CharacterLoader Loader(Game.Content.Registry.ContentDatabase? content = null) => new(content ?? TestGrids.SampleContent, NewId);

    private LoadedCharacter Load(CharacterState state, Game.Content.Registry.ContentDatabase? content = null, TimeSpan? away = null) => Loader(content).Load(state, At + (away ?? TimeSpan.Zero));

    [Fact]
    public void Unchanged_content_gives_everything_back_and_notes_nothing()
    {
        var saved = Full();
        var loaded = Load(saved);
        loaded.Notes.ShouldBeEmpty();
        loaded.MapId.ShouldBe(saved.MapId);
        loaded.Cell.ShouldBe(saved.Cell);
        loaded.Facing.ShouldBe("ne");
        (loaded.Hp, loaded.HpPercent, loaded.Sp).ShouldBe((31, 0, 7));
        loaded.SavePoint.ShouldBe(saved.SavePoint);
        var p = loaded.Progression;
        (p.BaseLevel, p.BaseExp, p.JobId, p.JobLevel, p.JobExp, p.StatPoints, p.SkillPoints, p.TurnInSequence).ShouldBe((14, 1234L, "job.warden", 6, 345L, 5, 2, 9L));
        p.Stats["str"].ShouldBe(12);
        p.Skills.ShouldBe(new Dictionary<string, int> { ["skill.twin_strike"] = 2 });
        p.CompletedQuests.ShouldBe(["quest.warden_trial"]);
        p.UnlockedJobs.ShouldBe(["job.warden"]);
        p.OwedRewards.ShouldBe(saved.OwedRewards);
        var quest = p.ActiveQuests["quest.first_hunt"];
        (quest.Step, quest.Token, quest.Waiting, quest.Held).ShouldBe((0, 4L, false, null));
        quest.Counters.ShouldBe([2, 3, 0]);
        loaded.Inventory.Entries.ShouldBe(saved.Items);
        loaded.Inventory.InSlot("weapon")!.Refine.ShouldBe(3);
        loaded.Inventory.Currency.ShouldBe(4321);
        loaded.Inventory.SettledTurnIns.ShouldBe([4, 7], ignoreOrder: true);
        loaded.Inventory.Weight.ShouldBeGreaterThan(0);
        loaded.SkillCooldowns["skill.twin_strike"].ShouldBe(9_000);
        loaded.ItemCooldowns["item.healing_draught"].ShouldBe(2_500);
        loaded.AutopilotUsedMs.ShouldBe(123_456);
        loaded.AutopilotDayAgeMs.ShouldBe(3L * 3_600_000);
        loaded.AsideItems.Single().Entry.ItemId.ShouldBe("item.long_gone");
        loaded.Aside.Select(a => a.Id).ShouldBe(["job.long_gone", "quest.long_gone"], ignoreOrder: true);
    }

    [Fact]
    public void An_item_that_is_gone_is_set_aside_and_comes_back_with_the_item()
    {
        var saved = Full();
        var without = SavedContent.Without(["item.healing_draught", "item.bone_dirk"]);
        var loaded = Load(saved, without);
        loaded.Inventory.Entries.Select(e => e.ItemId).ShouldBe(["item.padded_vest", "item.gel_glob"]);
        loaded.AsideItems.Select(a => a.Entry.ItemId).ShouldBe(["item.bone_dirk", "item.healing_draught", "item.long_gone"]);
        loaded.AsideItems[0].Entry.Refine.ShouldBe(3, "kept whole");
        loaded.Notes.Count(n => n.Contains("set aside")).ShouldBe(2, "the one set aside before says nothing again");

        var later = Load(saved with { Items = loaded.Inventory.Entries.ToList(), AsideItems = loaded.AsideItems });
        later.AsideItems.Single().Entry.ItemId.ShouldBe("item.long_gone");
        later.Inventory.Entries.Select(e => e.Id).ShouldBe(saved.Items.Select(i => i.Id), "the same entries with the same ids");
        later.Inventory.InSlot("weapon").ShouldBeNull("it comes back to the bag");
        later.Notes.Count(n => n.Contains("is back")).ShouldBe(2);
    }

    [Fact]
    public void A_card_that_is_gone_stays_in_its_socket()
    {
        var loaded = Load(Full(), SavedContent.Without(["item.gelcap_card"]));
        loaded.Inventory.Entries.Single(e => e.ItemId == "item.padded_vest").Cards.ShouldBe(["item.gelcap_card"]);
        loaded.Notes.Single().ShouldContain("item.gelcap_card");
    }

    [Fact]
    public void A_smaller_stack_splits_and_keeps_every_unit()
    {
        var content = SavedContent.Without([], items: i => i.Id == "item.healing_draught" ? i with { MaxStack = 3 } : i);
        var loaded = Load(Full(), content);
        var stacks = loaded.Inventory.Entries.Where(e => e.ItemId == "item.healing_draught").ToList();
        stacks.Select(s => s.Quantity).ShouldBe([3, 3, 1]);
        stacks.Select(s => s.Id).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void Something_worn_that_may_no_longer_be_worn_goes_to_the_bag()
    {
        var content = SavedContent.Without([], items: i => i.Id == "item.bone_dirk" ? i with { Equip = i.Equip! with { MinBaseLevel = 99 } } : i);
        var loaded = Load(Full(), content);
        loaded.Inventory.InSlot("weapon").ShouldBeNull();
        loaded.Inventory.Entries.Count.ShouldBe(4);
        loaded.Notes.Single().ShouldContain("in the bag");
        var elsewhere = SavedContent.Without([], items: i => i.Id == "item.bone_dirk" ? i with { Equip = i.Equip! with { Slots = ["shield"] } } : i);
        Load(Full(), elsewhere).Inventory.InSlot("weapon").ShouldBeNull("another slot now: it is not moved there by itself");
        Load(Full(), elsewhere).Inventory.Entries.Count.ShouldBe(4);
    }

    [Fact]
    public void Two_things_that_no_longer_go_together_leave_one_worn()
    {
        var saved = Full();
        saved = saved with { Items = [.. saved.Items, new InventoryEntry(NewId(), "item.wooden_buckler", 1, "shield")] };
        Load(saved).Inventory.InSlot("shield").ShouldNotBeNull();
        var content = SavedContent.Without([], items: i => i.Id == "item.bone_dirk" ? i with { Equip = i.Equip! with { WeaponType = "sword_2h" } } : i);
        var loaded = Load(saved, content);
        loaded.Inventory.InSlot("weapon").ShouldNotBeNull();
        loaded.Inventory.InSlot("shield").ShouldBeNull();
        loaded.Inventory.Entries.Count.ShouldBe(5);
    }

    [Fact]
    public void A_skill_that_is_gone_or_shorter_gives_its_points_back()
    {
        var loaded = Load(Full(), SavedContent.Without(["skill.twin_strike"]));
        loaded.Progression.Skills.ShouldBeEmpty();
        loaded.Progression.SkillPoints.ShouldBe(4);
        loaded.Aside.ShouldContain(a => a.Kind == SetAsideKinds.Skill && a.Id == "skill.twin_strike" && a.Data[0] == 2);
        loaded.SkillCooldowns.ShouldBeEmpty();
        loaded.Notes.Single().ShouldContain("2 skill point(s) given back");

        var shorter = Load(Full(), SavedContent.Without([], skills: s => s.Id == "skill.twin_strike" ? s with { MaxLevel = 1 } : s));
        shorter.Progression.Skills["skill.twin_strike"].ShouldBe(1);
        shorter.Progression.SkillPoints.ShouldBe(3);
    }

    [Fact]
    public void A_job_that_is_gone_leaves_the_start_job_and_a_record()
    {
        var loaded = Load(Full(), SavedContent.Without(["job.warden"]));
        var p = loaded.Progression;
        (p.JobId, p.JobLevel, p.JobExp, p.BaseLevel, p.SkillPoints).ShouldBe(("job.wanderer", 1, 0L, 14, 2));
        p.Skills["skill.twin_strike"].ShouldBe(2, "every skill is kept");
        p.UnlockedJobs.ShouldBeEmpty();
        loaded.Aside.ShouldContain(a => a.Kind == SetAsideKinds.Job && a.Id == "job.warden" && a.Data.SequenceEqual(new long[] { 6, 345 }));
        loaded.Aside.ShouldContain(a => a.Kind == SetAsideKinds.UnlockedJob && a.Id == "job.warden");
        var back = Load(Full() with { JobId = "job.wanderer", UnlockedJobs = [], Aside = loaded.Aside });
        back.Progression.UnlockedJobs.ShouldBe(["job.warden"], "an unlock comes back with its job");
        back.Progression.JobId.ShouldBe("job.wanderer", "the job itself does not: it may have changed since");
    }

    [Fact]
    public void A_quest_that_is_gone_is_set_aside_and_comes_back_where_it_was()
    {
        var saved = Full();
        var without = SavedContent.Without(["quest.first_hunt", "quest.warden_trial"]);
        var loaded = Load(saved, without);
        loaded.Progression.ActiveQuests.ShouldBeEmpty();
        loaded.Progression.CompletedQuests.ShouldBeEmpty();
        loaded.Aside.ShouldContain(a => a.Kind == SetAsideKinds.ActiveQuest && a.Id == "quest.first_hunt");
        var back = Load(saved with { ActiveQuests = [], CompletedQuests = [], Aside = loaded.Aside });
        back.Progression.CompletedQuests.ShouldBe(["quest.warden_trial"]);
        var quest = back.Progression.ActiveQuests["quest.first_hunt"];
        (quest.Step, quest.Token).ShouldBe((0, 4L));
        quest.Counters.ShouldBe([2, 3, 0]);
        back.Aside.ShouldNotContain(a => a.Id == "quest.first_hunt");
        back.Aside.ShouldContain(a => a.Id == "quest.long_gone");
    }

    [Fact]
    public void A_quest_that_has_changed_starts_its_step_over()
    {
        static QuestDef Shorter(QuestDef q) => q.Id == "quest.first_hunt" ? q with { Steps = [q.Steps[0] with { Objectives = [q.Steps[0].Objectives[0]] }] } : q;
        var loaded = Load(Full(), SavedContent.Without([], quests: Shorter));
        loaded.Progression.ActiveQuests["quest.first_hunt"].Counters.ShouldBe([0]);
        loaded.Progression.ActiveQuests["quest.first_hunt"].Token.ShouldBe(4, "what its turn-in has paid stays paid");
        loaded.Notes.Single().ShouldContain("starts over");

        var late = Full() with { ActiveQuests = [new SavedQuest("quest.first_hunt", 7, [1], 0)] };
        Load(late).Progression.ActiveQuests["quest.first_hunt"].Step.ShouldBe(0);
        var more = Full() with { ActiveQuests = [new SavedQuest("quest.first_hunt", 0, [99, 0, 0], 0)] };
        Load(more).Progression.ActiveQuests["quest.first_hunt"].Counters.ShouldBe([0, 0, 0]);
    }

    [Fact]
    public void A_map_that_is_gone_moves_to_the_save_point_or_the_start()
    {
        var saved = Full() with { MapId = "map.long_gone" };
        var loaded = Load(saved);
        (loaded.MapId, loaded.Cell).ShouldBe(("map.starter_meadow", new GridCell(12, 12)));
        loaded.Notes.Single().ShouldContain("moved to");
        var start = TestGrids.SampleContent.Balance.NewCharacter;
        var nowhere = Load(saved with { SavePoint = new SavePoint("map.long_gone", new GridCell(1, 1)) });
        (nowhere.MapId, nowhere.Cell).ShouldBe((start.StartMapId, new GridCell(start.StartX, start.StartY)));
        nowhere.SavePoint.ShouldBeNull();
        nowhere.Notes.Count.ShouldBe(2);
    }

    [Fact]
    public void A_cell_that_is_blocked_now_moves_to_the_maps_respawn_point()
    {
        var grid = TestGrids.SampleContent.GetGrid("map.starter_meadow");
        var wall = Enumerable.Range(0, grid.Width * grid.Height).Select(i => new GridCell(i % grid.Width, i / grid.Width)).First(c => !grid.IsWalkable(c.X, c.Y));
        foreach (var cell in new[] { wall, new GridCell(-5, 3), new GridCell(9_999, 0) })
        {
            var loaded = Load(Full() with { Cell = cell });
            loaded.MapId.ShouldBe("map.starter_meadow");
            grid.IsWalkable(loaded.Cell.X, loaded.Cell.Y).ShouldBeTrue();
            loaded.Notes.Single().ShouldContain("cannot be stood on");
        }
    }

    [Fact]
    public void A_dead_character_comes_back_as_if_it_had_respawned()
    {
        var loaded = Load(Full() with { Hp = 0 });
        (loaded.MapId, loaded.Cell).ShouldBe(("map.starter_meadow", new GridCell(12, 12)));
        loaded.HpPercent.ShouldBe(TestGrids.SampleContent.Balance.Death.RespawnHpPercent);
        loaded.Notes.ShouldBeEmpty();
        var hollow = Load(Full() with { Hp = 0, SavePoint = null, MapId = "map.thistle_hollow", Cell = new GridCell(0, 0) });
        var expected = CombatContent.RespawnPoints(TestGrids.SampleContent.Maps.All, TestGrids.SampleContent.Grids)["map.thistle_hollow"];
        (hollow.MapId, hollow.Cell).ShouldBe((expected.MapId, expected.Cell));
    }

    [Fact]
    public void Time_away_counts_for_cooldowns_and_the_autopilots_day()
    {
        var loaded = Load(Full(), away: TimeSpan.FromSeconds(4));
        loaded.SkillCooldowns["skill.twin_strike"].ShouldBe(5_000);
        loaded.ItemCooldowns.ShouldBeEmpty();
        loaded.AutopilotDayAgeMs.ShouldBe(3L * 3_600_000 + 4_000);
        var back = Load(Full(), away: TimeSpan.FromHours(-30));   // a clock set back
        back.SkillCooldowns["skill.twin_strike"].ShouldBe(9_000, "never longer than it was");
        back.AutopilotDayAgeMs.ShouldBe(0);
    }

    [Fact]
    public void A_state_from_nowhere_loads_without_throwing()
    {
        var state = new CharacterState
        {
            TakenAt = At, MapId = "", Cell = new GridCell(int.MaxValue, int.MinValue), Facing = "sideways", Hp = int.MaxValue, Sp = -5, BaseLevel = 9_999, JobId = "", JobLevel = 9_999,
            Skills = new Dictionary<string, int> { ["skill.x"] = 3, ["skill.twin_strike"] = -2 }, Stats = new Dictionary<string, int> { ["nonsense"] = 4 }, Currency = -1,
            ActiveQuests = [new SavedQuest("", -3, [], -1), new SavedQuest("quest.gear_up", -3, [-1], 0)],
            Items = [new InventoryEntry(1, "item.bone_dirk", 3, "nowhere"), new InventoryEntry(2, "", 1)],
            Aside = [new SetAside("unheard_of", "x", [], "?"), new SetAside(SetAsideKinds.ActiveQuest, "quest.gear_up", [], "?")],
        };
        var loaded = Load(state);
        loaded.Facing.ShouldBe("s");
        loaded.Inventory.Currency.ShouldBe(0);
        loaded.Inventory.Entries.Count(e => e.ItemId == "item.bone_dirk").ShouldBe(3, "three single pieces");
        loaded.Progression.ActiveQuests["quest.gear_up"].Step.ShouldBe(0);
        loaded.Progression.SkillPoints.ShouldBe(3);
    }
}
