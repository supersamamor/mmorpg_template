using Game.Shared.Combat;
using Game.Shared.Vocabulary;
using static Game.Tests.Combat.CombatTestContent;

namespace Game.Tests.Combat;

/// <summary>
/// Golden damage values for sample matchups from the real /content (formulas.yaml, elements.yaml, balance.yaml).
/// Rolls are scripted so each test pins one path. If the content changes these numbers are expected to change:
/// redo the arithmetic in the comments, don't paste the new output.
/// </summary>
/// <remarks>
/// physical_damage = max(1, floor((atk + weapon_atk + refine × 2) × 100 / (100 + t_def × 3)))
/// magic_damage    = max(1, floor(matk × 100 / (100 + t_mdef × 3)))
/// hit_chance      = clamp((hit − t_flee + 80) / 100, 0.05, 0.95), then balance [0.05, 0.95]
/// crit_chance     = clamp((crit − t_luk × 0.2) / 100, 0, 0.5), then balance [0, 0.5]
/// </remarks>
public class GoldenDamageTests
{
    private const double Tolerance = 1e-9;

    /// <summary>Warden + item.iron_blade +3 auto-attack on monster.gel_slime (level 1, water, def 0, agi 1, luk 1).</summary>
    [Fact]
    public void Warden_iron_blade_auto_attack_on_gel_slime()
    {
        var ctx = Attack(Warden(bladeRefine: 3), "item.iron_blade", refine: 3, "monster.gel_slime");
        ctx.Breakdown = new DamageBreakdown();
        var rng = new ScriptedRandom(0.5, 0.5);

        var r = new CombatResolver(Rules(), rng).Resolve(ctx);

        // hit_chance: slime flee = 1 + 1 + 10 = 12 (formula; not overridden); (40 − 12 + 80) / 100 = 1.08 → 0.95. Roll 0.5 hits.
        // crit_chance: (2.2 − 1×0.2) / 100 = 0.02. Roll 0.5: no crit.
        // physical: floor((26 + 38 + 3×2) × 100 / (100 + 0×3)) = floor(7000 / 100) = 70.
        // element: neutral (blade has none) vs water = 1. Final 70.
        r.Hit.ShouldBeTrue();
        r.Crit.ShouldBeFalse();
        r.Element.ShouldBe(Elements.Neutral);
        r.Multiplier.ShouldBe(1);
        r.Damage.ShouldBe(70);
        Step(ctx, BreakdownSteps.HitChance).ShouldBe(0.95, Tolerance);
        Step(ctx, BreakdownSteps.CritChance).ShouldBe(0.02, Tolerance);
        Step(ctx, BreakdownSteps.Base).ShouldBe(70);
        Step(ctx, BreakdownSteps.Defense).ShouldBe(70);
        rng.Remaining.ShouldBe(0);
    }

    /// <summary>Warden + item.iron_blade +0, skill.twin_strike level 3 on monster.rock_beetle (level 7, earth, def 12, agi 5, luk 3).</summary>
    [Fact]
    public void Warden_twin_strike_on_rock_beetle()
    {
        var ctx = Attack(Warden(bladeRefine: 0), "item.iron_blade", refine: 0, "monster.rock_beetle");
        ctx.Effect = DamageEffectOf("skill.twin_strike");
        ctx.SkillLevel = 3;
        ctx.Breakdown = new DamageBreakdown();

        var r = new CombatResolver(Rules(), new ScriptedRandom(0.9, 0.9)).Resolve(ctx);

        // hit_chance: beetle flee = 7 + 5 + 10 = 22; (40 − 22 + 80) / 100 = 0.98 → 0.95. Roll 0.9 hits.
        // crit_chance: (2.2 − 3×0.2) / 100 = 0.016. Roll 0.9: no crit.
        // base (t_def 0): floor((26 + 38) × 100 / 100) = 64.
        // physical: floor(64 × 100 / (100 + 12×3)) = floor(6400 / 136) = floor(47.06) = 47.
        // skill: 0.6 + 3×0.1 = 0.9. Element neutral vs earth = 1.
        // raw = 47 × 0.9 × 1 = 42.3 → 42. (hits: 2 splits the total visually only.)
        r.Hit.ShouldBeTrue();
        r.Crit.ShouldBeFalse();
        r.Damage.ShouldBe(42);
        Step(ctx, BreakdownSteps.CritChance).ShouldBe(0.016, Tolerance);
        Step(ctx, BreakdownSteps.Base).ShouldBe(64);
        Step(ctx, BreakdownSteps.Defense).ShouldBe(47);
        Step(ctx, BreakdownSteps.Skill).ShouldBe(0.9, Tolerance);
        Step(ctx, BreakdownSteps.Raw).ShouldBe(42.3, Tolerance);
    }

