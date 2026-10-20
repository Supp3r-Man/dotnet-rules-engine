using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

/// <summary>
/// Null semantics (documented in the README): any comparison with a null operand is false, including == and !=;
/// 'not' is applied afterwards, so not (b == 5) is TRUE when b is null. Arithmetic, len(), lower(), upper(), abs(),
/// daysSince() and member paths propagate null. is null / is not null are the only null tests.
/// Every row is evaluated by the compiled expression AND the interpreter, which must agree.
/// </summary>
public class SemanticsTests
{
    private static Rec Pick(string which) => which == "nulls" ? H.Nulls() : H.Values();

    [Theory]
    [InlineData("b == 5", false)]
    [InlineData("b != 5", false)]
    [InlineData("b < 5", false)]
    [InlineData("b >= 5", false)]
    [InlineData("not (b == 5)", true)]
    [InlineData("not (b != 5)", true)]
    [InlineData("b is null", true)]
    [InlineData("b is not null", false)]
    [InlineData("b in [1, 2]", false)]
    [InlineData("b not in [1, 2]", false)]
    [InlineData("not (b in [1, 2])", true)]
    [InlineData("b between 1 and 9", false)]
    [InlineData("b not between 1 and 9", false)]
    [InlineData("name == \"x\"", false)]
    [InlineData("name != \"x\"", false)]
    [InlineData("name startswith \"A\"", false)]
    [InlineData("name endswith \"A\"", false)]
    [InlineData("name contains \"\"", false)]
    [InlineData("name matches \".\"", false)]
    [InlineData("len(name) > 0", false)]
    [InlineData("len(name) is null", true)]
    [InlineData("lower(name) == \"a\"", false)]
    [InlineData("upper(name) is null", true)]
    [InlineData("b + 1 > 0", false)]
    [InlineData("b + 1 is null", true)]
    [InlineData("a + b is null", true)]
    [InlineData("-b is null", true)]
    [InlineData("abs(b) is null", true)]
    [InlineData("flagN", false)]
    [InlineData("not flagN", true)]
    [InlineData("flagN == true", false)]
    [InlineData("flagN == false", false)]
    [InlineData("flagN is null", true)]
    [InlineData("child.value > 1", false)]
    [InlineData("child.label is null", true)]
    [InlineData("child.value is null", true)]
    [InlineData("child.deeper.value > 1", false)]
    [InlineData("maybeColor == \"Red\"", false)]
    [InlineData("maybeColor != \"Red\"", false)]
    [InlineData("maybeColor is null", true)]
    [InlineData("rebate * 2 > 1", false)]
    [InlineData("rebate is null or rebate > 5", true)]
    [InlineData("rebate is not null and rebate > 5", false)]
    [InlineData("stamp is null", true)]
    [InlineData("stamp < now()", false)]
    [InlineData("daysSince(stamp) > 1", false)]
    [InlineData("name + \"x\" is null", true)]
    [InlineData("ratio >= 0 or a > 1", true)]
    [InlineData("a / 0 is null", true)]
    [InlineData("a % 0 is null", true)]
    [InlineData("a / b is null", true)]
    [InlineData("a / 2 == 3", true)]
    [InlineData("a / 2.0 > 3.4", true)]
    [InlineData("b == b", false)]
    [InlineData("b is null and name is null", true)]
    [InlineData("name in [\"a\", \"b\"]", false)]
    [InlineData("\"a\" in [name, \"a\"]", true)]
    [InlineData("\"a\" not in [name, \"b\"]", false)]
    public void Null_semantics_table(string rule, bool expected) =>
        Assert.Equal(expected, H.Eval(rule, H.Nulls()));

