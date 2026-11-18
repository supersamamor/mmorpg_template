using System;
using System.Collections.Generic;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Stats;

namespace Game.Shared.Effects
{
    /// <summary>
    /// One <see cref="EffectDef"/> with its formulas compiled (ARCHITECTURE §6.3). Built once at load by <see cref="EffectLibrary"/> and shared by
    /// every runner; immutable and thread-safe. <see cref="Def"/> is the content record; the concrete subclass carries what the runner needs.
    /// </summary>
    public abstract class CompiledEffect
    {
        private protected CompiledEffect(EffectDef def) => Def = def;

        /// <summary>The content record.</summary>
        public EffectDef Def { get; }

        /// <summary>Its <see cref="Vocabulary.EffectTypes"/> term.</summary>
        public string Type => Def.Type;

        /// <summary>
        /// Compiles one effect (and an <c>area</c>'s children). Throws <see cref="FormulaException"/> for a bad formula: validated content has none,
        /// so a throw here is a boot error, never a runtime one.
        /// </summary>
        public static CompiledEffect Compile(EffectDef def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            switch (def)
            {
                case DamageEffect d: return new CompiledDamage(d);
                case HealEffect h: return new CompiledAmount(h, Formula.Compile(h.Amount));
                case RestoreSpEffect r: return new CompiledAmount(r, Formula.Compile(r.Amount));
                case ApplyStatusEffect a: return new CompiledApplyStatus(a);
                case RemoveStatusEffect r: return new CompiledPlain(r);
                case ModifyStatEffect m: return new CompiledModifyStat(m);
                case KnockbackEffect k: return new CompiledPlain(k);
                case TeleportEffect t: return new CompiledPlain(t);
                case SummonEffect s: return new CompiledPlain(s);
                case AreaEffect a: return new CompiledArea(a, CompileList(a.Effects));
                default: throw new ArgumentException("Effect type '" + def.Type + "' has no runner.", nameof(def));
            }
        }

        /// <summary>Compiles a list of effects, keeping their order.</summary>
        public static IReadOnlyList<CompiledEffect> CompileList(IReadOnlyList<EffectDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            if (defs.Count == 0) return Array.Empty<CompiledEffect>();
            var list = new CompiledEffect[defs.Count];
            for (var i = 0; i < defs.Count; i++) list[i] = Compile(defs[i]);
            return list;
        }
    }

    /// <summary><c>damage</c>: the multiplier compiled for the resolver.</summary>
    public sealed class CompiledDamage : CompiledEffect
    {
        internal CompiledDamage(DamageEffect def) : base(def) => Damage = CompiledDamageEffect.Compile(def);

        /// <summary>What <see cref="AttackContext.Effect"/> points at.</summary>
        public CompiledDamageEffect Damage { get; }
    }

    /// <summary><c>heal</c> or <c>restore_sp</c>: the amount formula.</summary>
    public sealed class CompiledAmount : CompiledEffect
    {
        internal CompiledAmount(EffectDef def, Formula amount) : base(def) => Amount = amount;

        /// <summary>Points restored (caster and <c>t_</c> target variables, <c>level</c>); fractions are floored, below 0 counts as 0.</summary>
        public Formula Amount { get; }
    }

    /// <summary><c>apply_status</c>: chance and duration formulas.</summary>
    public sealed class CompiledApplyStatus : CompiledEffect
    {
        internal CompiledApplyStatus(ApplyStatusEffect def) : base(def)
        {
            StatusId = def.StatusId;
            Chance = Formula.Compile(def.Chance);
            DurationMs = Formula.Compile(def.DurationMs);
        }

        /// <summary>The status applied.</summary>
        public string StatusId { get; }

        /// <summary>Probability 0–1.</summary>
        public Formula Chance { get; }

        /// <summary>Duration in milliseconds of simulated time.</summary>
        public Formula DurationMs { get; }
    }

    /// <summary><c>modify_stat</c>: the modifier compiled (it goes on with the source <c>status:&lt;id&gt;</c>).</summary>
    public sealed class CompiledModifyStat : CompiledEffect
    {
        internal CompiledModifyStat(ModifyStatEffect def) : base(def) => Modifier = CompiledModifier.Compile(def.Modifier);

        /// <summary>The modifier.</summary>
        public CompiledModifier Modifier { get; }
    }

    /// <summary><c>area</c>: radius, filter and the children compiled.</summary>
    public sealed class CompiledArea : CompiledEffect
    {
        internal CompiledArea(AreaEffect def, IReadOnlyList<CompiledEffect> children) : base(def)
        {
            Radius = def.Radius;
            Friendly = def.Friendly;
            Children = children;
        }

        /// <summary>Chebyshev radius in cells.</summary>
        public int Radius { get; }

        /// <summary>True for allies, false for enemies.</summary>
        public bool Friendly { get; }

        /// <summary>Run on each entity in the square, all of them on one entity before the next.</summary>
        public IReadOnlyList<CompiledEffect> Children { get; }
    }

