using System.Diagnostics;
using Game.Content.Loading;
using Game.Content.Registry;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Vocabulary;
using Xunit.Abstractions;

namespace Game.Tests.Formulas;

/// <summary>
/// Compiles every formula string in /content. Stands in for the load-time FormulaRule until the
/// T-102 validation framework lands.
/// </summary>
[Collection(TimingCollection.Name)]
public class SampleContentFormulaTests(ITestOutputHelper output)
{
    private static readonly Lazy<ContentDatabase> Db = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
        var result = ContentLoader.Load(Path.Combine(dir.FullName, "content"));
        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        return result.Database!;
    });

    private static IEnumerable<(string Owner, FormulaSource Source)> AllFormulas(ContentDatabase db)
    {
        foreach (var s in FormulaSources.Of(db.Formulas)) yield return ("formulas.yaml", s);
        foreach (var d in db.Skills.All) foreach (var s in FormulaSources.Of(d)) yield return (d.Id, s);
        foreach (var d in db.Items.All) foreach (var s in FormulaSources.Of(d)) yield return (d.Id, s);
        foreach (var d in db.Jobs.All) foreach (var s in FormulaSources.Of(d)) yield return (d.Id, s);
        foreach (var d in db.Statuses.All) foreach (var s in FormulaSources.Of(d)) yield return (d.Id, s);
    }

    [Fact]
    public void Every_formula_in_sample_content_compiles()
    {
        var all = AllFormulas(Db.Value).ToList();
        var failures = all
            .Select(f => (f.Owner, f.Source, Result: Formula.Parse(f.Source.Text)))
            .Where(f => !f.Result.Success)
            .Select(f => $"{f.Owner} [{f.Source.Path}] '{f.Source.Text}': {string.Join("; ", f.Result.Errors)}")
            .ToList();

        failures.ShouldBeEmpty(string.Join("\n", failures));
        // formulas.yaml alone has 15 derived + 4 combat formulas; skills/items/jobs/statuses add more.
        all.Count.ShouldBeGreaterThan(DerivedStats.All.Count + 4);
        all.ShouldContain(f => f.Source.Path == "effects[0].effects[0].multiplier"); // nested area effect (skill.shockwave)
        all.ShouldContain(f => f.Source.Path.StartsWith("equip.modifiers[", StringComparison.Ordinal));
        all.ShouldContain(f => f.Source.Path.StartsWith("stat_bonuses[", StringComparison.Ordinal));
        all.ShouldContain(f => f.Source.Path.StartsWith("on_tick[", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_derived_stat_has_a_formula()
    {
        foreach (var stat in DerivedStats.All) Db.Value.Formulas.Derived.ShouldContainKey(stat);
    }

    [Fact]
    public void Formula_using_foo_fails_with_owner_file_and_path()
    {
        var db = Db.Value;
        var item = db.Items.All.First(i => i.Equip != null);
        var broken = item with
        {
            Equip = item.Equip! with { Modifiers = new[] { new ModifierDef(PrimaryStats.Str, ModifierOp.Flat, "foo + 1") } },
        };

        var source = FormulaSources.Of(broken).ShouldHaveSingleItem();
        source.Path.ShouldBe("equip.modifiers[0].value");
        var error = Formula.Parse(source.Text).Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(FormulaErrorCodes.UnknownVariable);
        error.Position.ShouldBe(0);
        db.SourceOf(item.Id).ShouldNotBeNull().File.ShouldEndWith(".yaml");
    }

    [Fact]
    public void Sources_walk_nested_effects_and_modifiers()
    {
        var skill = new SkillDef
        {
            Id = "skill.test",
            SpCost = "10 + level",
            Effects = new EffectDef[]
            {
                new HealEffect { Amount = "int" },
                new AreaEffect
                {
                    Effects = new EffectDef[]
                    {
                        new KnockbackEffect(),
                        new ApplyStatusEffect { Chance = "0.5", DurationMs = "1000" },
                        new ModifyStatEffect { Modifier = new ModifierDef("def", ModifierOp.Percent, "level * 2") },
                    },
                },
            },
            PassiveModifiers = new[] { new ModifierDef("str", ModifierOp.Flat, "1") },
        };

        FormulaSources.Of(skill).Select(s => s.Path).ShouldBe(new[]
        {
            "sp_cost", "cast_time_ms", "cooldown_ms",
            "effects[0].amount",
            "effects[1].effects[1].chance", "effects[1].effects[1].duration_ms",
            "effects[1].effects[2].modifier.value",
            "passive_modifiers[0].value",
        });
    }

    // Golden values for content/formulas.yaml (hand-computed). Update them only with a deliberate balance change.
    [Theory]
    [InlineData("max_hp", 120, "base_level", 10, "vit", 0)]
    [InlineData("max_hp", 120, "base_level", 10, "int", 99)]
    [InlineData("atk", 15, "str", 10, "dex", 12, "luk", 7, "base_level", 9)]
    [InlineData("hit", 61, "base_level", 20, "dex", 31)]
    [InlineData("cast_time_mod", 0.2, "dex", 150)]
    [InlineData("cast_time_mod", 1, "dex", 0)]
    [InlineData("aspd", 3, "agi", 300)]
    [InlineData("move_speed", 4.5, "agi", 100)]
    public void Derived_formulas_golden_values(string stat, double expected, params object[] vars)
    {
        var context = new FormulaContext();
        for (var i = 0; i < vars.Length; i += 2) context[(string)vars[i]] = Convert.ToDouble(vars[i + 1]);
        Formula.Compile(Db.Value.Formulas.Derived[stat]).Evaluate(context).ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void Combat_formulas_golden_values()
    {
        var f = Db.Value.Formulas;
        var context = new FormulaContext();
        context["atk"] = 30;
        context["weapon_atk"] = 20;
        context["refine"] = 5;
        context["t_def"] = 10;
        Formula.Compile(f.PhysicalDamage).Evaluate(context).ShouldBe(46); // floor(60 * 100 / 130)

        context.Clear();
        Formula.Compile(f.PhysicalDamage).Evaluate(context).ShouldBe(1); // max(1, ...)

        context["matk"] = 50;
        Formula.Compile(f.MagicDamage).Evaluate(context).ShouldBe(50);

        context["hit"] = 100;
        context["t_flee"] = 50;
        Formula.Compile(f.HitChance).Evaluate(context).ShouldBe(0.95); // capped
        context["hit"] = 50;
        context["t_flee"] = 100;
        Formula.Compile(f.HitChance).Evaluate(context).ShouldBe(0.3, 1e-12);

        context["crit"] = 10;
        Formula.Compile(f.CritChance).Evaluate(context).ShouldBe(0.1, 1e-12);
    }

    [Fact]
    public void Damage_formula_is_fast_and_allocation_free()
    {
        var formula = Formula.Compile(Db.Value.Formulas.PhysicalDamage);
        var context = new FormulaContext();
        context["atk"] = 30;
        context["weapon_atk"] = 20;
        context["t_def"] = 10;

        double sum = 0;
        for (var i = 0; i < 10_000; i++) sum += formula.Evaluate(context); // warm up the JIT

        var refine = FormulaSymbols.Default.SlotOf("refine");
        var watch = new Stopwatch();
        watch.Start();
        for (var i = 0; i < 1_000_000; i++)
        {
            context[refine] = i & 15;
            sum += formula.Evaluate(context);
        }
        watch.Stop();
        var allocated = AllocationProbe.MinAllocatedBytes(() =>
        {
            for (var i = 0; i < 100_000; i++)
            {
                context[refine] = i & 15;
                sum += formula.Evaluate(context);
            }
        });

        output.WriteLine($"1M evaluations: {watch.Elapsed.TotalMilliseconds:F0} ms, {allocated} bytes allocated");
        sum.ShouldBeGreaterThan(0);
        allocated.ShouldBe(0);
        // Target from the task: 1M evaluations under ~1 s in Release (measured ~190 ms; see the T-103 reply).
        // The time is logged, not asserted: wall-clock limits in a parallel Debug test run are flaky.
        // The zero-allocation assertion above is the deterministic guard.
    }
}
