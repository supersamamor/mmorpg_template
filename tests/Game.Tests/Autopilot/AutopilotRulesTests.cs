using Game.Shared.Autopilot;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Autopilot;

/// <summary>What the autopilot decides from data alone (T-604): the next objective, how a skill may be used, and which dialogue choice is safe.</summary>
public class AutopilotRulesTests
{
    // ---- objectives -------------------------------------------------------------------------------------------------------------------

    private static readonly QuestObjectiveDef Kill = new KillObjective { MonsterId = "monster.a", Count = 2 };
    private static readonly QuestObjectiveDef Collect = new CollectObjective { ItemId = "item.a", Count = 3 };
    private static readonly QuestObjectiveDef Reach = new ReachObjective { MapId = "map.a" };
    private static readonly QuestObjectiveDef Talk = new TalkObjective { NpcId = "npc.a" };
    private static readonly QuestObjectiveDef Level = new LevelObjective { BaseLevel = 10 };

    [Fact]
    public void The_next_objective_is_the_first_playable_one_then_the_players_own_then_the_talk()
    {
        QuestObjectiveDef[] step = [Talk, Level, Kill, Collect];
        AutopilotRules.NextObjective(step, [0, 5, 0, 0]).ShouldBe(2);
        AutopilotRules.NextObjective(step, [0, 5, 2, 0]).ShouldBe(3);
        AutopilotRules.NextObjective(step, [0, 5, 2, 3]).ShouldBe(1);
        AutopilotRules.NextObjective(step, [0, 10, 2, 3]).ShouldBe(0);
        AutopilotRules.NextObjective(step, [1, 10, 2, 3]).ShouldBe(-1);
        AutopilotRules.NextObjective([Reach, Kill], [0, 0]).ShouldBe(0);
    }

    [Fact]
    public void Every_objective_type_is_either_played_or_pauses()
    {
        foreach (var type in Discriminators.QuestObjectives)
        {
            var objective = (QuestObjectiveDef)Activator.CreateInstance(type.Value)!;
            var pause = AutopilotRules.PauseFor(objective);
            if (objective is KillObjective or CollectObjective or ReachObjective or TalkObjective) pause.ShouldBeNull(type.Key);
            else AutopilotPauses.All.Contains(pause!).ShouldBeTrue(type.Key);
        }
    }

    // ---- skills ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("skill.ember_bolt", AutopilotSkillUse.OnTarget)]
    [InlineData("skill.twin_strike", AutopilotSkillUse.OnTarget)]
    [InlineData("skill.shockwave", AutopilotSkillUse.AroundSelf)]
    [InlineData("skill.second_wind", AutopilotSkillUse.OnSelf)]
    [InlineData("skill.iron_guard", AutopilotSkillUse.OnSelf)]
    [InlineData("skill.cleanse", AutopilotSkillUse.OnSelf)]
    [InlineData("skill.mana_font", AutopilotSkillUse.Never)]       // a friendly ground skill
    [InlineData("skill.call_seedling", AutopilotSkillUse.Never)]   // a summon
    [InlineData("skill.return_home", AutopilotSkillUse.Never)]     // a teleport
    [InlineData("skill.tough_body", AutopilotSkillUse.Never)]      // passive
    public void The_sample_skills(string id, AutopilotSkillUse use) => new AutopilotSkill(TestGrids.SampleContent.Skills.Get(id)).Use.ShouldBe(use);

