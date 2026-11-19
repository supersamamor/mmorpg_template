using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Quests;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Quests;

/// <summary>The pure quest state machine (T-602): starts and refusals, counting per objective type on its own step only, step order, turn-in, rewards and markers.</summary>
public class QuestRulesTests
{
    private sealed class Player : IQuestPlayer
    {
        public Dictionary<string, int> Bag { get; } = new();
        public HashSet<string> Worn { get; } = new();
        public string MapId { get; set; } = "map.a";
        public GridCell Cell { get; set; } = new(0, 0);
        public int ItemCount(string itemId) => Bag.GetValueOrDefault(itemId);
        public bool IsWearing(string itemId) => Worn.Contains(itemId);
    }

    private static QuestStepDef Step(params QuestObjectiveDef[] objectives) => new() { Objectives = objectives };

    private static readonly QuestDef Hunt = new()
    {
        Id = "quest.hunt", Name = "Hunt", GiverNpcId = "npc.giver",
        Steps =
        [
            Step(new KillObjective { MonsterId = "monster.blob", Count = 2 }, new CollectObjective { ItemId = "item.glob", Count = 3, Consume = true }, new TalkObjective { NpcId = "npc.giver" }),
        ],
        Rewards =
        [
            new ExpReward { Base = 40, Job = 25 }, new ItemReward { ItemId = "item.potion", Count = 5 }, new CurrencyReward { Amount = 150 },
            new ItemReward { ItemId = "item.potion", Count = 1 }, new SkillPointReward { Amount = 1 }, new StatPointReward { Amount = 3 },
            new UnlockJobReward { JobId = "job.knight" }, new StatusReward { StatusId = "status.swift", DurationMs = 1000 },
        ],
    };

    // Every objective type, one step each, in this order: kill, use, equip, reach, level, job, collect, talk.
    private static readonly QuestDef Steps = new()
    {
        Id = "quest.steps", Name = "Steps", GiverNpcId = "npc.giver",
        Steps =
        [
            Step(new KillObjective { MonsterId = "monster.blob", Count = 1 }),
            Step(new UseItemObjective { ItemId = "item.potion", Count = 2 }),
            Step(new EquipObjective { ItemId = "item.cap" }),
            Step(new ReachObjective { MapId = "map.a", X = 10, Y = 10, Radius = 2 }),
            Step(new LevelObjective { BaseLevel = 5 }),
            Step(new JobObjective { JobId = "job.knight" }),
            Step(new CollectObjective { ItemId = "item.glob", Count = 2 }),
            Step(new TalkObjective { NpcId = "npc.other" }),
        ],
    };

    private static readonly QuestDef Gated = new()
    {
        Id = "quest.gated", Name = "Gated", GiverNpcId = "npc.other",
        Prerequisites = new QuestPrerequisitesDef { MinBaseLevel = 5, JobIds = ["job.wanderer"], QuestIds = ["quest.hunt"] },
        Steps = [Step(new TalkObjective { NpcId = "npc.other" })],
    };

    private static readonly QuestDef Daily = new()
    {
        Id = "quest.daily", Name = "Daily", GiverNpcId = "npc.giver", Repeatable = true,
        Steps = [Step(new UseItemObjective { ItemId = "item.potion" })],
    };

    private static QuestRules Rules(int maxActive = 10) => new([Hunt, Steps, Gated, Daily], maxActive);

    private static ProgressionState State(int level = 1, string job = "job.wanderer") => new() { BaseLevel = level, JobId = job };

    // ---- starting -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_start_refusal_has_its_reason_in_order()
    {
        var rules = Rules();
        var state = State();
        rules.CheckStart(state, "quest.nope").ShouldBe(QuestReasons.UnknownQuest);
        rules.CheckStart(state, "quest.gated").ShouldBe(QuestReasons.BaseLevelTooLow);
        state.BaseLevel = 5;
        state.JobId = "job.knight";
        rules.CheckStart(state, "quest.gated").ShouldBe(QuestReasons.WrongJob);
        state.JobId = "job.wanderer";
        rules.CheckStart(state, "quest.gated").ShouldBe(QuestReasons.QuestNotCompleted);
        state.CompletedQuests.Add("quest.hunt");
        rules.CheckStart(state, "quest.gated").ShouldBeNull();

        rules.CheckStart(state, "quest.hunt").ShouldBe(QuestReasons.AlreadyCompleted);   // not repeatable
        rules.Start(state, "quest.gated", new Player());
        rules.CheckStart(state, "quest.gated").ShouldBe(QuestReasons.AlreadyActive);
        Should.Throw<InvalidOperationException>(() => rules.Start(state, "quest.gated", new Player()));
    }

    [Fact]
    public void Max_active_refuses_one_more_start_until_one_is_dropped()
    {
        var rules = Rules(maxActive: 2);
        var state = State();
        rules.Start(state, "quest.hunt", new Player());
        rules.Start(state, "quest.steps", new Player());

        rules.CheckStart(state, "quest.daily").ShouldBe(QuestReasons.TooManyActive);
        rules.Standing(state, "quest.daily").Available.ShouldBeFalse();

        rules.Abandon(state, "quest.steps").ShouldBeTrue();
        rules.CheckStart(state, "quest.daily").ShouldBeNull();
    }

