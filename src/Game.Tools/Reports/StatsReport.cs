using Game.Content.Simulation;
using Game.Shared.Definitions;

namespace Game.Tools.Reports;

/// <summary>Builds the tables of the <c>stats</c> command from the simulation helpers.</summary>
internal static class StatsReport
{
    public static IReadOnlyList<ReportTable> ForCharacter(BalanceSimulator sim, SimCharacter ch, IReadOnlyList<MonsterDef> monsters)
    {
        var summary = new ReportTable("character", "field", "value")
            .Add("job", ch.JobId)
            .Add("base_level", ch.BaseLevel)
            .Add("job_level", ch.JobLevel)
            .Add("weapon", ch.Weapon?.Id)
            .Add("refine", ch.Refine)
            .Add("stat_points_left", ch.PointsLeft);

        var stats = new ReportTable("stats", "stat", "value");
        foreach (var pair in StatListing.All(ch.Sheet)) stats.Add(pair.Key, pair.Value);

        var tables = new List<ReportTable> { summary, stats };
        if (monsters.Count == 0) return tables;

        var damage = new ReportTable("damage", "monster", "attack", "level", "element", "elem_x", "hit_pct", "crit_pct",
            "min", "max", "avg", "hits_to_kill", "swings_to_kill", "ttk_s");
        var profiler = new DamageProfiler(sim);
        foreach (var monster in monsters)
            foreach (var r in profiler.Profile(ch, monster))
                damage.Add(monster.Id, r.Attack, r.SkillLevel, r.Element, r.ElementMultiplier, r.HitChance * 100, r.CritChance * 100,
                    r.Min, r.Max, r.Avg, r.HitsToKill, r.SwingsToKill, TimeToKillSeconds(r, ch.Sheet.Get("aspd")));
        tables.Add(damage);
        return tables;
    }

    /// <summary>
    /// Expected seconds to kill with the weapon auto-attack: swings to kill divided by <paramref name="aspd"/> (attacks per second).
    /// Null for skills (their cast time and cooldown are not modelled here) and when the attack never kills.
    /// </summary>
    public static double? TimeToKillSeconds(DamageRow row, double aspd) =>
        row.Attack == "auto" && row.SwingsToKill is { } swings && aspd > 0 ? swings / aspd : null;

    /// <summary>The <c>stats --duel</c> table: one matchup, expected values and the seeded simulation.</summary>
    public static IReadOnlyList<ReportTable> ForDuel(SimCharacter ch, MonsterDef monster, DuelResult r)
    {
        var table = new ReportTable("duel", "field", "value")
            .Add("player", ch.JobId + " L" + ch.BaseLevel + (ch.Weapon == null ? " unarmed" : " " + ch.Weapon.Id) + ", " + ch.PointsLeft + " points unspent")
            .Add("monster", monster.Id)
            .Add("player_hp", r.PlayerHp)
            .Add("monster_hp", r.MonsterHp)
            .Add("player_hit_pct", r.PlayerHitChance * 100)
            .Add("player_avg_hit", r.PlayerAvgHit)
            .Add("player_aspd", r.PlayerAspd)
            .Add("monster_hit_pct", r.MonsterHitChance * 100)
            .Add("monster_avg_hit", r.MonsterAvgHit)
            .Add("monster_aspd", r.MonsterAspd)
            .Add("expected_swings", r.ExpectedSwings)
            .Add("expected_seconds", r.ExpectedSeconds)
            .Add("expected_hp_left_pct", r.ExpectedHpLeftFraction * 100)
            .Add("sim_fights", r.Fights)
            .Add("sim_win_rate_pct", r.WinRate * 100)
            .Add("sim_mean_swings_on_win", r.MeanSwingsOnWin)
            .Add("sim_mean_hp_left_pct_on_win", r.MeanHpLeftFractionOnWin * 100);
        return new[] { table };
    }

    public static IReadOnlyList<ReportTable> ForMonsters(BalanceSimulator sim, IReadOnlyList<MonsterTtkRow> rows)
    {
        var table = new ReportTable("monsters", "monster", "monster_level", "max_hp", "job", "player_level", "avg", "hit_pct",
            "hits_to_kill", "swings_to_kill", "ttk_s");
        foreach (var r in rows)
            table.Add(r.MonsterId, r.MonsterLevel, r.MonsterHp, r.JobId, r.PlayerLevel, r.Damage.Avg, r.Damage.HitChance * 100,
                r.Damage.HitsToKill, r.Damage.SwingsToKill,
                TimeToKillSeconds(r.Damage, sim.Character(r.JobId, r.PlayerLevel).Sheet.Get("aspd")));
        return new[] { table };
    }
}