    [Fact]
    public void A_ground_skill_is_cast_only_when_every_effect_is_a_hostile_area_that_damages()
    {
        static SkillDef Ground(params EffectDef[] effects) => new() { Id = "skill.x", Target = new SkillTargetDef { Range = 5, Ground = true }, Effects = effects };
        var blast = new AreaEffect { Radius = 1, Effects = [new DamageEffect()] };
        new AutopilotSkill(Ground(blast)).Use.ShouldBe(AutopilotSkillUse.OnTargetCell);
        new AutopilotSkill(Ground(new AreaEffect { Radius = 1, Effects = [new DamageEffect(), new ApplyStatusEffect { StatusId = "status.stun" }] })).Use.ShouldBe(AutopilotSkillUse.OnTargetCell);
        new AutopilotSkill(Ground(new AreaEffect { Radius = 1, Effects = [new ApplyStatusEffect { StatusId = "status.stun" }] })).Use.ShouldBe(AutopilotSkillUse.Never);
        new AutopilotSkill(Ground(blast, new AreaEffect { Radius = 1, Friendly = true, Effects = [new HealEffect()] })).Use.ShouldBe(AutopilotSkillUse.Never);
        new AutopilotSkill(Ground(blast, new SummonEffect { MonsterId = "monster.x" })).Use.ShouldBe(AutopilotSkillUse.Never);
        new AutopilotSkill(Ground()).Use.ShouldBe(AutopilotSkillUse.Never);
        new AutopilotSkill(Ground(blast) with { Target = new SkillTargetDef { Range = 5, Ground = true, Friendly = true } }).Use.ShouldBe(AutopilotSkillUse.Never);
    }

    [Fact]
    public void A_skill_around_the_user_reaches_as_far_as_its_smallest_area()
    {
        var skill = new AutopilotSkill(new SkillDef
        {
            Id = "skill.x", Effects = [new AreaEffect { Radius = 3, Effects = [new DamageEffect()] }, new AreaEffect { Radius = 2, Effects = [new KnockbackEffect()] }],
        });
        (skill.Use, skill.Radius).ShouldBe((AutopilotSkillUse.AroundSelf, 2));
        new AutopilotSkill(new SkillDef { Id = "skill.x", Effects = [new DamageEffect()] }).Use.ShouldBe(AutopilotSkillUse.Never);   // hostile, yet aimed at the user
    }

    [Fact]
    public void A_supportive_skill_is_useful_only_when_it_would_do_something()
    {
        var sample = TestGrids.SampleContent;
        var heal = new AutopilotSkill(sample.Skills.Get("skill.second_wind"));
        heal.IsUseful(59, 100, 0, 100, null, 60).ShouldBeTrue();
        heal.IsUseful(60, 100, 0, 100, null, 60).ShouldBeFalse();
        heal.IsUseful(1, 100, 0, 100, null, 0).ShouldBeFalse();

        var statuses = new StatusSet();
        var guard = new AutopilotSkill(sample.Skills.Get("skill.iron_guard"));
        guard.IsUseful(100, 100, 100, 100, null, 60).ShouldBeTrue();
        statuses.Apply(new CompiledStatus(sample.Statuses.Get("status.iron_skin")), default, 1, 0, 1000, out _);
        guard.IsUseful(100, 100, 100, 100, statuses, 60).ShouldBeFalse();

        var cleanse = new AutopilotSkill(sample.Skills.Get("skill.cleanse"));
        cleanse.IsUseful(1, 100, 1, 100, statuses, 60).ShouldBeFalse();   // iron skin is nothing to cleanse
        statuses.Apply(new CompiledStatus(sample.Statuses.Get("status.poison")), default, 1, 0, 1000, out _);
        cleanse.IsUseful(100, 100, 100, 100, statuses, 60).ShouldBeTrue();   // by tag
    }

    // ---- dialogue ----------------------------------------------------------------------------------------------------------------------

    private static DialogueDef Graph(params (string Key, DialogueNodeDef Node)[] nodes) => new() { Id = "dialogue.x", Start = nodes[0].Key, Nodes = nodes.ToDictionary(n => n.Key, n => n.Node) };

    private static DialogueChoiceDef Choice(string? next, params DialogueActionDef[] actions) => new() { Text = "x", Next = next, Actions = actions };

    private static int? Choose(DialogueDef dialogue, string node, int[] shown, params string[] ticked) => AutopilotRules.ChooseDialogue(dialogue, node, shown, new HashSet<string>(ticked));