    [Fact]
    public void Abandoning_drops_progress_gives_nothing_back_and_a_second_abandon_is_refused()
    {
        var rules = Rules();
        var state = State();
        var progress = rules.Start(state, "quest.hunt", new Player());
        rules.OnKill(progress, "monster.blob").ShouldBeTrue();

        rules.Abandon(state, "quest.hunt").ShouldBeTrue();

        state.ActiveQuests.ShouldBeEmpty();
        state.CompletedQuests.ShouldBeEmpty();
        rules.Abandon(state, "quest.hunt").ShouldBeFalse();
        var again = rules.Start(state, "quest.hunt", new Player());   // starts over from nothing
        again.Counters.ShouldBe([0, 0, 0]);
    }

    [Fact]
    public void A_repeatable_quest_starts_again_after_completion_and_stays_completed()
    {
        var rules = Rules();
        var state = State();
        var player = new Player();
        var progress = rules.Start(state, "quest.daily", player);
        rules.OnUse(progress, "item.potion").ShouldBeTrue();
        rules.StepMet(progress).ShouldBeTrue();
        rules.Advance(state, progress, player).ShouldBeTrue();

        state.CompletedQuests.ShouldContain("quest.daily");
        rules.Standing(state, "quest.daily").State.ShouldBe(QuestStates.Completed);
        rules.Standing(state, "quest.daily").Available.ShouldBeTrue();
        rules.Start(state, "quest.daily", player);
        rules.Standing(state, "quest.daily").State.ShouldBe(QuestStates.Active);
    }

    // ---- counting --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Each_objective_type_counts_only_on_its_own_step_in_step_order()
    {
        var rules = Rules();
        var state = State();
        var player = new Player();
        var p = rules.Start(state, "quest.steps", player);

        // Step 1, kill: events of later steps do nothing now.
        rules.OnUse(p, "item.potion").ShouldBeFalse();
        rules.OnEquip(p, "item.cap").ShouldBeFalse();
        rules.OnTalk(p, "npc.other").ShouldBeFalse();
        rules.OnKill(p, "monster.other").ShouldBeFalse();
        rules.OnKill(p, "monster.blob").ShouldBeTrue();
        rules.OnKill(p, "monster.blob").ShouldBeFalse();   // never above its need
        rules.StepMet(p).ShouldBeTrue();
        rules.Advance(state, p, player).ShouldBeFalse();
        p.Step.ShouldBe(1);

        // Step 2, use_item x2: the kill no longer counts.
        rules.OnKill(p, "monster.blob").ShouldBeFalse();
        rules.OnUse(p, "item.potion").ShouldBeTrue();
        rules.StepMet(p).ShouldBeFalse();
        rules.OnUse(p, "item.potion").ShouldBeTrue();
        rules.Advance(state, p, player);

        // Step 3, equip: putting it on latches; taking it off later does not undo it.
        rules.Entry(p).Objectives.Single().ShouldBe(new Game.Shared.Protocol.QuestObjectiveRecord(QuestObjectiveTypes.Equip, "item.cap", 0, 1));
        rules.OnEquip(p, "item.cap").ShouldBeTrue();
        rules.Advance(state, p, player);

        // Step 4, reach: within the radius (Chebyshev) on the right map.
        player.Cell = new GridCell(13, 10);
        rules.Refresh(p, state, player).ShouldBeFalse();
        player.MapId = "map.b";
        player.Cell = new GridCell(10, 10);
        rules.Refresh(p, state, player).ShouldBeFalse();
        player.MapId = "map.a";
        player.Cell = new GridCell(12, 8);
        rules.Refresh(p, state, player).ShouldBeTrue();
        player.Cell = new GridCell(30, 30);
        rules.Refresh(p, state, player).ShouldBeFalse();   // latched
        rules.StepMet(p).ShouldBeTrue();
        rules.Advance(state, p, player);

        // Step 5, level: the base level counts up to its need.
        state.BaseLevel = 3;
        rules.Refresh(p, state, player).ShouldBeTrue();
        rules.Entry(p).Objectives.Single().Have.ShouldBe(3);
        state.BaseLevel = 9;
        rules.Refresh(p, state, player);
        rules.Entry(p).Objectives.Single().Have.ShouldBe(5);
        rules.Advance(state, p, player);

        // Step 6, job: the current job.
        rules.StepMet(p).ShouldBeFalse();
        state.JobId = "job.knight";
        rules.Refresh(p, state, player).ShouldBeTrue();
        rules.Advance(state, p, player);

        // Step 7, collect: the bag's quantity, live.
        player.Bag["item.glob"] = 1;
        rules.Refresh(p, state, player).ShouldBeTrue();
        rules.StepMet(p).ShouldBeFalse();
        player.Bag["item.glob"] = 5;
        rules.Refresh(p, state, player);
        rules.Entry(p).Objectives.Single().Have.ShouldBe(2);
        player.Bag["item.glob"] = 0;
        rules.Refresh(p, state, player);
        rules.StepMet(p).ShouldBeFalse();
        player.Bag["item.glob"] = 2;
        rules.Refresh(p, state, player);
        rules.Consumed(p).ShouldBeEmpty();   // consume is false here
        rules.Advance(state, p, player);

        // Step 8, talk: with the NPC only.
        rules.OnTalk(p, "npc.giver").ShouldBeFalse();
        rules.OnTalk(p, "npc.other").ShouldBeTrue();
        rules.IsLastStep(p).ShouldBeTrue();
        rules.Advance(state, p, player).ShouldBeTrue();
        state.CompletedQuests.ShouldContain("quest.steps");
        state.ActiveQuests.ShouldNotContainKey("quest.steps");
    }

