using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;

namespace Game.Tests.StatPipeline; // not Game.Tests.Stats: that would shadow Vocabulary.Stats in sibling test namespaces

/// <summary>
/// The §6.1 pipeline on small hand-written formula sets: stacking, removal, override precedence,
/// caching/versioning, derived ordering and load errors.
/// </summary>
[Collection(TimingCollection.Name)]
public class StatPipelineTests
{
    private static StatSheet Sheet(params (string Stat, string Formula)[] derived)
    {
        var set = new FormulaSetDef { Derived = derived.ToDictionary(d => d.Stat, d => d.Formula) };
        return new StatSheet(DerivedStatFormulas.Compile(set));
    }

    [Fact]
    public void Flat_and_percent_stack_as_base_plus_flat_times_one_plus_percent()
    {
        var sheet = Sheet();
        sheet.Base[PrimaryStats.Str] = 10;
        sheet.Modifiers.Add("equip:weapon", PrimaryStats.Str, ModifierOp.Flat, 3);
        sheet.Modifiers.Add("equip:armor", PrimaryStats.Str, ModifierOp.Flat, 2);
        sheet.Modifiers.Add("status:a", PrimaryStats.Str, ModifierOp.Percent, 10);
        sheet.Modifiers.Add("status:b", PrimaryStats.Str, ModifierOp.Percent, 20);

        // (10 + 3 + 2) × (1 + 0.10 + 0.20) = 15 × 1.3 = 19.5 → floor 19
        sheet.Get(PrimaryStats.Str).ShouldBe(19);
    }

    [Fact]
    public void Primary_stats_floor_at_the_end_with_tolerance_for_binary_fractions()
    {
        var sheet = Sheet();
        sheet.Base[PrimaryStats.Agi] = 10;
        sheet.Modifiers.Add("s", PrimaryStats.Agi, ModifierOp.Percent, 30);
        sheet.Get(PrimaryStats.Agi).ShouldBe(13); // 10 × 130 / 100 = 13 exactly, not 12.999…

        sheet.Modifiers.Add("t", PrimaryStats.Agi, ModifierOp.Percent, 2.5);
        sheet.Get(PrimaryStats.Agi).ShouldBe(13); // 10 × 132.5 / 100 = 13.25 → 13

        sheet.Base[PrimaryStats.Vit] = 5;
        sheet.Modifiers.Add("u", PrimaryStats.Vit, ModifierOp.Flat, -8);
        sheet.Get(PrimaryStats.Vit).ShouldBe(-3); // no clamping: content decides whether negatives can happen
    }

    [Fact]
    public void Removing_a_source_restores_exact_prior_values()
    {
        var sheet = Sheet((DerivedStats.Aspd, "1 + agi * 0.01"), (DerivedStats.Atk, "str * 2"));
        sheet.Base[PrimaryStats.Str] = 7;
        sheet.Base[PrimaryStats.Agi] = 13;
        sheet.Modifiers.Add("job:x", PrimaryStats.Agi, ModifierOp.Flat, 1);
        var before = Enumerable.Range(0, StatIndex.Count).Select(sheet.Get).ToArray();

        sheet.Modifiers.Add("status:s", PrimaryStats.Agi, ModifierOp.Percent, 15);
        sheet.Modifiers.Add("status:s", DerivedStats.Aspd, ModifierOp.Percent, 7.5);
        sheet.Modifiers.Add("status:s", DerivedStats.Atk, ModifierOp.Override, 999);
        sheet.Modifiers.Add("status:s", PrimaryStats.Str, ModifierOp.Flat, 0.3);
        sheet.Get(DerivedStats.Aspd).ShouldNotBe(before[StatIndex.IndexOf(DerivedStats.Aspd)]);

        sheet.Modifiers.RemoveBySource("status:s").ShouldBe(4);
        var after = Enumerable.Range(0, StatIndex.Count).Select(sheet.Get).ToArray();
        after.ShouldBe(before); // bit-for-bit: recompute is from scratch, never incremental add/subtract
    }