    public static TheoryData<string, DialogueActionDef> Unsafe => new()
    {
        { "service", new OpenServiceAction { Service = ServiceIds.Shop, ShopId = "shop.x" } },
        { "save point", new SetSavePointAction { X = 1, Y = 1 } },
        { "reset", new RunEffectsAction { Effects = [new ResetSkillsEffect()] } },
        { "any effect", new RunEffectsAction { Effects = [new HealEffect()] } },
        { "take", new TakeItemAction { ItemId = "item.x" } },
        { "give", new GiveItemAction { ItemId = "item.x" } },
        { "another quest", new StartQuestAction { QuestId = "quest.other" } },
    };

    [Theory]
    [MemberData(nameof(Unsafe))]
    public void A_choice_with_any_other_action_is_never_picked_even_beside_a_ticked_quest(string what, DialogueActionDef action)
    {
        var start = new StartQuestAction { QuestId = "quest.mine" };
        var dialogue = Graph(("a", new DialogueNodeDef { Choices = [Choice("b", action), Choice("b", start, action), Choice(null, action, start)] }),
            ("b", new DialogueNodeDef { Choices = [Choice(null, start)] }));
        Choose(dialogue, "a", [0, 1, 2], "quest.mine").ShouldBeNull(what);
        // Beside it, a safe one is.
        var offered = Graph(("a", new DialogueNodeDef { Choices = [Choice("b", action), Choice(null, start)] }));
        Choose(offered, "a", [0, 1], "quest.mine").ShouldBe(1, what);
    }

    [Fact]
    public void Every_dialogue_action_type_but_start_quest_is_covered_by_the_unsafe_list() =>
        Unsafe.Select(row => ((DialogueActionDef)row[1]).Type).Distinct().Order().ShouldBe(DialogueActionTypes.All.Order());

    [Fact]
    public void A_wanted_choice_is_picked_where_it_is_shown_and_plain_ones_lead_to_it()
    {
        var start = new StartQuestAction { QuestId = "quest.mine" };
        var dialogue = Graph(
            ("a", new DialogueNodeDef { Choices = [Choice("dead_end"), Choice("b")] }),
            ("b", new DialogueNodeDef { Next = "c" }),
            ("c", new DialogueNodeDef { Choices = [Choice("a"), Choice(null, start)] }),
            ("dead_end", new DialogueNodeDef { Choices = [Choice("a"), Choice(null)] }));
        Choose(dialogue, "a", [1], "quest.mine").ShouldBe(1);
        Choose(dialogue, "a", [0, 1], "quest.mine").ShouldBe(1);      // the first only leads back here
        Choose(dialogue, "b", [], "quest.mine").ShouldBe(0);          // its continue
        Choose(dialogue, "c", [0, 1], "quest.mine").ShouldBe(1);
        Choose(dialogue, "c", [0], "quest.mine").ShouldBeNull();      // not on screen: the way round in circles is not taken
        Choose(dialogue, "a", [0, 1], "quest.other").ShouldBeNull();
        Choose(dialogue, "b", [], "quest.other").ShouldBeNull();
        Choose(dialogue, "gone", [0], "quest.mine").ShouldBeNull();
        Choose(dialogue, "a", [7, -1], "quest.mine").ShouldBeNull();
    }

    [Fact]
    public void A_plain_choice_about_a_step_of_a_ticked_quest_is_wanted()
    {
        var dialogue = Graph(("a", new DialogueNodeDef
        {
            Choices =
            [
                new() { Text = "bye" },
                new() { Text = "state", Conditions = [new QuestStateCondition { QuestId = "quest.mine", State = QuestStates.Available }] },
                new() { Text = "step", Conditions = [new QuestStepCondition { QuestId = "quest.mine", Step = 2 }] },
            ],
        }));
        Choose(dialogue, "a", [0, 1, 2], "quest.mine").ShouldBe(2);
        Choose(dialogue, "a", [0, 1], "quest.mine").ShouldBeNull();   // an offer is not a turn-in
        Choose(dialogue, "a", [0, 1, 2], "quest.other").ShouldBeNull();
    }

