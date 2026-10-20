using System.Globalization;
using System.Text;

namespace RulesEngine;

public enum TokenKind
{
    Number,
    String,
    Identifier,
    And,
    Or,
    Not,
    In,
    Between,
    StartsWith,
    EndsWith,
    Contains,
    Matches,
    Is,
    Null,
    True,
    False,
    EqEq,
    NotEq,
    Lt,
    Le,
    Gt,
    Ge,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    LParen,
    RParen,
    LBracket,
    RBracket,
    Comma,
    End,
}

/// <summary>A lexical token. <see cref="Value"/> is the number (long/decimal) or the unescaped string.</summary>
public readonly record struct Token(TokenKind Kind, string Text, Span Span, object? Value = null);

/// <summary>Hand-written lexer. Never throws anything but <see cref="RuleException"/>.</summary>
public static class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["and"] = TokenKind.And,
        ["or"] = TokenKind.Or,
        ["not"] = TokenKind.Not,
        ["in"] = TokenKind.In,
        ["between"] = TokenKind.Between,
        ["startswith"] = TokenKind.StartsWith,
        ["endswith"] = TokenKind.EndsWith,
        ["contains"] = TokenKind.Contains,
        ["matches"] = TokenKind.Matches,
        ["is"] = TokenKind.Is,
        ["null"] = TokenKind.Null,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
    };

    public static IReadOnlyList<Token> Tokenize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tokens = new List<Token>();
        int i = 0;
        int n = source.Length;
        while (i < n)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (IsIdentStart(c))
            {
                i = ReadIdentifier(source, i, tokens);
            }
            else if (IsDigit(c))
            {
                i = ReadNumber(source, i, tokens);
            }
            else if (c == '"' || c == '\'')
            {
                i = ReadString(source, i, tokens);
            }
            else
            {
                char next = i + 1 < n ? source[i + 1] : '\0';
                switch (c)
                {
                    case '(': i = Single(tokens, TokenKind.LParen, i); break;
                    case ')': i = Single(tokens, TokenKind.RParen, i); break;
                    case '[': i = Single(tokens, TokenKind.LBracket, i); break;
                    case ']': i = Single(tokens, TokenKind.RBracket, i); break;
                    case ',': i = Single(tokens, TokenKind.Comma, i); break;
                    case '+': i = Single(tokens, TokenKind.Plus, i); break;
                    case '-': i = Single(tokens, TokenKind.Minus, i); break;
                    case '*': i = Single(tokens, TokenKind.Star, i); break;
                    case '/': i = Single(tokens, TokenKind.Slash, i); break;
                    case '%': i = Single(tokens, TokenKind.Percent, i); break;
                    case '=':
                        if (next != '=')
                        {
                            throw new RuleException("unexpected '='; use '==' to test equality", i, 1);
                        }

                        i = Double(tokens, TokenKind.EqEq, i);
                        break;
                    case '!':
                        if (next != '=')
                        {
                            throw new RuleException("unexpected '!'; use 'not' to negate a condition", i, 1);
                        }

                        i = Double(tokens, TokenKind.NotEq, i);
                        break;
                    case '<':
                        i = next == '=' ? Double(tokens, TokenKind.Le, i) : Single(tokens, TokenKind.Lt, i);
                        break;
                    case '>':
                        i = next == '=' ? Double(tokens, TokenKind.Ge, i) : Single(tokens, TokenKind.Gt, i);
                        break;
                    default:
                        throw new RuleException($"unexpected character '{Printable(c)}'", i, 1);
                }
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, new Span(n, n)));
        return tokens;
    }

    private static string Printable(char c) => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString();

    private static int Single(List<Token> tokens, TokenKind kind, int i)
    {
        tokens.Add(new Token(kind, string.Empty, new Span(i, i + 1)));
        return i + 1;
    }

    private static int Double(List<Token> tokens, TokenKind kind, int i)
    {
        tokens.Add(new Token(kind, string.Empty, new Span(i, i + 2)));
        return i + 2;
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsDigit(char c) => char.IsAsciiDigit(c);

    private static int ReadIdentifier(string s, int start, List<Token> tokens)
    {
        int i = start;
        int n = s.Length;
        bool dotted = false;
        while (true)
        {
            while (i < n && IsIdentPart(s[i]))
            {
                i++;
            }

            if (i < n && s[i] == '.')
            {
                if (i + 1 < n && IsIdentStart(s[i + 1]))
                {
                    dotted = true;
                    i++;
                    continue;
                }

                throw new RuleException("expected a property name after '.'", i, 1);
            }

            break;
        }

        string text = s.Substring(start, i - start);
        var span = new Span(start, i);
        if (!dotted && Keywords.TryGetValue(text, out TokenKind keyword))
        {
            tokens.Add(new Token(keyword, text, span));
        }
        else
        {
            tokens.Add(new Token(TokenKind.Identifier, text, span));
        }

        return i;
    }

    private static int ReadNumber(string s, int start, List<Token> tokens)
    {
        int i = start;
        int n = s.Length;
        while (i < n && IsDigit(s[i]))
        {
            i++;
        }

        bool isDecimal = false;
        if (i < n && s[i] == '.')
        {
            if (i + 1 < n && IsDigit(s[i + 1]))
            {
                isDecimal = true;
                i++;
                while (i < n && IsDigit(s[i]))
                {
                    i++;
                }
            }
            else
            {
                throw new RuleException("expected a digit after the decimal point", i, 1);
            }
        }

        if (i < n && (IsIdentPart(s[i]) || s[i] == '.'))
        {
            throw new RuleException($"invalid number: unexpected '{s[i]}' after digits", i, 1);
        }

        string text = s.Substring(start, i - start);
        var span = new Span(start, i);
        if (isDecimal)
        {
            if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal d))
            {
                throw new RuleException("decimal literal is out of range", start, i - start);
            }

            tokens.Add(new Token(TokenKind.Number, text, span, d));
        }
        else
        {
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long l))
            {
                throw new RuleException("integer literal is out of range", start, i - start);
            }

            tokens.Add(new Token(TokenKind.Number, text, span, l));
        }

        return i;
    }

    private static int ReadString(string s, int start, List<Token> tokens)
    {
        char quote = s[start];
        int n = s.Length;
        int i = start + 1;
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= n)
            {
                throw new RuleException("unterminated string literal", start, n - start);
            }

            char c = s[i];
            if (c == quote)
            {
                i++;
                break;
            }

            if (c == '\n' || c == '\r')
            {
                throw new RuleException("unterminated string literal (line break inside the string)", start, i - start);
            }

            if (c != '\\')
            {
                sb.Append(c);
                i++;
                continue;
            }

            if (i + 1 >= n)
            {
                throw new RuleException("unterminated string literal", start, n - start);
            }

            char e = s[i + 1];
            switch (e)
            {
                case '\\': sb.Append('\\'); i += 2; break;
                case '"': sb.Append('"'); i += 2; break;
                case '\'': sb.Append('\''); i += 2; break;
                case 'n': sb.Append('\n'); i += 2; break;
                case 'r': sb.Append('\r'); i += 2; break;
                case 't': sb.Append('\t'); i += 2; break;
                case 'u':
                    if (i + 6 > n || !int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                    {
                        throw new RuleException("invalid \\u escape: expected four hex digits", i, Math.Min(6, n - i));
                    }

                    sb.Append((char)code);
                    i += 6;
                    break;
                default:
                    throw new RuleException($"unknown escape sequence '\\{Printable(e)}'", i, 2);
            }
        }

        tokens.Add(new Token(TokenKind.String, s.Substring(start, i - start), new Span(start, i), sb.ToString()));
        return i;
    }
}
