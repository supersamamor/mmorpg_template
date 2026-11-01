using System;
using Game.Shared.Formulas;
using Game.Shared.Random;
using Game.Shared.Vocabulary;

namespace Game.Shared.Combat
{
    /// <summary>
    /// Resolves one attack (auto-attack or skill damage effect) into hit/miss, crit, element and final damage, using only
    /// content formulas and tables (<see cref="CombatRules"/>) and the injected <see cref="IRandom"/>. Deterministic:
    /// the same rules, inputs and random sequence always give the same results. One resolver per simulation thread; it
    /// owns a reusable <see cref="FormulaContext"/> and allocates nothing per call. Not thread-safe.
    /// </summary>
    /// <remarks>
    /// <para><b>Pipeline</b> (<see cref="BreakdownSteps"/> names in brackets):</para>
    /// <list type="number">
    /// <item>Fill the formula context: attacker as caster variables, defender as <c>t_</c> variables, then
    /// <c>weapon_atk</c>, <c>refine</c> and <c>level</c> (skill level) from the <see cref="AttackContext"/>.</item>
    /// <item>Physical attacks only: hit chance = <c>hit_chance</c> clamped to
    /// [<c>min_hit_chance</c>, <c>max_hit_chance</c>] [hit_chance]; draw one <see cref="IRandom.NextDouble"/> [hit_roll];
    /// the attack lands when roll &lt; chance. A miss ends here with damage 0 and draws nothing else.</item>
    /// <item>Physical hits only: crit chance = <c>crit_chance</c> clamped to [<c>min_crit_chance</c>,
    /// <c>max_crit_chance</c>] [crit_chance]; draw one more roll [crit_roll]; critical when roll &lt; chance.</item>
    /// <item>Magic attacks (a damage effect with <c>magic: true</c>) always land and never crit, and draw no rolls:
    /// <c>hit</c>/<c>flee</c>/<c>crit</c> are physical stats in the vocabulary.</item>
    /// <item>Damage = <c>physical_damage</c> or <c>magic_damage</c> [defense] × skill multiplier [skill_multiplier] ×
    /// element multiplier [element_multiplier] × each <see cref="IDamageModifier"/> [its name] × crit multiplier
    /// [crit_multiplier] = [raw]. When recording, [base] is the damage formula with <c>t_def</c> and <c>t_mdef</c> set to 0,
    /// so base → defense shows the defense reduction.</item>
    /// <item>Final [final]: element multiplier 0 → 0 (immune). Otherwise |raw| is floored (with a 1e-9 tolerance for binary
    /// fractions such as 1.05) and clamped to [<c>min_damage</c>, <c>max_damage</c>]; a negative raw value (negative element
    /// multiplier) keeps its sign and heals. So <c>min_damage</c> applies to landed, non-immune hits only; a miss is 0.</item>
    /// </list>
    /// </remarks>
    public sealed class CombatResolver
    {
        private const double FloorTolerance = 1e-9;

