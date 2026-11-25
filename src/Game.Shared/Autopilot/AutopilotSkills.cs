using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Effects;

namespace Game.Shared.Autopilot
{
    /// <summary>How the autopilot may use a skill (T-604).</summary>
    public enum AutopilotSkillUse
    {
        /// <summary>Not at all.</summary>
        Never,

        /// <summary>On the monster it fights.</summary>
        OnTarget,

        /// <summary>On the cell of the monster it fights.</summary>
        OnTargetCell,

        /// <summary>Around the player, when the monster it fights is within <see cref="AutopilotSkill.Radius"/>.</summary>
        AroundSelf,

        /// <summary>On the player, when it would do something (<c>AutopilotSkill.IsUseful</c>).</summary>
        OnSelf,
    }

    /// <summary>
    /// What the autopilot makes of one active skill, from its targeting and effects alone (T-604). The rule, in this order:
    /// <list type="number">
    /// <item><description><b>Never</b>: a passive skill, one without effects, and one with a <c>teleport</c>, <c>summon</c>, <c>reset_stats</c> or <c>reset_skills</c> effect
    /// anywhere (it would move the character off its plan, or change it for good).</description></item>
    /// <item><description><b>Ground skills</b> only when clearly offensive: aimed at enemies, and every effect is a hostile <c>area</c> with a <c>damage</c> effect in it. They are cast
    /// on the target's cell. Any other ground skill: never.</description></item>
    /// <item><description><b>Friendly skills</b> (ranged or on the user) are supportive: cast on the player only, when useful.</description></item>
    /// <item><description><b>Hostile skills with a range</b> are offensive: cast on the monster being fought.</description></item>
    /// <item><description><b>Hostile skills around the user</b> (range 0) when every effect is a hostile <c>area</c>: cast when the monster stands within the smallest of their radii.
    /// Any other: never.</description></item>
    /// </list>
    /// </summary>
    public sealed class AutopilotSkill
    {
        /// <summary>Classifies <paramref name="def"/>.</summary>
        public AutopilotSkill(SkillDef def)
        {
            Def = def ?? throw new ArgumentNullException(nameof(def));
            Use = Classify(def, out var radius);
            Radius = radius;
        }

        /// <summary>The skill.</summary>
        public SkillDef Def { get; }

        /// <summary>How it may be used.</summary>
        public AutopilotSkillUse Use { get; }

        /// <summary>For <see cref="AutopilotSkillUse.AroundSelf"/>: the farthest (Chebyshev) the monster may stand.</summary>
        public int Radius { get; }

        private static AutopilotSkillUse Classify(SkillDef def, out int radius)
        {
            radius = 0;
            if (def.Passive || def.Effects.Count == 0 || HasUnsafe(def.Effects)) return AutopilotSkillUse.Never;
            if (def.Target.Ground)
            {
                if (def.Target.Friendly) return AutopilotSkillUse.Never;
                foreach (var effect in def.Effects)
                    if (!(effect is AreaEffect { Friendly: false } area) || !HasDamage(area.Effects)) return AutopilotSkillUse.Never;
                return AutopilotSkillUse.OnTargetCell;
            }
            if (def.Target.Friendly) return AutopilotSkillUse.OnSelf;
            if (def.Target.Range > 0) return AutopilotSkillUse.OnTarget;
            radius = int.MaxValue;
            foreach (var effect in def.Effects)
            {
                if (!(effect is AreaEffect { Friendly: false } area)) return AutopilotSkillUse.Never;
                radius = Math.Min(radius, area.Radius);
            }
            return AutopilotSkillUse.AroundSelf;
        }

        private static bool HasUnsafe(IReadOnlyList<EffectDef> effects)
        {
            foreach (var effect in effects)
            {
                if (effect is TeleportEffect || effect is SummonEffect || effect is ResetStatsEffect || effect is ResetSkillsEffect) return true;
                if (effect is AreaEffect area && HasUnsafe(area.Effects)) return true;
            }
            return false;
        }

        private static bool HasDamage(IReadOnlyList<EffectDef> effects)
        {
            foreach (var effect in effects)
                if (effect is DamageEffect) return true;
            return false;
        }

        /// <summary>
        /// For <see cref="AutopilotSkillUse.OnSelf"/>: true when one of the effects (an area's too) would do something for the player now. <c>heal</c> and <c>restore_sp</c> below
        /// <paramref name="belowPercent"/> of the maximum, <c>apply_status</c> when the status is not on already, <c>remove_status</c> when something it removes is on. Anything else
        /// never asks for a cast.
        /// </summary>
        public bool IsUseful(int hp, int maxHp, int sp, int maxSp, StatusSet? statuses, int belowPercent) =>
            IsUseful(Def.Effects, (long)hp * 100 < (long)maxHp * belowPercent, (long)sp * 100 < (long)maxSp * belowPercent, statuses);

        private static bool IsUseful(IReadOnlyList<EffectDef> effects, bool lowHp, bool lowSp, StatusSet? statuses)
        {
            for (var i = 0; i < effects.Count; i++)
            {
                switch (effects[i])
                {
                    case HealEffect _:
                        if (lowHp) return true;
                        break;
                    case RestoreSpEffect _:
                        if (lowSp) return true;
                        break;
                    case ApplyStatusEffect apply:
                        if (statuses?.Find(apply.StatusId) == null) return true;
                        break;
                    case RemoveStatusEffect remove:
                        if (statuses != null && Removes(remove, statuses)) return true;
                        break;
                    case AreaEffect area:
                        if (IsUseful(area.Effects, lowHp, lowSp, statuses)) return true;
                        break;
                }
            }
            return false;
        }

        private static bool Removes(RemoveStatusEffect remove, StatusSet statuses)
        {
            for (var i = 0; i < statuses.Active.Count; i++)
            {
                var status = statuses.Active[i].Status.Def;
                for (var j = 0; j < remove.StatusIds.Count; j++)
                    if (string.Equals(remove.StatusIds[j], status.Id, StringComparison.Ordinal)) return true;
                for (var j = 0; j < remove.Tags.Count; j++)
                    for (var k = 0; k < status.Tags.Count; k++)
                        if (string.Equals(remove.Tags[j], status.Tags[k], StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
