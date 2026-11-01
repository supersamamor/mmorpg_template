using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;

namespace Game.Shared.Combat
{
    /// <summary>
    /// Everything <see cref="CombatResolver.Resolve"/> needs for one attack: both stat sheets, the weapon and skill inputs,
    /// and the defender's element, size and race. No inventory or entity types: the caller copies the few values it needs.
    /// A mutable class so the server can keep one per resolver and refill it for each hit without allocating.
    /// </summary>
    public sealed class AttackContext
    {
        private static readonly IReadOnlyList<IDamageModifier> NoModifiers = Empty.List<IDamageModifier>();

        /// <summary>Attacker stats (written as caster variables: <c>atk</c>, <c>hit</c>, ...).</summary>
        public StatSheet Attacker { get; set; } = null!;

        /// <summary>Defender stats (written as <c>t_</c> variables: <c>t_def</c>, <c>t_flee</c>, ...).</summary>
        public StatSheet Defender { get; set; } = null!;

        /// <summary>Formula variable <c>weapon_atk</c>: the equipped weapon's <see cref="ItemEquipDef.WeaponAtk"/>, 0 for none.</summary>
        public int WeaponAtk { get; set; }

        /// <summary>Formula variable <c>refine</c>: the equipped weapon's refine level.</summary>
        public int Refine { get; set; }

        /// <summary>
        /// The attacker's own attack element: the weapon's <see cref="ItemEquipDef.Element"/> for characters,
        /// <see cref="MonsterDef.Element"/> for monsters. Null means <see cref="Elements.Neutral"/>.
        /// </summary>
        public string? AttackerElement { get; set; }

        /// <summary>The skill's damage effect, or null for an auto-attack (physical, multiplier 1).</summary>
        public CompiledDamageEffect? Effect { get; set; }

        /// <summary>Formula variable <c>level</c>: the skill level, used by <see cref="CompiledDamageEffect.Multiplier"/>.</summary>
        public int SkillLevel { get; set; }

        /// <summary>Defending element (<see cref="MonsterDef.Element"/> for monsters). Null means <see cref="Elements.Neutral"/>.</summary>
        public string? DefenderElement { get; set; }

        /// <summary>Defender size (<see cref="MonsterSizes"/>) for <see cref="Modifiers"/>; null when it has none.</summary>
        public string? DefenderSize { get; set; }

        /// <summary>Defender race (<see cref="MonsterRaces"/>) for <see cref="Modifiers"/>; null when it has none.</summary>
        public string? DefenderRace { get; set; }

        /// <summary>Size, race and similar damage hooks applied after the element multiplier, in order. Empty by default.</summary>
        public IReadOnlyList<IDamageModifier> Modifiers { get; set; } = NoModifiers;

        /// <summary>Destination for the step-by-step breakdown, or null (default) to skip recording.</summary>
        public DamageBreakdown? Breakdown { get; set; }

        /// <summary>True when the magic damage formula applies.</summary>
        public bool IsMagic => Effect != null && Effect.Magic;

        /// <summary>The attack element: the effect's element, else <see cref="AttackerElement"/>, else neutral.</summary>
        public string Element => Effect?.Element ?? AttackerElement ?? Elements.Neutral;

        /// <summary>The defending element, neutral when unset.</summary>
        public string DefendingElement => DefenderElement ?? Elements.Neutral;
    }
}
