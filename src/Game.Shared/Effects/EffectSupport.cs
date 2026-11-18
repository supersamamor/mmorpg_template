using Game.Shared.Definitions;

namespace Game.Shared.Effects
{
    /// <summary>Where an effect list runs, for <see cref="EffectSupport"/>.</summary>
    public enum EffectSite
    {
        /// <summary>A skill aimed at an entity (<c>target.range</c> above 0, not ground).</summary>
        EntitySkill,

        /// <summary>A skill on the caster itself (<c>target.range</c> 0, not ground).</summary>
        SelfSkill,

        /// <summary>A skill aimed at a ground cell (<c>target.ground</c>).</summary>
        GroundSkill,

        /// <summary>A consumable: the user is caster and target.</summary>
        Item,

        /// <summary>A status's <c>on_apply</c>: its applier is the caster, its bearer the target.</summary>
        StatusOnApply,

        /// <summary>A status's <c>on_tick</c>: its applier is the caster, its bearer the target.</summary>
        StatusOnTick,
    }

    /// <summary>
    /// What the <see cref="EffectRunner"/> can run where (T-501). Content validation calls <see cref="Check"/> for every effect, so an effect the
    /// runner could not run is a load error, never a runtime refusal.
    /// </summary>
    public static class EffectSupport
    {
        /// <summary>
        /// Null when <paramref name="effect"/> can run at <paramref name="site"/> (<paramref name="inArea"/>: inside an <c>area</c>), else why not.
        /// <paramref name="ownerStatusId"/> is the status the list belongs to (status sites); <paramref name="skillFriendly"/> the owning skill's
        /// <c>target.friendly</c> (skill sites).
        /// </summary>
        public static string? Check(EffectDef effect, EffectSite site, bool inArea, string? ownerStatusId = null, bool? skillFriendly = null)
        {
            var status = site == EffectSite.StatusOnApply || site == EffectSite.StatusOnTick;
            switch (effect)
            {
                case ModifyStatEffect _:
                    return site == EffectSite.StatusOnApply && !inArea
                        ? null
                        : "modify_stat lasts as long as a status: it is allowed only directly in a status's on_apply";
                case AreaEffect area:
                    if (inArea) return "an area inside an area is not supported";
                    if (skillFriendly.HasValue && area.Friendly != skillFriendly.Value)
                        return "the area's friendly (" + Bool(area.Friendly) + ") must match the skill's target.friendly (" + Bool(skillFriendly.Value) + ")";
                    if (area.Radius < 0) return "radius must not be negative";
                    return null;
                case TeleportEffect teleport:
                    if (status || inArea) return "teleport moves its caster: it is allowed only directly in a skill or a consumable";
                    if (teleport.ToSavePoint) return null;
                    if (teleport.X.HasValue != teleport.Y.HasValue) return "give both x and y, or neither";
                    if (!string.IsNullOrEmpty(teleport.MapId) && !teleport.X.HasValue) return "a teleport to another map needs x and y";
                    return null;
                case SummonEffect summon:
                    if (status || inArea) return "summon is allowed only directly in a skill or a consumable";
                    if (summon.Count < 1) return "count must be at least 1";
                    if (summon.DurationMs.HasValue && summon.DurationMs.Value <= 0) return "duration_ms must be above 0 when given";
                    return null;
                case KnockbackEffect knockback:
                    if (!inArea && (site == EffectSite.Item || site == EffectSite.SelfSkill))
                        return "knockback pushes its target away from the caster: the target here is the caster itself";
                    if (knockback.Cells < 1) return "cells must be at least 1";
                    break;
                case ApplyStatusEffect apply:
                    if (status && apply.StatusId == ownerStatusId) return "a status may not apply itself";
                    break;
                case DamageEffect damage:
                    if (damage.Hits < 1) return "hits must be at least 1";
                    break;
                case HealEffect _:
                case RestoreSpEffect _:
                case RemoveStatusEffect _:
                    break;
                default:
                    return "effect type '" + effect.Type + "' has no runner";
            }
            // Everything that reaches here acts on an entity.
            if (site == EffectSite.GroundSkill && !inArea)
                return effect.Type + " needs an entity, but a ground skill's own effects aim at a cell: put it inside an area";
            return null;
        }

        private static string Bool(bool value) => value ? "true" : "false";
    }
}
