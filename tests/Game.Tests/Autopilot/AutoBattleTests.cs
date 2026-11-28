using Game.Content.Registry;
using Game.Server.World;
using Game.Server.World.Autopilot;
using Game.Server.World.Combat;
using Game.Server.World.Effects;
using Game.Server.World.Inventory;
using Game.Server.World.Quests;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Server;
using static Game.Tests.Autopilot.AutopilotContentKit;

namespace Game.Tests.Autopilot;

/// <summary>
/// Auto-battle (T-811) on the whole pipeline: the autopilot's second mode fights the monsters around its anchor with the skill bar, through the same intents and ends as a quest run.
/// The kit's map alpha: tame monsters in x 12-14, y 5-7; the sample's <c>auto_battle</c> numbers unless a test changes them (radius 6, retaliation 3 cells beyond).
/// </summary>
public class AutoBattleTests
{
    private static ContentDatabase Battle(Func<AutoBattleBalanceDef, AutoBattleBalanceDef>? battle = null, IReadOnlyDictionary<string, MapGrid>? grids = null, int tameHp = 8,
        Func<MonsterDef, MonsterDef>? monster = null, Func<AutopilotBalanceDef, AutopilotBalanceDef>? autopilot = null, Func<BalanceDef, BalanceDef>? balance = null) =>
        Build(autopilot, grids, tameHp, monster: monster,
            balance: b => (balance ?? (x => x))(b with { AutoBattle = (battle ?? (x => x))(b.AutoBattle) }));

    /// <summary>Ticks and returns the farthest the player got from <paramref name="anchor"/> (Chebyshev) on any tick.</summary>
    private static int Watch(AutopilotRig rig, EntityId player, GridCell anchor, int ticks, Func<bool>? until = null)
    {
        var farthest = 0;
        for (var i = 0; i < ticks && !(until?.Invoke() ?? false); i++)
        {
            rig.Ticks();
            farthest = Math.Max(farthest, GridDistance.Chebyshev(anchor, rig.Get(player).Cell));
        }
        return farthest;
    }

    private static int Kills(AutopilotRig rig, EntityId player, string? monster = null) =>
        rig.Of<MonsterKilled>().Count(k => k.Credited.Contains(player) && (monster is null || k.MonsterId == monster));

    // ---- where it fights ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void It_fights_the_nearest_in_the_radius_first_and_never_leaves_the_area()
    {
        var rig = new AutopilotRig(Battle());
        var anchor = new GridCell(6, 6);
        var player = rig.Join(anchor);
        var near = rig.SpawnMonster(Tame, new GridCell(4, 6), Alpha);   // two cells; the spawn area's are six away
        rig.Battle(player);

        var farthest = Watch(rig, player, anchor, 600, () => Kills(rig, player) >= 3);
        rig.Of<AttackStarted>().First(e => e.Attacker == player).Target.ShouldBe(near);
        Kills(rig, player).ShouldBeGreaterThanOrEqualTo(3);
        rig.Status(player).ShouldBe(new AutopilotStatus(AutopilotModes.Running, null, rig.Status(player).Doing, null));   // no quest: auto-battle
        rig.Activities(player).ShouldContain(AutopilotActivities.Fighting);
        rig.Activities(player).ShouldNotContain(AutopilotActivities.Hunting);
        // Its walks stay in the radius; combat stands it next to a target in the radius, so at most one cell more.
        farthest.ShouldBeLessThanOrEqualTo(rig.Content.Balance.AutoBattle.RadiusCells + 1);
    }

    [Fact]
    public void Nothing_beyond_the_radius_is_fought_and_it_waits_at_the_anchor()
    {
        var rig = new AutopilotRig(Battle(b => b with { RadiusCells = 4 }));
        var anchor = new GridCell(6, 6);   // the tame ones are six or more away
        var player = rig.Join(anchor);
        rig.Battle(player);
        Watch(rig, player, anchor, 200).ShouldBe(0);
        rig.Of<AttackStarted>().ShouldBeEmpty();
        rig.Status(player).ShouldBe(new AutopilotStatus(AutopilotModes.Running, null, AutopilotActivities.Waiting, null));
    }

