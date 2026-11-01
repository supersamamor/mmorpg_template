using Game.Content.Registry;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Progression;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;

namespace Game.Content.Simulation;

/// <summary>A character as the simulator builds it: levels, spent stat points, weapon and the resulting stat sheet.</summary>
/// <param name="JobId">Job id.</param>
/// <param name="BaseLevel">Base level.</param>
/// <param name="JobLevel">Job level (see <see cref="BalanceSimulator.JobLevelFor"/>).</param>
/// <param name="Primary">Primary stat values after spending points, keyed by stat id.</param>
/// <param name="PointsLeft">Stat points not spent.</param>
/// <param name="Weapon">The equipped weapon, or null.</param>
/// <param name="Refine">Weapon refine level.</param>
/// <param name="Sheet">Stats with job bonuses and weapon modifiers applied.</param>
public sealed record SimCharacter(string JobId, int BaseLevel, int JobLevel, IReadOnlyDictionary<string, int> Primary,
    int PointsLeft, ItemDef? Weapon, int Refine, StatSheet Sheet);

/// <summary>
/// Host-side helpers that turn content into stat sheets: players (job + level + stat build + equipment) and monsters.
/// Shared by the server, the tools and the tests so there is one copy. Not thread-safe (it owns progression rules).
/// </summary>
public sealed class BalanceSimulator
{
    private readonly DerivedStatFormulas _formulas;

    public BalanceSimulator(ContentDatabase db)
    {
        Db = db ?? throw new ArgumentNullException(nameof(db));
        _formulas = DerivedStatFormulas.Compile(db.Formulas);
        Progression = new ProgressionRules(db.Balance, new ContentProgression(db));
        Combat = CombatRules.Compile(db.Formulas, db.Elements, db.Balance.Combat);
    }

    public ContentDatabase Db { get; }

    public ProgressionRules Progression { get; }

    public CombatRules Combat { get; }

    /// <summary>Job level paired with a base level: the same number, capped at the job's own maximum.</summary>
    public int JobLevelFor(string jobId, int baseLevel) =>
        Math.Max(1, Math.Min(baseLevel, Progression.MaxJobLevel(jobId)));

    /// <summary>Total stat points a character has earned at <paramref name="baseLevel"/>: the starting points plus each level's award.</summary>
    public int StatPointsAt(int baseLevel)
    {
        var points = Db.Balance.Progression.StartingStatPoints;
        for (var level = 2; level <= baseLevel; level++) points += Progression.StatPointsForBaseLevel(level);
        return points;
    }

    /// <summary>
    /// Spends the points earned by <paramref name="baseLevel"/> by weight, using the real stat point costs: each step raises the
    /// stat with the highest weight / (raises so far + 1) that is still affordable (ties: vocabulary order), until nothing is.
    /// </summary>
    public (IReadOnlyDictionary<string, int> Stats, int PointsLeft) SpendByWeight(string jobId, int baseLevel,
        IReadOnlyDictionary<string, int> weights)
    {
        var state = Progression.NewCharacter(jobId);
        state.StatPoints = StatPointsAt(baseLevel);
        var raises = PrimaryStats.All.Ordered.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        while (true)
        {
            string? best = null;
            foreach (var stat in PrimaryStats.All.Ordered)
            {
                if (!weights.TryGetValue(stat, out var w) || w <= 0) continue;
                if (state.StatPoints < Progression.StatRaiseCost(state.StatValue(stat))) continue;
                if (state.StatValue(stat) >= Db.Balance.Progression.MaxStatValue) continue;
                if (best == null || w * (raises[best] + 1) > weights[best] * (raises[stat] + 1)) best = stat;
            }
            if (best == null) break;
            Progression.RaiseStat(state, best);
            raises[best]++;
        }
        return (state.Stats, state.StatPoints);
    }

    /// <summary>
    /// Builds the stats for explicit final primary values (unlisted stats keep the starting value), checking that the
    /// points earned by <paramref name="baseLevel"/> pay for them with the real costs. Throws <see cref="ArgumentException"/> otherwise.
    /// </summary>
    public (IReadOnlyDictionary<string, int> Stats, int PointsLeft) SpendExact(string jobId, int baseLevel,
        IReadOnlyDictionary<string, int> targets)
    {
        var state = Progression.NewCharacter(jobId);
        state.StatPoints = StatPointsAt(baseLevel);
        foreach (var stat in targets.Keys)
            if (!PrimaryStats.All.Contains(stat)) throw new ArgumentException("Unknown stat '" + stat + "'.");
        foreach (var stat in PrimaryStats.All.Ordered)
        {
            if (!targets.TryGetValue(stat, out var target)) continue;
            var amount = target - state.StatValue(stat);
            if (amount <= 0) continue;
            var result = Progression.RaiseStat(state, stat, amount);
            if (!result.Success)
                throw new ArgumentException("Cannot raise " + stat + " to " + target + " at level " + baseLevel + ": " + result.Error
                    + " (" + state.StatPoints + " points left before this stat).");
        }
        return (state.Stats, state.StatPoints);
    }

