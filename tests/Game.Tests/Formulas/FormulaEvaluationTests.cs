using Game.Shared.Formulas;

namespace Game.Tests.Formulas;

public class FormulaEvaluationTests
{
    private static double Eval(string text, params (string Name, double Value)[] vars)
    {
        var context = new FormulaContext();
        foreach (var (name, value) in vars) context[name] = value;
        return Formula.Compile(text).Evaluate(context);
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]
    [InlineData("100 / 10 / 5", 2)]
    [InlineData("7 % 3", 1)]
    [InlineData("-3", -3)]
    [InlineData("- -3", 3)]
    [InlineData("+4", 4)]
    [InlineData("2 * -3", -6)]
    [InlineData(".5 + 0.25", 0.75)]
    [InlineData("  60000 ", 60000)]
    public void Arithmetic_follows_usual_precedence(string text, double expected) =>
        Eval(text).ShouldBe(expected);

    [Theory]
    [InlineData("str > 5 ? 1 : 2", 1)]
    [InlineData("str < 5 ? 1 : 2", 2)]
    [InlineData("str >= 10 ? 1 : str >= 5 ? 2 : 3", 2)]
    [InlineData("(str == 7) + (str != 7) * 10", 1)]
    [InlineData("str <= 7 && agi > 0", 1)]
    [InlineData("str <= 7 && agi > 1", 0)]
    [InlineData("str > 100 || agi == 1", 1)]
    [InlineData("!str", 0)]
    [InlineData("!(str - 7)", 1)]
    [InlineData("1 + (str > 3) * 4", 5)]
    public void Ternary_comparison_and_logic(string text, double expected) =>
        Eval(text, ("str", 7), ("agi", 1)).ShouldBe(expected);

    [Fact]
    public void Ternary_and_logic_evaluate_only_needed_operands()
    {
        var context = new CountingContext();
        var formula = Formula.Compile("str > 0 ? agi : vit");
        formula.Evaluate(context);
        context.Reads.ShouldBe(new[] { "str", "vit" });

        context.Reads.Clear();
        Formula.Compile("str && agi").Evaluate(context);
        context.Reads.ShouldBe(new[] { "str" });

        context.Reads.Clear();
        Formula.Compile("1 + str || agi").Evaluate(context);
        context.Reads.ShouldBe(new[] { "str" });
    }

    [Theory]
    [InlineData("min(3, 1, 2)", 1)]
    [InlineData("max(3, 1, 2)", 3)]
    [InlineData("min(4, -1)", -1)]
    [InlineData("floor(2.7)", 2)]
    [InlineData("floor(-2.2)", -3)]
    [InlineData("ceil(2.1)", 3)]
    [InlineData("round(2.5)", 3)]
    [InlineData("round(-2.5)", -3)]
    [InlineData("round(2.4)", 2)]
    [InlineData("clamp(5, 0, 3)", 3)]
    [InlineData("clamp(-5, 0, 3)", 0)]
    [InlineData("clamp(2, 0, 3)", 2)]
    [InlineData("clamp(2, 5, 3)", 5)]
    [InlineData("pow(2, 10)", 1024)]
    [InlineData("sqrt(81)", 9)]
    public void Functions(string text, double expected) =>
        Eval(text).ShouldBe(expected);

    [Theory]
    [InlineData("10 / 0", 0)]
    [InlineData("str / (agi - agi)", 0)]
    [InlineData("str % 0", 0)]
    [InlineData("sqrt(-4)", 0)]
    [InlineData("pow(10, 400)", 0)]
    [InlineData("pow(-8, 0.5)", 0)]
    [InlineData("pow(0, -1)", 0)]
    [InlineData("1 + 10 / 0", 1)]
    public void Undefined_arithmetic_is_zero(string text, double expected) =>
        Eval(text, ("str", 5), ("agi", 3)).ShouldBe(expected);

    [Fact]
    public void Non_finite_variable_values_read_as_zero()
    {
        Eval("str + 1", ("str", double.NaN)).ShouldBe(1);
        Eval("str + 1", ("str", double.PositiveInfinity)).ShouldBe(1);
        Eval("str * str", ("str", 1e200)).ShouldBe(0);
    }

