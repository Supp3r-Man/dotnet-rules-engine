using System.Globalization;
using System.Text;

namespace RulesEngine;

/// <summary>
/// Pretty-printer with minimal parentheses. Guarantee (tested): <c>Parse(Print(ast))</c> is structurally
/// equal to <c>ast</c> (spans excluded), and printing is invariant-culture.
/// </summary>
public static class Printer
{
    private const int PrecOr = 10;
    private const int PrecAnd = 20;
    private const int PrecNot = 30;
    private const int PrecCmp = 40;
    private const int PrecAdd = 50;
    private const int PrecMul = 60;
    private const int PrecNeg = 70;
    private const int PrecPrimary = 100;

    public static string Print(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var sb = new StringBuilder();
        Write(sb, node, 0);
        return sb.ToString();
    }

    private static int Prec(Node n) => n switch
    {
        Binary b => b.Op switch
        {
            BinaryOp.Or => PrecOr,
            BinaryOp.And => PrecAnd,
            BinaryOp.Add or BinaryOp.Sub => PrecAdd,
            BinaryOp.Mul or BinaryOp.Div or BinaryOp.Mod => PrecMul,
            _ => PrecCmp,
        },
        Unary u => u.Op == UnaryOp.Not ? PrecNot : PrecNeg,
        InList or Between or IsNull => PrecCmp,
        _ => PrecPrimary,
    };

    private static string OpText(BinaryOp op) => op switch
    {
        BinaryOp.Or => "or",
        BinaryOp.And => "and",
        BinaryOp.Eq => "==",
        BinaryOp.Ne => "!=",
        BinaryOp.Lt => "<",
        BinaryOp.Le => "<=",
        BinaryOp.Gt => ">",
        BinaryOp.Ge => ">=",
        BinaryOp.Add => "+",
        BinaryOp.Sub => "-",
        BinaryOp.Mul => "*",
        BinaryOp.Div => "/",
        BinaryOp.Mod => "%",
        BinaryOp.StartsWith => "startswith",
        BinaryOp.EndsWith => "endswith",
        BinaryOp.Contains => "contains",
        _ => "matches",
    };

    private static void Write(StringBuilder sb, Node n, int required)
    {
        bool paren = Prec(n) < required;
        if (paren)
        {
            sb.Append('(');
        }

        switch (n)
        {
            case NumberLiteral num:
                sb.Append(num.Value is long l
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : ((decimal)num.Value).ToString(CultureInfo.InvariantCulture));
                break;
            case StringLiteral s:
                Quote(sb, s.Value);
                break;
            case BoolLiteral b:
                sb.Append(b.Value ? "true" : "false");
                break;
            case Identifier id:
                sb.Append(string.Join('.', id.Segments));
                break;
            case Call c:
                sb.Append(c.Name).Append('(');
                for (int i = 0; i < c.Args.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(sb, c.Args[i], 0);
                }

                sb.Append(')');
                break;
            case Unary u when u.Op == UnaryOp.Not:
                sb.Append("not ");
                Write(sb, u.Operand, PrecNot);
                break;
            case Unary u:
                sb.Append('-');
                Write(sb, u.Operand, PrecNeg);
                break;
            case Binary bin:
                {
                    int prec = Prec(bin);
                    bool comparison = prec == PrecCmp;
                    Write(sb, bin.Left, comparison ? PrecAdd : prec);
                    sb.Append(' ').Append(OpText(bin.Op)).Append(' ');
                    Write(sb, bin.Right, comparison ? PrecAdd : prec + 1);
                    break;
                }

            case InList il:
                Write(sb, il.Operand, PrecAdd);
                sb.Append(il.Negated ? " not in [" : " in [");
                for (int i = 0; i < il.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(sb, il.Items[i], 0);
                }

                sb.Append(']');
                break;
            case Between bt:
                Write(sb, bt.Operand, PrecAdd);
                sb.Append(bt.Negated ? " not between " : " between ");
                Write(sb, bt.Low, PrecAdd);
                sb.Append(" and ");
                Write(sb, bt.High, PrecAdd);
                break;
            case IsNull isn:
                Write(sb, isn.Operand, PrecAdd);
                sb.Append(isn.Negated ? " is not null" : " is null");
                break;
            default:
                throw new InvalidOperationException("unknown node type " + n.GetType().Name);
        }

        if (paren)
        {
            sb.Append(')');
        }
    }

    private static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
