using System.Collections.Generic;
using Game.Shared.Definitions;

namespace Game.Shared.Formulas
{
    /// <summary>One formula string inside a definition.</summary>
    /// <param name="Path">YAML path inside the definition's file, in the loader's format (<c>effects[0].multiplier</c>).</param>
    /// <param name="Text">The formula source.</param>
    /// <param name="Symbols">Narrower variable set this field must compile with, or null for the default whitelist.</param>
    public sealed record FormulaSource(string Path, string Text, FormulaSymbols? Symbols = null);

    /// <summary>
    /// Lists every formula string in a definition, with its YAML path, so validation and engine code can compile
    /// them without knowing where formulas live. Explicit (no reflection) to stay trim/AOT-safe: when a definition
    /// or effect type gains a formula field, add it here.
    /// </summary>
    public static class FormulaSources
    {
        /// <summary>Formulas in <c>content/formulas.yaml</c>.</summary>
        /// <param name="set">The formula set.</param>
        public static IReadOnlyList<FormulaSource> Of(FormulaSetDef set)
        {
            var list = new List<FormulaSource>();
            foreach (var pair in set.Derived) list.Add(new FormulaSource("derived." + pair.Key, pair.Value));
            list.Add(new FormulaSource("physical_damage", set.PhysicalDamage));
            list.Add(new FormulaSource("magic_damage", set.MagicDamage));
            list.Add(new FormulaSource("hit_chance", set.HitChance));
            list.Add(new FormulaSource("crit_chance", set.CritChance));
            return list;
        }

        /// <summary>Formulas in <c>content/balance.yaml</c>, each limited to the one variable that makes sense for it.</summary>
        /// <param name="balance">The balance set.</param>
        public static IReadOnlyList<FormulaSource> Of(BalanceDef balance)
        {
            var p = balance.Progression;
            return new[]
            {
                new FormulaSource("progression.stat_points_per_base_level", p.StatPointsPerBaseLevel, FormulaSymbols.BaseLevelOnly),
                new FormulaSource("progression.skill_points_per_job_level", p.SkillPointsPerJobLevel, FormulaSymbols.JobLevelOnly),
                new FormulaSource("progression.stat_point_cost", p.StatPointCost, FormulaSymbols.StatValueOnly),
                new FormulaSource("combat.crit_multiplier", balance.Combat.CritMultiplier),
            };
        }

        /// <summary>Formulas in a skill: costs, timings, effects and passive modifiers.</summary>
        /// <param name="skill">The skill.</param>
        public static IReadOnlyList<FormulaSource> Of(SkillDef skill)
        {
            var list = new List<FormulaSource>
            {
                new FormulaSource("sp_cost", skill.SpCost),
                new FormulaSource("cast_time_ms", skill.CastTimeMs),
                new FormulaSource("cooldown_ms", skill.CooldownMs),
            };
            AddEffects(list, "effects", skill.Effects);
            AddModifiers(list, "passive_modifiers", skill.PassiveModifiers);
            return list;
        }

        /// <summary>Formulas in an item: consumable effects, equipment and card modifiers.</summary>
        /// <param name="item">The item.</param>
        public static IReadOnlyList<FormulaSource> Of(ItemDef item)
        {
            var list = new List<FormulaSource>();
            if (item.Consumable != null) AddEffects(list, "consumable.effects", item.Consumable.Effects);
            if (item.Equip != null) AddModifiers(list, "equip.modifiers", item.Equip.Modifiers);
            if (item.Card != null) AddModifiers(list, "card.modifiers", item.Card.Modifiers);
            return list;
        }

        /// <summary>Formulas in a job: stat bonuses.</summary>
        /// <param name="job">The job.</param>
        public static IReadOnlyList<FormulaSource> Of(JobDef job)
        {
            var list = new List<FormulaSource>();
            AddModifiers(list, "stat_bonuses", job.StatBonuses);
            return list;
        }

        /// <summary>Formulas in a status: apply and tick effects.</summary>
        /// <param name="status">The status.</param>
        public static IReadOnlyList<FormulaSource> Of(StatusDef status)
        {
            var list = new List<FormulaSource>();
            AddEffects(list, "on_apply", status.OnApply);
            AddEffects(list, "on_tick", status.OnTick);
            return list;
        }

        private static void AddModifiers(List<FormulaSource> list, string path, IReadOnlyList<ModifierDef> modifiers)
        {
            for (var i = 0; i < modifiers.Count; i++) list.Add(new FormulaSource(path + "[" + i + "].value", modifiers[i].Value));
        }

        private static void AddEffects(List<FormulaSource> list, string path, IReadOnlyList<EffectDef> effects)
        {
            for (var i = 0; i < effects.Count; i++)
            {
                var at = path + "[" + i + "].";
                switch (effects[i])
                {
                    case DamageEffect damage:
                        list.Add(new FormulaSource(at + "multiplier", damage.Multiplier));
                        break;
                    case HealEffect heal:
                        list.Add(new FormulaSource(at + "amount", heal.Amount));
                        break;
                    case RestoreSpEffect restore:
                        list.Add(new FormulaSource(at + "amount", restore.Amount));
                        break;
                    case ApplyStatusEffect apply:
                        list.Add(new FormulaSource(at + "chance", apply.Chance));
                        list.Add(new FormulaSource(at + "duration_ms", apply.DurationMs));
                        break;
                    case ModifyStatEffect modify:
                        list.Add(new FormulaSource(at + "modifier.value", modify.Modifier.Value));
                        break;
                    case AreaEffect area:
                        AddEffects(list, at + "effects", area.Effects);
                        break;
                }
            }
        }
    }
}