    [Fact]
    public void RemoveBySource_keeps_other_sources_in_order_and_reports_count()
    {
        var set = new ModifierSet();
        set.Add("a", PrimaryStats.Str, ModifierOp.Flat, 1);
        set.Add("b", PrimaryStats.Str, ModifierOp.Flat, 2);
        set.Add("a", PrimaryStats.Agi, ModifierOp.Flat, 3);
        set.Add("c", PrimaryStats.Agi, ModifierOp.Flat, 4);
        var version = set.Version;

        set.RemoveBySource("missing").ShouldBe(0);
        set.Version.ShouldBe(version); // nothing changed, nothing to recompute

        set.RemoveBySource("a").ShouldBe(2);
        set.Version.ShouldBeGreaterThan(version);
        set.Count.ShouldBe(2);
        set[0].SourceId.ShouldBe("b");
        set[1].SourceId.ShouldBe("c");
        set.HasSource("a").ShouldBeFalse();
    }

    [Fact]
    public void Override_beats_flat_and_percent_and_the_latest_override_wins()
    {
        var sheet = Sheet((DerivedStats.MoveSpeed, "3.5 + agi * 0.01"));
        sheet.Base[PrimaryStats.Agi] = 50;
        sheet.Modifiers.Add("status:swift", DerivedStats.MoveSpeed, ModifierOp.Flat, 1);
        sheet.Modifiers.Add("status:swift", DerivedStats.MoveSpeed, ModifierOp.Percent, 20);
        sheet.Get(DerivedStats.MoveSpeed).ShouldBe(6.0, 1e-12); // (4 + 1) × 1.2

        sheet.Modifiers.Add("status:root", DerivedStats.MoveSpeed, ModifierOp.Override, 0);
        sheet.Get(DerivedStats.MoveSpeed).ShouldBe(0);

        sheet.Modifiers.Add("status:haste", DerivedStats.MoveSpeed, ModifierOp.Override, 9);
        sheet.Get(DerivedStats.MoveSpeed).ShouldBe(9); // latest override wins

        sheet.Modifiers.RemoveBySource("status:haste");
        sheet.Get(DerivedStats.MoveSpeed).ShouldBe(0); // earlier override takes over again

        sheet.Modifiers.RemoveBySource("status:root");
        sheet.Get(DerivedStats.MoveSpeed).ShouldBe(6.0, 1e-12);
    }

    [Fact]
    public void Override_on_a_primary_stat_feeds_derived_formulas()
    {
        var sheet = Sheet((DerivedStats.Atk, "str * 2"));
        sheet.Base[PrimaryStats.Str] = 10;
        sheet.Modifiers.Add("s", PrimaryStats.Str, ModifierOp.Flat, 5);
        sheet.Modifiers.Add("curse", PrimaryStats.Str, ModifierOp.Override, 1);
        sheet.Get(PrimaryStats.Str).ShouldBe(1);
        sheet.Get(DerivedStats.Atk).ShouldBe(2);
    }

    [Fact]
    public void Derived_stats_take_formula_value_then_their_own_modifiers()
    {
        var sheet = Sheet((DerivedStats.Def, "floor(vit / 2) + floor(base_level / 5)"));
        sheet.Base.BaseLevel = 12;
        sheet.Base[PrimaryStats.Vit] = 9;
        sheet.Modifiers.Add("job", PrimaryStats.Vit, ModifierOp.Flat, 2);   // vit 11
        sheet.Modifiers.Add("equip:body", DerivedStats.Def, ModifierOp.Flat, 4);
        sheet.Modifiers.Add("status:skin", DerivedStats.Def, ModifierOp.Percent, 30);

        // formula: floor(11 / 2) + floor(12 / 5) = 5 + 2 = 7; then (7 + 4) × 1.3 = 14.3 (not rounded)
        sheet.Get(DerivedStats.Def).ShouldBe(14.3, 1e-12);
        sheet.GetInt(DerivedStats.Def).ShouldBe(14);
    }

