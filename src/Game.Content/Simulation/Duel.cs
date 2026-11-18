using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Formulas;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
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
/// <param name="MeanSkillCasts">Mean monster skill casts per simulated fight (T-501).</param>
/// <param name="MeanSkillDamage">Mean HP the player lost to monster skill hits per fight.</param>
/// <param name="MeanStatusDamage">Mean HP the player lost to status ticks (poison) per fight.</param>
/// <param name="MeanStatusesApplied">Mean statuses the player received (started, stacked or refreshed) per fight.</param>
public sealed record DuelResult(int PlayerHp, int MonsterHp, double PlayerAspd, double MonsterAspd, double PlayerHitChance,
    double PlayerAvgHit, double MonsterHitChance, double MonsterAvgHit, double ExpectedSwings, double ExpectedSeconds,
    double ExpectedHpLeftFraction, int Fights, double WinRate, double MeanSwingsOnWin, double MeanHpLeftFractionOnWin,
    double MeanSkillCasts = 0, double MeanSkillDamage = 0, double MeanStatusDamage = 0, double MeanStatusesApplied = 0);

/// <summary>
/// One player against one monster, no player skills: both sides auto-attack at their <c>aspd</c> (the server's Ready() cadence, ignoring tick
/// rounding), no regeneration, no movement. The expected-value part models auto-attacks only, with the resolver's exact hit/crit chances. The
/// simulation draws every swing through the real <see cref="CombatResolver"/> with a seeded <see cref="Pcg32Random"/>, and models the monster's
/// <c>ai.skills</c> as the server's AI does (T-501, ADR-002 row 118): when its attack is due, the first skill off cooldown whose chance roll wins is
/// cast instead (cast time = <c>cast_time_ms</c> × <c>cast_time_mod</c>, cooldown from the end of the cast), and its effects and the statuses they
/// apply (poison ticks, its DEF modifier) run through the real <see cref="EffectRunner"/>. At one moment, in order: status ticks, the player's
/// swing, a cast that ends, the monster's action. Both sides act at t = 0, the player first. The server resolves a cast one tick after it ends;
/// the duel ignores that rounding too.
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
        var world = new DuelWorld(player.Sheet, player.Weapon?.Equip?.WeaponAtk ?? 0, player.Refine, player.Weapon?.Equip?.Element, monsterSheet, monster);
        var runner = new EffectRunner(sim.Combat, sim.Effects, world, rng);
        var skills = MonsterSkills(sim.Effects, monster, monsterSheet);
        var ready = new double[skills.Length];
        const long P = DuelWorld.PlayerId, M = DuelWorld.MonsterId;
        var wins = 0;
        long winSwings = 0, casts = 0, skillDamage = 0, statusDamage = 0, statuses = 0;
        double winHp = 0;
        for (var i = 0; i < fights; i++)
        {
            world.Reset(playerHp, monsterHp);
            Array.Clear(ready);
            var pNext = 0.0;
            var mNext = 0.0;
            var castEnd = double.PositiveInfinity;
            var casting = -1;
            var pSwings = 0;
            while (world.Hp(P) > 0 && world.Hp(M) > 0)
            {
                var statusMs = Math.Min(world.NextStatusEvent(P), world.NextStatusEvent(M));
                var statusAt = statusMs == long.MaxValue ? double.PositiveInfinity : statusMs / 1000.0;
                var t = Math.Min(Math.Min(pNext, castEnd), Math.Min(casting < 0 ? mNext : double.PositiveInfinity, statusAt));
                world.NowMs = (long)Math.Floor(t * 1000 + Epsilon);
                if (statusAt <= t)
                {
                    runner.TickStatuses(P);
                    runner.TickStatuses(M);
                }
                else if (pNext <= t)
                {
                    pSwings++;
                    world.Hit(M, resolver.Resolve(Context(player.Sheet, monsterSheet, player.Weapon?.Equip?.WeaponAtk ?? 0, player.Refine,
                        player.Weapon?.Equip?.Element, monster.Element, monster.Size, monster.Race)).Damage);
                    pNext += 1.0 / pAspd;
                }
                else if (castEnd <= t)
                {
                    var skill = skills[casting];
                    runner.Run(skill.Skill.Effects, world.Actor(M), skill.Self ? M : P, null, skill.Level);
                    ready[casting] = castEnd + skill.CooldownS;
                    castEnd = double.PositiveInfinity;
                    casting = -1;
                    if (mNext < t) mNext = t;   // as the AI: an attack that fell due during the cast comes at once
                }
                else
                {
                    mNext += 1.0 / mAspd;
                    casting = PickSkill(skills, ready, t, rng);
                    if (casting >= 0)
                    {
                        casts++;
                        castEnd = t + skills[casting].CastS;
                    }
                    else
                    {
                        world.Hit(P, resolver.Resolve(Context(monsterSheet, player.Sheet, 0, 0, monster.Element, null, null, null)).Damage);
                    }
                }
            }
            if (world.Hp(M) <= 0)
            {
                wins++;
                winSwings += pSwings;
                winHp += world.Hp(P) / (double)playerHp;
            }
            skillDamage += world.SkillDamage;
            statusDamage += world.StatusDamage;
            statuses += world.StatusesApplied;
            runner.EndAll(P, StatusEndReasons.Died);   // takes the statuses' modifiers off the shared player sheet for the next fight
            runner.EndAll(M, StatusEndReasons.Died);
        }

        double PerFight(long total) => fights > 0 ? total / (double)fights : 0;
        return new DuelResult(playerHp, monsterHp, pAspd, mAspd, playerAttack.Hit, playerAttack.Avg, monsterAttack.Hit, monsterAttack.Avg,
            swings, seconds, hpLeft, fights, fights > 0 ? wins / (double)fights : 0,
            wins > 0 ? winSwings / (double)wins : 0, wins > 0 ? winHp / wins : 0,
            PerFight(casts), PerFight(skillDamage), PerFight(statusDamage), PerFight(statuses));
    }

    private sealed record DuelSkill(CompiledSkill Skill, int Level, double Chance, double CastS, double CooldownS, bool Self);

    /// <summary>The monster's <c>ai.skills</c> with their cast time and cooldown worked out once (they depend on the skill level and the monster's stats only).</summary>
    private static DuelSkill[] MonsterSkills(EffectLibrary effects, MonsterDef monster, Game.Shared.Stats.StatSheet sheet)
    {
        var list = new List<DuelSkill>();
        var f = new FormulaContext();
        foreach (var entry in monster.Ai.Skills)
        {
            if (!effects.TryGetSkill(entry.SkillId, out var skill)) continue;
            var level = Math.Max(1, entry.Level);
            f.Clear();
            sheet.WriteTo(f);
            f[FormulaVariables.SkillLevel] = level;
            var cast = Math.Max(0, Math.Floor(skill.CastTimeMs.Evaluate(f) * sheet.Get(DerivedStats.CastTimeMod) + Epsilon));
            var cooldown = Math.Max(0, Math.Floor(skill.CooldownMs.Evaluate(f) + Epsilon));
            var self = !skill.Def.Target.Ground && skill.Def.Target.Range <= 0;
            list.Add(new DuelSkill(skill, level, entry.Chance, cast / 1000, cooldown / 1000, self));
        }
        return list.ToArray();
    }

    /// <summary>As the AI: the first skill off cooldown whose chance roll wins (a roll only for a chance strictly between 0 and 1), or -1.</summary>
    private static int PickSkill(DuelSkill[] skills, double[] ready, double t, IRandom rng)
    {
        for (var i = 0; i < skills.Length; i++)
        {
            if (t + Epsilon < ready[i]) continue;
            var chance = skills[i].Chance;
            if (!(chance > 0) || (chance < 1 && rng.NextDouble() >= chance)) continue;
            return i;
        }
        return -1;
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