    [Fact]
    public void In_the_sample_dialogues_nothing_but_quest_talk_is_ever_picked()
    {
        var content = TestGrids.SampleContent;
        var quests = content.Quests.All.Select(q => q.Id).ToArray();
        foreach (var dialogue in content.Dialogue.All)
        foreach (var (key, node) in dialogue.Nodes)
        {
            var all = Enumerable.Range(0, node.Choices.Count).ToArray();
            if (Choose(dialogue, key, all, quests) is not { } picked || node.Choices.Count == 0) continue;
            node.Choices[picked].Actions.ShouldAllBe(a => a is StartQuestAction, dialogue.Id + " " + key);
        }
    }
}

/// <summary>Distances and ways over maps (T-604): the fields, the warp graph, and the nearest-of-several search.</summary>
public class TravelTests
{
    private static MapGrid Grid(string id, string[] rows, params GridWarp[] warps)
    {
        var width = rows[0].Length;
        var cells = string.Concat(rows).Select(c => c == '#' ? CellFlagBits.BlocksLos : CellFlagBits.Walkable).ToArray();
        return MapGrid.Create(new MapGridFile { MapId = id, Width = width, Height = rows.Length, Cells = Convert.ToBase64String(cells), Warps = warps });
    }

    private static GridWarp Warp(int x, int y, string map, int tx, int ty) => new() { Cell = new GridCell(x, y), TargetMapId = map, TargetCell = new GridCell(tx, ty) };

    private static string[] Open(int w, int h) => Enumerable.Repeat(new string('.', w), h).ToArray();

    [Fact]
    public void A_field_agrees_with_the_pathfinder_everywhere()
    {
        foreach (var grid in TestGrids.SampleContent.Grids.Values)
        {
            var goal = grid.Warps[0].Cell;
            var field = DistanceField.Build(grid, [goal]);
            var pathfinder = new Pathfinder();
            var path = new List<GridCell>();
            for (var y = 0; y < grid.Height; y += 3)
            for (var x = 0; x < grid.Width; x += 3)
            {
                var cell = new GridCell(x, y);
                var status = pathfinder.FindPath(grid, cell, goal, TestGrids.Roomy, path);
                if (status != PathStatus.Found)
                {
                    field.At(cell).ShouldBe(DistanceField.Unreachable, $"{grid.MapId} {cell} {status}");
                    continue;
                }
                var cost = 0;
                var from = cell;
                foreach (var step in path)
                {
                    cost += DistanceField.Octile(from, step);
                    from = step;
                }
                field.At(cell).ShouldBe(cost, $"{grid.MapId} {cell}");
                field.Along(cell, 1000).ShouldBe(goal);
            }
        }
    }

    [Fact]
    public void A_field_goes_around_avoided_cells_but_a_walker_on_one_can_leave_it()
    {
        var grid = Grid("map.a", Open(9, 3));
        GridCell[] avoid = [new(4, 0), new(4, 1)];
        var field = DistanceField.Build(grid, [new GridCell(8, 1)], avoid);
        for (var cell = new GridCell(0, 1); !cell.Equals(new GridCell(8, 1)); cell = field.Along(cell, 1)) avoid.ShouldNotContain(cell);
        field.At(new GridCell(4, 1)).ShouldBe(40);
        field.Along(new GridCell(4, 1), 1).X.ShouldBe(5);
        DistanceField.Build(grid, []).At(new GridCell(0, 0)).ShouldBe(DistanceField.Unreachable);
        field.At(new GridCell(-1, 0)).ShouldBe(DistanceField.Unreachable);
    }