    /// <summary>
    /// Spellbinder base 15 / job 10, STR 1 AGI 5 VIT 5 INT 20 DEX 12 LUK 3, item.oak_staff +2,
    /// skill.ember_bolt level 5 (fire) on monster.sting_wasp (wind, mdef 2): fire is strong against wind.
    /// </summary>
    [Fact]
    public void Spellbinder_ember_bolt_on_sting_wasp()
    {
        var mage = Character("job.spellbinder", 15, 10, str: 1, agi: 5, vit: 5, intel: 20, dex: 12, luk: 3);
        Equip(mage, "item.oak_staff", refine: 2);
        var ctx = Attack(mage, "item.oak_staff", refine: 2, "monster.sting_wasp");
        ctx.Effect = DamageEffectOf("skill.ember_bolt");
        ctx.SkillLevel = 5;
        ctx.Breakdown = new DamageBreakdown();
        var rng = new ScriptedRandom(); // magic draws no rolls

        var r = new CombatResolver(Rules(), rng).Resolve(ctx);

        // int = 20 + 2 (staff) + floor(10/10) = 1 (job) = 23.
        // matk = floor(23 × 1.5) = floor(34.5) = 34 + floor(15/4) = 3 → 37; + staff 10 + 2×3 = 16 → 53.
        // magic: floor(53 × 100 / (100 + 2×3)) = floor(5300 / 106) = 50. Base (t_mdef 0) = 53.
        // skill: 0.9 + 5×0.15 = 1.65. Element: effect fire (overrides the staff's earth) vs wind = 1.5.
        // raw = 50 × 1.65 × 1.5 = 123.75 → 123.
        r.Hit.ShouldBeTrue();
        r.Crit.ShouldBeFalse();
        r.Element.ShouldBe(Elements.Fire);
        r.Multiplier.ShouldBe(1.5);
        r.Damage.ShouldBe(123);
        mage.Get(DerivedStats.Matk).ShouldBe(53);
        Step(ctx, BreakdownSteps.Base).ShouldBe(53);
        Step(ctx, BreakdownSteps.Defense).ShouldBe(50);
        Step(ctx, BreakdownSteps.Skill).ShouldBe(1.65, Tolerance);
        Step(ctx, BreakdownSteps.Raw).ShouldBe(123.75, Tolerance);
    }

    /// <summary>The same Spellbinder auto-attacking monster.mossling with the oak staff: earth vs earth halves the damage.</summary>
    [Fact]
    public void Spellbinder_earth_staff_on_earth_mossling()
    {
        var mage = Character("job.spellbinder", 15, 10, str: 1, agi: 5, vit: 5, intel: 20, dex: 12, luk: 3);
        Equip(mage, "item.oak_staff", refine: 2);
        var ctx = Attack(mage, "item.oak_staff", refine: 2, "monster.mossling");

        var r = new CombatResolver(Rules(), new ScriptedRandom(0.1, 0.9)).Resolve(ctx);

        // atk = str 1 + floor(12/5) = 2 + floor(3/5) = 0 + floor(15/4) = 3 → 6. Mossling def 2.
        // physical: floor((6 + 15 + 2×2) × 100 / (100 + 2×3)) = floor(2500 / 106) = floor(23.58) = 23.
        // element: earth (staff) vs earth = 0.5 → 11.5 → 11.
        r.Element.ShouldBe(Elements.Earth);
        r.Multiplier.ShouldBe(0.5);
        r.Damage.ShouldBe(11);
    }

    private static double Step(AttackContext ctx, string name)
    {
        ctx.Breakdown!.TryGet(name, out var value).ShouldBeTrue(name);
        return value;
    }
}
