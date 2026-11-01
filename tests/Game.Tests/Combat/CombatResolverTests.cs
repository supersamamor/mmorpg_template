using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;
using static Game.Tests.Combat.CombatTestContent;

namespace Game.Tests.Combat;

/// <summary>Hit/miss edges, crit path, clamps, element table, hooks and breakdown of <see cref="CombatResolver"/>.</summary>
public class CombatResolverTests
{
    // The Warden + iron blade +3 on gel slime deals 70 on a normal hit with hit chance 0.95 (see GoldenDamageTests).
    private static AttackContext WardenOnSlime() => Attack(Warden(bladeRefine: 3), "item.iron_blade", 3, "monster.gel_slime");

    private static CombatBalanceDef BalanceWith(Func<CombatBalanceDef, CombatBalanceDef> change) => change(Db.Balance.Combat);

    [Fact]
    public void Roll_just_below_hit_chance_hits_and_at_hit_chance_misses()
    {
        new CombatResolver(Rules(), new ScriptedRandom(0.9499, 0.9)).Resolve(WardenOnSlime()).Hit.ShouldBeTrue();

        var rng = new ScriptedRandom(0.95);
        var miss = new CombatResolver(Rules(), rng).Resolve(WardenOnSlime());
        miss.Hit.ShouldBeFalse();
        miss.Crit.ShouldBeFalse();
        miss.Damage.ShouldBe(0);
        rng.Remaining.ShouldBe(0); // a miss draws no crit roll
    }

    [Fact]
    public void Hit_chance_is_clamped_to_balance_minimum_for_a_hopeless_attacker()
    {
        // A level-1 novice (hit 1 + 1 + 10 = 12) against a boar whose flee is overridden to 1000:
        // (12 − 1000 + 80) / 100 = −9.08 → 0.05 (formula clamp and balance min_hit_chance agree).
        var novice = Character("job.novice", 1, 1, 1, 1, 1, 1, 1, 1);
        var boar = Monster("monster.thornback_boar");
        boar.Modifiers.Add("test", DerivedStats.Flee, ModifierOp.Override, 1000);
        var ctx = Attack(novice, null, 0, "monster.thornback_boar", boar);
        ctx.Breakdown = new DamageBreakdown();

        new CombatResolver(Rules(), new ScriptedRandom(0.0499, 0.9)).Resolve(ctx).Hit.ShouldBeTrue();
        ctx.Breakdown.TryGet(BreakdownSteps.HitChance, out var chance).ShouldBeTrue();
        chance.ShouldBe(0.05, 1e-12);
        new CombatResolver(Rules(), new ScriptedRandom(0.05)).Resolve(ctx).Hit.ShouldBeFalse();
    }

    [Fact]
    public void Balance_hit_chance_of_one_always_hits_and_zero_always_misses()
    {
        var always = Rules(balance: BalanceWith(b => b with { MinHitChance = 1, MaxHitChance = 1 }));
        new CombatResolver(always, new ScriptedRandom(0.9999999, 0.5)).Resolve(WardenOnSlime()).Hit.ShouldBeTrue();

        var never = Rules(balance: BalanceWith(b => b with { MinHitChance = 0, MaxHitChance = 0 }));
        new CombatResolver(never, new ScriptedRandom(0.0)).Resolve(WardenOnSlime()).Hit.ShouldBeFalse();
    }

    [Fact]
    public void Crit_path_applies_the_crit_multiplier()
    {
        // crit_chance 0.02 (golden test). Roll 0.0199 crits; with a 1.5 multiplier: 70 × 1.5 = 105.
        var ctx = WardenOnSlime();
        ctx.Breakdown = new DamageBreakdown();
        var r = new CombatResolver(Rules(critMultiplier: "1.5"), new ScriptedRandom(0.1, 0.0199)).Resolve(ctx);
        r.Crit.ShouldBeTrue();
        r.Damage.ShouldBe(105);
        ctx.Breakdown.TryGet(BreakdownSteps.Crit, out var m).ShouldBeTrue();
        m.ShouldBe(1.5);

        // Roll at the chance: no crit, multiplier 1.
        var normal = new CombatResolver(Rules(critMultiplier: "1.5"), new ScriptedRandom(0.1, 0.02)).Resolve(WardenOnSlime());
        normal.Crit.ShouldBeFalse();
        normal.Damage.ShouldBe(70);
    }

    [Fact]
    public void Crit_without_content_multiplier_deals_normal_damage()
    {
        var r = new CombatResolver(Rules(), new ScriptedRandom(0.1, 0.0)).Resolve(WardenOnSlime());
        r.Crit.ShouldBeTrue();
        r.Damage.ShouldBe(70);
    }

