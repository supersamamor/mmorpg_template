using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Random;
using Game.Shared.Stats;

namespace Game.Content.Simulation;

/// <summary>One attack of a character against one monster, summarised exactly (no sampling).</summary>
/// <param name="Attack">"auto" for the weapon auto-attack, else the skill id.</param>
/// <param name="SkillLevel">Skill level used (0 for auto).</param>
/// <param name="Element">Attack element id.</param>
/// <param name="ElementMultiplier">Element table multiplier against the monster.</param>
/// <param name="HitChance">Chance the attack lands (1 for magic).</param>
/// <param name="CritChance">Chance a landed hit is critical (0 for magic).</param>
/// <param name="Min">Lowest damage of a landed hit.</param>
/// <param name="Max">Highest damage of a landed hit.</param>
/// <param name="Avg">Expected damage of a landed hit (crit-weighted).</param>
/// <param name="HitsToKill">Landed hits of average damage needed to bring the monster's max HP to 0; null if the attack never kills.</param>
/// <param name="SwingsToKill">HitsToKill divided by HitChance: expected attacks including misses; null if it never kills.</param>
public sealed record DamageRow(string Attack, int SkillLevel, string Element, double ElementMultiplier, double HitChance,
    double CritChance, int Min, int Max, double Avg, int? HitsToKill, double? SwingsToKill);

/// <summary>
/// Computes <see cref="DamageRow"/>s with the real <see cref="CombatResolver"/>. It is exact, not a simulation: the hit and crit
/// chances come from the resolver's breakdown, and the two possible damages (normal and crit) from resolving once with the
/// crit roll forced below the chance and once above it. Average = (1 − crit) × normal + crit × critical.
/// </summary>
public sealed class DamageProfiler
{
    private const double Epsilon = 1e-9;

    private readonly BalanceSimulator _sim;

    public DamageProfiler(BalanceSimulator sim) => _sim = sim ?? throw new ArgumentNullException(nameof(sim));

    /// <summary>The auto-attack row followed by one row per damaging skill in the job's tree (and its parents'), at the skill's maximum level.</summary>
    public IReadOnlyList<DamageRow> Profile(SimCharacter character, MonsterDef monster)
    {
        var rows = new List<DamageRow> { Row(character, monster, "auto", null, 0) };
        foreach (var (skill, level) in DamagingSkills(character.JobId))
        {
            var effect = CompiledDamageEffect.Compile(Flatten(skill.Effects).OfType<DamageEffect>().First());
            rows.Add(Row(character, monster, skill.Id, effect, level));
        }
        return rows;
    }

    /// <summary>Skills with a damage effect reachable from the job (its own list, then its parents'), with their maximum usable level.</summary>
    public IEnumerable<(SkillDef Skill, int Level)> DamagingSkills(string jobId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? id = jobId;
        for (var guard = 0; id != null && guard < 64; guard++)
        {
            var job = _sim.Db.Jobs.Get(id);
            foreach (var entry in job.Skills)
            {
                if (!seen.Add(entry.SkillId)) continue;
                var skill = _sim.Db.Skills.Get(entry.SkillId);
                if (!Flatten(skill.Effects).OfType<DamageEffect>().Any()) continue;
                yield return (skill, Math.Min(skill.MaxLevel, entry.MaxLevel ?? skill.MaxLevel));
            }
            id = job.ParentJobId;
        }
    }

    /// <summary>One attack: <paramref name="effect"/> null means the weapon auto-attack.</summary>
    public DamageRow Row(SimCharacter character, MonsterDef monster, string attack, CompiledDamageEffect? effect, int skillLevel)
    {
        var defender = _sim.Monster(monster);
        var weapon = character.Weapon?.Equip;
        var breakdown = new DamageBreakdown();
        AttackContext Context() => new()
        {
            Attacker = character.Sheet,
            Defender = defender,
            WeaponAtk = weapon?.WeaponAtk ?? 0,
            Refine = character.Refine,
            AttackerElement = weapon?.Element,
            DefenderElement = monster.Element,
            DefenderSize = monster.Size,
            DefenderRace = monster.Race,
            Effect = effect,
            SkillLevel = skillLevel,
            Breakdown = breakdown,
        };

        // Roll 0 always beats a chance above 0; a crit roll of 0 crits whenever the crit chance is above 0.
        var crit = new CombatResolver(_sim.Combat, new FixedRandom(0, 0)).Resolve(Context());
        var hitChance = breakdown.TryGet(BreakdownSteps.HitChance, out var h) ? h : 1;
        var critChance = breakdown.TryGet(BreakdownSteps.CritChance, out var c) ? c : 0;
        var normal = new CombatResolver(_sim.Combat, new FixedRandom(0, 1 - Epsilon)).Resolve(Context());
        if (!normal.Hit) return new DamageRow(attack, skillLevel, normal.Element, normal.Multiplier, hitChance, critChance, 0, 0, 0, null, null);

        var outcomes = critChance > 0 ? new[] { normal.Damage, crit.Damage } : new[] { normal.Damage };
        var avg = critChance > 0 ? (1 - critChance) * normal.Damage + critChance * crit.Damage : normal.Damage;
        var hp = defender.GetInt("max_hp");
        int? hits = avg > 0 ? (int)Math.Ceiling(hp / avg - Epsilon) : null;
        double? swings = hits == null || hitChance <= 0 ? null : hits.Value / hitChance;
        return new DamageRow(attack, skillLevel, normal.Element, normal.Multiplier, hitChance, critChance,
            outcomes.Min(), outcomes.Max(), avg, hits, swings);
    }

    private static IEnumerable<EffectDef> Flatten(IEnumerable<EffectDef> effects) =>
        effects.SelectMany(e => e is AreaEffect area ? Flatten(area.Effects) : new[] { e });

    private sealed class FixedRandom : IRandom
    {
        private readonly double[] _values;
        private int _next;

        public FixedRandom(params double[] values) => _values = values;

        public double NextDouble() => _values[Math.Min(_next++, _values.Length - 1)];

        public uint NextUInt() => throw new NotSupportedException();

        public int Next(int maxExclusive) => throw new NotSupportedException();
    }
}
