using Game.Content.Loading;
using Game.Content.Simulation;

namespace Game.Tests.Golden;

/// <summary>
/// T-404: the meadow after weapons, refining, cards, effects, statuses, player skills and stat points. Each test pins one target of tasks/prompts/T-404.md
/// with the real /content and a seeded simulation through the real CombatResolver and effect runner (<see cref="Duel"/>): player skills are cast by the
/// duel rotation (the highest-damage skill off cooldown and affordable, a heal below half HP, else auto-attack), SP is the full pool at the start of
/// the fight and does not regenerate, and monsters use their <c>ai.skills</c>. The numbers are documented in docs/BALANCING.md section 5.
/// Job levels: a Wanderer job level follows its base level; a Warden changed job at base level 10, so its job level is 4 here (3 skill points: Twin Strike 3).
/// </summary>
public class WeaponBalanceTests
{
    private const int Fights = 1000;
    private const ulong Seed = 1;

    private static readonly Lazy<BalanceSimulator> Content = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
        var result = ContentLoader.Load(Path.Combine(dir.FullName, "content"));
        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        return new BalanceSimulator(result.Database!);
    });

    private static Dictionary<string, int> W(params (string Stat, int Weight)[] w) => w.ToDictionary(x => x.Stat, x => x.Weight);

    /// <summary>The simple rule: two str raises for each vit raise.</summary>
    private static readonly Dictionary<string, int> StrVit = W(("str", 2), ("vit", 1));

    private static readonly Dictionary<string, int> StrOnly = W(("str", 1));

    private static DuelResult Level7Wanderer(IReadOnlyDictionary<string, int> weights, IReadOnlyDictionary<string, int>? skills)
    {
        var sim = Content.Value;
        var player = sim.Character("job.wanderer", 7, weights: weights, skills: skills);
        player.Weapon!.Id.ShouldBe("item.bone_dirk");
        player.Refine.ShouldBe(0);
        return Duel.Run(sim, player, sim.Db.Monsters.Get("monster.rock_beetle"), Fights, Seed);
    }

    private static DuelResult Level10Warden(IReadOnlyDictionary<string, int> weights, bool gear = true, int refine = 2)
    {
        var sim = Content.Value;
        var db = sim.Db;
        var worn = gear
            ? new[] { new GearPiece(db.Items.Get("item.padded_vest"), refine), new GearPiece(db.Items.Get("item.leather_cap"), 0) }
            : null;
        var player = sim.Character("job.warden", 10, weights: weights, weaponId: "item.iron_blade", refine: refine,
            gear: worn, skills: new Dictionary<string, int> { ["skill.twin_strike"] = 3 }, jobLevel: 4);
        return Duel.Run(sim, player, db.Monsters.Get("monster.thornback_boar"), Fights, Seed);
    }

    /// <summary>
    /// Level 7 Wanderer, Bone Dirk +0, 28 points by the str/vit rule, Second Wind 3 (used below half HP) vs the Rock Beetle
    /// (160 HP, atk 17, def 9; before T-404: 320 HP, atk 30, def 12, which no level 7 character could beat): 62.3%, target 50-75%. Second Wind is what makes the
    /// fight winnable: without it the same character wins about 1%. All points in str: 43% (a Second Wind user needs the HP of the vit raises).
    /// </summary>
    [Fact]
    public void Level_7_wanderer_with_second_wind_has_a_real_but_winnable_fight_with_a_rock_beetle()
    {
        var r = Level7Wanderer(StrVit, new Dictionary<string, int> { ["skill.second_wind"] = 3 });

        r.PlayerHp.ShouldBe(100);
        r.MonsterHp.ShouldBe(160);
        r.WinRate.ShouldBeInRange(0.50, 0.75);
        r.WinRate.ShouldBe(0.623, 1e-9);
        r.MeanHpLeftFractionOnWin.ShouldBe(0.401926, 1e-4);
        r.MeanPlayerSkillCasts.ShouldBeGreaterThan(1.0);   // the skill really is cast
    }

    [Fact]
    public void Without_second_wind_the_same_level_7_wanderer_loses_to_the_rock_beetle()
    {
        Level7Wanderer(StrVit, null).WinRate.ShouldBeLessThan(0.05);
    }

    [Fact]
    public void Rock_beetle_is_not_trivial_for_a_level_7_wanderer_who_spent_everything_on_str()
    {
        Level7Wanderer(StrOnly, new Dictionary<string, int> { ["skill.second_wind"] = 3 }).WinRate.ShouldBeInRange(0.30, 0.60);
    }

    /// <summary>
    /// Level 10 Warden (job level 4, Twin Strike 3), Iron Blade +2, Padded Vest +2, Leather Cap (the best gear the meadow drops, refined with two Temper Shards
    /// each), points by the str/vit rule vs the Thornback Boar (240 HP, atk 26, def 10; before: 620 HP, atk 48, def 16): 56.2%, target 45-70%.
    /// All points in str: 52.4%. Without the armour or refining it is much harder.
    /// </summary>
    [Fact]
    public void Level_10_warden_in_the_best_meadow_gear_has_a_boss_like_fight_with_the_thornback_boar()
    {
        var r = Level10Warden(StrVit);

        r.PlayerHp.ShouldBe(130);
        r.MonsterHp.ShouldBe(240);
        r.WinRate.ShouldBeInRange(0.45, 0.70);
        r.WinRate.ShouldBe(0.562, 1e-9);
        r.MeanPlayerSkillCasts.ShouldBeGreaterThan(2.0);   // Twin Strike on every cooldown
    }

    [Fact]
    public void Level_10_warden_who_spent_everything_on_str_is_also_inside_the_band()
    {
        Level10Warden(StrOnly).WinRate.ShouldBeInRange(0.45, 0.70);
    }

    [Fact]
    public void The_boar_without_armour_or_refine_is_much_harder()
    {
        Level10Warden(StrVit, gear: false, refine: 0).WinRate.ShouldBeLessThan(0.30);
    }

    /// <summary>The seeded duel with skills is deterministic.</summary>
    [Fact]
    public void Duel_with_skills_is_deterministic()
    {
        Level10Warden(StrVit).ShouldBe(Level10Warden(StrVit));
    }

    /// <summary>
    /// Time to level 10 (<see cref="LevelingEstimate"/>): at each level the Wanderer (Bone Dirk, str/vit rule, every skill point in Second Wind) farms the monster
    /// with the best EXP per second among those it beats at least half the time and whose level is at most two above its own. One kill costs 8 s of walking and
    /// looting, the won fight, and the regeneration of the HP it cost; a lost fight costs 30 s and no EXP. Per kill: slime 14 EXP (levels 1-3), mossling 55
    /// (level 4), wasp 200 (levels 5-7), beetle 450 (levels 8-9); the sum of ExpNeeded is 12,269 (the base curve up to level 10) in 65.3 minutes. Target 45-90.
    /// </summary>
    [Fact]
    public void Time_to_level_10_is_about_an_hour_with_the_arithmetic_shown()
    {
        var sim = Content.Value;
        var rows = LevelingEstimate.Run(sim, "job.wanderer", StrVit,
            l => new Dictionary<string, int> { ["skill.second_wind"] = Math.Min(5, l - 1) }, 10, 8, 0.5, 30, 2, Fights, Seed);

        rows.Select(r => r.MonsterId).ShouldBe(
        [
            "monster.gel_slime", "monster.gel_slime", "monster.gel_slime", "monster.mossling", "monster.sting_wasp",
            "monster.sting_wasp", "monster.sting_wasp", "monster.rock_beetle", "monster.rock_beetle",
        ]);
        rows.Select(r => r.BaseExp).ShouldBe([14L, 14, 14, 55, 200, 200, 200, 450, 450]);
        rows.Sum(r => r.ExpNeeded).ShouldBe(12269);
        foreach (var r in rows)
        {
            r.KillsPerMinute.ShouldBe(60 / r.SecondsPerKill, 1e-9);
            r.Minutes.ShouldBe(r.ExpNeeded / (r.BaseExp * r.KillsPerMinute), 1e-9);
        }
        // Worked example, level 6: 1,527 EXP at 200 EXP a kill = 7.6 kills at 0.732 kills a minute = 10.43 minutes.
        rows[5].KillsPerMinute.ShouldBe(0.732, 1e-3);
        rows[5].Minutes.ShouldBe(10.43, 1e-2);
        var total = rows.Sum(r => r.Minutes);
        total.ShouldBeInRange(45, 90);
        total.ShouldBe(65.283, 1e-2);
    }

    /// <summary>
    /// The gear the targets assume is reachable: drop chances of the Iron Blade (boar), Padded Vest (beetle) and Leather Cap (mossling).
    /// P(at least one drop) = 1 - (1 - chance)^kills.
    /// </summary>
    [Fact]
    public void The_best_meadow_gear_drops_often_enough_to_be_expected_by_level_10()
    {
        var db = Content.Value.Db;
        double Chance(string table, string item) => db.Drops.Get(table).Entries.Single(e => e.ItemId == item).Chance;

        Chance("drop.thornback_boar", "item.iron_blade").ShouldBe(0.10, 1e-9);   // was 0.01: a boar dies a few times an hour, so 1% never came
        Chance("drop.thornback_boar", "item.oak_staff").ShouldBe(0.10, 1e-9);
        Chance("drop.rock_beetle", "item.padded_vest").ShouldBe(0.08, 1e-9);     // was 0.015
        Chance("drop.mossling", "item.leather_cap").ShouldBe(0.05, 1e-9);        // was 0.02
        // 14 beetle kills take a level 8 character to level 10 (6,247 EXP / 450): a 68% chance of a vest on the way.
        (1 - Math.Pow(1 - 0.08, 6247 / 450.0)).ShouldBe(0.686, 0.01);
    }
}