    [Fact]
    public void The_nearest_way_is_the_fewest_warps_then_the_shortest_walk()
    {
        // a → b (a long walk) and a → c → b (two warps, next door).
        var travel = new TravelMap(
        [
            Grid("map.a", Open(40, 3), Warp(39, 1, "map.b", 1, 1), Warp(2, 1, "map.c", 1, 1)),
            Grid("map.b", Open(10, 3)),
            Grid("map.c", Open(10, 3), Warp(3, 1, "map.b", 1, 1)),
        ]);
        var target = travel.AddTarget("map.b", [new GridCell(5, 1)]);
        var route = travel.Plan("map.a", new GridCell(0, 1), target);
        route.Cost.ShouldBe(new TravelCost(1, 390 + 8 + 40));   // around the other warp: two steps of the 39 are diagonal
        route.ToWarp.ShouldBeTrue();
        route.Field!.Along(new GridCell(0, 1), 1000).ShouldBe(new GridCell(39, 1));
        travel.Plan("map.c", new GridCell(1, 1), target).Cost.ShouldBe(new TravelCost(1, 20 + 40));
        travel.Plan("map.b", new GridCell(5, 1), target).Cost.ShouldBe(new TravelCost(0, 0));
        travel.Plan("map.b", new GridCell(9, 1), target).ToWarp.ShouldBeFalse();

        var none = travel.AddTarget("map.a", [new GridCell(5, 1)]);
        travel.Plan("map.b", new GridCell(1, 1), none).Cost.Reachable.ShouldBeFalse();   // no warp back
    }

    [Fact]
    public void A_place_behind_a_wall_is_reached_over_a_loop_of_warps_or_not_at_all()
    {
        // Map a is cut in two; its east half is reached through b only.
        string[] cut = ["....#....", "....#....", "....#...."];
        var travel = new TravelMap([Grid("map.a", cut, Warp(0, 0, "map.b", 1, 1)), Grid("map.b", Open(5, 3), Warp(4, 1, "map.a", 6, 1))]);
        var east = travel.AddTarget("map.a", [new GridCell(8, 1)]);
        travel.Plan("map.a", new GridCell(3, 1), east).Cost.Warps.ShouldBe(2);
        var west = travel.AddTarget("map.a", [new GridCell(3, 1)]);
        travel.Plan("map.a", new GridCell(8, 1), west).Cost.Reachable.ShouldBeFalse();
        Should.Throw<ArgumentException>(() => travel.AddTarget("map.none", [default]));
    }

    [Fact]
    public void A_stop_never_lies_beyond_a_warp_that_is_not_meant()
    {
        var travel = new TravelMap([Grid("map.a", Open(30, 5), Warp(15, 2, "map.a", 1, 1), Warp(29, 2, "map.a", 1, 1))]);
        var target = travel.AddTarget("map.a", [new GridCell(25, 2)]);
        var cell = new GridCell(5, 2);
        var pathfinder = new Pathfinder();
        var path = new List<GridCell>();
        for (var leg = 0; leg < 50 && target.Field.At(cell) > 0; leg++)
        {
            var stop = travel.NextStop("map.a", cell, target.Field, 24);
            pathfinder.FindPath(target.Field.Grid, cell, stop, TestGrids.Roomy, path).ShouldBe(PathStatus.Found);
            path.ShouldNotContain(new GridCell(15, 2));   // the pathfinder's own way there, not just the field's
            path.Count.ShouldBeGreaterThan(0);
            cell = stop;
        }
        target.Field.At(cell).ShouldBe(0);

        // Standing on the warp one wants (arrived on it): a step aside, so it can be stepped on.
        var warp = travel.Plan("map.a", new GridCell(29, 2), travel.AddTarget("map.a", [new GridCell(1, 1)]));
        var aside = travel.NextStop("map.a", new GridCell(29, 2), DistanceField.Build(warp.Field!.Grid, [new GridCell(29, 2)]), 24);
        GridDistance.Chebyshev(aside, new GridCell(29, 2)).ShouldBe(1);
    }