    /// <summary>An effect with no formula (<c>remove_status</c>, <c>knockback</c>, <c>teleport</c>, <c>summon</c>): the record is enough.</summary>
    public sealed class CompiledPlain : CompiledEffect
    {
        internal CompiledPlain(EffectDef def) : base(def)
        {
        }
    }

    /// <summary>A status with its <c>on_apply</c> and <c>on_tick</c> compiled.</summary>
    public sealed class CompiledStatus
    {
        /// <summary>Compiles <paramref name="def"/>.</summary>
        public CompiledStatus(StatusDef def)
        {
            Def = def ?? throw new ArgumentNullException(nameof(def));
            OnApply = CompiledEffect.CompileList(def.OnApply);
            OnTick = CompiledEffect.CompileList(def.OnTick);
            ModifierSource = "status:" + def.Id;
        }

        /// <summary>The content record.</summary>
        public StatusDef Def { get; }

        /// <summary>Its id.</summary>
        public string Id => Def.Id;

        /// <summary>Run once per stack when a stack is added; its <c>modify_stat</c> effects last until the status ends.</summary>
        public IReadOnlyList<CompiledEffect> OnApply { get; }

        /// <summary>Run once per stack every <see cref="StatusDef.TickIntervalMs"/>.</summary>
        public IReadOnlyList<CompiledEffect> OnTick { get; }

        /// <summary>The modifier source of its <c>modify_stat</c> effects (ARCHITECTURE §6.1): <c>status:&lt;id&gt;</c>.</summary>
        public string ModifierSource { get; }
    }

    /// <summary>A skill with its effects and timing formulas compiled.</summary>
    public sealed class CompiledSkill
    {
        /// <summary>Compiles <paramref name="def"/>.</summary>
        public CompiledSkill(SkillDef def)
        {
            Def = def ?? throw new ArgumentNullException(nameof(def));
            Effects = CompiledEffect.CompileList(def.Effects);
            CastTimeMs = Formula.Compile(def.CastTimeMs);
            CooldownMs = Formula.Compile(def.CooldownMs);
            SpCost = Formula.Compile(def.SpCost);
        }

        /// <summary>The content record.</summary>
        public SkillDef Def { get; }

        /// <summary>Its id.</summary>
        public string Id => Def.Id;

        /// <summary>Effects run on the target when it resolves.</summary>
        public IReadOnlyList<CompiledEffect> Effects { get; }

        /// <summary>Cast time in ms before the caster's <c>cast_time_mod</c>.</summary>
        public Formula CastTimeMs { get; }

        /// <summary>Cooldown in ms, from the moment it resolves.</summary>
        public Formula CooldownMs { get; }

        /// <summary>SP cost (players, T-502).</summary>
        public Formula SpCost { get; }
    }

    /// <summary>
    /// Every effect list of the content compiled once: skills, consumables and statuses. Built at boot and shared by every instance and tool;
    /// immutable after construction, so thread-safe. Unknown ids return false.
    /// </summary>
    public sealed class EffectLibrary
    {
        private readonly Dictionary<string, CompiledSkill> _skills = new Dictionary<string, CompiledSkill>(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<CompiledEffect>> _items = new Dictionary<string, IReadOnlyList<CompiledEffect>>(StringComparer.Ordinal);
        private readonly Dictionary<string, CompiledStatus> _statuses = new Dictionary<string, CompiledStatus>(StringComparer.Ordinal);

        /// <summary>Compiles the effects of every skill, consumable and status given.</summary>
        public EffectLibrary(IEnumerable<SkillDef> skills, IEnumerable<ItemDef> items, IEnumerable<StatusDef> statuses)
        {
            if (skills == null) throw new ArgumentNullException(nameof(skills));
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (statuses == null) throw new ArgumentNullException(nameof(statuses));
            foreach (var skill in skills) _skills[skill.Id] = new CompiledSkill(skill);
            foreach (var item in items)
                if (item.Consumable != null) _items[item.Id] = CompiledEffect.CompileList(item.Consumable.Effects);
            foreach (var status in statuses) _statuses[status.Id] = new CompiledStatus(status);
        }

        /// <summary>An empty library (tests that need no effects).</summary>
        public static EffectLibrary Empty { get; } =
            new EffectLibrary(Array.Empty<SkillDef>(), Array.Empty<ItemDef>(), Array.Empty<StatusDef>());

        /// <summary>A compiled skill.</summary>
        public bool TryGetSkill(string id, out CompiledSkill skill) => _skills.TryGetValue(id, out skill!);

        /// <summary>A consumable's compiled effects (false for an item without <c>consumable</c>).</summary>
        public bool TryGetItem(string id, out IReadOnlyList<CompiledEffect> effects) => _items.TryGetValue(id, out effects!);

        /// <summary>A compiled status.</summary>
        public bool TryGetStatus(string id, out CompiledStatus status) => _statuses.TryGetValue(id, out status!);
    }
}
