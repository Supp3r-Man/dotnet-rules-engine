using System.Globalization;

namespace RulesEngine.Tests;

public class PrinterTests
{
    [Theory]
    [InlineData("age>=18", "age >= 18")]
    [InlineData("(a)", "a")]
    [InlineData("((a + b))", "a + b")]
    [InlineData("a + (b + c)", "a + (b + c)")]
    [InlineData("(a + b) + c", "a + b + c")]
    [InlineData("(a * b) + c", "a * b + c")]
    [InlineData("a * (b + c)", "a * (b + c)")]
    [InlineData("a - (b - c)", "a - (b - c)")]
    [InlineData("not (a == b)", "not a == b")]
    [InlineData("(not a) == b", "(not a) == b")]
    [InlineData("-(a + b)", "-(a + b)")]
    [InlineData("a - (-b)", "a - -b")]
    [InlineData("'x' == \"y\"", "\"x\" == \"y\"")]
    [InlineData("x IN [1,2]", "x in [1, 2]")]
    [InlineData("X NOT BETWEEN 1 AND 2", "X not between 1 and 2")]
    [InlineData("(a < b) == c", "(a < b) == c")]
    [InlineData("1.50 + 2", "1.50 + 2")]
    [InlineData("LEN( name ) > 3", "LEN(name) > 3")]
    [InlineData("a or (b or c)", "a or (b or c)")]
    [InlineData("(a and b) or c", "a and b or c")]
    [InlineData("a and (b or c)", "a and (b or c)")]
    [InlineData("not (a and b)", "not (a and b)")]
    [InlineData("a is NOT null", "a is not null")]
    [InlineData("(a + 1) in [1]", "a + 1 in [1]")]
    [InlineData("(a == 1) in [true]", "(a == 1) in [true]")]
    public void Known_answer_printing(string input, string expected) =>
        Assert.Equal(expected, Printer.Print(Parser.Parse(input)));

    [Fact]
    public void Spec_example_prints_in_normal_form()
    {
        const string rule = "age >= 18 and country in ['US','CA'] and not (status == \"closed\") and name startswith \"A\" or vip";
        Assert.Equal(
            "age >= 18 and country in [\"US\", \"CA\"] and not status == \"closed\" and name startswith \"A\" or vip",
            Printer.Print(Parser.Parse(rule)));
    }

    public static IEnumerable<object[]> Corpus() =>
        new[]
        {
            "a or b and c", "a and b or c", "not a and b", "not a == b", "(not a) == b", "not (a or b)",
            "a + b * c - d / e % f", "(a + b) * (c - d)", "a - (b - c) - d", "-a * -b", "- - a", "-(a + 1) * 2",
            "x in [1, 2 + 3, -4] and y not in ['a']", "x between 1 and 2 + 3 and y", "(x between 1 and 2) == true",
            "x not between a * 2 and b - 1", "x is null or y is not null", "(x is null) == (y is null)",
            "name startswith \"A\" or name endswith 'z' or name contains \"mid\" or name matches \"^a.*z$\"",
            "f(a, g(b, c), 1 + 2) > h()", "a.b.c == d.e", "x == 'q'",
            "(a < b) == (c >= d)", "a < (b == c)", "not not not a", "((((a))))", "1.50 == 1.5", "0.10 + 100",
            "x in [(a and b), (c or d)]", "\"quote\\\"d\" == \"tab\\t\"", "'\\u0001\\u007f' != x",
            "a == 9223372036854775807", "a % 2 == 0 and a / 3 > 1", "not (a in [1]) and not (b between 1 and 2)",
            "(a and b) or (c and d) or not (e or f)", "len(lower(upper(x))) between abs(-1) and 10",
        }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Print_then_parse_round_trips(string text)
    {
        Node ast = Parser.Parse(text);
        string printed = Printer.Print(ast);
        Node reparsed = Parser.Parse(printed);
        Assert.True(AstComparer.AreEqual(ast, reparsed), $"{text} -> {printed}");
        Assert.Equal(printed, Printer.Print(reparsed));
    }

    [Theory]
    [InlineData("quote \" and \\ backslash")]
    [InlineData("line\nbreak\r\ttab")]
    [InlineData("\u0001\u001f\u007f\u0085")]
    [InlineData("unicode é中\U0001F600")]
    [InlineData("")]
    public void String_literals_round_trip_through_the_printer(string value)
    {
        var literal = new StringLiteral(value, new Span(0, 0));
        Node reparsed = Parser.Parse(Printer.Print(literal));
        Assert.Equal(value, ((StringLiteral)reparsed).Value);
    }

    [Fact]
    public void Printing_is_invariant_under_a_comma_decimal_culture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("a == 1.5 and b == 1000", Printer.Print(Parser.Parse("a == 1.5 and b == 1000")));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Ast_comparer_ignores_spans_but_not_structure()
    {
        Assert.True(AstComparer.AreEqual(Parser.Parse("a+b"), Parser.Parse("  a  +  b ")));
        Assert.False(AstComparer.AreEqual(Parser.Parse("a+b"), Parser.Parse("a-b")));
        Assert.False(AstComparer.AreEqual(Parser.Parse("a+(b+c)"), Parser.Parse("(a+b)+c")));
        Assert.False(AstComparer.AreEqual(Parser.Parse("x in [1]"), Parser.Parse("x not in [1]")));
        Assert.False(AstComparer.AreEqual(Parser.Parse("1"), Parser.Parse("1.0")));
        Assert.False(AstComparer.AreEqual(Parser.Parse("a"), null));
    }
}