    [Fact]
    public void With_nothing_left_it_goes_back_to_the_anchor_and_waits_for_the_respawn()
    {
        var rig = new AutopilotRig(Battle(grids: Grids(tame: 1)));
        var anchor = new GridCell(9, 6);
        var player = rig.Join(anchor);
        rig.Battle(player);
        rig.TickUntil(() => Kills(rig, player) >= 1, 300, "the first kill");
        rig.TickUntil(() => rig.Get(player).Cell.Equals(anchor) && rig.Status(player).Doing == AutopilotActivities.Waiting, 100, "back at the anchor");
        var after = rig.Activities(player);
        after.IndexOf(AutopilotActivities.Returning).ShouldBeGreaterThan(after.IndexOf(AutopilotActivities.Fighting));
        rig.TickUntil(() => Kills(rig, player) >= 2, 300, "the respawn fought");
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(0, false)]
    public void What_hits_it_from_just_outside_the_radius_is_fought_back_within_its_bound(int extra, bool fought)
    {
        // A fierce one that strikes from five cells: it stands outside a radius of 4 and hits.
        var rig = new AutopilotRig(Battle(b => b with { RadiusCells = 4, RetaliateExtraCells = extra }, Grids(tame: 0, fierce: 1),
            monster: m => m.Id == Fierce ? m with { AttackRange = 5, Ai = m.Ai with { AggroRange = 9 } } : m));
        var anchor = new GridCell(14, 6);   // it spawns in x 20-21, beyond the radius
        var player = rig.Join(anchor);
        var fierce = rig.World.GetInstance(Alpha).Entities.Single(e => e.DefinitionId == Fierce).Id;
        rig.Battle(player);

        var farthest = Watch(rig, player, anchor, 300, () => Kills(rig, player, Fierce) > 0);
        rig.Of<DamageDealt>().ShouldContain(e => e.Source == fierce && e.Target == player);
        if (fought)
        {
            Kills(rig, player, Fierce).ShouldBe(1);
            rig.Events.First(e => e.Event is AttackStarted a && a.Attacker == player).Tick
                .ShouldBeGreaterThanOrEqualTo(rig.Events.First(e => e.Event is DamageDealt d && d.Target == player).Tick);   // only once it was hit
            farthest.ShouldBeLessThanOrEqualTo(4 + extra + 1);
        }
        else
        {
            rig.Of<AttackStarted>().Where(e => e.Attacker == player).ShouldBeEmpty();
            farthest.ShouldBe(0);
        }
    }

    // ---- what it fights -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AutoBattleFilters.Nearby, 1, false, true)]
    [InlineData(AutoBattleFilters.Nearby, 6, false, true)]     // five above the character: the edge of the band
    [InlineData(AutoBattleFilters.Nearby, 7, false, false)]
    [InlineData(AutoBattleFilters.Nearby, 1, true, false)]     // a boss only when asked
    [InlineData(AutoBattleFilters.WithBosses, 1, true, true)]
    [InlineData(AutoBattleFilters.WithBosses, 7, true, false)] // the band holds for bosses too
    [InlineData(AutoBattleFilters.Attackers, 1, false, false)] // the tame ones never hit
    public void The_filter_decides_what_it_fights(string filter, int level, bool boss, bool fights)
    {
        var rig = new AutopilotRig(Battle(monster: m => m.Id == Tame ? m with { Level = level, Boss = boss } : m));
        var player = rig.Join(new GridCell(10, 6));
        rig.Battle(player, filter);
        rig.Ticks(200);
        (Kills(rig, player) > 0).ShouldBe(fights);
        if (!fights) rig.Of<AttackStarted>().Where(e => e.Attacker == player).ShouldBeEmpty();
    }

    [Fact]
    public void Only_those_that_attack_it_are_fought_with_the_attackers_filter()
    {
        var rig = new AutopilotRig(Battle(grids: Grids(fierce: 1)));
        var player = rig.Join(new GridCell(16, 6));   // the tame ones in x 12-14, the fierce one in x 20-21 and aggressive
        rig.Battle(player, AutoBattleFilters.Attackers);
        rig.TickUntil(() => Kills(rig, player, Fierce) == 1, 300, "the attacker fought");
        rig.Ticks(100);
        Kills(rig, player, Tame).ShouldBe(0);
    }

