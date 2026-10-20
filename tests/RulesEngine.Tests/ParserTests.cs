using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

public class ParserTests
{
    private static string Sx(string text) => H.Sx(Parser.Parse(text));

    // Precedence and associativity table: loosest binding first in the documentation, one row per rule.
    [Theory]
    [InlineData("a or b and c", "(or a (and b c))")]
    [InlineData("a and b or c", "(or (and a b) c)")]
    [InlineData("a or b or c", "(or (or a b) c)")]
    [InlineData("a and b and c", "(and (and a b) c)")]
    [InlineData("not a and b", "(and (not a) b)")]
    [InlineData("not a or b", "(or (not a) b)")]
    [InlineData("not a == b", "(not (== a b))")]
    [InlineData("not not a", "(not (not a))")]
    [InlineData("not (a and b)", "(not (and a b))")]
    [InlineData("(a or b) and c", "(and (or a b) c)")]
    [InlineData("a == b and c == d", "(and (== a b) (== c d))")]
    [InlineData("a + b * c", "(+ a (* b c))")]
    [InlineData("a * b + c", "(+ (* a b) c)")]
    [InlineData("a - b - c", "(- (- a b) c)")]
    [InlineData("a / b / c", "(/ (/ a b) c)")]
    [InlineData("a % b * c", "(* (% a b) c)")]
    [InlineData("-a * b", "(* (neg a) b)")]
    [InlineData("-a + b", "(+ (neg a) b)")]
    [InlineData("a - -b", "(- a (neg b))")]
    [InlineData("- - a", "(neg (neg a))")]
    [InlineData("a + b == c * d", "(== (+ a b) (* c d))")]
    [InlineData("a < b + 1 and c", "(and (< a (+ b 1)) c)")]
    [InlineData("x in [1, 2] and y", "(and (in x [1 2]) y)")]
    [InlineData("x not in [1]", "(not-in x [1])")]
    [InlineData("x between 1 and 5 and y", "(and (between x 1 5) y)")]
    [InlineData("x between a + 1 and b * 2", "(between x (+ a 1) (* b 2))")]
    [InlineData("x not between 1 and 2", "(not-between x 1 2)")]
    [InlineData("x is null and y is not null", "(and (is-null x) (is-not-null y))")]
    [InlineData("x is not null or y", "(or (is-not-null x) y)")]
    [InlineData("name startswith \"A\" or vip", "(or (startswith name 'A') vip)")]
    [InlineData("a.b.c == 1", "(== a.b.c 1)")]
    [InlineData("f(a, b + 1) > 2", "(> (call f a (+ b 1)) 2)")]
    [InlineData("now() > x", "(> (call now) x)")]
    [InlineData("a - b + c", "(+ (- a b) c)")]
    [InlineData("a * b % c / d", "(/ (% (* a b) c) d)")]
    [InlineData("1 + 2 * 3 - 4", "(- (+ 1 (* 2 3)) 4)")]
    [InlineData("a == 1.5", "(== a 1.5)")]
    public void Precedence_and_associativity(string text, string expected) =>
        Assert.Equal(expected, Sx(text));

    [Fact]
    public void Spec_example_rule_parses_with_the_documented_precedence()
    {
        const string rule = "age >= 18 and country in ['US','CA'] and not (status == \"closed\") and name startswith \"A\" or vip";
        Assert.Equal(
            "(or (and (and (and (>= age 18) (in country ['US' 'CA'])) (not (== status 'closed'))) (startswith name 'A')) vip)",
            Sx(rule));
    }

    [Theory]
    [InlineData("a < b < c")]
    [InlineData("a == b == c")]
    [InlineData("a in [1] == true")]
    [InlineData("a is null == b")]
    [InlineData("a startswith \"x\" contains \"y\"")]
    [InlineData("a between 1 and 2 == b")]
    public void Chained_comparisons_are_rejected(string text)
    {
        var ex = Assert.Throws<RuleException>(() => Parser.Parse(text));
        Assert.Contains("cannot be chained", ex.Message);
    }

    [Fact]
    public void Chained_comparison_error_points_at_the_second_operator()
    {
        var ex = Assert.Throws<RuleException>(() => Parser.Parse("a < b < c"));
        Assert.Equal(6, ex.Position);
    }

    [Fact]
    public void Parentheses_make_a_chain_legal()
    {
        Assert.Equal("(== (< a b) c)", Sx("(a < b) == c"));
    }

    [Fact]
    public void Root_and_child_spans()
    {
        var root = (Binary)Parser.Parse("age >= 18 and vip");
        Assert.Equal(new Span(0, 17), root.Span);
        Assert.Equal(new Span(0, 9), root.Left.Span);
        Assert.Equal(new Span(14, 17), root.Right.Span);
        var cmp = (Binary)root.Left;
        Assert.Equal(new Span(0, 3), cmp.Left.Span);
        Assert.Equal(new Span(7, 9), cmp.Right.Span);
    }