    [Fact]
    public void Connected_says_what_a_search_would_find()
    {
        string[] rows = ["..#..", ".##..", "#....", "..#.."];   // the corner at the top left only touches the rest diagonally
        var grid = Grid("map.a", rows);
        var travel = new TravelMap([grid]);
        var pathfinder = new Pathfinder();
        var path = new List<GridCell>();
        for (var a = 0; a < 20; a++)
        for (var b = 0; b < 20; b++)
        {
            GridCell from = new(a % 5, a / 5), to = new(b % 5, b / 5);
            var found = pathfinder.FindPath(grid, from, to, TestGrids.Roomy, path) == PathStatus.Found;
            travel.Connected("map.a", from, to).ShouldBe(found, $"{from} {to}");
        }
        travel.Connected("map.none", default, default).ShouldBeFalse();
        travel.Connected("map.a", new GridCell(-1, 0), default).ShouldBeFalse();
    }

    [Fact]
    public void Find_nearest_is_the_best_of_all_single_searches()
    {
        var grid = TestGrids.SampleGrid("map.starter_meadow");
        var pathfinder = new Pathfinder();
        var path = new List<GridCell>();
        var random = new Game.Shared.Random.Pcg32Random(604, 1);
        for (var round = 0; round < 40; round++)
        {
            var start = Walkable(grid, random);
            var goals = Enumerable.Range(0, 6).Select(_ => Walkable(grid, random)).ToList();
            var best = int.MaxValue;
            foreach (var goal in goals)
                if (pathfinder.FindPath(grid, start, goal, TestGrids.Roomy, path) == PathStatus.Found) best = Math.Min(best, Cost(start, path));
            var status = pathfinder.FindNearest(grid, start, goals, TestGrids.Roomy, path, out var index);
            if (best == int.MaxValue)
            {
                (status, index).ShouldBe((PathStatus.NoPath, -1));
                continue;
            }
            status.ShouldBe(PathStatus.Found);
            Cost(start, path).ShouldBe(best);
            if (path.Count > 0) path[^1].ShouldBe(goals[index]);
        }
    }

    [Fact]
    public void Find_nearest_keeps_the_limits_and_leaves_nothing_behind()
    {
        var grid = Grid("map.a", Open(60, 3));
        var pathfinder = new Pathfinder();
        var path = new List<GridCell>();
        pathfinder.FindNearest(grid, new GridCell(0, 1), [new GridCell(59, 1)], new PathLimits(20, 100_000), path, out _).ShouldBe(PathStatus.TooLong);
        pathfinder.FindNearest(grid, new GridCell(0, 1), [new GridCell(59, 1)], new PathLimits(100, 30), path, out _).ShouldBe(PathStatus.NodeLimit);
        pathfinder.FindNearest(grid, new GridCell(0, 1), [], new PathLimits(100, 1000), path, out var none).ShouldBe(PathStatus.NoPath);
        none.ShouldBe(-1);
        pathfinder.FindNearest(grid, new GridCell(0, 1), [new GridCell(99, 1), new GridCell(5, 1), new GridCell(5, 1)], new PathLimits(100, 1000), path, out var index).ShouldBe(PathStatus.Found);
        index.ShouldBe(1);   // the lower index on a shared cell; off the map never
        pathfinder.FindNearest(grid, new GridCell(4, 1), [new GridCell(59, 1)], new PathLimits(100, 1000), path, out index).ShouldBe(PathStatus.Found);
        path.Count.ShouldBe(55);   // the goals of earlier searches are forgotten
        pathfinder.FindNearest(grid, new GridCell(7, 1), [new GridCell(7, 1)], new PathLimits(100, 1000), path, out index).ShouldBe(PathStatus.Found);
        path.ShouldBeEmpty();
    }

    private static int Cost(GridCell from, List<GridCell> path)
    {
        var cost = 0;
        foreach (var cell in path)
        {
            cost += DistanceField.Octile(from, cell);
            from = cell;
        }
        return cost;
    }

    private static GridCell Walkable(MapGrid grid, Game.Shared.Random.IRandom random)
    {
        while (true)
        {
            var cell = new GridCell(random.Next(grid.Width), random.Next(grid.Height));
            if (grid.IsWalkable(cell.X, cell.Y)) return cell;
        }
    }
}