    [Fact]
    public void The_server_refuses_a_start_for_a_filter_it_does_not_know()
    {
        var rig = new AutopilotRig(Battle());
        var player = rig.Join();
        rig.Battle(player, "everything");
        rig.Ticks(2);
        rig.Of<AutopilotRefused>().ShouldHaveSingleItem().Reason.ShouldBe(AutoBattleReasons.UnknownFilter);
    }

    // ---- skills, loot, staying alive -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Shortcut_skills_are_chosen_as_for_a_quest_run()
    {
        var rig = new AutopilotRig(Battle(tameHp: 100_000));
        var player = rig.Join(new GridCell(10, 6), boost: false);
        rig.Boost(player, atk: 50);
        rig.Get(player).Stats!.Modifiers.Add("test:sp", "max_sp", ModifierOp.Flat, 10_000);
        rig.Ticks();
        rig.Get(player).Vitals!.Sp = 10_000;
        foreach (var skill in new[] { "skill.ember_bolt", "skill.twin_strike", "skill.second_wind", "skill.return_home", "skill.mana_font", "skill.call_seedling", "skill.shockwave" }) rig.Learn(player, skill);
        rig.Battle(player, AutoBattleFilters.Nearby, "skill.return_home", "skill.call_seedling", "skill.mana_font", "skill.shockwave", "skill.second_wind", "skill.ember_bolt", "skill.twin_strike");
        rig.Ticks(200);

        var casts = rig.Of<SkillRequested>().Where(e => e.Caster == player).Select(e => e.SkillId).ToList();
        casts[0].ShouldBe("skill.ember_bolt");
        casts.Distinct().ShouldBe(["skill.ember_bolt", "skill.twin_strike"], ignoreOrder: true);   // never a teleport, a summon, a friendly ground skill or a heal at full HP
        rig.Of<SkillRefused>().ShouldBeEmpty();
    }

    [Fact]
    public void A_heal_on_the_bar_is_cast_when_it_helps_and_no_potion_is_drunk()
    {
        var rig = new AutopilotRig(Battle(tameHp: 100_000));
        var player = rig.Join(new GridCell(10, 6), boost: false);
        rig.Boost(player, atk: 50);
        rig.Learn(player, "skill.second_wind");
        rig.Get(player).Vitals!.Hp = rig.Get(player).Vitals!.MaxHp / 3;
        rig.Battle(player, AutoBattleFilters.Nearby, "skill.second_wind");
        rig.TickUntil(() => rig.Of<HpRestored>().Any(e => e.Target == player), 200, "the heal");
        rig.Of<ItemUsed>().ShouldBeEmpty();
        rig.Get(player).Autopilot!.Active.ShouldBeTrue();
    }

    [Fact]
    public void Low_hp_without_a_heal_stops_it()
    {
        var rig = new AutopilotRig(Battle());
        var player = rig.Join(new GridCell(10, 6));
        rig.Get(player).Vitals!.Hp = rig.Get(player).Vitals!.MaxHp / 10;
        rig.Battle(player);
        rig.RunsTo(player, AutopilotStops.LowHp, 5);
        rig.Of<ItemUsed>().ShouldBeEmpty();
    }

