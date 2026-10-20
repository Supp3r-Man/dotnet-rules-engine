using System.Numerics;

namespace RulesEngine;

/// <summary>Result of explaining one (sub-)condition. <c>Result == null</c> means short-circuited, not evaluated.</summary>
public sealed record ExplainNode(string Kind, string Text, bool? Result, IReadOnlyList<ExplainNode> Children)
{
    /// <summary>Indented multi-line rendering, e.g. "[TRUE ] age &gt;= 18".</summary>
    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        Render(sb, 0);
        return sb.ToString().TrimEnd();
    }

    private void Render(System.Text.StringBuilder sb, int indent)
    {
        string mark = Result switch
        {
            true => "[TRUE ]",
            false => "[FALSE]",
            null => "[SKIP ]",
        };
        string label = Kind == "test" ? Text : Kind.ToUpperInvariant();
        sb.Append(' ', indent * 2).Append(mark).Append(' ').Append(label).AppendLine();
        foreach (ExplainNode child in Children)
        {
            child.Render(sb, indent + 1);
        }
    }
}

/// <summary>
/// Straightforward tree-walking evaluator over the bound tree. It shares only the binder (types, name resolution)
/// with the expression-tree compiler; values are boxed, members are read by reflection and arithmetic uses
/// generic math, so it is a genuinely separate implementation of the semantics.
/// </summary>
internal sealed class Interpreter(RuleOptions options)
{
    public object? Eval(BoundExpr e, object root)
    {
        switch (e)
        {
            case BoundConst c:
                return c.Value;
            case BoundMember m:
                return EvalMember(m, root);
            case BoundConvert cv:
                {
                    object? v = Eval(cv.Operand, root);
                    return v switch
                    {
                        null => null,
                        long l when cv.Kind == ValueKind.Decimal => (decimal)l,
                        long l => (double)l,
                        _ => (double)(decimal)v,
                    };
                }

            case BoundNow nw:
                return Now(nw.Kind);
            case BoundCall call:
                return EvalCall(call, root);
            case BoundNot n:
                return !(bool)Eval(n.Operand, root)!;
            case BoundNeg ng:
                {
                    object? v = Eval(ng.Operand, root);
                    return v switch
                    {
                        null => null,
                        long l => -l,
                        decimal d => -d,
                        _ => -(double)v,
                    };
                }

            case BoundTruthy t:
                return Eval(t.Operand, root) is true;
            case BoundLogical lg:
                {
                    bool left = (bool)Eval(lg.Left, root)!;
                    if (lg.IsAnd)
                    {
                        return left && (bool)Eval(lg.Right, root)!;
                    }

                    return left || (bool)Eval(lg.Right, root)!;
                }

            case BoundArith a:
                return EvalArith(a, root);
            case BoundCompare cmp:
                return CompareValues(cmp.Op, Eval(cmp.Left, root), Eval(cmp.Right, root), cmp.OperandKind);
            case BoundIn bi:
                {
                    object? operand = Eval(bi.Operand, root);
                    foreach (BoundExpr item in bi.Items)
                    {
                        object? candidate = Eval(item, root);
                        if (!bi.Negated)
                        {
                            if (CompareValues(BinaryOp.Eq, operand, candidate, bi.Operand.Kind))
                            {
                                return true;
                            }
                        }
                        else if (!CompareValues(BinaryOp.Ne, operand, candidate, bi.Operand.Kind))
                        {
                            return false;
                        }
                    }

                    return bi.Negated;
                }

            case BoundBetween bt:
                {
                    object? x = Eval(bt.Operand, root);
                    object? lo = Eval(bt.Low, root);
                    object? hi = Eval(bt.High, root);
                    ValueKind k = bt.Operand.Kind;
                    if (bt.Negated)
                    {
                        return CompareValues(BinaryOp.Lt, x, lo, k) || CompareValues(BinaryOp.Gt, x, hi, k);
                    }

                    return CompareValues(BinaryOp.Ge, x, lo, k) && CompareValues(BinaryOp.Le, x, hi, k);
                }

            case BoundIsNull isn:
                return (Eval(isn.Operand, root) == null) != isn.Negated;
            case BoundStringOp so:
                return EvalStringOp(so, root);
            default:
                throw new InvalidOperationException("unknown bound node " + e.GetType().Name);
        }
    }

    private static object? EvalMember(BoundMember m, object root)
    {
        object? current = root;
        foreach (System.Reflection.PropertyInfo p in m.Path)
        {
            if (current == null)
            {
                return null;
            }

            current = p.GetValue(current);
        }

        if (current == null)
        {
            return null;
        }