    [Fact]
    public void Crit_chance_is_clamped_to_balance_maximum()
    {
        var capped = Rules(balance: BalanceWith(b => b with { MaxCritChance = 0.01 }));
        var ctx = WardenOnSlime();
        ctx.Breakdown = new DamageBreakdown();
        new CombatResolver(capped, new ScriptedRandom(0.1, 0.015)).Resolve(ctx).Crit.ShouldBeFalse(); // formula says 0.02
        ctx.Breakdown.TryGet(BreakdownSteps.CritChance, out var c).ShouldBeTrue();
        c.ShouldBe(0.01);
    }

    [Fact]
    public void Min_damage_applies_to_landed_hits()
    {
        // Level-1 novice, all stats 1: atk = 1. On a rock beetle (def 12): max(1, floor(100 / 136)) = 1.
        // Pretend the beetle is ghost: neutral vs ghost = 0.25 → raw 0.25 → floor 0 → min_damage 1.
        var novice = Character("job.novice", 1, 1, 1, 1, 1, 1, 1, 1);
        var ctx = Attack(novice, null, 0, "monster.rock_beetle");
        ctx.DefenderElement = Elements.Ghost;
        ctx.Breakdown = new DamageBreakdown();
        var r = new CombatResolver(Rules(), new ScriptedRandom(0.0, 0.9)).Resolve(ctx);
        r.Hit.ShouldBeTrue();
        r.Multiplier.ShouldBe(0.25);
        r.Damage.ShouldBe(1);
        ctx.Breakdown.TryGet(BreakdownSteps.Raw, out var raw).ShouldBeTrue();
        raw.ShouldBe(0.25);
    }

    [Fact]
    public void Max_damage_caps_a_hit()
    {
        var capped = Rules(balance: BalanceWith(b => b with { MaxDamage = 50 }));
        new CombatResolver(capped, new ScriptedRandom(0.1, 0.9)).Resolve(WardenOnSlime()).Damage.ShouldBe(50);
    }

    [Fact]
    public void Immune_element_deals_zero_even_with_min_damage()
    {
        // holy vs holy = 0 in elements.yaml.
        var ctx = WardenOnSlime();
        ctx.AttackerElement = Elements.Holy;
        ctx.DefenderElement = Elements.Holy;
        var r = new CombatResolver(Rules(), new ScriptedRandom(0.1, 0.9)).Resolve(ctx);
        r.Hit.ShouldBeTrue();
        r.Multiplier.ShouldBe(0);
        r.Damage.ShouldBe(0);
    }

    [Fact]
    public void Negative_element_multiplier_heals()
    {
        var table = new ElementTableDef
        {
            Multipliers = new Dictionary<string, IReadOnlyDictionary<string, double>>
            {
                ["neutral"] = new Dictionary<string, double> { ["neutral"] = 1, ["water"] = -0.5 },
                ["water"] = new Dictionary<string, double> { ["neutral"] = 1, ["water"] = 1 },
            },
        };
        var rules = CombatRules.Compile(Db.Formulas, table, Db.Balance.Combat);
        // 70 × −0.5 = −35.
        new CombatResolver(rules, new ScriptedRandom(0.1, 0.9)).Resolve(WardenOnSlime()).Damage.ShouldBe(-35);
    }

    [Theory]
    [InlineData("fire", "wind", 1.5)]
    [InlineData("water", "fire", 1.5)]
    [InlineData("holy", "undead", 2)]
    [InlineData("shadow", "undead", 0)]
    [InlineData("neutral", "ghost", 0.25)]
    [InlineData("ghost", "neutral", 0.25)]
    [InlineData("earth", "earth", 0.5)]
    [InlineData("neutral", "neutral", 1)]
    public void Element_table_lookup(string attack, string defend, double expected) =>
        Rules().ElementMultiplier(attack, defend).ShouldBe(expected);

    [Fact]
    public void Every_element_pair_of_the_vocabulary_is_in_the_table()
    {
        var rules = Rules();
        foreach (var a in Elements.All)
            foreach (var d in Elements.All)
                Should.NotThrow(() => rules.ElementMultiplier(a, d), a + " → " + d);
    }

    [Fact]
    public void Unknown_element_throws()
    {
        Should.Throw<KeyNotFoundException>(() => Rules().ElementMultiplier("element.nope", Elements.Fire));
    }