        private static readonly int WeaponAtkSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.WeaponAtk);
        private static readonly int RefineSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.Refine);
        private static readonly int SkillLevelSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.SkillLevel);
        private static readonly int TargetDefSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.TargetPrefix + DerivedStats.Def);
        private static readonly int TargetMdefSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.TargetPrefix + DerivedStats.Mdef);

        private readonly CombatRules _rules;
        private readonly IRandom _random;
        private readonly FormulaContext _formulaContext = new FormulaContext(FormulaSymbols.Default);

        /// <summary>Creates a resolver.</summary>
        /// <param name="rules">Compiled combat content.</param>
        /// <param name="random">Random source for hit and crit rolls.</param>
        public CombatResolver(CombatRules rules, IRandom random)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _random = random ?? throw new ArgumentNullException(nameof(random));
        }

        /// <summary>Resolves one attack. See the class remarks for the exact steps and roll order.</summary>
        /// <param name="context">The attack. Its sheets must be set; it is not modified (except its <see cref="AttackContext.Breakdown"/>).</param>
        public AttackResult Resolve(AttackContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Attacker == null || context.Defender == null)
                throw new ArgumentException("Attacker and Defender sheets are required.", nameof(context));

            var balance = _rules.Balance;
            var record = context.Breakdown;
            record?.Clear();

            var f = _formulaContext;
            f.Clear();
            context.Attacker.WriteTo(f, asTarget: false);
            context.Defender.WriteTo(f, asTarget: true);
            f[WeaponAtkSlot] = context.WeaponAtk;
            f[RefineSlot] = context.Refine;
            f[SkillLevelSlot] = context.SkillLevel;

            var element = context.Element;
            var elementMultiplier = _rules.ElementMultiplier(element, context.DefendingElement);
            var magic = context.IsMagic;
            var crit = false;

            if (!magic)
            {
                var hitChance = Clamp(_rules.HitChance.Evaluate(f), balance.MinHitChance, balance.MaxHitChance);
                var hitRoll = _random.NextDouble();
                record?.Add(BreakdownSteps.HitChance, hitChance);
                record?.Add(BreakdownSteps.HitRoll, hitRoll);
                if (hitRoll >= hitChance)
                {
                    record?.Add(BreakdownSteps.Final, 0);
                    return new AttackResult(false, false, element, elementMultiplier, 0, record);
                }

                var critChance = Clamp(_rules.CritChance.Evaluate(f), balance.MinCritChance, balance.MaxCritChance);
                var critRoll = _random.NextDouble();
                record?.Add(BreakdownSteps.CritChance, critChance);
                record?.Add(BreakdownSteps.CritRoll, critRoll);
                crit = critRoll < critChance;
            }

            var formula = magic ? _rules.MagicDamage : _rules.PhysicalDamage;
            var afterDefense = formula.Evaluate(f);
            if (record != null)
            {
                var def = f[TargetDefSlot];
                var mdef = f[TargetMdefSlot];
                f[TargetDefSlot] = 0;
                f[TargetMdefSlot] = 0;
                record.Add(BreakdownSteps.Base, formula.Evaluate(f));
                f[TargetDefSlot] = def;
                f[TargetMdefSlot] = mdef;
                record.Add(BreakdownSteps.Defense, afterDefense);
            }

            var skillMultiplier = context.Effect == null ? 1 : context.Effect.Multiplier.Evaluate(f);
            record?.Add(BreakdownSteps.Skill, skillMultiplier);
            record?.Add(BreakdownSteps.Element, elementMultiplier);
            var raw = afterDefense * skillMultiplier * elementMultiplier;

            var modifiers = context.Modifiers;
            for (var i = 0; i < modifiers.Count; i++)
            {
                var m = modifiers[i].GetMultiplier(context);
                if (double.IsNaN(m) || double.IsInfinity(m)) m = 1;
                record?.Add(modifiers[i].Name, m);
                raw *= m;
            }

            var critMultiplier = crit ? _rules.CritMultiplier.Evaluate(f) : 1;
            record?.Add(BreakdownSteps.Crit, critMultiplier);
            raw *= critMultiplier;
            record?.Add(BreakdownSteps.Raw, raw);

            var damage = FinalDamage(raw, elementMultiplier, balance.MinDamage, balance.MaxDamage);
            record?.Add(BreakdownSteps.Final, damage);
            return new AttackResult(true, crit, element, elementMultiplier, damage, record);
        }

        private static int FinalDamage(double raw, double elementMultiplier, int minDamage, int maxDamage)
        {
            if (elementMultiplier == 0) return 0;
            var magnitude = Clamp(Math.Floor(Math.Abs(raw) + FloorTolerance), minDamage, maxDamage);
            var value = (int)magnitude;
            return raw < 0 ? -value : value;
        }

        private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);
    }
}
