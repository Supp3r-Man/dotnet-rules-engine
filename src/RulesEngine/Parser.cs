namespace RulesEngine;

/// <summary>
/// Pratt (precedence-climbing) parser. Binding powers, loosest to tightest:
/// <code>
///  10 or            (left associative)
///  20 and           (left associative)
///  30 not           (prefix)
///  40 == != &lt; &lt;= &gt; &gt;= in, not in, between, is [not] null, startswith, endswith, contains, matches
///                   (NON-associative: a comparison cannot be chained into another comparison)
///  50 + -           (left associative)
///  60 * / %         (left associative)
///  70 - (prefix)
/// </code>
/// Nesting depth (of parentheses, prefix operators and of the finished tree) is limited by
/// <c>maxDepth</c>, so hostile input produces a <see cref="RuleException"/> rather than a stack overflow.
/// </summary>
public static class Parser
{
    public const int DefaultMaxDepth = 256;

    public static Node Parse(string source, int maxDepth = DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }

        IReadOnlyList<Token> tokens = Lexer.Tokenize(source);
        return new ParserState(tokens, maxDepth).ParseRule();
    }
}

internal sealed class ParserState(IReadOnlyList<Token> tokens, int maxDepth)
{
    private const int BpOr = 10;
    private const int BpAnd = 20;
    private const int BpNot = 30;
    private const int BpCmp = 40;
    private const int BpAdd = 50;
    private const int BpMul = 60;
    private const int BpNeg = 70;

    private int _pos;
    private int _nest;

    private Token Peek => tokens[_pos];

    private Token PeekAt(int offset) => tokens[Math.Min(_pos + offset, tokens.Count - 1)];

    private Token Next()
    {
        Token t = tokens[_pos];
        if (t.Kind != TokenKind.End)
        {
            _pos++;
        }

        return t;
    }

    public Node ParseRule()
    {
        Node e = ParseExpr(0);
        if (Peek.Kind != TokenKind.End)
        {
            throw Unexpected(Peek, "an operator or the end of the rule");
        }

        return e;
    }

    private static string Describe(Token t) => t.Kind switch
    {
        TokenKind.End => "the end of the rule",
        TokenKind.String => "a string literal",
        TokenKind.Number => $"the number {t.Text}",
        _ => $"'{TokenText(t)}'",
    };

    private static string TokenText(Token t) => t.Text.Length > 0 ? t.Text : t.Kind switch
    {
        TokenKind.EqEq => "==",
        TokenKind.NotEq => "!=",
        TokenKind.Lt => "<",
        TokenKind.Le => "<=",
        TokenKind.Gt => ">",
        TokenKind.Ge => ">=",
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.LParen => "(",
        TokenKind.RParen => ")",
        TokenKind.LBracket => "[",
        TokenKind.RBracket => "]",
        TokenKind.Comma => ",",
        _ => t.Kind.ToString(),
    };

    private static RuleException Unexpected(Token t, string expected) =>
        new($"expected {expected} but found {Describe(t)}", t.Span.Start, t.Span.Length);

    private RuleException TooDeep(Span span) =>
        new($"rule is nested too deeply (limit {maxDepth})", span.Start, Math.Max(1, span.Length));

    private T Mk<T>(T node) where T : Node
    {
        if (node.Depth > maxDepth)
        {
            throw TooDeep(node.Span);
        }

        return node;
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (Peek.Kind != kind)
        {
            throw Unexpected(Peek, what);
        }

        return Next();
    }

    private int InfixBp(Token t) => t.Kind switch
    {
        TokenKind.Or => BpOr,
        TokenKind.And => BpAnd,
        TokenKind.EqEq or TokenKind.NotEq or TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge
            or TokenKind.In or TokenKind.Between or TokenKind.Is
            or TokenKind.StartsWith or TokenKind.EndsWith or TokenKind.Contains or TokenKind.Matches => BpCmp,
        TokenKind.Not => PeekAt(1).Kind is TokenKind.In or TokenKind.Between ? BpCmp : 0,
        TokenKind.Plus or TokenKind.Minus => BpAdd,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => BpMul,
        _ => 0,
    };

    private Node ParseExpr(int minBp)
    {
        if (++_nest > maxDepth)
        {
            throw TooDeep(Peek.Span);
        }

        try
        {
            Node left = ParsePrefix();
            while (true)
            {
                int bp = InfixBp(Peek);
                if (bp == 0 || bp < minBp)
                {
                    break;
                }

                left = bp == BpCmp ? ParseComparison(left) : ParseBinary(left, bp);
            }

            return left;
        }
        finally
        {
            _nest--;
        }
    }

    private Node ParseBinary(Node left, int bp)
    {
        Token op = Next();
        Node right = ParseExpr(bp + 1);
        BinaryOp kind = op.Kind switch
        {
            TokenKind.Or => BinaryOp.Or,
            TokenKind.And => BinaryOp.And,
            TokenKind.Plus => BinaryOp.Add,
            TokenKind.Minus => BinaryOp.Sub,
            TokenKind.Star => BinaryOp.Mul,
            TokenKind.Slash => BinaryOp.Div,
            _ => BinaryOp.Mod,
        };
        return Mk(new Binary(kind, left, right, Span.Join(left.Span, right.Span)));
    }

