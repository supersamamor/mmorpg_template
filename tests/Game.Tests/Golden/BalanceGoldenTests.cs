using Game.Content.Loading;
using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Shared.Definitions;
using Game.Tools.Reports;

namespace Game.Tests.Golden;

/// <summary>
/// Snapshots of the balance as the real /content produces it: derived stats per sample job at levels 1/10/50/99 and damage
/// against every sample monster. A formula or content change fails these with the keys and old/new values; if intended,
/// regenerate with UPDATE_GOLDEN=1 (docs/BALANCING.md).
/// </summary>
/// <remarks>
/// <para><b>Builds.</b> Points are the ones the progression rules award (starting points + per-level awards) and are spent
/// with the real costs by <see cref="BalanceSimulator.SpendByWeight"/>: repeatedly raise the stat with the highest
/// weight / (raises + 1) that is affordable. The weights below are the per-job build; a job not listed gets equal weights.</para>
/// <para><b>Gear and levels.</b> Job level = base level capped at the job's maximum. The weapon is the job's best usable one
/// (<see cref="BalanceSimulator.PickWeapon"/>) at refine 0. Skills are used at their maximum level.</para>
/// <para><b>Damage.</b> Exact, not sampled: hit/crit chances from the resolver, normal and critical damage from two forced
/// rolls. Crits currently deal ×1 (no crit multiplier in content yet). Average is per landed hit.</para>
/// </remarks>
public class BalanceGoldenTests
{
    private static readonly int[] Levels = { 1, 10, 50, 99 };

    // job id → stat weights. Test data only: the builds are a modelling choice, not game content.
    private static readonly Dictionary<string, Dictionary<string, int>> Builds = new()
    {
        ["job.warden"] = new() { ["str"] = 4, ["vit"] = 3, ["dex"] = 2, ["agi"] = 1 },
        ["job.spellbinder"] = new() { ["int"] = 4, ["dex"] = 2, ["vit"] = 1, ["agi"] = 1 },
    };

