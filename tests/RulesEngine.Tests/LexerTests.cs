namespace RulesEngine.Tests;

public class LexerTests
{
    private static List<(TokenKind Kind, int Start, int End)> Lex(string s) =>
        Lexer.Tokenize(s).Select(t => (t.Kind, t.Span.Start, t.Span.End)).ToList();

    [Fact]
    public void Simple_comparison_has_exact_kinds_and_spans()
    {
        Assert.Equal(
            [(TokenKind.Identifier, 0, 3), (TokenKind.Ge, 4, 6), (TokenKind.Number, 7, 9), (TokenKind.End, 9, 9)],
            Lex("age >= 18"));
    }

    [Fact]
    public void Empty_input_gives_only_End()
    {
        Assert.Equal([(TokenKind.End, 0, 0)], Lex(string.Empty));
        Assert.Equal([(TokenKind.End, 3, 3)], Lex("   "));
    }

    [Theory]
    [InlineData("==", TokenKind.EqEq)]
    [InlineData("!=", TokenKind.NotEq)]
    [InlineData("<", TokenKind.Lt)]
    [InlineData("<=", TokenKind.Le)]
    [InlineData(">", TokenKind.Gt)]
    [InlineData(">=", TokenKind.Ge)]
    [InlineData("+", TokenKind.Plus)]
    [InlineData("-", TokenKind.Minus)]
    [InlineData("*", TokenKind.Star)]
    [InlineData("/", TokenKind.Slash)]
    [InlineData("%", TokenKind.Percent)]
    [InlineData("(", TokenKind.LParen)]
    [InlineData(")", TokenKind.RParen)]
    [InlineData("[", TokenKind.LBracket)]
    [InlineData("]", TokenKind.RBracket)]
    [InlineData(",", TokenKind.Comma)]
    public void Operators_and_punctuation(string text, TokenKind expected)
    {
        IReadOnlyList<Token> tokens = Lexer.Tokenize(text);
        Assert.Equal(expected, tokens[0].Kind);
        Assert.Equal(new Span(0, text.Length), tokens[0].Span);
    }

    [Theory]
    [InlineData("and", TokenKind.And)]
    [InlineData("AND", TokenKind.And)]
    [InlineData("Or", TokenKind.Or)]
    [InlineData("NOT", TokenKind.Not)]
    [InlineData("in", TokenKind.In)]
    [InlineData("between", TokenKind.Between)]
    [InlineData("StartsWith", TokenKind.StartsWith)]
    [InlineData("endswith", TokenKind.EndsWith)]
    [InlineData("CONTAINS", TokenKind.Contains)]
    [InlineData("matches", TokenKind.Matches)]
    [InlineData("is", TokenKind.Is)]
    [InlineData("null", TokenKind.Null)]
    [InlineData("True", TokenKind.True)]
    [InlineData("FALSE", TokenKind.False)]
    public void Keywords_are_case_insensitive(string text, TokenKind expected) =>
        Assert.Equal(expected, Lexer.Tokenize(text)[0].Kind);

    [Fact]
    public void Dotted_identifier_is_one_token_with_full_span()
    {
        IReadOnlyList<Token> tokens = Lexer.Tokenize("x + address.city");
        Assert.Equal(TokenKind.Identifier, tokens[2].Kind);
        Assert.Equal("address.city", tokens[2].Text);
        Assert.Equal(new Span(4, 16), tokens[2].Span);
    }

    [Fact]
    public void Keyword_after_a_dot_is_part_of_the_identifier()
    {
        Token t = Lexer.Tokenize("a.in")[0];
        Assert.Equal(TokenKind.Identifier, t.Kind);
        Assert.Equal("a.in", t.Text);
    }

    [Fact]
    public void Identifier_with_digits_and_underscores()
    {
        Assert.Equal("_a1_b2", Lexer.Tokenize("_a1_b2")[0].Text);
    }

