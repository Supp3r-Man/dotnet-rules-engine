using System.Linq.Expressions;
using System.Reflection;

namespace RulesEngine;

/// <summary>
/// Bound tree -> System.Linq.Expressions. The generated tree can call ONLY these methods: string.StartsWith /
/// EndsWith / Contains / ToLowerInvariant / ToUpperInvariant / Concat / CompareOrdinal, Math.Abs,
/// DateOnly.FromDateTime, TimeProvider.GetUtcNow and the two RuleRuntime helpers. No method is ever looked up by
/// name from rule text.
/// </summary>
internal sealed class ExpressionCompiler
{
    private static readonly MethodInfo GetUtcNow = typeof(TimeProvider).GetMethod(nameof(TimeProvider.GetUtcNow), Type.EmptyTypes)!;
    private static readonly MethodInfo DateOnlyFromDateTime = typeof(DateOnly).GetMethod(nameof(DateOnly.FromDateTime), [typeof(DateTime)])!;
    private static readonly MethodInfo CompareOrdinal = typeof(string).GetMethod(nameof(string.CompareOrdinal), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo Concat = typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo ToLowerInvariant = typeof(string).GetMethod(nameof(string.ToLowerInvariant), Type.EmptyTypes)!;
    private static readonly MethodInfo ToUpperInvariant = typeof(string).GetMethod(nameof(string.ToUpperInvariant), Type.EmptyTypes)!;

    private readonly RuleOptions _options;

    public ExpressionCompiler(Type rootType, RuleOptions options)
    {
        _options = options;
        Parameter = Expression.Parameter(rootType, "x");
    }

    public ParameterExpression Parameter { get; }

    private static Type NullableOf(Type t) => t.IsValueType && Nullable.GetUnderlyingType(t) == null ? typeof(Nullable<>).MakeGenericType(t) : t;

    public static Type ClrOf(BoundExpr e)
    {
        Type t = Kinds.ClrOf(e.Kind, e.EnumType);
        return e.MaybeNull ? NullableOf(t) : t;
    }

    public Expression Compile(BoundExpr e)
    {
        Expression x = e switch
        {
            BoundConst c => Expression.Constant(c.Value, Kinds.ClrOf(c.Kind, c.EnumType)),
            BoundMember m => CompileMember(m),
            BoundConvert cv => ConvertValue(Compile(cv.Operand), ClrOf(cv)),
            BoundNow nw => CompileNow(nw.Kind),
            BoundCall call => CompileCall(call),
            BoundNot n => Expression.Not(Compile(n.Operand)),
            BoundNeg ng => Expression.Negate(Compile(ng.Operand)),
            BoundTruthy t => Expression.Equal(Compile(t.Operand), Expression.Constant(true, typeof(bool?))),
            BoundLogical l => l.IsAnd
                ? Expression.AndAlso(Compile(l.Left), Compile(l.Right))
                : Expression.OrElse(Compile(l.Left), Compile(l.Right)),
            BoundArith a => CompileArith(a),
            BoundCompare cmp => Compare(cmp.Op, Compile(cmp.Left), Compile(cmp.Right), cmp.OperandKind),
            BoundIn bi => CompileIn(bi),
            BoundBetween bt => CompileBetween(bt),
            BoundIsNull isn => CompileIsNull(isn),
            BoundStringOp so => CompileStringOp(so),
            _ => throw new InvalidOperationException("unknown bound node " + e.GetType().Name),
        };
        if (x.Type != ClrOf(e))
        {
            throw new InvalidOperationException($"internal error: {e.GetType().Name} compiled to {x.Type} but {ClrOf(e)} was expected");
        }

        return x;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Convert that also handles non-nullable to nullable-of-another-type in two steps.</summary>
    public static Expression ConvertValue(Expression e, Type target)
    {
        if (e.Type == target)
        {
            return e;
        }

        Type? targetUnder = Nullable.GetUnderlyingType(target);
        if (targetUnder != null && Nullable.GetUnderlyingType(e.Type) == null)
        {
            Expression mid = e.Type == targetUnder ? e : Expression.Convert(e, targetUnder);
            return Expression.Convert(mid, target);
        }

        return Expression.Convert(e, target);
    }

    private static Expression? NotNullGuard(Expression e)
    {
        if (!e.Type.IsValueType)
        {
            return Expression.NotEqual(e, Expression.Constant(null, e.Type));
        }

        return Nullable.GetUnderlyingType(e.Type) != null ? Expression.Property(e, "HasValue") : null;
    }

    private static Expression Val(Expression e) =>
        Nullable.GetUnderlyingType(e.Type) != null ? Expression.Property(e, "Value") : e;

    private const int SimpleBudget = 24;

    /// <summary>
    /// "Simple" = side-effect free, built only from parameters, constants, property reads, conversions, + - *,
    /// null checks and conditionals, and at most 24 nodes. Such expressions are repeated instead of being stored
    /// in a variable, which keeps the tree free of Block nodes (EF Core cannot translate blocks).
    /// </summary>
    private static bool IsSimple(Expression e)
    {
        int budget = SimpleBudget;
        return Simple(e, ref budget);
    }

    private static bool Simple(Expression e, ref int budget)
    {
        if (--budget < 0)
        {
            return false;
        }

        switch (e)
        {
            case ParameterExpression or ConstantExpression or DefaultExpression:
                return true;
            case MemberExpression me:
                return me.Expression == null || Simple(me.Expression, ref budget);
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.Negate } u:
                return Simple(u.Operand, ref budget);
            case BinaryExpression
            {
                NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply,
            } b:
                return Simple(b.Left, ref budget) && Simple(b.Right, ref budget);
            case ConditionalExpression c:
                return Simple(c.Test, ref budget) && Simple(c.IfTrue, ref budget) && Simple(c.IfFalse, ref budget);
            default:
                return false;
        }
    }

    /// <summary>Evaluates <paramref name="e"/> once: simple expressions are repeated, others go in a variable.</summary>
    private static Expression Share(Expression e, Func<Expression, Expression> body)
    {
        if (IsSimple(e))
        {
            return body(e);
        }

        ParameterExpression v = Expression.Variable(e.Type);
        return Expression.Block([v], Expression.Assign(v, e), body(v));
    }

    /// <summary>Like <see cref="Share"/> but a value that cannot be null is used only once, so it is never shared.</summary>
    private static Expression ShareIfGuarded(Expression e, Func<Expression, Expression> body) =>
        NotNullGuard(e) == null ? body(e) : Share(e, body);

    private static bool IsNonNullConstant(Expression e) => e is ConstantExpression { Value: not null };

    private static Expression AllOf(List<Expression> guards, Expression core)
    {
        for (int i = guards.Count - 1; i >= 0; i--)
        {
            core = Expression.AndAlso(guards[i], core);
        }

        return core;
    }

    // ---------------------------------------------------------------- leaves

    private Expression CompileMember(BoundMember m)
    {
        Expression current = Parameter;
        var guards = new List<Expression>();
        for (int i = 0; i < m.Path.Count; i++)
        {
            current = Expression.Property(current, m.Path[i]);
            if (i < m.Path.Count - 1)
            {
                guards.Add(current);
            }
        }

        Type resultType = ClrOf(m);
        Expression result = ConvertValue(current, resultType);
        for (int i = guards.Count - 1; i >= 0; i--)
        {
            result = Expression.Condition(
                Expression.Equal(guards[i], Expression.Constant(null, guards[i].Type)),
                Expression.Default(resultType),
                result);
        }

        return result;
    }

    private Expression CompileNow(ValueKind kind)
    {
        Expression now = Expression.Call(Expression.Constant(_options.TimeProvider, typeof(TimeProvider)), GetUtcNow);
        return kind switch
        {
            ValueKind.DateTimeOffset => now,
            ValueKind.DateTime => Expression.Property(now, nameof(DateTimeOffset.UtcDateTime)),
            _ => Expression.Call(DateOnlyFromDateTime, Expression.Property(now, nameof(DateTimeOffset.UtcDateTime))),
        };
    }

    /// <summary>Applies <paramref name="f"/> to the value, giving null when the argument is null.</summary>
    private static Expression Lift(Expression arg, bool maybeNull, Func<Expression, Expression> f)
    {
        if (!maybeNull)
        {
            return f(arg);
        }

        return Share(arg, v =>
        {
            Expression body = f(Val(v));
            Type resultType = NullableOf(body.Type);
            return Expression.Condition(NotNullGuard(v)!, ConvertValue(body, resultType), Expression.Default(resultType));
        });
    }

    private Expression CompileCall(BoundCall call)
    {
        Expression arg = Compile(call.Args[0]);
        bool maybeNull = call.Args[0].MaybeNull;
        switch (call.Function)
        {
            case "len":
                return Lift(arg, maybeNull, v => ConvertValue(Expression.Property(v, nameof(string.Length)), typeof(long)));
            case "lower":
                return Lift(arg, maybeNull, v => Expression.Call(v, ToLowerInvariant));
            case "upper":
                return Lift(arg, maybeNull, v => Expression.Call(v, ToUpperInvariant));
            case "abs":
                return Lift(arg, maybeNull, v => Expression.Call(typeof(Math).GetMethod(nameof(Math.Abs), [v.Type])!, v));
            default: // daysSince
                return Lift(arg, maybeNull, v => Expression.Call(
                    typeof(RuleRuntime).GetMethod(nameof(RuleRuntime.DaysSince), [v.Type, typeof(DateTimeOffset)])!,
                    v,
                    CompileNow(ValueKind.DateTimeOffset)));
        }
    }

    // ---------------------------------------------------------------- arithmetic

    private static ExpressionType ArithType(BinaryOp op) => op switch
    {
        BinaryOp.Add => ExpressionType.Add,
        BinaryOp.Sub => ExpressionType.Subtract,
        BinaryOp.Mul => ExpressionType.Multiply,
        BinaryOp.Div => ExpressionType.Divide,
        _ => ExpressionType.Modulo,
    };

    private Expression CompileArith(BoundArith a)
    {
        Expression l = Compile(a.Left);
        Expression r = Compile(a.Right);
        if (a.Kind == ValueKind.String)
        {
            return ShareIfGuarded(l, ls => ShareIfGuarded(r, rs =>
            {
                Expression core = Expression.Call(Concat, Val(ls), Val(rs));
                var guards = new List<Expression>();
                if (NotNullGuard(ls) is { } gl)
                {
                    guards.Add(gl);
                }

                if (NotNullGuard(rs) is { } gr)
                {
                    guards.Add(gr);
                }

                return guards.Count == 0
                    ? core
                    : Expression.Condition(AllOf(guards.GetRange(0, guards.Count - 1), guards[^1]), core, Expression.Constant(null, typeof(string)));
            }));
        }

        Type resultType = ClrOf(a);
        if (a.Op is not (BinaryOp.Div or BinaryOp.Mod) || a.DivisorNonZero)
        {
            return Expression.MakeBinary(ArithType(a.Op), ConvertValue(l, resultType), ConvertValue(r, resultType));
        }

        // Division or modulo by something that may be zero: zero (or null) divisor gives null.
        Type baseType = Kinds.ClrOf(a.Kind, null);
        object zero = a.Kind switch
        {
            ValueKind.Integer => 0L,
            ValueKind.Decimal => 0m,
            _ => 0.0,
        };
        return Share(ConvertValue(r, resultType), rv => Expression.Condition(
            Expression.Equal(rv, Expression.Constant(zero, NullableOf(baseType))),
            Expression.Default(resultType),
            Expression.MakeBinary(ArithType(a.Op), ConvertValue(l, resultType), rv)));
    }

    // ---------------------------------------------------------------- comparison

    private static ExpressionType CmpType(BinaryOp op) => op switch
    {
        BinaryOp.Eq => ExpressionType.Equal,
        BinaryOp.Ne => ExpressionType.NotEqual,
        BinaryOp.Lt => ExpressionType.LessThan,
        BinaryOp.Le => ExpressionType.LessThanOrEqual,
        BinaryOp.Gt => ExpressionType.GreaterThan,
        _ => ExpressionType.GreaterThanOrEqual,
    };

    /// <summary>Comparison with the null rule: if either side is null the result is false (for == and != too).</summary>
    private static Expression Compare(BinaryOp op, Expression left, Expression right, ValueKind kind)
    {
        // string == non-null constant is already false for a null string, so no guard (and no variable) is needed.
        if (kind == ValueKind.String && op == BinaryOp.Eq && (IsNonNullConstant(left) || IsNonNullConstant(right)))
        {
            return Expression.Equal(left, right);
        }

        return CompareGuarded(op, left, right, kind);
    }

    private static Expression CompareGuarded(BinaryOp op, Expression left, Expression right, ValueKind kind) =>
        ShareIfGuarded(left, ls => ShareIfGuarded(right, rs =>
        {
            var guards = new List<Expression>();
            if (NotNullGuard(ls) is { } gl)
            {
                guards.Add(gl);
            }

            if (NotNullGuard(rs) is { } gr)
            {
                guards.Add(gr);
            }

            Expression l = Val(ls);
            Expression r = Val(rs);
            Expression core = kind == ValueKind.String && op is not (BinaryOp.Eq or BinaryOp.Ne)
                ? Expression.MakeBinary(CmpType(op), Expression.Call(CompareOrdinal, l, r), Expression.Constant(0))
                : Expression.MakeBinary(CmpType(op), l, r);
            return AllOf(guards, core);
        }));

    private Expression CompileIn(BoundIn bi)
    {
        BinaryOp op = bi.Negated ? BinaryOp.Ne : BinaryOp.Eq;
        ValueKind kind = bi.Operand.Kind;
        return Share(Compile(bi.Operand), ov =>
        {
            Expression? acc = null;
            foreach (BoundExpr item in bi.Items)
            {
                Expression term = Compare(op, ov, Compile(item), kind);
                acc = acc == null ? term : bi.Negated ? Expression.AndAlso(acc, term) : Expression.OrElse(acc, term);
            }

            return acc!;
        });
    }

    private Expression CompileBetween(BoundBetween bt)
    {
        ValueKind kind = bt.Operand.Kind;
        return Share(Compile(bt.Operand), xv =>
        {
            Expression lo = Compile(bt.Low);
            Expression hi = Compile(bt.High);
            return bt.Negated
                ? Expression.OrElse(Compare(BinaryOp.Lt, xv, lo, kind), Compare(BinaryOp.Gt, xv, hi, kind))
                : Expression.AndAlso(Compare(BinaryOp.Ge, xv, lo, kind), Compare(BinaryOp.Le, xv, hi, kind));
        });
    }

    private Expression CompileIsNull(BoundIsNull isn)
    {
        Expression operand = Compile(isn.Operand);
        if (operand.Type.IsValueType && Nullable.GetUnderlyingType(operand.Type) == null)
        {
            return Expression.Constant(isn.Negated);
        }

        Expression nullConstant = Expression.Constant(null, operand.Type);
        return isn.Negated ? Expression.NotEqual(operand, nullConstant) : Expression.Equal(operand, nullConstant);
    }

    private Expression CompileStringOp(BoundStringOp so)
    {
        Expression left = Compile(so.Left);
        Expression right = Compile(so.Right);
        return ShareIfGuarded(left, ls => ShareIfGuarded(right, rs =>
        {
            var guards = new List<Expression>();
            if (NotNullGuard(ls) is { } gl)
            {
                guards.Add(gl);
            }

            if (NotNullGuard(rs) is { } gr)
            {
                guards.Add(gr);
            }

            Expression l = Val(ls);
            Expression r = Val(rs);
            Expression core = so.Op switch
            {
                BinaryOp.StartsWith => StartsEnds(nameof(string.StartsWith), l, r),
                BinaryOp.EndsWith => StartsEnds(nameof(string.EndsWith), l, r),
                BinaryOp.Contains => Expression.Call(l, typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!, r),
                _ => Expression.Call(
                    typeof(RuleRuntime).GetMethod(nameof(RuleRuntime.RegexIsMatch))!,
                    Expression.Constant(so.Regex, typeof(System.Text.RegularExpressions.Regex)),
                    l,
                    Expression.Constant(so.PatternPosition)),
            };
            return AllOf(guards, core);
        }));
    }

    private Expression StartsEnds(string name, Expression l, Expression r)
    {
        if (_options.QueryFriendly)
        {
            return Expression.Call(l, typeof(string).GetMethod(name, [typeof(string)])!, r);
        }

        return Expression.Call(
            l,
            typeof(string).GetMethod(name, [typeof(string), typeof(StringComparison)])!,
            r,
            Expression.Constant(StringComparison.Ordinal));
    }
}