    [Theory]
    [InlineData("b == 5", true)]
    [InlineData("b != 5", false)]
    [InlineData("b in [1, 5]", true)]
    [InlineData("b not in [1, 2]", true)]
    [InlineData("b between 1 and 9", true)]
    [InlineData("b not between 1 and 9", false)]
    [InlineData("name == \"Alice\"", true)]
    [InlineData("name == \"alice\"", false)]
    [InlineData("lower(name) == \"alice\"", true)]
    [InlineData("name startswith \"Al\"", true)]
    [InlineData("name startswith \"al\"", false)]
    [InlineData("name endswith \"ce\"", true)]
    [InlineData("name contains \"lic\"", true)]
    [InlineData("name matches \"^A.i\"", true)]
    [InlineData("name matches \"^a\"", false)]
    [InlineData("len(name) == 5", true)]
    [InlineData("name + code == \"AliceX1\"", true)]
    [InlineData("name < \"Bob\" and name > \"Abe\"", true)]
    [InlineData("a + b == 12", true)]
    [InlineData("abs(-a) == 7", true)]
    [InlineData("abs(a - 10) == 3", true)]
    [InlineData("flagN", true)]
    [InlineData("flagN == true", true)]
    [InlineData("not flagN", false)]
    [InlineData("child.value == 11", true)]
    [InlineData("child.deeper.value == 99", true)]
    [InlineData("child.label == \"kid\"", true)]
    [InlineData("maybeColor == \"blue\"", true)]
    [InlineData("color in [\"Red\", \"Green\"]", true)]
    [InlineData("color == \"GREEN\"", true)]
    [InlineData("price > 20.5", false)]
    [InlineData("price >= 20.50", true)]
    [InlineData("price * 2 == 41", true)]
    [InlineData("rebate * 4 == 10", true)]
    [InlineData("score * 4 == 1.0", true)]
    [InlineData("ratio > 1.4", true)]
    [InlineData("a < big and a < price", true)]
    [InlineData("stamp < now()", true)]
    [InlineData("daysSince(stamp) == 1", true)]
    [InlineData("daysSince(day) == 10", true)]
    [InlineData("daysSince(moment) == 14", true)]
    [InlineData("day < now()", true)]
    [InlineData("moment < now()", true)]
    [InlineData("day == \"2025-06-05\"", true)]
    [InlineData("moment >= \"2025-06-01T00:00:00Z\"", true)]
    [InlineData("stamp == \"2025-06-14T10:00:00Z\"", true)]
    [InlineData("moment == \"2025-06-01\"", true)]
    [InlineData("day between \"2025-06-01\" and \"2025-06-30\"", true)]
    [InlineData("not (name == \"Alice\") or flag", true)]
    [InlineData("a > 100 or b > 100 or child.value > 100 or name == \"Alice\"", true)]
    public void Value_semantics_table(string rule, bool expected) =>
        Assert.Equal(expected, H.Eval(rule, H.Values()));

    [Fact]
    public void Decimal_arithmetic_is_exact_but_double_arithmetic_is_not()
    {
        var rec = new Rec { Price = 0.1m, Score = 0.1 };
        Assert.True(H.Eval("price * 3 == 0.3", rec));
        Assert.False(H.Eval("score * 3 == 0.3", rec));
    }

    [Fact]
    public void Decimal_literal_adapts_to_double_but_decimal_values_do_not_mix()
    {
        Assert.True(H.Eval("score > 0.2 and score < 0.3", H.Values()));
        H.Fail("score > price");
        H.Fail("score + price > 1");
    }

    [Fact]
    public void Integer_literal_widens_to_either()
    {
        Assert.True(H.Eval("score < 1 and price > 20 and a > 6", H.Values()));
    }