        return m.Kind switch
        {
            ValueKind.Integer => Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture),
            ValueKind.Double => Convert.ToDouble(current, System.Globalization.CultureInfo.InvariantCulture),
            _ => current,
        };
    }

    private object Now(ValueKind kind)
    {
        DateTimeOffset now = options.TimeProvider.GetUtcNow();
        return kind switch
        {
            ValueKind.Date => DateOnly.FromDateTime(now.UtcDateTime),
            ValueKind.DateTime => now.UtcDateTime,
            _ => now,
        };
    }

    private object? EvalCall(BoundCall call, object root)
    {
        object? v = Eval(call.Args[0], root);
        if (v == null)
        {
            return null;
        }

        switch (call.Function)
        {
            case "len":
                return (long)((string)v).Length;
            case "lower":
                return ((string)v).ToLowerInvariant();
            case "upper":
                return ((string)v).ToUpperInvariant();
            case "abs":
                return v switch
                {
                    long l => long.Abs(l),
                    decimal d => decimal.Abs(d),
                    _ => double.Abs((double)v),
                };
            default:
                {
                    DateTimeOffset now = options.TimeProvider.GetUtcNow();
                    return v switch
                    {
                        DateOnly d => (long)(DateOnly.FromDateTime(now.UtcDateTime).DayNumber - d.DayNumber),
                        DateTime dt => (now.UtcDateTime - dt).Ticks / TimeSpan.TicksPerDay,
                        _ => (now - (DateTimeOffset)v).Ticks / TimeSpan.TicksPerDay,
                    };
                }
        }
    }

    private object? EvalArith(BoundArith a, object root)
    {
        object? l = Eval(a.Left, root);
        object? r = Eval(a.Right, root);
        if (l == null || r == null)
        {
            return null;
        }

        return a.Kind switch
        {
            ValueKind.Integer => Arith(a.Op, (long)l, (long)r),
            ValueKind.Decimal => Arith(a.Op, (decimal)l, (decimal)r),
            ValueKind.Double => Arith(a.Op, (double)l, (double)r),
            _ => string.Concat((string)l, (string)r),
        };
    }

    private static object? Arith<T>(BinaryOp op, T a, T b) where T : INumber<T> => op switch
    {
        BinaryOp.Add => a + b,
        BinaryOp.Sub => a - b,
        BinaryOp.Mul => a * b,
        BinaryOp.Div => b == T.Zero ? null : a / b,
        BinaryOp.Mod => b == T.Zero ? null : a % b,
        _ => throw new InvalidOperationException("not an arithmetic operator"),
    };

    private static bool CompareValues(BinaryOp op, object? a, object? b, ValueKind kind)
    {
        if (a == null || b == null)
        {
            return false;
        }

        switch (kind)
        {
            case ValueKind.Integer: return Cmp(op, (long)a, (long)b);
            case ValueKind.Decimal: return Cmp(op, (decimal)a, (decimal)b);
            case ValueKind.Double: return Cmp(op, (double)a, (double)b);
            case ValueKind.Date: return CmpOrdered(op, (DateOnly)a, (DateOnly)b);
            case ValueKind.DateTime: return CmpOrdered(op, (DateTime)a, (DateTime)b);
            case ValueKind.DateTimeOffset: return CmpOrdered(op, (DateTimeOffset)a, (DateTimeOffset)b);
            case ValueKind.String:
                {
                    int c = string.CompareOrdinal((string)a, (string)b);
                    return op switch
                    {
                        BinaryOp.Eq => c == 0,
                        BinaryOp.Ne => c != 0,
                        BinaryOp.Lt => c < 0,
                        BinaryOp.Le => c <= 0,
                        BinaryOp.Gt => c > 0,
                        _ => c >= 0,
                    };
                }

            default: // Bool and Enum: only equality is allowed by the binder
                return op == BinaryOp.Eq ? a.Equals(b) : !a.Equals(b);
        }
    }

    private static bool CmpOrdered<T>(BinaryOp op, T a, T b) where T : IComparable<T>
    {
        int c = a.CompareTo(b);
        return op switch
        {
            BinaryOp.Eq => c == 0,
            BinaryOp.Ne => c != 0,
            BinaryOp.Lt => c < 0,
            BinaryOp.Le => c <= 0,
            BinaryOp.Gt => c > 0,
            _ => c >= 0,
        };
    }

    private static bool Cmp<T>(BinaryOp op, T a, T b) where T : IComparisonOperators<T, T, bool> => op switch
    {
        BinaryOp.Eq => a == b,
        BinaryOp.Ne => a != b,
        BinaryOp.Lt => a < b,
        BinaryOp.Le => a <= b,
        BinaryOp.Gt => a > b,
        _ => a >= b,
    };

    private bool EvalStringOp(BoundStringOp so, object root)
    {
        object? l = Eval(so.Left, root);
        object? r = Eval(so.Right, root);
        if (l == null || r == null)
        {
            return false;
        }

        string s = (string)l;
        string p = (string)r;
        switch (so.Op)
        {
            case BinaryOp.StartsWith: return s.StartsWith(p, StringComparison.Ordinal);
            case BinaryOp.EndsWith: return s.EndsWith(p, StringComparison.Ordinal);
            case BinaryOp.Contains: return s.Contains(p, StringComparison.Ordinal);
            default:
                try
                {
                    return so.Regex!.IsMatch(s);
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    throw new RuleException(
                        $"regular expression timed out after {so.Regex!.MatchTimeout.TotalMilliseconds:0} ms",
                        so.PatternPosition);
                }
        }
    }

    // ---------------------------------------------------------------- explain

    public ExplainNode Explain(BoundExpr e, object root)
    {
        switch (e)
        {
            case BoundLogical lg:
                {
                    ExplainNode left = Explain(lg.Left, root);
                    bool leftValue = left.Result == true;
                    bool skipRight = lg.IsAnd ? !leftValue : leftValue;
                    ExplainNode right = skipRight ? Skipped(lg.Right) : Explain(lg.Right, root);
                    bool result = lg.IsAnd ? leftValue && right.Result == true : leftValue || right.Result == true;
                    return new ExplainNode(lg.IsAnd ? "and" : "or", Printer.Print(e.Source), result, [left, right]);
                }

            case BoundNot n:
                {
                    ExplainNode child = Explain(n.Operand, root);
                    return new ExplainNode("not", Printer.Print(e.Source), child.Result != true, [child]);
                }

            default:
                return new ExplainNode("test", Printer.Print(e.Source), (bool)Eval(e, root)!, []);
        }
    }

    private static ExplainNode Skipped(BoundExpr e)
    {
        string kind = e switch
        {
            BoundLogical lg => lg.IsAnd ? "and" : "or",
            BoundNot => "not",
            _ => "test",
        };
        return new ExplainNode(kind, Printer.Print(e.Source), null, []);
    }
}