    [Fact]
    public void Level_job_equip_and_reach_already_true_are_counted_when_their_step_starts()
    {
        var quest = new QuestDef
        {
            Id = "quest.already", Name = "Already",
            Steps = [Step(new LevelObjective { BaseLevel = 3 }, new JobObjective { JobId = "job.wanderer" }, new EquipObjective { ItemId = "item.cap" }, new ReachObjective { MapId = "map.a", X = 0, Y = 0 })],
        };
        var rules = new QuestRules([quest], 10);
        var player = new Player { Worn = { "item.cap" } };

        var progress = rules.Start(State(level: 4), "quest.already", player);

        rules.StepMet(progress).ShouldBeTrue();
    }

    [Fact]
    public void A_talk_counts_only_when_every_other_objective_of_its_step_is_met()
    {
        var rules = Rules();
        var state = State();
        var player = new Player();
        var p = rules.Start(state, "quest.hunt", player);
        var offer = new SortedSet<string>(StringComparer.Ordinal);
        var turnIn = new SortedSet<string>(StringComparer.Ordinal);

        rules.OnTalk(p, "npc.giver").ShouldBeFalse();
        rules.OnKill(p, "monster.blob");
        rules.OnKill(p, "monster.blob");
        rules.Markers(state, offer, turnIn);
        turnIn.ShouldBeEmpty();   // globs still missing
        rules.OnTalk(p, "npc.giver").ShouldBeFalse();

        player.Bag["item.glob"] = 3;
        rules.Refresh(p, state, player);
        rules.Markers(state, offer, turnIn);
        turnIn.ShouldBe(["npc.giver"]);
        rules.OnTalk(p, "npc.giver").ShouldBeTrue();
        rules.StepMet(p).ShouldBeTrue();
        rules.Consumed(p).ShouldBe([new KeyValuePair<string, int>("item.glob", 3)]);
    }

    // ---- rewards and markers --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Rewards_are_split_by_who_pays_them_and_item_rewards_merge()
    {
        var split = Rules().Rewards("quest.hunt");

        split.Items.ShouldBe([new KeyValuePair<string, int>("item.potion", 6)]);
        split.Currency.ShouldBe(150);
        (split.BaseExp, split.JobExp, split.SkillPoints, split.StatPoints).ShouldBe((40L, 25L, 1, 3));
        split.UnlockJobs.ShouldBe(["job.knight"]);
        split.StatusRewards.ShouldBe([7]);
        QuestRules.RewardKey("quest.hunt", 7).ShouldBe("quest.hunt#reward#7");
    }

    [Fact]
    public void Offer_markers_follow_availability()
    {
        var rules = Rules();
        var state = State(level: 5);
        var offer = new SortedSet<string>(StringComparer.Ordinal);
        var turnIn = new SortedSet<string>(StringComparer.Ordinal);

        rules.Markers(state, offer, turnIn);
        offer.ShouldBe(["npc.giver"]);   // the gated quest needs the hunt first

        state.CompletedQuests.Add("quest.hunt");
        rules.Markers(state, offer, turnIn);
        offer.ShouldBe(["npc.giver", "npc.other"]);
    }

    [Fact]
    public void Standing_reports_state_step_and_availability()
    {
        var rules = Rules();
        var state = State();
        rules.Standing(state, "quest.hunt").ShouldBe(new QuestStanding(QuestStates.NotStarted, 0, true));
        var p = rules.Start(state, "quest.steps", new Player());
        rules.Standing(state, "quest.steps").ShouldBe(new QuestStanding(QuestStates.Active, 1, false));
        rules.OnKill(p, "monster.blob");
        rules.Advance(state, p, new Player());
        rules.Standing(state, "quest.steps").Step.ShouldBe(2);
        QuestRules.IsInState(rules.Standing(state, "quest.hunt"), QuestStates.Available).ShouldBeTrue();
        QuestRules.IsInState(rules.Standing(state, "quest.steps"), QuestStates.Active).ShouldBeTrue();
    }
}
