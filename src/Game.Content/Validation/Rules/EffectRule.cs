using Game.Shared.Definitions;
using Game.Shared.Effects;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Every effect must be one the effect runner can run where it stands (<see cref="EffectSupport"/>, T-501): a skill's effects by its target kind,
/// a consumable's, a status's <c>on_apply</c> and <c>on_tick</c>, and an area's children. Anything else is a load error, not a runtime refusal.
/// A status with <c>on_tick</c> needs a <c>tick_interval_ms</c> above 0, and <c>max_stacks</c> is at least 1.
/// </summary>
public sealed class EffectRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "effect";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;
        foreach (var skill in db.Skills.All)
        {
            if (skill.Passive) continue;
            var site = skill.Target.Ground ? EffectSite.GroundSkill : skill.Target.Range <= 0 ? EffectSite.SelfSkill : EffectSite.EntitySkill;
            Check(context, skill.Id, "effects", skill.Effects, site, null, skill.Target.Friendly);
        }
        foreach (var item in db.Items.All)
            if (item.Consumable is { } consumable) Check(context, item.Id, "consumable.effects", consumable.Effects, EffectSite.Item, null, null);
        foreach (var status in db.Statuses.All)
        {
            Check(context, status.Id, "on_apply", status.OnApply, EffectSite.StatusOnApply, status.Id, null);
            Check(context, status.Id, "on_tick", status.OnTick, EffectSite.StatusOnTick, status.Id, null);
            if (status.OnTick.Count > 0 && status.TickIntervalMs <= 0)
                context.Error(status.Id, "tick_interval_ms", ValidationCodes.EffectUnsupported, "on_tick needs a tick_interval_ms above 0");
            if (status.MaxStacks < 1)
                context.Error(status.Id, "max_stacks", ValidationCodes.EffectUnsupported, "max_stacks must be at least 1");
        }
    }

    private static void Check(ValidationContext context, string owner, string path, IReadOnlyList<EffectDef> effects, EffectSite site, string? statusId,
        bool? friendly, bool inArea = false)
    {
        for (var i = 0; i < effects.Count; i++)
        {
            var p = $"{path}[{i}]";
            var problem = EffectSupport.Check(effects[i], site, inArea, statusId, friendly);
            if (problem is not null) context.Error(owner, p, ValidationCodes.EffectUnsupported, problem);
            if (effects[i] is AreaEffect area && !inArea) Check(context, owner, p + ".effects", area.Effects, site, statusId, friendly, inArea: true);
        }
    }
}
