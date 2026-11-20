using Game.Content.Loading;
using Game.Content.Simulation;

namespace Game.Tests.Golden;

/// <summary>
/// T-407: Thistle Hollow's monsters, each tuned through the same seeded duel as T-404 (<see cref="WeaponBalanceTests"/>): 2,000 fights, seed 1, points by the
/// str/vit rule, the gear the meadow and the Hollow drop (Padded Vest +2, Leather Cap; Iron Blade +2 for a Warden), skills used by the duel rotation.
/// Target 50-80% each, and moving to the Hollow must pay: EXP per minute (the cost of a kill is the T-404 leveling arithmetic, <see cref="LevelingEstimate"/>)
/// beats the best meadow monster the same character beats at least half the time. Numbers: docs/BALANCING.md section 6.
/// </summary>
public class HollowBalanceTests
{
    private const int Fights = 2000;
    private const ulong Seed = 1;
    private const double Overhead = 8, DeathSeconds = 30, MinWin = 0.5;   // as in T-404's time to level 10

    private static readonly Lazy<BalanceSimulator> Content = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
        var result = ContentLoader.Load(Path.Combine(dir.FullName, "content"));
        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        return new BalanceSimulator(result.Database!);
    });

    private static readonly Dictionary<string, int> StrVit = new() { ["str"] = 2, ["vit"] = 1 };
    private static readonly Dictionary<string, int> StrOnly = new() { ["str"] = 1 };
    private static readonly string[] Meadow = ["monster.gel_slime", "monster.mossling", "monster.sting_wasp", "monster.rock_beetle", "monster.thornback_boar"];

    /// <summary>Level 8 Wanderer: Bone Dirk +0, Padded Vest +2, Leather Cap, Second Wind 3.</summary>
    private static SimCharacter Wanderer8(IReadOnlyDictionary<string, int> weights)
    {
        var sim = Content.Value;
        return sim.Character("job.wanderer", 8, weights: weights, gear: Gear(2), skills: new Dictionary<string, int> { ["skill.second_wind"] = 3 });
    }

    /// <summary>Level 10 / 12 Warden (job level 4 / 6: Twin Strike 3 / 5): Iron Blade +2, Padded Vest +2, Leather Cap.</summary>
    private static SimCharacter Warden(int level, IReadOnlyDictionary<string, int> weights)
    {
        var sim = Content.Value;
        var jobLevel = level - 6;
        return sim.Character("job.warden", level, weights: weights, weaponId: "item.iron_blade", refine: 2, gear: Gear(2), jobLevel: jobLevel,
            skills: new Dictionary<string, int> { ["skill.twin_strike"] = jobLevel - 1 });
    }

    private static GearPiece[] Gear(int refine) =>
        [new GearPiece(Content.Value.Db.Items.Get("item.padded_vest"), refine), new GearPiece(Content.Value.Db.Items.Get("item.leather_cap"), 0)];

    private static DuelResult Fight(SimCharacter c, string monster) => Duel.Run(Content.Value, c, Content.Value.Db.Monsters.Get(monster), Fights, Seed);

    /// <summary>EXP per minute of killing one monster, by the T-404 arithmetic: seconds per kill = (8 + p (fight + rest) + (1 - p) 30) / p.</summary>
    private static (double Rate, double SecondsPerKill) ExpPerMinute(SimCharacter c, string monster)
    {
        var sim = Content.Value;
        var m = sim.Db.Monsters.Get(monster);
        var r = Fight(c, monster);
        var regen = c.Sheet.Get("hp_regen");
        var maxHp = c.Sheet.GetInt("max_hp");
        var lost = (1 - r.MeanHpLeftFractionOnWin) * maxHp;
        var rest = lost > 0.5 && regen > 0 ? sim.Db.Balance.Regen.OutOfCombatDelayMs / 1000.0 + lost / regen : 0;
        var seconds = (Overhead + r.WinRate * (r.MeanSecondsOnWin + rest) + (1 - r.WinRate) * DeathSeconds) / r.WinRate;
        return (m.BaseExp * 60 / seconds, seconds);
    }

    /// <summary>The best meadow monster the character beats at least half the time, by EXP per minute.</summary>
    private static (string Monster, double Rate) BestMeadow(SimCharacter c) =>
        Meadow.Where(id => Fight(c, id).WinRate >= MinWin).Select(id => (id, ExpPerMinute(c, id).Rate)).OrderByDescending(x => x.Item2).First();

    private static void Pin(SimCharacter c, string monster, double winRate, double hpLeft, double expPerMinute, string bestMeadow, double bestRate)
    {
        var r = Fight(c, monster);
        r.WinRate.ShouldBeInRange(0.50, 0.80);
        r.WinRate.ShouldBe(winRate, 1e-9);
        r.MeanHpLeftFractionOnWin.ShouldBe(hpLeft, 1e-4);
        var (rate, _) = ExpPerMinute(c, monster);
        var (best, bestMeadowRate) = BestMeadow(c);
        best.ShouldBe(bestMeadow);
        bestMeadowRate.ShouldBe(bestRate, 0.01);
        rate.ShouldBe(expPerMinute, 0.01);
        rate.ShouldBeGreaterThan(bestMeadowRate, "moving to the Hollow must pay");
    }

    /// <summary>Thistle Burr (L8, 315 HP, def 12, atk 16: the tank): a long fight. 63.25%; 340 EXP/min vs the Rock Beetle's 298 (1.14x).</summary>
    [Fact]
    public void Level_8_wanderer_vs_the_tanky_thistle_burr()
    {
        var c = Wanderer8(StrVit);
        c.Weapon!.Id.ShouldBe("item.bone_dirk");
        c.Sheet.GetInt("max_hp").ShouldBe(110);
        Fight(c, "monster.thistle_burr").MeanSecondsOnWin.ShouldBeGreaterThan(15, "the burr is a long fight");
        Pin(c, "monster.thistle_burr", 0.6325, 0.278829, 340.06, "monster.rock_beetle", 297.80);
        Fight(Wanderer8(StrOnly), "monster.thistle_burr").WinRate.ShouldBeInRange(0.50, 0.80);
    }

    /// <summary>Dewhare (L10, 160 HP, def 4, agi 30: the fast one): a short fight. 66.5%; 436 EXP/min vs 338 (1.29x).</summary>
    [Fact]
    public void Level_10_warden_vs_the_fast_dewhare()
    {
        var c = Warden(10, StrVit);
        c.Sheet.GetInt("max_hp").ShouldBe(130);
        Fight(c, "monster.dewhare").MeanSecondsOnWin.ShouldBeLessThan(5, "the dewhare is a short, hard-hitting fight");
        Pin(c, "monster.dewhare", 0.665, 0.331689, 435.63, "monster.rock_beetle", 338.26);
        Fight(Warden(10, StrOnly), "monster.dewhare").WinRate.ShouldBeInRange(0.50, 0.80);
    }

    /// <summary>Duskwing Moth (L12, 250 HP, Dusting Scales: wind damage and a 25% stun): 60.25%; 584 EXP/min vs 479 (1.22x).</summary>
    [Fact]
    public void Level_12_warden_vs_the_duskwing_moth_and_its_stun()
    {
        var c = Warden(12, StrVit);
        c.Sheet.GetInt("max_hp").ShouldBe(150);
        var r = Fight(c, "monster.duskwing_moth");
        r.MeanSkillCasts.ShouldBeGreaterThan(0.5, "the moth really casts its skill");
        r.MeanStatusesApplied.ShouldBeGreaterThan(0.1, "and the stun lands");
        Pin(c, "monster.duskwing_moth", 0.6025, 0.263292, 583.90, "monster.rock_beetle", 478.89);
        Fight(Warden(12, StrOnly), "monster.duskwing_moth").WinRate.ShouldBeInRange(0.50, 0.80);
    }

    /// <summary>The Hollow's ladder: each monster pays more per kill than the meadow's, and the new material drops from all three.</summary>
    [Fact]
    public void The_hollow_ladder_and_drops()
    {
        var db = Content.Value.Db;
        db.Monsters.Get("monster.thistle_burr").BaseExp.ShouldBe(1000);
        db.Monsters.Get("monster.dewhare").BaseExp.ShouldBe(1100);
        db.Monsters.Get("monster.duskwing_moth").BaseExp.ShouldBe(1700);
        foreach (var id in new[] { "drop.thistle_burr", "drop.dewhare", "drop.duskwing_moth" })
            db.Drops.Get(id).Entries.ShouldContain(e => e.ItemId == "item.thistle_down");
        // The Hollow spawns exactly these three (the Gel Slimes are gone: a level 1 monster is no fight and no EXP there); the grid is generated from the authoring scene.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Game.sln"))) dir = dir.Parent;
        var grid = File.ReadAllText(Path.Combine(dir.FullName, "content", "maps", "thistle_hollow.grid.json"));
        foreach (var id in new[] { "monster.thistle_burr", "monster.dewhare", "monster.duskwing_moth" }) grid.ShouldContain("\"" + id + "\"");
        grid.ShouldNotContain("monster.gel_slime");
    }
}
