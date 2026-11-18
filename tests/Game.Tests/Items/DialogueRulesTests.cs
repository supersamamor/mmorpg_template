using Game.Shared.Definitions;
using Game.Shared.Dialogue;
using Game.Shared.Items;
using Game.Shared.Vocabulary;

namespace Game.Tests.Items;

/// <summary>The pure dialogue rules (T-601): conditions over a player, and the bag's all-or-nothing exchange.</summary>
public class DialogueRulesTests
{
    private sealed class Player : IDialoguePlayer
    {
        public int BaseLevel { get; init; } = 1;
        public int JobLevel { get; init; } = 1;
        public string JobId { get; init; } = "job.wanderer";
        public Dictionary<string, int> Items { get; init; } = new();
        public HashSet<string> Statuses { get; init; } = new();

        public int ItemCount(string itemId) => Items.GetValueOrDefault(itemId);

        public bool HasStatus(string statusId) => Statuses.Contains(statusId);
    }

    [Theory]
    [InlineData(1, 5, null, false)]
    [InlineData(5, 5, null, true)]
    [InlineData(9, 5, 9, true)]
    [InlineData(10, 5, 9, false)]
    [InlineData(3, null, 3, true)]
    [InlineData(4, null, 3, false)]
    public void A_base_level_condition_checks_both_bounds_and_either_may_be_left_out(int level, int? min, int? max, bool expected) =>
        DialogueRules.Matches(new BaseLevelCondition { Min = min, Max = max }, new Player { BaseLevel = level }).ShouldBe(expected);

    [Fact]
    public void A_job_level_condition_reads_the_job_level_not_the_base_level()
    {
        var player = new Player { BaseLevel = 50, JobLevel = 2 };

        DialogueRules.Matches(new JobLevelCondition { Min = 3 }, player).ShouldBeFalse();
        DialogueRules.Matches(new JobLevelCondition { Min = 2, Max = 2 }, player).ShouldBeTrue();
    }

    [Fact]
    public void Job_is_and_job_is_not_are_opposites()
    {
        var player = new Player { JobId = "job.warden" };

        DialogueRules.Matches(new JobIsCondition { JobId = "job.warden" }, player).ShouldBeTrue();
        DialogueRules.Matches(new JobIsCondition { JobId = "job.wanderer" }, player).ShouldBeFalse();
        DialogueRules.Matches(new JobIsNotCondition { JobId = "job.warden" }, player).ShouldBeFalse();
        DialogueRules.Matches(new JobIsNotCondition { JobId = "job.wanderer" }, player).ShouldBeTrue();
    }

    [Fact]
    public void Has_item_needs_the_quantity_and_has_status_needs_the_status()
    {
        var player = new Player { Items = { ["item.gel_glob"] = 2 }, Statuses = { "status.swift" } };

        DialogueRules.Matches(new HasItemCondition { ItemId = "item.gel_glob" }, player).ShouldBeTrue();
        DialogueRules.Matches(new HasItemCondition { ItemId = "item.gel_glob", Quantity = 2 }, player).ShouldBeTrue();
        DialogueRules.Matches(new HasItemCondition { ItemId = "item.gel_glob", Quantity = 3 }, player).ShouldBeFalse();
        DialogueRules.Matches(new HasItemCondition { ItemId = "item.other" }, player).ShouldBeFalse();
        DialogueRules.Matches(new HasStatusCondition { StatusId = "status.swift" }, player).ShouldBeTrue();
        DialogueRules.Matches(new HasStatusCondition { StatusId = "status.stun" }, player).ShouldBeFalse();
    }

    [Fact]
    public void Every_condition_must_hold_and_none_means_always()
    {
        var player = new Player { BaseLevel = 4 };

        DialogueRules.Matches([], player).ShouldBeTrue();
        DialogueRules.Matches([new BaseLevelCondition { Min = 2 }, new JobIsCondition { JobId = "job.wanderer" }], player).ShouldBeTrue();
        DialogueRules.Matches([new BaseLevelCondition { Min = 2 }, new JobIsCondition { JobId = "job.warden" }], player).ShouldBeFalse();
    }

