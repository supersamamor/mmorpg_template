using Game.Content.Loading;
using Game.Content.Registry;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Random;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;

namespace Game.Tests.Combat;

/// <summary>Builds stat sheets and attack contexts from the real /content for combat tests.</summary>
internal static class CombatTestContent
{
    private static readonly Lazy<ContentDatabase> LazyDb = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
        var result = ContentLoader.Load(Path.Combine(dir.FullName, "content"));
        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        return result.Database!;
    });

    private static readonly Lazy<DerivedStatFormulas> LazyFormulas = new(() => DerivedStatFormulas.Compile(Db.Formulas));

    public static ContentDatabase Db => LazyDb.Value;

    public static CombatRules Rules(string? critMultiplier = null, CombatBalanceDef? balance = null) =>
        CombatRules.Compile(Db.Formulas, Db.Elements, balance ?? Db.Balance.Combat, critMultiplier);

    public static StatSheet Character(string jobId, int baseLevel, int jobLevel,
        int str, int agi, int vit, int intel, int dex, int luk)
    {
        var sheet = new StatSheet(LazyFormulas.Value);
        sheet.Base.BaseLevel = baseLevel;
        sheet.Base.JobLevel = jobLevel;
        sheet.Base[PrimaryStats.Str] = str;
        sheet.Base[PrimaryStats.Agi] = agi;
        sheet.Base[PrimaryStats.Vit] = vit;
        sheet.Base[PrimaryStats.Int] = intel;
        sheet.Base[PrimaryStats.Dex] = dex;
        sheet.Base[PrimaryStats.Luk] = luk;
        var context = new FormulaContext();
        context[FormulaVariables.BaseLevel] = baseLevel;
        context[FormulaVariables.JobLevel] = jobLevel;
        foreach (var def in Db.Jobs.Get(jobId).StatBonuses)
            sheet.Modifiers.Add("job:" + jobId, CompiledModifier.Compile(def), context);
        return sheet;
    }

    public static void Equip(StatSheet sheet, string itemId, int refine = 0)
    {
        var context = new FormulaContext();
        context[FormulaVariables.Refine] = refine;
        foreach (var def in Db.Items.Get(itemId).Equip!.Modifiers)
            sheet.Modifiers.Add("equip:" + itemId, CompiledModifier.Compile(def), context);
    }

    /// <summary>A monster: primary stats as base values, listed derived stats as overrides (MonsterDef.Stats docs).</summary>
    public static StatSheet Monster(string monsterId)
    {
        var def = Db.Monsters.Get(monsterId);
        var sheet = new StatSheet(LazyFormulas.Value);
        sheet.Base.BaseLevel = def.Level;
        foreach (var pair in def.Stats)
        {
            if (PrimaryStats.All.Contains(pair.Key)) sheet.Base[pair.Key] = (int)pair.Value;
            else sheet.Modifiers.Add("monster", pair.Key, ModifierOp.Override, pair.Value);
        }
        return sheet;
    }

    /// <summary>An attack with the weapon's inputs and the monster's defensive element, size and race.</summary>
    public static AttackContext Attack(StatSheet attacker, string? weaponId, int refine, string monsterId, StatSheet? defender = null)
    {
        var weapon = weaponId == null ? null : Db.Items.Get(weaponId).Equip!;
        var monster = Db.Monsters.Get(monsterId);
        return new AttackContext
        {
            Attacker = attacker,
            Defender = defender ?? Monster(monsterId),
            WeaponAtk = weapon?.WeaponAtk ?? 0,
            Refine = refine,
            AttackerElement = weapon?.Element,
            DefenderElement = monster.Element,
            DefenderSize = monster.Size,
            DefenderRace = monster.Race,
        };
    }

    public static CompiledDamageEffect DamageEffectOf(string skillId)
    {
        static IEnumerable<EffectDef> Flatten(IEnumerable<EffectDef> effects) =>
            effects.SelectMany(e => e is AreaEffect area ? Flatten(area.Effects) : new[] { e });
        return CompiledDamageEffect.Compile(Flatten(Db.Skills.Get(skillId).Effects).OfType<DamageEffect>().Single());
    }

    /// <summary>The Warden of the golden tests: base 20 / job 12, STR 18 AGI 8 VIT 14 INT 1 DEX 10 LUK 4, item.iron_blade.</summary>
    /// <remarks>
    /// str 18 + 1 (blade) = 19; atk = 19 + floor(10/5)=2 + floor(4/5)=0 + floor(20/4)=5 = 26;
    /// hit = 20 + 10 + 10 = 40; crit = 1 + 4×0.3 = 2.2; luk 4.
    /// </remarks>
    public static StatSheet Warden(int bladeRefine)
    {
        var c = Character("job.warden", 20, 12, str: 18, agi: 8, vit: 14, intel: 1, dex: 10, luk: 4);
        Equip(c, "item.iron_blade", bladeRefine);
        return c;
    }
}

/// <summary>An <see cref="IRandom"/> that returns scripted doubles, and fails the test if asked for more.</summary>
internal sealed class ScriptedRandom : IRandom
{
    private readonly Queue<double> _values;

    public ScriptedRandom(params double[] values) => _values = new Queue<double>(values);

    public int Remaining => _values.Count;

    public double NextDouble() =>
        _values.Count > 0 ? _values.Dequeue() : throw new InvalidOperationException("ScriptedRandom exhausted");

    public uint NextUInt() => throw new NotSupportedException();

    public int Next(int maxExclusive) => throw new NotSupportedException();
}

/// <summary>A fixed multiplier for one defender size: stands in for the card hooks of Phase 4.</summary>
internal sealed class SizeMultiplier : IDamageModifier
{
    private readonly string _size;
    private readonly double _multiplier;

    public SizeMultiplier(string name, string size, double multiplier)
    {
        Name = name;
        _size = size;
        _multiplier = multiplier;
    }

    public string Name { get; }

    public double GetMultiplier(AttackContext context) => context.DefenderSize == _size ? _multiplier : 1;
}