    [Fact]
    public void Incomplete_element_table_is_rejected_at_compile()
    {
        var table = new ElementTableDef
        {
            Multipliers = new Dictionary<string, IReadOnlyDictionary<string, double>>
            {
                ["neutral"] = new Dictionary<string, double> { ["neutral"] = 1, ["fire"] = 1 },
                ["fire"] = new Dictionary<string, double> { ["fire"] = 0.5 },
            },
        };
        Should.Throw<ArgumentException>(() => CombatRules.Compile(Db.Formulas, table, Db.Balance.Combat));
    }

    [Fact]
    public void Attack_element_comes_from_effect_then_attacker_then_neutral()
    {
        var ctx = WardenOnSlime();
        ctx.Element.ShouldBe(Elements.Neutral);
        ctx.AttackerElement = Elements.Earth;
        ctx.Element.ShouldBe(Elements.Earth);
        ctx.Effect = DamageEffectOf("skill.ember_bolt");
        ctx.Element.ShouldBe(Elements.Fire);
        ctx.Effect = DamageEffectOf("skill.twin_strike"); // no element of its own
        ctx.Element.ShouldBe(Elements.Earth);
    }

    [Fact]
    public void Size_hook_scales_damage_after_the_element_and_is_recorded()
    {
        // Gel slime is small: 70 × 1.2 = 84.
        var ctx = WardenOnSlime();
        ctx.Modifiers = new IDamageModifier[]
        {
            new SizeMultiplier("test.vs_small", MonsterSizes.Small, 1.2),
            new SizeMultiplier("test.vs_large", MonsterSizes.Large, 3),
        };
        ctx.Breakdown = new DamageBreakdown();
        new CombatResolver(Rules(), new ScriptedRandom(0.1, 0.9)).Resolve(ctx).Damage.ShouldBe(84);
        ctx.Breakdown.TryGet("test.vs_small", out var small).ShouldBeTrue();
        small.ShouldBe(1.2);
        ctx.Breakdown.TryGet("test.vs_large", out var large).ShouldBeTrue();
        large.ShouldBe(1);
    }

    [Fact]
    public void Breakdown_names_every_step_in_order()
    {
        var ctx = WardenOnSlime();
        ctx.Breakdown = new DamageBreakdown();
        new CombatResolver(Rules(), new ScriptedRandom(0.1, 0.9)).Resolve(ctx);

        ctx.Breakdown.Steps.Select(s => s.Name).ShouldBe(new[]
        {
            BreakdownSteps.HitChance, BreakdownSteps.HitRoll, BreakdownSteps.CritChance, BreakdownSteps.CritRoll,
            BreakdownSteps.Base, BreakdownSteps.Defense, BreakdownSteps.Skill, BreakdownSteps.Element,
            BreakdownSteps.Crit, BreakdownSteps.Raw, BreakdownSteps.Final,
        });
        ctx.Breakdown.Steps.Select(s => s.Value).ShouldBe(new[] { 0.95, 0.1, 0.02, 0.9, 70, 70, 1, 1, 1, 70, 70 }, 1e-12);
    }

    [Fact]
    public void Miss_breakdown_ends_with_final_zero()
    {
        var ctx = WardenOnSlime();
        ctx.Breakdown = new DamageBreakdown();
        new CombatResolver(Rules(), new ScriptedRandom(0.99)).Resolve(ctx);
        ctx.Breakdown.Steps.Select(s => s.Name).ShouldBe(new[] { BreakdownSteps.HitChance, BreakdownSteps.HitRoll, BreakdownSteps.Final });
        ctx.Breakdown[2].Value.ShouldBe(0);
    }

    [Fact]
    public void Breakdown_is_optional_and_does_not_change_the_result()
    {
        var with = WardenOnSlime();
        with.Breakdown = new DamageBreakdown();
        var a = new CombatResolver(Rules(), new ScriptedRandom(0.1, 0.9)).Resolve(with);
        var b = new CombatResolver(Rules(), new ScriptedRandom(0.1, 0.9)).Resolve(WardenOnSlime());

        a.Breakdown.ShouldBeSameAs(with.Breakdown);
        b.Breakdown.ShouldBeNull();
        (a.Hit, a.Crit, a.Element, a.Multiplier, a.Damage).ShouldBe((b.Hit, b.Crit, b.Element, b.Multiplier, b.Damage));
    }

    [Fact]
    public void Reused_breakdown_is_cleared_each_call()
    {
        var ctx = WardenOnSlime();
        ctx.Breakdown = new DamageBreakdown();
        var resolver = new CombatResolver(Rules(), new ScriptedRandom(0.1, 0.9, 0.1, 0.9));
        resolver.Resolve(ctx);
        var count = ctx.Breakdown.Count;
        resolver.Resolve(ctx);
        ctx.Breakdown.Count.ShouldBe(count);
    }
}