    [Fact]
    public void Unset_variables_are_zero() => Eval("str + agi + t_def").ShouldBe(0);

    [Fact]
    public void Target_and_skill_variables_resolve()
    {
        Eval("t_def * 2 + level", ("t_def", 4), ("level", 3)).ShouldBe(11);
        Eval("t_level + base_level", ("t_level", 10), ("base_level", 5)).ShouldBe(15);
    }

    [Theory]
    [InlineData("17", 17)]
    [InlineData("60 * 1000", 60000)]
    [InlineData("-3", -3)]
    [InlineData("floor(10 / 3) + max(1, 2)", 5)]
    [InlineData("1 > 0 ? 4 : str", 4)]
    [InlineData("0 ? str : 4", 4)]
    public void Constant_expressions_fold_to_a_single_constant(string text, double expected)
    {
        var formula = Formula.Compile(text);
        formula.IsConstant.ShouldBeTrue();
        formula.TryGetConstant(out var value).ShouldBeTrue();
        value.ShouldBe(expected);
        formula.Evaluate(FormulaContext.None).ShouldBe(expected);
    }

    [Fact]
    public void Formulas_with_live_variables_are_not_constant()
    {
        var formula = Formula.Compile("level * 2");
        formula.IsConstant.ShouldBeFalse();
        formula.TryGetConstant(out _).ShouldBeFalse();
    }

    [Fact]
    public void Constant_factory()
    {
        Formula.Constant(2.5).Evaluate(FormulaContext.None).ShouldBe(2.5);
        Formula.Constant(double.NaN).Evaluate(FormulaContext.None).ShouldBe(0);
        Formula.Constant(1).Variables.Count.ShouldBe(0);
    }

    [Fact]
    public void Variables_lists_each_whitelisted_variable_once_in_order()
    {
        var formula = Formula.Compile("max(1, floor((atk + weapon_atk + refine * 2) * 100 / (100 + t_def * 3))) + atk");
        formula.Variables.Ordered.ShouldBe(new[] { "atk", "weapon_atk", "refine", "t_def" });
        formula.Variables.Contains("t_def").ShouldBeTrue();
        formula.Variables.Contains("str").ShouldBeFalse();
    }

    [Fact]
    public void Variables_survive_constant_folding_of_their_branch() =>
        Formula.Compile("0 ? str : 1").Variables.Ordered.ShouldBe(new[] { "str" });

    [Fact]
    public void Custom_symbols_narrow_the_whitelist_and_define_slots()
    {
        var symbols = new FormulaSymbols(new Game.Shared.Vocabulary.VocabularySet("level", "refine"));
        Formula.Parse("str", symbols).Errors.Single().Code.ShouldBe(FormulaErrorCodes.UnknownVariable);

        var context = new FormulaContext(symbols);
        context["refine"] = 4;
        context[symbols.SlotOf("level")] = 2;
        Formula.Compile("level * 10 + refine", symbols).Evaluate(context).ShouldBe(24);
    }

    [Fact]
    public void Context_clear_resets_values()
    {
        var context = new FormulaContext();
        context["str"] = 9;
        context.Clear();
        context["str"].ShouldBe(0);
        Should.Throw<KeyNotFoundException>(() => context["foo"] = 1);
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var formula = Formula.Compile("clamp((hit - t_flee + 80) / 100, 0.05, 0.95) * pow(1.1, level) + sqrt(luk)");
        var context = new FormulaContext();
        context["hit"] = 77;
        context["t_flee"] = 31;
        context["level"] = 7;
        context["luk"] = 13;
        var first = formula.Evaluate(context);
        for (var i = 0; i < 1000; i++) formula.Evaluate(context).ShouldBe(first);
    }

    /// <summary>Records reads; every variable is 0.</summary>
    private sealed class CountingContext : IFormulaContext
    {
        public List<string> Reads { get; } = new();

        public double GetVariable(int slot)
        {
            Reads.Add(FormulaSymbols.Default.NameOf(slot));
            return 0;
        }
    }
}