    private Node ParseComparison(Node left)
    {
        Token t = Next();
        Node result;
        switch (t.Kind)
        {
            case TokenKind.EqEq:
            case TokenKind.NotEq:
            case TokenKind.Lt:
            case TokenKind.Le:
            case TokenKind.Gt:
            case TokenKind.Ge:
            case TokenKind.StartsWith:
            case TokenKind.EndsWith:
            case TokenKind.Contains:
            case TokenKind.Matches:
                {
                    Node right = ParseExpr(BpCmp + 1);
                    BinaryOp op = t.Kind switch
                    {
                        TokenKind.EqEq => BinaryOp.Eq,
                        TokenKind.NotEq => BinaryOp.Ne,
                        TokenKind.Lt => BinaryOp.Lt,
                        TokenKind.Le => BinaryOp.Le,
                        TokenKind.Gt => BinaryOp.Gt,
                        TokenKind.Ge => BinaryOp.Ge,
                        TokenKind.StartsWith => BinaryOp.StartsWith,
                        TokenKind.EndsWith => BinaryOp.EndsWith,
                        TokenKind.Contains => BinaryOp.Contains,
                        _ => BinaryOp.Matches,
                    };
                    result = Mk(new Binary(op, left, right, Span.Join(left.Span, right.Span)));
                    break;
                }

            case TokenKind.In:
                result = ParseIn(left, false);
                break;
            case TokenKind.Between:
                result = ParseBetween(left, false);
                break;
            case TokenKind.Not:
                {
                    Token k = Next();
                    result = k.Kind == TokenKind.In ? ParseIn(left, true) : ParseBetween(left, true);
                    break;
                }

            default: // TokenKind.Is
                {
                    bool negated = false;
                    if (Peek.Kind == TokenKind.Not)
                    {
                        Next();
                        negated = true;
                    }

                    Token nul = Expect(TokenKind.Null, "'null' after 'is'");
                    result = Mk(new IsNull(left, negated, Span.Join(left.Span, nul.Span)));
                    break;
                }
        }

        if (InfixBp(Peek) == BpCmp)
        {
            throw new RuleException(
                "comparison operators cannot be chained; combine conditions with 'and' / 'or' or add parentheses",
                Peek.Span.Start,
                Math.Max(1, Peek.Span.Length));
        }

        return result;
    }

    private Node ParseIn(Node left, bool negated)
    {
        Expect(TokenKind.LBracket, "'[' after 'in'");
        if (Peek.Kind == TokenKind.RBracket)
        {
            throw new RuleException("an 'in' list needs at least one item", Peek.Span.Start, 1);
        }

        var items = new List<Node>();
        do
        {
            items.Add(ParseExpr(0));
        }
        while (TryConsume(TokenKind.Comma));

        Token close = Expect(TokenKind.RBracket, "',' or ']'");
        return Mk(new InList(left, items, negated, Span.Join(left.Span, close.Span)));
    }

    private Node ParseBetween(Node left, bool negated)
    {
        Node low = ParseExpr(BpCmp + 1);
        Expect(TokenKind.And, "'and' in 'between ... and ...'");
        Node high = ParseExpr(BpCmp + 1);
        return Mk(new Between(left, low, high, negated, Span.Join(left.Span, high.Span)));
    }

    private bool TryConsume(TokenKind kind)
    {
        if (Peek.Kind != kind)
        {
            return false;
        }

        Next();
        return true;
    }

    private Node ParsePrefix()
    {
        Token t = Peek;
        switch (t.Kind)
        {
            case TokenKind.Number:
                Next();
                return new NumberLiteral(t.Value!, t.Span);
            case TokenKind.String:
                Next();
                return new StringLiteral((string)t.Value!, t.Span);
            case TokenKind.True:
                Next();
                return new BoolLiteral(true, t.Span);
            case TokenKind.False:
                Next();
                return new BoolLiteral(false, t.Span);
            case TokenKind.Identifier:
                Next();
                return Peek.Kind == TokenKind.LParen ? ParseCall(t) : new Identifier(t.Text.Split('.'), t.Span);
            case TokenKind.LParen:
                {
                    Next();
                    Node inner = ParseExpr(0);
                    Expect(TokenKind.RParen, "')'");
                    return inner;
                }

            case TokenKind.Not:
                {
                    Next();
                    Node operand = ParseExpr(BpNot);
                    return Mk(new Unary(UnaryOp.Not, operand, Span.Join(t.Span, operand.Span)));
                }

            case TokenKind.Minus:
                {
                    Next();
                    Node operand = ParseExpr(BpNeg);
                    return Mk(new Unary(UnaryOp.Negate, operand, Span.Join(t.Span, operand.Span)));
                }

            case TokenKind.Null:
                throw new RuleException("'null' cannot be used as a value; use 'x is null' or 'x is not null'", t.Span.Start, t.Span.Length);
            case TokenKind.LBracket:
                throw new RuleException("a list '[...]' is only allowed after 'in'", t.Span.Start, 1);
            default:
                throw Unexpected(t, "an expression");
        }
    }

    private Node ParseCall(Token nameToken)
    {
        if (nameToken.Text.Contains('.'))
        {
            throw new RuleException(
                $"method calls are not supported ('{nameToken.Text}(...)'); only whitelisted functions such as len(...) can be called",
                nameToken.Span.Start,
                nameToken.Span.Length);
        }

        Next(); // '('
        var args = new List<Node>();
        if (Peek.Kind != TokenKind.RParen)
        {
            do
            {
                args.Add(ParseExpr(0));
            }
            while (TryConsume(TokenKind.Comma));
        }

        Token close = Expect(TokenKind.RParen, "',' or ')'");
        return Mk(new Call(nameToken.Text, args, Span.Join(nameToken.Span, close.Span)));
    }
}
