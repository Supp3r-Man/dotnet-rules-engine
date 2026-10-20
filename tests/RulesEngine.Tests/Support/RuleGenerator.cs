using System.Globalization;

namespace RulesEngine.Tests.Support;

/// <summary>
/// Seeded generator of TYPE-CORRECT rule text over <see cref="Rec"/>. Every binary sub-expression is fully
/// parenthesised, so the generated text does not depend on the parser's precedence rules (the printer round-trip
/// oracle then checks the parser against its own printer, the compiler/interpreter oracles check the semantics).
/// </summary>
public sealed class RuleGenerator(int seed)
{
    private readonly Random _r = new(seed);

    private int Pick(int n) => _r.Next(n);

    private string Choose(params string[] xs) => xs[_r.Next(xs.Length)];

    private string Id(string name) => _r.Next(6) == 0 ? name.ToUpperInvariant() : name;

    public string Bool(int depth)
    {
        if (depth > 0)
        {
            switch (Pick(7))
            {
                case 0: return $"({Bool(depth - 1)} and {Bool(depth - 1)})";
                case 1: return $"({Bool(depth - 1)} or {Bool(depth - 1)})";
                case 2: return $"(not {Bool(depth - 1)})";
            }
        }

        return BoolLeaf(Math.Max(depth, 0));
    }

    private string BoolLeaf(int depth)
    {
        int d = Math.Min(depth, 2);
        switch (Pick(13))
        {
            case 0:
                return Choose(Id("flag"), Id("flagN"), "true", "false");
            case 1:
            case 2:
                {
                    int k = Pick(3);
                    return $"({Num(k, d)} {Choose("==", "!=", "<", "<=", ">", ">=")} {Num(k, d)})";
                }

            case 3:
                return $"({Str(d)} {Choose("==", "!=", "<", "<=", ">", ">=")} {Str(d)})";
            case 4:
                return $"({Str(d)} {Choose("startswith", "endswith", "contains")} {Str(d)})";
            case 5:
                return $"({Str(d)} matches {Choose("\"^A\"", "\"[0-9]+$\"", "\"a.*b\"", "\"(?i)alice\"", "\"^$\"", "\"b|z\"")})";
            case 6:
                return InList(d);
            case 7:
                {
                    int k = Pick(3);
                    return $"({Num(k, d)} {Choose("between", "not between")} {Num(k, d)} and {Num(k, d)})";
                }

            case 8:
                {
                    string operand = Choose(Num(0, d), Num(1, d), Num(2, d), Str(d), Id("day"), Id("stamp"), Id("maybeColor"), Id("flagN"), Id("child.deeper.value"));
                    return $"({operand} is {Choose("null", "not null")})";
                }

            case 9:
                return $"({Choose(Id("color"), Id("maybeColor"))} {Choose("==", "!=")} \"{Choose("Red", "green", "BLUE")}\")";
            case 10:
                return DateComparison();
            case 11:
                return $"({Choose(Id("flag"), Id("flagN"))} {Choose("==", "!=")} {Choose("true", "false", Id("flag"), Id("flagN"))})";
            default:
                return $"({Num(0, d)} {Choose("<", ">=")} {Num(0, d)})";
        }
    }

    private string DateComparison()
    {
        string op = Choose("<", "<=", ">", ">=", "==", "!=");
        switch (Pick(7))
        {
            case 0: return $"({Id("day")} {op} \"{RandomDate()}\")";
            case 1: return $"({Id("moment")} {op} \"{RandomDate()}T10:30:00Z\")";
            case 2: return $"({Id("stamp")} {op} \"{RandomDate()}T00:00:00+02:00\")";
            case 3: return $"({Id("day")} {op} now())";
            case 4: return $"({Id("moment")} {op} now())";
            case 5: return $"({Id("stamp")} {Choose("<", ">")} now())";
            default: return $"(daysSince({Choose(Id("day"), Id("moment"), Id("stamp"))}) {op} {_r.Next(-50, 900)})";
        }
    }

    private string RandomDate() =>
        string.Create(CultureInfo.InvariantCulture, $"{_r.Next(2019, 2027)}-{_r.Next(1, 13):00}-{_r.Next(1, 29):00}");

    private string InList(int d)
    {
        string neg = Choose(" in ", " not in ");
        int n = 1 + Pick(4);
        switch (Pick(4))
        {
            case 0:
                return $"({Num(0, d)}{neg}[{string.Join(", ", Enumerable.Range(0, n).Select(_ => IntLiteral()))}])";
            case 1:
                return $"({Str(d)}{neg}[{string.Join(", ", Enumerable.Range(0, n).Select(_ => StrLiteral()))}])";
            case 2:
                return $"({Choose(Id("color"), Id("maybeColor"))}{neg}[{string.Join(", ", Enumerable.Range(0, n).Select(_ => "\"" + Choose("Red", "green", "BLUE") + "\""))}])";
            default:
                return $"({Num(1, d)}{neg}[{string.Join(", ", Enumerable.Range(0, n).Select(_ => DecLiteral()))}])";
        }
    }

    private string IntLiteral() => _r.Next(-20, 121).ToString(CultureInfo.InvariantCulture);

    private string DecLiteral() => string.Create(CultureInfo.InvariantCulture, $"{_r.Next(0, 1000)}.{_r.Next(0, 100):00}");

    private string StrLiteral() => Choose("\"Alice\"", "\"bob\"", "\"ALICE\"", "\"\"", "\"a1b2\"", "\"X1\"", "\"Zed\"", "\"li\"", "\"x\"");

    /// <summary>kind 0 = integer, 1 = decimal, 2 = double.</summary>
    public string Num(int kind, int depth)
    {
        if (depth <= 0 || Pick(3) == 0)
        {
            return NumLeaf(kind);
        }

        switch (Pick(7))
        {
            case 0:
            case 1:
            case 2:
                return $"({Num(kind, depth - 1)} {Choose("+", "-", "*")} {Num(kind, depth - 1)})";
            case 3:
            case 4:
                return $"({Num(kind, depth - 1)} {Choose("/", "%")} {Num(kind, depth - 1)})";
            case 5:
                return $"abs({Num(kind, depth - 1)})";
            default:
                return $"(-{Num(kind, depth - 1)})";
        }
    }

    private string NumLeaf(int kind)
    {
        switch (kind)
        {
            case 0:
                return Choose(Id("a"), Id("b"), Id("child.value"), Id("big"), $"len({Id("name")})", $"len({Id("code")})", $"daysSince({Id("day")})", IntLiteral(), IntLiteral());
            case 1:
                return Choose(Id("price"), Id("rebate"), DecLiteral(), IntLiteral(), Id("a"), Id("b"));
            default:
                // Decimal literals appear only directly next to a double property, because two decimal literals
                // combined would be a decimal VALUE, which may not mix with double.
                return Choose(Id("score"), Id("ratio"), $"({Id("score")} + {DecLiteral()})", $"({Id("ratio")} * 2.25)", IntLiteral(), Id("a"));
        }
    }

    public string Str(int depth)
    {
        if (depth <= 0 || Pick(3) != 0)
        {
            return Choose(Id("name"), Id("code"), Id("child.label"), StrLiteral(), StrLiteral());
        }

        switch (Pick(3))
        {
            case 0: return $"lower({Str(depth - 1)})";
            case 1: return $"upper({Str(depth - 1)})";
            default: return $"({Str(depth - 1)} + {Str(depth - 1)})";
        }
    }
}