    [Fact]
    public void Parenthesised_operand_span_excludes_the_parentheses()
    {
        var root = (Binary)Parser.Parse("(a + b) * c");
        Assert.Equal(new Span(1, 11), root.Span);
        Assert.Equal(new Span(1, 6), root.Left.Span);
    }

    [Fact]
    public void Call_unary_inlist_between_isnull_spans()
    {
        Assert.Equal(new Span(0, 7), Parser.Parse("len(ab)").Span);
        Assert.Equal(new Span(0, 7), Parser.Parse("not abc").Span);
        Assert.Equal(new Span(0, 14), Parser.Parse("x in [1, 2, 3]").Span);
        Assert.Equal(new Span(0, 18), Parser.Parse("x between 1 and 20").Span);
        Assert.Equal(new Span(0, 12), Parser.Parse("name is null").Span);
        Assert.Equal(new Span(0, 16), Parser.Parse("name is not null").Span);
    }

    [Fact]
    public void Literal_nodes_have_values()
    {
        var b = (Binary)Parser.Parse("x == 'q' or true");
        var lit = (StringLiteral)((Binary)b.Left).Right;
        Assert.Equal("q", lit.Value);
        Assert.Equal(new Span(5, 8), lit.Span);
        Assert.True(((BoolLiteral)b.Right).Value);
    }

    [Fact]
    public void Number_nodes_are_long_or_decimal()
    {
        var b = (Binary)Parser.Parse("1 + 2.5");
        Assert.IsType<long>(((NumberLiteral)b.Left).Value);
        Assert.IsType<decimal>(((NumberLiteral)b.Right).Value);
    }

    [Fact]
    public void Identifier_segments_are_split()
    {
        var id = (Identifier)Parser.Parse("address.city");
        Assert.Equal(["address", "city"], id.Segments);
    }

    [Theory]
    [InlineData("a +", 3, "end of the rule")]
    [InlineData("(a", 2, "')'")]
    [InlineData("a )", 2, "')'")]
    [InlineData(")", 0, "an expression")]
    [InlineData("a b", 2, "end of the rule")]
    [InlineData("a and", 5, "an expression")]
    [InlineData("x in 1", 5, "'['")]
    [InlineData("x in [1,]", 8, "an expression")]
    [InlineData("x in [1", 7, "']'")]
    [InlineData("x between 1", 11, "'and'")]
    [InlineData("x is 5", 5, "'null'")]
    [InlineData("x is not 5", 9, "'null'")]
    [InlineData("a not b", 2, "end of the rule")]
    [InlineData("f(1,", 4, "an expression")]
    [InlineData("f(1 2)", 4, "')'")]
    [InlineData("== 1", 0, "an expression")]
    [InlineData("", 0, "an expression")]
    public void Syntax_errors_have_positions(string text, int position, string messagePart)
    {
        var ex = Assert.Throws<RuleException>(() => Parser.Parse(text));
        Assert.Equal(position, ex.Position);
        Assert.Contains(messagePart, ex.Message);
    }

    [Fact]
    public void Empty_in_list_is_rejected()
    {
        var ex = Assert.Throws<RuleException>(() => Parser.Parse("x in []"));
        Assert.Contains("at least one item", ex.Message);
        Assert.Equal(6, ex.Position);
    }

    [Fact]
    public void Bare_list_is_rejected()
    {
        var ex = Assert.Throws<RuleException>(() => Parser.Parse("[1, 2]"));
        Assert.Contains("only allowed after 'in'", ex.Message);
    }

    [Theory]
    [InlineData("name.ToUpper()")]
    [InlineData("a.b.c(1)")]
    [InlineData("System.Environment.Exit(0)")]
    public void Dotted_calls_are_a_parse_error(string text)
    {
        var ex = Assert.Throws<RuleException>(() => Parser.Parse(text));
        Assert.Contains("method calls are not supported", ex.Message);
        Assert.Equal(0, ex.Position);
    }

    [Fact]
    public void Render_shows_source_line_and_caret()
    {
        var ex = Assert.Throws<RuleException>(() => Parser.Parse("age >= 18 and"));
        string rendered = ex.Render("age >= 18 and");
        string[] lines = rendered.Split(Environment.NewLine);
        Assert.Equal("  age >= 18 and", lines[1]);
        Assert.Equal(new string(' ', 15) + "^", lines[2]);
    }

    [Fact]
    public void Parse_rejects_bad_depth_argument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Parser.Parse("a", 0));
    }
}
