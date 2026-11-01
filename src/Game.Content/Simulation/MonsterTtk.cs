namespace Game.Content.Simulation;

/// <summary>Time-to-kill of one monster by one job's auto-attack at the monster's expected player level.</summary>
/// <param name="MonsterId">Monster id.</param>
/// <param name="MonsterLevel">Monster level.</param>
/// <param name="MonsterHp">Monster max HP.</param>
/// <param name="JobId">Job id.</param>
/// <param name="PlayerLevel">Expected player level: the monster's level, raised to the job's minimum base level.</param>
/// <param name="Damage">The auto-attack against the monster.</param>
public sealed record MonsterTtkRow(string MonsterId, int MonsterLevel, int MonsterHp, string JobId, int PlayerLevel, DamageRow Damage);

/// <summary>Builds the <c>stats --monsters</c> report.</summary>
public static class MonsterTtk
{
    /// <summary>
    /// Every monster against every job, ordered by monster id then job id. The expected player level comes from data:
    /// <c>max(monster.level, job.requirements.min_base_level)</c>. Stat points are spent by <paramref name="weights"/>
    /// (equal when null) with the real costs; the weapon is <see cref="BalanceSimulator.PickWeapon"/>'s choice.
    /// </summary>
    public static IReadOnlyList<MonsterTtkRow> Compute(BalanceSimulator sim, IReadOnlyDictionary<string, int>? weights = null)
    {
        var profiler = new DamageProfiler(sim);
        var rows = new List<MonsterTtkRow>();
        foreach (var monster in sim.Db.Monsters.All.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var hp = sim.Monster(monster).GetInt("max_hp");
            foreach (var job in sim.Db.Jobs.All.OrderBy(j => j.Id, StringComparer.Ordinal))
            {
                var level = Math.Max(monster.Level, job.Requirements.MinBaseLevel);
                var character = sim.Character(job.Id, level, weights: weights);
                rows.Add(new MonsterTtkRow(monster.Id, monster.Level, hp, job.Id, level, profiler.Row(character, monster, "auto", null, 0)));
            }
        }
        return rows;
    }
}