    [Fact]
    public void Derived_stat_without_a_formula_is_zero_plus_modifiers()
    {
        var sheet = Sheet();
        sheet.Get(DerivedStats.Crit).ShouldBe(0);
        sheet.Modifiers.Add("card", DerivedStats.Crit, ModifierOp.Flat, 5);
        sheet.Get(DerivedStats.Crit).ShouldBe(5);
    }

    [Fact]
    public void Derived_formulas_may_read_other_derived_stats_after_their_modifiers()
    {
        // Declared "backwards" on purpose: hit depends on atk, which must be evaluated first.
        var sheet = Sheet((DerivedStats.Hit, "atk + 1"), (DerivedStats.Atk, "str"));
        sheet.Base[PrimaryStats.Str] = 10;
        sheet.Modifiers.Add("weapon", DerivedStats.Atk, ModifierOp.Flat, 5);
        sheet.Get(DerivedStats.Hit).ShouldBe(16); // (10 + 5) + 1
    }

    [Fact]
    public void Derived_formula_cycles_are_load_errors()
    {
        var set = new FormulaSetDef { Derived = new Dictionary<string, string> { ["atk"] = "hit", ["hit"] = "atk" } };
        Should.Throw<ArgumentException>(() => DerivedStatFormulas.Compile(set)).Message.ShouldContain("cycle");

        var self = new FormulaSetDef { Derived = new Dictionary<string, string> { ["atk"] = "atk + 1" } };
        Should.Throw<ArgumentException>(() => DerivedStatFormulas.Compile(self));
    }

    [Theory]
    [InlineData("str", "1")]               // primary stats are not derived
    [InlineData("not_a_stat", "1")]
    [InlineData("atk", "weapon_atk + str")] // not supplied by a stat sheet
    [InlineData("def", "t_def")]
    [InlineData("hit", "level * 2")]
    public void Unsupported_derived_keys_and_variables_are_load_errors(string stat, string formula)
    {
        var set = new FormulaSetDef { Derived = new Dictionary<string, string> { [stat] = formula } };
        Should.Throw<ArgumentException>(() => DerivedStatFormulas.Compile(set));
    }

    [Fact]
    public void Version_changes_on_every_input_change_and_values_are_cached_between()
    {
        var sheet = Sheet((DerivedStats.Atk, "str"));
        var v0 = sheet.Version;

        sheet.Base[PrimaryStats.Str] = 3;
        var v1 = sheet.Version;
        v1.ShouldBeGreaterThan(v0);

        sheet.Base[PrimaryStats.Str] = 3; // same value: no change
        sheet.Version.ShouldBe(v1);

        sheet.Base.BaseLevel = 2;
        sheet.Version.ShouldBeGreaterThan(v1);
        var v2 = sheet.Version;

        sheet.Modifiers.Add("a", PrimaryStats.Str, ModifierOp.Flat, 1);
        sheet.Version.ShouldBeGreaterThan(v2);
        sheet.Get(DerivedStats.Atk).ShouldBe(4);

        sheet.Modifiers.Clear();
        sheet.Get(DerivedStats.Atk).ShouldBe(3);
    }

    [Fact]
    public void Compiled_modifier_values_are_evaluated_once_against_the_source_context()
    {
        var staffMatk = CompiledModifier.Compile(new ModifierDef(DerivedStats.Matk, ModifierOp.Flat, "10 + refine * 3"));
        var context = new FormulaContext();
        context[FormulaVariables.Refine] = 4;

        var sheet = Sheet();
        sheet.Modifiers.Add("equip:weapon", staffMatk, context);
        context[FormulaVariables.Refine] = 9; // later changes to the context do not leak in
        sheet.Get(DerivedStats.Matk).ShouldBe(22);

        // Re-refining = remove the source and add it again with the new context.
        sheet.Modifiers.RemoveBySource("equip:weapon");
        sheet.Modifiers.Add("equip:weapon", staffMatk, context);
        sheet.Get(DerivedStats.Matk).ShouldBe(37);
    }