    [Fact]
    public void A_condition_type_this_build_does_not_know_hides_the_choice()
    {
        DialogueRules.Matches(new UnknownCondition(), new Player()).ShouldBeFalse();
    }

    private sealed record UnknownCondition : DialogueConditionDef
    {
        public override string Type => "quest_done";
    }

    // ---- the exchange ------------------------------------------------------------------------------------------------------------------

    private readonly InventoryRules _rules = new(new ItemLookup(InventoryRulesTests.Items()), InventoryRulesTests.Balance(maxEntries: 2));
    private long _ids;

    private long NewId() => ++_ids;

    private InventoryState Holding(params (string Item, int Quantity)[] items)
    {
        var state = new InventoryState();
        foreach (var (item, quantity) in items) _rules.TryAdd(state, item, quantity, int.MaxValue, NewId, new InventoryDelta()).ShouldBeNull();
        return state;
    }

    private static KeyValuePair<string, int>[] Of(params (string Item, int Quantity)[] items) => items.Select(i => KeyValuePair.Create(i.Item, i.Quantity)).ToArray();

    private static string Describe(InventoryState state) =>
        string.Join(",", state.Entries.Select(e => e.Id + ":" + e.ItemId + "x" + e.Quantity + (e.Slot is null ? "" : "@" + e.Slot))) + " w" + state.Weight;

    [Fact]
    public void An_exchange_takes_first_then_gives_so_a_swap_fits_a_full_bag()
    {
        var state = Holding(("item.gel_glob", 1), ("item.healing_draught", 1));   // two entries: full
        var delta = new InventoryDelta();

        _rules.TryExchange(state, Of(("item.gel_glob", 1)), Of(("item.leather_cap", 1)), int.MaxValue, NewId, delta).ShouldBeNull();

        state.Entries.Select(e => e.ItemId).ShouldBe(["item.healing_draught", "item.leather_cap"], ignoreOrder: true);
        delta.Removed.Count.ShouldBe(1);
        delta.Upserted.Count.ShouldBe(1);
    }

    [Fact]
    public void A_missing_take_changes_nothing()
    {
        var state = Holding(("item.gel_glob", 1));
        var before = Describe(state);
        var delta = new InventoryDelta();

        _rules.TryExchange(state, Of(("item.gel_glob", 2)), Of(), int.MaxValue, NewId, delta).ShouldBe(DialogueReasons.MissingItem);

        Describe(state).ShouldBe(before);
        delta.Upserted.ShouldBeEmpty();
        delta.Removed.ShouldBeEmpty();
    }

    [Fact]
    public void A_failed_give_puts_back_what_the_takes_removed()
    {
        var state = Holding(("item.gel_glob", 3), ("item.healing_draught", 2));   // full: two entries
        var before = Describe(state);
        var delta = new InventoryDelta();

        // Taking every glob frees an entry, but two new equipment entries need two: the second give fails and the whole exchange is undone.
        _rules.TryExchange(state, Of(("item.gel_glob", 3)), Of(("item.leather_cap", 1), ("item.wooden_buckler", 1)), int.MaxValue, NewId, delta)
            .ShouldBe(DialogueReasons.InventoryFull);

        Describe(state).ShouldBe(before);
        delta.Upserted.ShouldBeEmpty();
        delta.Removed.ShouldBeEmpty();
    }

    [Fact]
    public void A_give_past_the_weight_limit_is_too_heavy_and_changes_nothing()
    {
        var state = Holding(("item.gel_glob", 1));
        var before = Describe(state);

        _rules.TryExchange(state, Of(), Of(("item.gel_glob", 50)), 5, NewId, new InventoryDelta()).ShouldBe(DialogueReasons.TooHeavy);

        Describe(state).ShouldBe(before);
    }

    [Fact]
    public void A_take_spans_stacks_lowest_id_first()
    {
        var state = Holding(("item.healing_draught", 99), ("item.healing_draught", 5));
        state.Entries.Count.ShouldBe(2);

        _rules.TryExchange(state, Of(("item.healing_draught", 100)), Of(), int.MaxValue, NewId, new InventoryDelta()).ShouldBeNull();

        state.Entries.Single().Quantity.ShouldBe(4);
    }
}