    [Fact]
    public void Integer_literal_is_long()
    {
        Token t = Lexer.Tokenize("007")[0];
        Assert.Equal(TokenKind.Number, t.Kind);
        Assert.Equal(7L, t.Value);
    }

    [Fact]
    public void Decimal_literal_is_decimal_and_keeps_scale()
    {
        Token t = Lexer.Tokenize("1.50")[0];
        Assert.IsType<decimal>(t.Value);
        Assert.Equal(1.50m, t.Value);
        Assert.Equal("1.50", ((decimal)t.Value!).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Long_max_is_accepted()
    {
        Assert.Equal(long.MaxValue, Lexer.Tokenize("9223372036854775807")[0].Value);
    }

    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("'abc'", "abc")]
    [InlineData("\"a\\\"b\"", "a\"b")]
    [InlineData("'it\\'s'", "it's")]
    [InlineData("\"back\\\\slash\"", "back\\slash")]
    [InlineData("\"tab\\there\"", "tab\there")]
    [InlineData("\"nl\\nx\"", "nl\nx")]
    [InlineData("\"cr\\rx\"", "cr\rx")]
    [InlineData("\"\\u0041\\u00e9\"", "A\u00e9")]
    [InlineData("\"\"", "")]
    [InlineData("'say \"hi\"'", "say \"hi\"")]
    public void String_literals_and_escapes(string text, string expected)
    {
        Token t = Lexer.Tokenize(text)[0];
        Assert.Equal(TokenKind.String, t.Kind);
        Assert.Equal(expected, t.Value);
        Assert.Equal(new Span(0, text.Length), t.Span);
    }

    [Fact]
    public void Full_rule_from_the_spec_tokenizes()
    {
        const string rule = "age >= 18 and country in ['US','CA'] and not (status == \"closed\") and name startswith \"A\" or vip";
        IReadOnlyList<Token> tokens = Lexer.Tokenize(rule);
        Assert.Equal(TokenKind.End, tokens[^1].Kind);
        Assert.Equal(rule.Length, tokens[^1].Span.Start);
        Assert.Equal(25, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[^2].Kind);
        Assert.Equal("vip", tokens[^2].Text);
    }

    [Theory]
    [InlineData("\"abc", 0, "unterminated")]
    [InlineData("x == 'abc", 5, "unterminated")]
    [InlineData("\"a\nb\"", 0, "unterminated")]
    [InlineData("\"a\rb\"", 0, "unterminated")]
    [InlineData("1.", 1, "decimal point")]
    [InlineData("1.x", 1, "decimal point")]
    [InlineData("1.5.2", 3, "invalid number")]
    [InlineData("12abc", 2, "invalid number")]
    [InlineData("a.", 1, "property name")]
    [InlineData("a.1", 1, "property name")]
    [InlineData("a & b", 2, "unexpected character")]
    [InlineData("a | b", 2, "unexpected character")]
    [InlineData("x = 1", 2, "==")]
    [InlineData("!x", 0, "not")]
    [InlineData("\"\\q\"", 1, "escape")]
    [InlineData("\"\\u12\"", 1, "\\u")]
    [InlineData("99999999999999999999", 0, "out of range")]
    [InlineData("79228162514264337593543950336.0", 0, "out of range")]
    [InlineData("x # y", 2, "unexpected character")]
    [InlineData("a . b", 2, "unexpected character")]
    public void Lexical_errors_carry_the_right_position(string text, int position, string messagePart)
    {
        var ex = Assert.Throws<RuleException>(() => Lexer.Tokenize(text));
        Assert.Equal(position, ex.Position);
        Assert.Contains(messagePart, ex.Message);
    }

    [Fact]
    public void Null_source_is_an_argument_error_not_a_rule_error()
    {
        Assert.Throws<ArgumentNullException>(() => Lexer.Tokenize(null!));
    }

    [Fact]
    public void Control_characters_are_reported_printably()
    {
        var ex = Assert.Throws<RuleException>(() => Lexer.Tokenize("a \u0001 b"));
        Assert.Contains("\\u0001", ex.Message);
    }
}
