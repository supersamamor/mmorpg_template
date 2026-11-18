using Game.Shared.Definitions;

namespace Game.Content.Simulation;

/// <summary>One base level of <see cref="LevelingEstimate"/>: what the character kills and how long the level takes.</summary>
/// <param name="Level">Base level the character is at (it needs <paramref name="ExpNeeded"/> to reach the next).</param>
/// <param name="MonsterId">The monster it farms at this level.</param>
/// <param name="BaseExp">EXP per kill (the monster's <c>base_exp</c>).</param>
/// <param name="SecondsPerKill">Walk + loot, plus the mean length of a won fight, plus the time to regenerate the HP it cost.</param>
/// <param name="KillsPerMinute">60 / SecondsPerKill.</param>
/// <param name="ExpNeeded">EXP from this level to the next (the base curve).</param>
/// <param name="Minutes">ExpNeeded / (BaseExp × KillsPerMinute).</param>
public sealed record LevelingRow(int Level, string MonsterId, long BaseExp, double SecondsPerKill, double KillsPerMinute, long ExpNeeded, double Minutes);

/// <summary>
/// T-404: how long a new character takes to reach a level by killing monsters, from data only. At each base level the character (a Wanderer wearing the
/// best weapon it may use, points spent by <c>weights</c>, skills by <c>skillsAt</c>) farms the monster that gives the most EXP per second among those it beats in at least
/// <c>minWinRate</c> of the seeded duels and whose level is at most <c>maxLevelGap</c> above the character's. One kill costs <c>overheadSeconds</c> (walking to the next monster and picking up the drop), the mean length
/// of a won fight, and the time natural regeneration needs to give back the HP the fight cost: the out-of-combat delay plus lost HP / <c>hp_regen</c>
/// (draughts and Second Wind are not counted, so this is the slow end). A lost fight costs <c>deathSeconds</c> (respawn and walk back) and no EXP, so a
/// kill costs (overhead + p × (fight + rest) + (1 − p) × deathSeconds) / p seconds at win rate p. The kills per minute are therefore the arithmetic,
/// not a measurement.
/// </summary>
public static class LevelingEstimate
{
    public static IReadOnlyList<LevelingRow> Run(BalanceSimulator sim, string jobId, IReadOnlyDictionary<string, int> weights, Func<int, IReadOnlyDictionary<string, int>> skillsAt, int toLevel,
        double overheadSeconds, double minWinRate, double deathSeconds, int maxLevelGap, int fights, ulong seed)
    {
        var table = sim.Db.ExpTables.Get(sim.Db.Balance.Progression.BaseExpTableId);
        var restDelay = sim.Db.Balance.Regen.OutOfCombatDelayMs / 1000.0;
        var rows = new List<LevelingRow>();
        for (var level = 1; level < toLevel; level++)
        {
            var character = sim.Character(jobId, level, weights: weights, skills: skillsAt(level));
            var regen = character.Sheet.Get("hp_regen");
            var maxHp = character.Sheet.GetInt("max_hp");
            LevelingRow? best = null;
            var bestRate = 0.0;
            foreach (var monster in sim.Db.Monsters.All.OrderBy(m => m.Id, StringComparer.Ordinal))
            {
                if (monster.Level > level + maxLevelGap) continue;   // a player does not walk up to a monster far above their level
                var r = Duel.Run(sim, character, monster, fights, seed);
                if (r.WinRate < minWinRate || monster.BaseExp <= 0) continue;
                var lost = (1 - r.MeanHpLeftFractionOnWin) * maxHp;
                var rest = lost > 0.5 && regen > 0 ? restDelay + lost / regen : 0;
                // One attempt: the walk, then a won fight and its rest (chance p) or a lost one and the trip back from the respawn (1 - p).
                var p = r.WinRate;
                var seconds = (overheadSeconds + p * (r.MeanSecondsOnWin + rest) + (1 - p) * deathSeconds) / p;   // per kill
                var rate = monster.BaseExp / seconds;
                if (rate <= bestRate) continue;
                bestRate = rate;
                var needed = table.Exp[level - 1];
                best = new LevelingRow(level, monster.Id, monster.BaseExp, seconds, 60 / seconds, needed, needed / (monster.BaseExp * 60 / seconds));
            }
            if (best == null) throw new InvalidOperationException("No monster is winnable at level " + level + ".");
            rows.Add(best);
        }
        return rows;
    }
}