    private static readonly Lazy<(ContentDatabase Db, BalanceSimulator Sim)> Content = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
        var result = ContentLoader.Load(Path.Combine(dir.FullName, "content"));
        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        return (result.Database!, new BalanceSimulator(result.Database!));
    });

    private static IEnumerable<JobDef> Jobs => Content.Value.Db.Jobs.All.OrderBy(j => j.Id, StringComparer.Ordinal);

    private static SimCharacter Build(string jobId, int level) =>
        Content.Value.Sim.Character(jobId, level, weights: Builds.GetValueOrDefault(jobId));

    [Fact]
    public void Derived_stats_match_the_snapshot()
    {
        var golden = new GoldenFile();
        foreach (var job in Jobs)
            foreach (var level in Levels)
            {
                var ch = Build(job.Id, level);
                var prefix = $"{job.Id}/L{level:00}/";
                golden.Add(prefix + "job_level", ch.JobLevel);
                golden.Add(prefix + "weapon", ch.Weapon?.Id);
                golden.Add(prefix + "points_left", ch.PointsLeft);
                foreach (var stat in StatListing.All(ch.Sheet)) golden.Add(prefix + "stat/" + stat.Key, stat.Value);
            }
        golden.AssertMatches("derived_stats.json");
    }

    [Fact]
    public void Damage_against_each_monster_matches_the_snapshot()
    {
        var sim = Content.Value.Sim;
        var profiler = new DamageProfiler(sim);
        var golden = new GoldenFile();
        foreach (var job in Jobs)
            foreach (var level in Levels)
            {
                var ch = Build(job.Id, level);
                foreach (var monster in sim.Db.Monsters.All.OrderBy(m => m.Id, StringComparer.Ordinal))
                    foreach (var row in profiler.Profile(ch, monster))
                    {
                        var prefix = $"{job.Id}/L{level:00}/{monster.Id}/{row.Attack}/";
                        golden.Add(prefix + "element", row.Element);
                        golden.Add(prefix + "element_multiplier", row.ElementMultiplier);
                        golden.Add(prefix + "hit_chance", row.HitChance);
                        golden.Add(prefix + "crit_chance", row.CritChance);
                        golden.Add(prefix + "min", row.Min);
                        golden.Add(prefix + "max", row.Max);
                        golden.Add(prefix + "avg", row.Avg);
                        golden.AddOrNull(prefix + "hits_to_kill", row.HitsToKill);
                        golden.AddOrNull(prefix + "swings_to_kill", row.SwingsToKill);
                    }
            }
        golden.AssertMatches("damage.json");
    }

    [Fact]
    public void Monster_time_to_kill_matches_the_snapshot()
    {
        var golden = new GoldenFile();
        foreach (var row in MonsterTtk.Compute(Content.Value.Sim))
        {
            var prefix = $"{row.MonsterId}/{row.JobId}/";
            golden.Add(prefix + "player_level", row.PlayerLevel);
            golden.Add(prefix + "monster_hp", row.MonsterHp);
            golden.Add(prefix + "avg", row.Damage.Avg);
            golden.AddOrNull(prefix + "hits_to_kill", row.Damage.HitsToKill);
            golden.AddOrNull(prefix + "ttk_s", StatsReport.TimeToKillSeconds(
                row.Damage, Content.Value.Sim.Character(row.JobId, row.PlayerLevel).Sheet.Get("aspd")));
        }
        golden.AssertMatches("monster_ttk.json");
    }

    [Fact]
    public void Builds_spend_only_points_the_rules_award()
    {
        var sim = Content.Value.Sim;
        foreach (var job in Jobs)
            foreach (var level in Levels)
            {
                var ch = Build(job.Id, level);
                ch.PointsLeft.ShouldBeGreaterThanOrEqualTo(0);
                ch.PointsLeft.ShouldBeLessThan(sim.Progression.StatRaiseCost(99), "nothing affordable should be left unspent");
                ch.Primary.Values.Sum(v => v - 1).ShouldBeGreaterThan(0);
            }
        sim.StatPointsAt(1).ShouldBe(sim.Db.Balance.Progression.StartingStatPoints);
    }

    /// <summary>Cross-checks the profiler against the hand-computed value in GoldenDamageTests (Warden +3 blade on a gel slime: 70, hit 0.95, crit 0.02).</summary>
    [Fact]
    public void Profiler_agrees_with_the_hand_computed_combat_golden()
    {
        var sim = Content.Value.Sim;
        var primary = new Dictionary<string, int> { ["str"] = 18, ["agi"] = 8, ["vit"] = 14, ["int"] = 1, ["dex"] = 10, ["luk"] = 4 };
        var warden = sim.Player("job.warden", 20, 12, primary, 0, sim.Db.Items.Get("item.iron_blade"), refine: 3);

        var row = new DamageProfiler(sim).Row(warden, sim.Db.Monsters.Get("monster.gel_slime"), "auto", null, 0);

        row.HitChance.ShouldBe(0.95, 1e-9);
        row.CritChance.ShouldBe(0.02, 1e-9);
        row.Min.ShouldBe(70);
        row.Max.ShouldBe(105);          // crit: floor(70 × 1.5)
        row.Avg.ShouldBe(70.7, 1e-9);   // 0.98 × 70 + 0.02 × 105
        row.HitsToKill.ShouldBe(1);
    }

    [Fact]
    public void Exact_builds_must_be_payable()
    {
        var sim = Content.Value.Sim;
        Should.Throw<ArgumentException>(() => sim.Character("job.warden", 1, new Dictionary<string, int> { ["str"] = 99 }));
        Should.Throw<ArgumentException>(() => sim.Character("job.warden", 10, new Dictionary<string, int> { ["bogus"] = 5 }));
        sim.Character("job.warden", 10, new Dictionary<string, int> { ["str"] = 6 }).Primary["str"].ShouldBe(6);
    }
}
