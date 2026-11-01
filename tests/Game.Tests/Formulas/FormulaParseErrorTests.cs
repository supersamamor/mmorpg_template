using Game.Shared.Formulas;
using Game.Shared.Vocabulary;

namespace Game.Tests.Formulas;

public class FormulaParseErrorTests
{
    private static FormulaError SingleError(string? text)
    {
        var result = Formula.Parse(text);
        result.Success.ShouldBeFalse();
        result.Formula.ShouldBeNull();
        return result.Errors.ShouldHaveSingleItem();
    }

    [Fact]
    public void Unknown_variable_is_rejected_with_its_position()
    {
        var error = SingleError("str + foo * 2");
        error.Code.ShouldBe(FormulaErrorCodes.UnknownVariable);
        error.Position.ShouldBe(6);
        error.Length.ShouldBe(3);
        error.Column.ShouldBe(7);
        error.Message.ShouldContain("'foo'");
    }

    [Fact]
    public void All_unknown_names_are_reported_and_listed()
    {
        var result = Formula.Parse("foo + str + bar(1) + baz + foo");
        result.Errors.Select(e => (e.Code, e.Position)).ShouldBe(new[]
        {
            (FormulaErrorCodes.UnknownVariable, 0),
            (FormulaErrorCodes.UnknownFunction, 12),
            (FormulaErrorCodes.UnknownVariable, 21),
            (FormulaErrorCodes.UnknownVariable, 27),
        });
        result.Variables.ShouldBe(new[] { "foo", "str", "baz" });
    }

    [Fact]
    public void Variables_are_case_sensitive() =>
        SingleError("STR").Code.ShouldBe(FormulaErrorCodes.UnknownVariable);

    [Fact]
    public void Function_used_as_variable_gets_a_hint()
    {
        var error = SingleError("min + 1");
        error.Code.ShouldBe(FormulaErrorCodes.UnknownVariable);
        error.Message.ShouldContain("is a function");
    }

    [Theory]
    [InlineData("random(1, 2)", 0)]
    [InlineData("str(1)", 0)]
    [InlineData("1 + abs(str)", 4)]
    [InlineData("Math(1)", 0)]
    public void Unknown_function_is_rejected(string text, int position)
    {
        var error = SingleError(text);
        error.Code.ShouldBe(FormulaErrorCodes.UnknownFunction);
        error.Position.ShouldBe(position);
    }

    [Theory]
    [InlineData("min(1)")]
    [InlineData("max()")]
    [InlineData("floor(1, 2)")]
    [InlineData("clamp(1, 2)")]
    [InlineData("pow(2)")]
    [InlineData("sqrt()")]
    public void Wrong_argument_count_is_rejected(string text) =>
        SingleError(text).Code.ShouldBe(FormulaErrorCodes.ArgumentCount);

    [Theory]
    [InlineData("1 +", 3)]
    [InlineData("(1 + 2", 6)]
    [InlineData("1 + 2)", 5)]
    [InlineData("str agi", 4)]
    [InlineData("1 ? 2", 5)]
    [InlineData("* 2", 0)]
    [InlineData("2 ^ 3", 2)]
    [InlineData("str = 1", 4)]
    [InlineData("1 < 2 < 3", 6)]
    [InlineData("1.", 0)]
    [InlineData("2str", 1)]
    [InlineData("1e5", 1)]
    [InlineData("str.x", 3)]
    [InlineData("min(1,, 2)", 6)]
    [InlineData("str & agi", 4)]
    [InlineData("\"str\"", 0)]
    public void Syntax_errors_report_the_offending_position(string text, int position)
    {
        var error = SingleError(text);
        error.Code.ShouldBe(FormulaErrorCodes.Syntax);
        error.Position.ShouldBe(position);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_formula_is_rejected(string? text) =>
        SingleError(text).Code.ShouldBe(FormulaErrorCodes.Empty);

    [Fact]
    public void Overlong_formula_is_rejected()
    {
        var text = "1" + string.Concat(Enumerable.Repeat(" + 1", FormulaLimits.MaxLength / 4));
        text.Length.ShouldBeGreaterThan(FormulaLimits.MaxLength);
        var error = SingleError(text);
        error.Code.ShouldBe(FormulaErrorCodes.TooLong);
        error.Position.ShouldBe(FormulaLimits.MaxLength);
    }

    [Theory]
    [InlineData("(", ")")]
    [InlineData("-", "")]
    [InlineData("floor(", ")")]
    [InlineData("str + (", ")")]
    public void Deep_nesting_is_rejected_without_overflowing_the_stack(string open, string close)
    {
        var depth = FormulaLimits.MaxDepth + 10;
        var text = string.Concat(Enumerable.Repeat(open, depth)) + "str" + string.Concat(Enumerable.Repeat(close, depth));
        SingleError(text).Code.ShouldBe(FormulaErrorCodes.TooDeep);
    }

    [Fact]
    public void Long_operator_chain_is_rejected_when_too_deep()
    {
        var text = "str" + string.Concat(Enumerable.Repeat(" + str", FormulaLimits.MaxDepth + 1));
        SingleError(text).Code.ShouldBe(FormulaErrorCodes.TooDeep);
    }

    [Fact]
    public void Nesting_at_the_limit_is_allowed()
    {
        var depth = FormulaLimits.MaxDepth - 2;
        var text = string.Concat(Enumerable.Repeat("floor(", depth)) + "str" + new string(')', depth);
        Formula.Parse(text).Success.ShouldBeTrue();
    }

    [Fact]
    public void Constant_chains_fold_and_do_not_count_toward_depth()
    {
        var text = "1" + string.Concat(Enumerable.Repeat(" + 1", 200));
        Formula.Compile(text).Evaluate(FormulaContext.None).ShouldBe(201);
    }

    [Fact]
    public void Compile_throws_with_errors()
    {
        var ex = Should.Throw<FormulaException>(() => Formula.Compile("foo"));
        ex.Errors.ShouldHaveSingleItem().Code.ShouldBe(FormulaErrorCodes.UnknownVariable);
        ex.Message.ShouldContain("column 1");
    }

    [Fact]
    public void Garbage_input_never_throws()
    {
        // Seeded so failures reproduce. System.Random is fine in tests; Game.Shared never uses it.
        var random = new Random(1234);
        const string alphabet = "0123456789.+-*/%()?:,<>=!&| \tstragivitlukdexminaxflorceoundpwsqtclamp_";
        for (var i = 0; i < 5000; i++)
        {
            var chars = new char[random.Next(1, 40)];
            for (var j = 0; j < chars.Length; j++) chars[j] = alphabet[random.Next(alphabet.Length)];
            var result = Formula.Parse(new string(chars));
            if (result.Success) double.IsFinite(result.Formula!.Evaluate(new FormulaContext())).ShouldBeTrue();
            else result.Errors.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void Every_whitelisted_function_is_implemented()
    {
        foreach (var name in FormulaFunctions.All)
        {
            var args = name switch { "clamp" => "1, 2, 3", "min" or "max" or "pow" => "1, 2", _ => "1" };
            Formula.Parse(name + "(" + args + ")").Success.ShouldBeTrue(name);
        }
    }

    [Fact]
    public void Every_whitelisted_variable_resolves()
    {
        foreach (var name in FormulaVariables.All)
        {
            var formula = Formula.Compile(name);
            formula.Variables.Ordered.ShouldBe(new[] { name });
        }
        FormulaSymbols.Default.Count.ShouldBe(FormulaVariables.All.Count);
    }
}