    [Fact]
    public void Compiling_a_modifier_with_an_unknown_stat_throws()
    {
        Should.Throw<ArgumentException>(() => CompiledModifier.Compile(new ModifierDef("power", ModifierOp.Flat, "1")));
    }

    [Fact]
    public void WriteTo_fills_caster_or_target_variables_for_combat_formulas()
    {
        var sheet = Sheet((DerivedStats.Def, "vit"));
        sheet.Base.BaseLevel = 7;
        sheet.Base.JobLevel = 3;
        sheet.Base[PrimaryStats.Vit] = 12;

        var context = new FormulaContext();
        sheet.WriteTo(context);
        sheet.WriteTo(context, asTarget: true);

        context[DerivedStats.Def].ShouldBe(12);
        context[FormulaVariables.BaseLevel].ShouldBe(7);
        context[FormulaVariables.JobLevel].ShouldBe(3);
        context["t_def"].ShouldBe(12);
        context["t_vit"].ShouldBe(12);
        context["t_base_level"].ShouldBe(7);
        context[FormulaVariables.TargetLevel].ShouldBe(7);

        var damage = Formula.Compile("max(1, floor((atk + weapon_atk) * 100 / (100 + t_def * 3)))");
        context[FormulaVariables.WeaponAtk] = 20;
        damage.Evaluate(context).ShouldBe(14); // atk 0: floor(20 × 100 / 136) = 14
    }

    [Fact]
    public void WriteTo_works_with_a_narrower_symbol_table()
    {
        var sheet = Sheet();
        sheet.Base[PrimaryStats.Luk] = 9;
        var context = new FormulaContext(new FormulaSymbols(new VocabularySet("luk", "t_luk")));
        sheet.WriteTo(context, asTarget: true);
        context["t_luk"].ShouldBe(9);
        context["luk"].ShouldBe(0);
    }

    /// <summary>
    /// Measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/> around 1,000 forced recomputes on the real
    /// formula set shape (15 derived formulas) with 12 modifiers. Guards the "per entity, per change" budget.
    /// </summary>
    [Fact]
    public void Recompute_allocates_nothing()
    {
        var sheet = Sheet(
            ("max_hp", "floor((40 + base_level * 8) * (1 + vit * 0.01))"), ("max_sp", "floor((10 + base_level * 3) * (1 + int * 0.01))"),
            ("atk", "str + floor(dex / 5) + floor(luk / 5) + floor(base_level / 4)"), ("matk", "floor(int * 1.5) + floor(base_level / 4)"),
            ("def", "floor(vit / 2) + floor(base_level / 5)"), ("mdef", "floor(int / 3) + floor(vit / 5)"),
            ("hit", "base_level + dex + 10"), ("flee", "base_level + agi + 10"), ("crit", "1 + luk * 0.3"),
            ("aspd", "min(3, 1 + agi * 0.01 + dex * 0.002)"), ("move_speed", "min(6, 3.5 + agi * 0.01)"),
            ("cast_time_mod", "clamp(1 - dex / 150, 0.2, 1)"), ("hp_regen", "0.2 + vit * 0.05 + base_level * 0.02"),
            ("sp_regen", "0.1 + int * 0.03 + base_level * 0.01"), ("weight_limit", "2000 + str * 30 + base_level * 10"));
        for (var i = 0; i < 12; i++) sheet.Modifiers.Add("src" + (i % 4), i % StatIndex.Count, (ModifierOp)(i % 2), i);
        var maxHp = StatIndex.IndexOf(DerivedStats.MaxHp);
        AllocationProbe.MinAllocatedBytes(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                sheet.Base.BaseLevel = i;
                sheet.Get(maxHp);
            }
        }).ShouldBe(0);
    }
}