    [Fact]
    public void Low_hp_tries_the_heal_on_the_bar_before_it_stops()
    {
        var rig = new AutopilotRig(Battle(b => b with { StopBelowHpPercent = 50 }));
        var player = rig.Join(new GridCell(10, 6));
        rig.Learn(player, "skill.second_wind");
        rig.Get(player).Vitals!.Hp = rig.Get(player).Vitals!.MaxHp / 10;
        rig.Battle(player, AutoBattleFilters.Nearby, "skill.second_wind");
        rig.TickUntil(() => rig.Of<SkillRequested>().Any(e => e.Caster == player && e.SkillId == "skill.second_wind"), 20, "the heal");
        rig.Of<AutopilotChanged>().ShouldNotContain(e => e.Status.Mode == AutopilotModes.Off);
        rig.RunsTo(player, AutopilotStops.LowHp, 200);   // one heal is not enough; on cooldown it hands over
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void It_picks_up_its_own_kills_drops_only_when_loot_is_on(bool loot)
    {
        var rig = new AutopilotRig(Battle(b => b with { Loot = loot }));
        var player = rig.Join(new GridCell(10, 6));
        rig.Battle(player);
        rig.TickUntil(() => Kills(rig, player) >= 2, 300, "two kills");
        rig.Ticks(100);
        (rig.Count(player, Wanted) > 0).ShouldBe(loot);
        (rig.Count(player, Other) > 0).ShouldBe(loot);
        rig.Activities(player).Contains(AutopilotActivities.Gathering).ShouldBe(loot);
    }

    [Fact]
    public void A_drop_that_does_not_fit_ends_it()
    {
        var rig = new AutopilotRig(Battle(balance: b => b with { Inventory = b.Inventory with { MaxEntries = 4 } }));   // the starting items fill it
        var player = rig.Join(new GridCell(10, 6));
        rig.Battle(player);
        rig.RunsTo(player, AutopilotStops.BagFull);
    }

    [Fact]
    public void A_load_too_heavy_to_fight_ends_it()
    {
        var rig = new AutopilotRig(Battle(balance: b => b with { Inventory = b.Inventory with { BurdenedPercent = 1, OverweightPercent = 1 } }));
        var player = rig.Join(new GridCell(10, 6));
        rig.Battle(player);
        rig.RunsTo(player, AutopilotStops.BagFull);
    }

    // ---- limits ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void It_shares_the_daily_limit_with_the_quest_autopilot()
    {
        var rig = new AutopilotRig(Battle(autopilot: a => a with { DailyLimitMinutes = 1, NoProgressMs = 10_000_000 }, grids: Grids(tame: 0)));
        var player = rig.Join();
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.Ticks(300);                                // half the minute on quests
        rig.Battle(player);
        rig.Ticks(280);
        rig.Status(player).Mode.ShouldBe(AutopilotModes.Running);
        rig.RunsTo(player, AutopilotStops.TimeLimit, 30);   // the other half on auto-battle

        rig.Battle(player);
        rig.Ticks(2);
        rig.Start(player, "quest.kill");
        rig.Ticks(2);
        rig.Of<AutopilotRefused>().Select(e => e.Reason).ShouldBe([AutopilotReasons.TimeLimit, AutopilotReasons.TimeLimit]);
        rig.Clock.Advance(24L * 60 * 60 * 1000);
        rig.Battle(player);
        rig.Ticks(2);
        rig.Status(player).Mode.ShouldBe(AutopilotModes.Running);
    }

    [Fact]
    public void Without_a_kill_for_long_it_stops()
    {
        var rig = new AutopilotRig(Battle(b => b with { IdleStopMs = 3000 }, Grids(tame: 0)));
        var player = rig.Join();
        rig.Battle(player);
        rig.RunsTo(player, AutopilotStops.NothingToFight, 60);
    }

    [Fact]
    public void Switched_off_it_is_refused_and_the_quest_autopilot_is_not()
    {
        var rig = new AutopilotRig(Battle(b => b with { Enabled = false }));
        var player = rig.Join();
        rig.Battle(player);
        rig.Ticks(2);
        rig.Of<AutopilotRefused>().ShouldHaveSingleItem().Reason.ShouldBe(AutoBattleReasons.BattleDisabled);
        rig.Accept(player, "quest.kill");
        rig.Start(player, "quest.kill");
        rig.Ticks(2);
        rig.Status(player).Mode.ShouldBe(AutopilotModes.Running);
    }

    public static TheoryData<string> Refusals => [AutoBattleReasons.QuestsGiven, AutopilotReasons.Dead, AutopilotReasons.NotLearned, AutopilotReasons.TooManySkills];

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_bad_start_is_refused_with_its_reason_and_changes_nothing(string reason)
    {
        var rig = new AutopilotRig(Battle(autopilot: a => a with { MaxSkills = 1 }));
        var player = rig.Join();
        rig.Accept(player, "quest.kill");
        rig.Learn(player, "skill.second_wind");
        string[] quests = [];
        SkillLevelRecord[] skills = [];
        string? skill = null;
        switch (reason)
        {
            case AutoBattleReasons.QuestsGiven: quests = ["quest.kill"]; break;
            case AutopilotReasons.Dead: rig.Get(player).Dead = true; break;
            case AutopilotReasons.NotLearned: skills = [new(skill = "skill.ember_bolt", 0)]; break;
            case AutopilotReasons.TooManySkills: skills = [new("skill.second_wind", 0), new("skill.second_wind", 0)]; break;
        }
        rig.Send(new StartAutopilot(player, quests, skills, AutoBattleFilters.Nearby));
        rig.Ticks(2);
        rig.Of<AutopilotRefused>().ShouldHaveSingleItem().ShouldBe(new AutopilotRefused(player, reason, null, skill));
        rig.Of<AutopilotChanged>().ShouldBeEmpty();
    }

    // ---- replacing and ending -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void It_replaces_a_quest_run_where_the_character_stands_and_a_quest_run_replaces_it()
    {
        var rig = new AutopilotRig(Battle(grids: Grids(tame: 0)));
        var player = rig.Join();
        rig.Accept(player, "quest.reach_far");
        rig.Start(player, "quest.reach_far");
        rig.TickUntil(() => rig.Status(player).Doing == AutopilotActivities.Travelling && !rig.Get(player).Cell.Equals(Start), 60, "the journey");

        rig.Battle(player);
        rig.Ticks();
        var anchor = rig.Get(player).Autopilot!.Battle!.Anchor;
        rig.Status(player).ShouldBe(new AutopilotStatus(AutopilotModes.Running, null, AutopilotActivities.Waiting, null));
        Watch(rig, player, anchor, 100).ShouldBeLessThanOrEqualTo(2);   // the journey ended with the quest run: it does not carry the battle away
        rig.Get(player).MapId.ShouldBe(Alpha);
        rig.Get(player).Cell.ShouldBe(anchor);

        rig.Start(player, "quest.reach_far");
        rig.Ticks(2);
        rig.Status(player).QuestId.ShouldBe("quest.reach_far");
        rig.Get(player).Autopilot!.Battle.ShouldBeNull();
        rig.TickUntil(() => rig.Get(player).MapId == Gamma, 600, "the quest run playing on");
    }

    [Fact]
    public void The_players_own_order_ends_it_and_is_carried_out()
    {
        var rig = new AutopilotRig(Battle(tameHp: 100_000));
        var player = rig.Join(new GridCell(10, 6));
        rig.Battle(player);
        rig.TickUntil(() => rig.Of<AttackStarted>().Any(e => e.Attacker == player), 100, "the fight");
        rig.Send(new ManualInput(player, true));
        rig.Send(new MoveTo(player, new GridCell(2, 2)));
        rig.RunsTo(player, AutopilotStops.ManualInput, 3);
        rig.Ticks(80);
        rig.Get(player).Cell.ShouldBe(new GridCell(2, 2));
        rig.Get(player).Autopilot!.Active.ShouldBeFalse();
    }

    [Fact]
    public void A_stop_ends_it_and_the_character_stands_still()
    {
        var rig = new AutopilotRig(Battle(tameHp: 100_000));
        var player = rig.Join(new GridCell(10, 6));
        rig.Battle(player);
        rig.TickUntil(() => rig.Of<AttackStarted>().Any(e => e.Attacker == player), 100, "the fight");
        rig.Send(new StopAutopilot(player, AutopilotStops.Stopped));
        rig.RunsTo(player, AutopilotStops.Stopped, 3);
        rig.Ticks(3);
        var cell = rig.Get(player).Cell;
        var hits = rig.Of<DamageDealt>().Count(e => e.Source == player);
        rig.Ticks(40);
        rig.Get(player).Cell.ShouldBe(cell);
        rig.Of<DamageDealt>().Count(e => e.Source == player).ShouldBe(hits);
    }

    [Theory]
    [InlineData(Beta)]
    [InlineData(Alpha)]   // a warp on the same map moves it off its anchor too
    public void A_warp_ends_it(string map)
    {
        var rig = new AutopilotRig(Battle(grids: Grids(tame: 0)));
        var player = rig.Join();
        rig.Battle(player);
        rig.Ticks(3);
        rig.World.Warp(player, map, new GridCell(3, 3));
        rig.RunsTo(player, AutopilotStops.LeftArea, 5);
    }

    [Fact]
    public void Death_ends_it()
    {
        var rig = new AutopilotRig(Battle());
        var player = rig.Join(new GridCell(10, 6));
        rig.Battle(player);
        rig.Ticks(3);
        rig.Get(player).Dead = true;
        rig.RunsTo(player, AutopilotStops.Died, 3);
    }
}
