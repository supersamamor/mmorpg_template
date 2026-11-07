using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Random;
using Game.Shared.Stats;

namespace Game.Content.Simulation;

/// <summary>Outcome of one matchup: the expected-value model and a seeded simulation of many fights.</summary>
/// <param name="PlayerHp">Player max HP.</param>
/// <param name="MonsterHp">Monster max HP.</param>
/// <param name="PlayerAspd">Player attacks per second.</param>
/// <param name="MonsterAspd">Monster attacks per second.</param>
/// <param name="PlayerHitChance">Chance a player swing lands.</param>
/// <param name="PlayerAvgHit">Expected damage of a landed player hit (crit-weighted).</param>
/// <param name="MonsterHitChance">Chance a monster swing lands.</param>
/// <param name="MonsterAvgHit">Expected damage of a landed monster hit (crit-weighted).</param>
/// <param name="ExpectedSwings">Expected player swings to kill the monster: hits to kill / hit chance.</param>
/// <param name="ExpectedSeconds">ExpectedSwings / player aspd.</param>
/// <param name="ExpectedHpLeftFraction">1 − expected damage taken during that time / player max HP, floored at 0.</param>
/// <param name="Fights">Fights simulated.</param>
/// <param name="WinRate">Share of simulated fights where the monster died first.</param>
/// <param name="MeanSwingsOnWin">Mean player swings in the won fights (0 when none).</param>
/// <param name="MeanHpLeftFractionOnWin">Mean player HP fraction left in the won fights (0 when none).</param>
public sealed record DuelResult(int PlayerHp, int MonsterHp, double PlayerAspd, double MonsterAspd, double PlayerHitChance,
    double PlayerAvgHit, double MonsterHitChance, double MonsterAvgHit, double ExpectedSwings, double ExpectedSeconds,
    double ExpectedHpLeftFraction, int Fights, double WinRate, double MeanSwingsOnWin, double MeanHpLeftFractionOnWin);

/// <summary>
/// One player against one monster, no weapon skills: auto-attacks only, both sides at their <c>aspd</c> (the server's Ready() cadence, ignoring
/// tick rounding), no regeneration, no movement. The expected-value part uses the resolver's exact hit/crit chances; the simulation draws
/// every swing through the real <see cref="CombatResolver"/> with a seeded <see cref="Pcg32Random"/>. Both sides swing at t = 0, the player first.
/// </summary>
public static class Duel
{
    private const double Epsilon = 1e-9;

    public static DuelResult Run(BalanceSimulator sim, SimCharacter player, MonsterDef monster, int fights, ulong seed)
    {
        var monsterSheet = sim.Monster(monster);
        var playerAttack = Profile(sim, player.Sheet, monsterSheet, player.Weapon?.Equip?.WeaponAtk ?? 0, player.Refine,
            player.Weapon?.Equip?.Element, monster.Element, monster.Size, monster.Race);
        var monsterAttack = Profile(sim, monsterSheet, player.Sheet, 0, 0, monster.Element, null, null, null);

        var playerHp = player.Sheet.GetInt("max_hp");
        var monsterHp = monsterSheet.GetInt("max_hp");
        var pAspd = player.Sheet.Get("aspd");
        var mAspd = monsterSheet.Get("aspd");

        var hits = playerAttack.Avg > 0 ? (int)Math.Ceiling(monsterHp / playerAttack.Avg - Epsilon) : 0;
        var swings = playerAttack.Avg > 0 && playerAttack.Hit > 0 ? hits / playerAttack.Hit : double.PositiveInfinity;
        var seconds = pAspd > 0 ? swings / pAspd : double.PositiveInfinity;
        var taken = seconds * mAspd * monsterAttack.Hit * monsterAttack.Avg;
        var hpLeft = Math.Max(0, 1 - taken / playerHp);

        var rng = new Pcg32Random(seed);
        var resolver = new CombatResolver(sim.Combat, rng);
        var wins = 0;
        long winSwings = 0;
        double winHp = 0;
        for (var i = 0; i < fights; i++)
        {
            var pHp = playerHp;
            var mHp = monsterHp;
            var pNext = 0.0;
            var mNext = 0.0;
            var pSwings = 0;
            while (pHp > 0 && mHp > 0)
            {
                if (pNext <= mNext)
                {
                    pSwings++;
                    mHp -= resolver.Resolve(Context(player.Sheet, monsterSheet, player.Weapon?.Equip?.WeaponAtk ?? 0, player.Refine,
                        player.Weapon?.Equip?.Element, monster.Element, monster.Size, monster.Race)).Damage;
                    pNext += 1.0 / pAspd;
                }
                else
                {
                    pHp -= resolver.Resolve(Context(monsterSheet, player.Sheet, 0, 0, monster.Element, null, null, null)).Damage;
                    mNext += 1.0 / mAspd;
                }
            }
            if (mHp <= 0)
            {
                wins++;
                winSwings += pSwings;
                winHp += Math.Max(0, pHp) / (double)playerHp;
            }
        }

        return new DuelResult(playerHp, monsterHp, pAspd, mAspd, playerAttack.Hit, playerAttack.Avg, monsterAttack.Hit, monsterAttack.Avg,
            swings, seconds, hpLeft, fights, fights > 0 ? wins / (double)fights : 0,
            wins > 0 ? winSwings / (double)wins : 0, wins > 0 ? winHp / wins : 0);
    }

    private static AttackContext Context(StatSheet attacker, StatSheet defender, int weaponAtk, int refine, string? attackerElement,
        string? defenderElement, string? defenderSize, string? defenderRace) => new()
    {
        Attacker = attacker,
        Defender = defender,
        WeaponAtk = weaponAtk,
        Refine = refine,
        AttackerElement = attackerElement,
        DefenderElement = defenderElement,
        DefenderSize = defenderSize,
        DefenderRace = defenderRace,
    };

    /// <summary>Exact hit chance and crit-weighted average damage of a landed hit (rolls forced, as in <see cref="DamageProfiler"/>).</summary>
    private static (double Hit, double Avg) Profile(BalanceSimulator sim, StatSheet attacker, StatSheet defender, int weaponAtk, int refine,
        string? attackerElement, string? defenderElement, string? defenderSize, string? defenderRace)
    {
        var breakdown = new DamageBreakdown();
        AttackContext Ctx()
        {
            var c = Context(attacker, defender, weaponAtk, refine, attackerElement, defenderElement, defenderSize, defenderRace);
            c.Breakdown = breakdown;
            return c;
        }
        var crit = new CombatResolver(sim.Combat, new Fixed(0, 0)).Resolve(Ctx());
        var hit = breakdown.TryGet(BreakdownSteps.HitChance, out var h) ? h : 1;
        var critChance = breakdown.TryGet(BreakdownSteps.CritChance, out var c2) ? c2 : 0;
        var normal = new CombatResolver(sim.Combat, new Fixed(0, 1 - Epsilon)).Resolve(Ctx());
        var avg = critChance > 0 ? (1 - critChance) * normal.Damage + critChance * crit.Damage : normal.Damage;
        return (hit, avg);
    }

    private sealed class Fixed : IRandom
    {
        private readonly double[] _values;
        private int _next;

        public Fixed(params double[] values) => _values = values;

        public double NextDouble() => _values[Math.Min(_next++, _values.Length - 1)];

        public uint NextUInt() => throw new NotSupportedException();

        public int Next(int maxExclusive) => throw new NotSupportedException();
    }
}