    [Fact]
    public void Compiled_and_interpreted_results_agree_for_typed_expressions()
    {
        Rec v = H.Values();
        Assert.Equal(15L, RuleCompiler.Compile<Rec, long?>("a * 2 + 1", H.Options).Evaluate(v));
        Assert.Equal(15L, RuleCompiler.Compile<Rec, long?>("a * 2 + 1", H.Options).Interpret(v));
        Assert.Equal(42.00m, RuleCompiler.Compile<Rec, decimal?>("price * 2 + 1", H.Options).Evaluate(v));
        Assert.Equal(0.5, RuleCompiler.Compile<Rec, double?>("score * 2", H.Options).Evaluate(v));
        Assert.Equal("X1!", RuleCompiler.Compile<Rec, string>("code + \"!\"", H.Options).Evaluate(v));
        Assert.Equal(new DateOnly(2025, 6, 5), RuleCompiler.Compile<Rec, DateOnly>("day", H.Options).Evaluate(v));
        Assert.True(RuleCompiler.Compile<Rec, bool>("flagN", H.Options).Evaluate(v));
        Assert.False(RuleCompiler.Compile<Rec, bool>("flagN", H.Options).Evaluate(H.Nulls()));
    }

    [Theory]
    [InlineData("a / 0", null)]
    [InlineData("a % 0", null)]
    [InlineData("a / 2", 3L)]
    [InlineData("-a % 4", -3L)]
    [InlineData("10 / 4", 2L)]
    [InlineData("10 % 3", 1L)]
    [InlineData("a / (b - 5)", null)]
    [InlineData("a / (b - 4)", 7L)]
    [InlineData("abs(b - 9) * 2", 8L)]
    [InlineData("-(-a)", 7L)]
    public void Integer_arithmetic_and_division_by_zero(string rule, long? expected)
    {
        CompiledExpression<Rec, long?> e = RuleCompiler.Compile<Rec, long?>(rule, H.Options);
        Assert.Equal(expected, e.Evaluate(H.Values()));
        Assert.Equal(expected, (long?)e.Interpret(H.Values()));
    }

    [Theory]
    [InlineData("price / 4", "5.125")]
    [InlineData("10 / 4.0", "2.5")]
    [InlineData("price / 0", null)]
    [InlineData("price % 7", "6.50")]
    [InlineData("a + price", "27.50")]
    public void Decimal_arithmetic(string rule, string? expected)
    {
        CompiledExpression<Rec, decimal?> e = RuleCompiler.Compile<Rec, decimal?>(rule, H.Options);
        decimal? want = expected == null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(want, e.Evaluate(H.Values()));
        Assert.Equal(want, (decimal?)e.Interpret(H.Values()));
    }

    [Fact]
    public void Double_division_by_zero_is_null_not_infinity()
    {
        CompiledExpression<Rec, double?> e = RuleCompiler.Compile<Rec, double?>("score / 0", H.Options);
        Assert.Null(e.Evaluate(H.Values()));
        Assert.Null(e.Interpret(H.Values()));
    }

    [Fact]
    public void Typed_result_errors()
    {
        var a = Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec, long>("b + 1", H.Options));
        Assert.Contains("may be null", a.Message);
        var b = Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec, double?>("a * 0.5", H.Options));
        Assert.Contains("decimal", b.Message);
        var c = Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec, string>("a + 1", H.Options));
        Assert.Contains("cannot be returned as String", c.Message);
        RuleCompiler.Compile<Rec, long>("a + 1", H.Options);
    }

    [Fact]
    public void Right_operand_of_and_is_not_evaluated_when_the_left_is_false()
    {
        // The regex below would time out on this input, so reaching it would throw.
        var rec = new Rec { Flag = false, Name = new string('a', 40) + "!" };
        Assert.False(H.Eval("flag and name matches \"^(a+)+$\"", rec));
        Assert.True(H.Eval("not flag or name matches \"^(a+)+$\"", rec));
    }

    [Theory]
    [InlineData("nulls")]
    [InlineData("values")]
    public void Both_implementations_agree_on_a_mixed_rule(string which)
    {
        Rec r = Pick(which);
        bool result = H.Eval(
            "(a > 5 and b is not null) or (name startswith \"A\" and not flagN) or child.deeper.value between 50 and 100",
            r);
        Assert.Equal(which == "values", result);
    }
}
