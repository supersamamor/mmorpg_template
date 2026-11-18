using Game.Shared.Definitions;
using Game.Shared.Effects;

namespace Game.Tests.Validation;

/// <summary>T-501: an effect the runner cannot run where it stands is a load error (<see cref="EffectSupport"/>, the <c>effect</c> rule).</summary>
public class EffectRuleTests
{
    public static TheoryData<string, EffectDef, EffectSite, bool> Refused => new()
    {
        { "modify_stat in a skill", new ModifyStatEffect(), EffectSite.EntitySkill, false },
        { "modify_stat in on_tick", new ModifyStatEffect(), EffectSite.StatusOnTick, false },
        { "an area in an area", new AreaEffect(), EffectSite.SelfSkill, true },
        { "teleport in a status", new TeleportEffect { ToSavePoint = true }, EffectSite.StatusOnTick, false },
        { "teleport to another map without a cell", new TeleportEffect { MapId = "map.x" }, EffectSite.Item, false },
        { "summon in an area", new SummonEffect { MonsterId = "monster.x" }, EffectSite.GroundSkill, true },
        { "knockback of oneself", new KnockbackEffect(), EffectSite.Item, false },
        { "damage aimed at a cell", new DamageEffect(), EffectSite.GroundSkill, false },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Is_refused(string what, EffectDef effect, EffectSite site, bool inArea) =>
        EffectSupport.Check(effect, site, inArea).ShouldNotBeNull(what);

    [Fact]
    public void Allows_what_the_sample_data_does()
    {
        EffectSupport.Check(new ModifyStatEffect(), EffectSite.StatusOnApply, false).ShouldBeNull();
        EffectSupport.Check(new DamageEffect(), EffectSite.GroundSkill, true).ShouldBeNull();
        EffectSupport.Check(new KnockbackEffect(), EffectSite.SelfSkill, true).ShouldBeNull();   // shockwave: inside its area
        EffectSupport.Check(new ApplyStatusEffect { StatusId = "status.a" }, EffectSite.StatusOnApply, false, "status.b").ShouldBeNull();
        EffectSupport.Check(new ApplyStatusEffect { StatusId = "status.a" }, EffectSite.StatusOnApply, false, "status.a").ShouldNotBeNull();
        EffectSupport.Check(new AreaEffect { Friendly = false }, EffectSite.GroundSkill, false, skillFriendly: true).ShouldNotBeNull();
    }
}