    /// <summary>
    /// The best weapon for a job at a level, chosen from data: a weapon whose type the job allows, whose job list is empty or
    /// has the job, and whose minimum level is reached; highest weapon attack wins, then id. Null when none qualifies (unarmed).
    /// </summary>
    public ItemDef? PickWeapon(string jobId, int baseLevel)
    {
        var job = Db.Jobs.Get(jobId);
        return Db.Items.All
            .Where(i => i.Equip is { } e && e.Slots.Contains(EquipSlots.Weapon) && e.WeaponType != null
                && job.WeaponTypes.Contains(e.WeaponType) && e.MinBaseLevel <= baseLevel
                && (e.JobIds.Count == 0 || e.JobIds.Contains(jobId)))
            .OrderByDescending(i => i.Equip!.WeaponAtk).ThenBy(i => i.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>A player sheet: primary stats as base, the job's stat bonuses, and the weapon's modifiers at <paramref name="refine"/>.</summary>
    public SimCharacter Player(string jobId, int baseLevel, int jobLevel, IReadOnlyDictionary<string, int> primary,
        int pointsLeft, ItemDef? weapon, int refine = 0)
    {
        var sheet = new StatSheet(_formulas);
        sheet.Base.BaseLevel = baseLevel;
        sheet.Base.JobLevel = jobLevel;
        foreach (var stat in PrimaryStats.All.Ordered) sheet.Base[stat] = primary.TryGetValue(stat, out var v) ? v : 0;
        var jobContext = new FormulaContext();
        jobContext[FormulaVariables.BaseLevel] = baseLevel;
        jobContext[FormulaVariables.JobLevel] = jobLevel;
        foreach (var def in Db.Jobs.Get(jobId).StatBonuses)
            sheet.Modifiers.Add("job:" + jobId, CompiledModifier.Compile(def), jobContext);
        if (weapon?.Equip != null)
        {
            var equipContext = new FormulaContext();
            equipContext[FormulaVariables.Refine] = refine;
            foreach (var def in weapon.Equip.Modifiers)
                sheet.Modifiers.Add("equip:" + weapon.Id, CompiledModifier.Compile(def), equipContext);
        }
        return new SimCharacter(jobId, baseLevel, jobLevel, primary, pointsLeft, weapon, refine, sheet);
    }

    /// <summary>
    /// A character at a base level: the job level from <see cref="JobLevelFor"/>, stat points spent by the explicit
    /// <paramref name="exact"/> targets or else by <paramref name="weights"/> (equal weights when both are null), and the
    /// weapon from <see cref="PickWeapon"/> unless <paramref name="weaponId"/> names one.
    /// </summary>
    public SimCharacter Character(string jobId, int baseLevel, IReadOnlyDictionary<string, int>? exact = null,
        IReadOnlyDictionary<string, int>? weights = null, string? weaponId = null, int refine = 0)
    {
        if (!Db.Jobs.Contains(jobId)) throw new ArgumentException("Unknown job '" + jobId + "'.");
        var (stats, left) = exact != null
            ? SpendExact(jobId, baseLevel, exact)
            : SpendByWeight(jobId, baseLevel, weights ?? PrimaryStats.All.Ordered.ToDictionary(s => s, _ => 1, StringComparer.Ordinal));
        var weapon = weaponId != null ? Db.Items.Get(weaponId) : PickWeapon(jobId, baseLevel);
        return Player(jobId, baseLevel, JobLevelFor(jobId, baseLevel), stats, left, weapon, refine);
    }

    /// <summary>A monster: primary stats as base values, listed derived stats as overrides (see <see cref="MonsterDef.Stats"/>).</summary>
    public StatSheet Monster(MonsterDef def)
    {
        var sheet = new StatSheet(_formulas);
        sheet.Base.BaseLevel = def.Level;
        foreach (var pair in def.Stats)
        {
            if (PrimaryStats.All.Contains(pair.Key)) sheet.Base[pair.Key] = (int)pair.Value;
            else sheet.Modifiers.Add("monster", pair.Key, ModifierOp.Override, pair.Value);
        }
        return sheet;
    }
}
