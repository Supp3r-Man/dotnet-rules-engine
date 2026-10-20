using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RulesEngine;

/// <summary>
/// Type checker / binder. Resolves property paths against a CLR type and checks every operator.
/// Security property: the only members it can ever bind are PUBLIC, READABLE, NON-INDEXER INSTANCE PROPERTIES
/// of the root type and of nested class types, plus a fixed function whitelist. It never looks up methods.
/// </summary>
internal sealed class Binder(Type rootType, RuleOptions options)
{
    private static readonly Dictionary<string, string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["len"] = "len",
        ["lower"] = "lower",
        ["upper"] = "upper",
        ["abs"] = "abs",
        ["now"] = "now",
        ["daysSince"] = "daysSince",
    };

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    public static IReadOnlyList<string> FunctionNames { get; } = Functions.Values.ToArray();

    private static RuleException Err(Node n, string message, string? suggestion = null) =>
        new(message, n.Span.Start, Math.Max(1, n.Span.Length), suggestion);

    public BoundExpr Bind(Node n)
    {
        switch (n)
        {
            case NumberLiteral nl:
                return nl.Value is long l
                    ? new BoundConst(n, l, ValueKind.Integer, null)
                    : new BoundConst(n, (decimal)nl.Value, ValueKind.Decimal, null);
            case StringLiteral s:
                return new BoundConst(n, s.Value, ValueKind.String, null);
            case BoolLiteral b:
                return new BoundConst(n, b.Value, ValueKind.Bool, null);
            case Identifier id:
                return BindIdentifier(id);
            case Unary u:
                return BindUnary(u);
            case Binary bin:
                return BindBinary(bin);
            case InList il:
                {
                    var all = new List<BoundExpr> { Bind(il.Operand) };
                    all.AddRange(il.Items.Select(Bind));
                    IReadOnlyList<BoundExpr> unified = Unify(all, il, "in");
                    return new BoundIn(il, unified[0], unified.Skip(1).ToList(), il.Negated);
                }

            case Between bt:
                {
                    IReadOnlyList<BoundExpr> unified = Unify([Bind(bt.Operand), Bind(bt.Low), Bind(bt.High)], bt, "between");
                    RequireOrdered(unified[0], bt, "between");
                    return new BoundBetween(bt, unified[0], unified[1], unified[2], bt.Negated);
                }

            case IsNull isn:
                return new BoundIsNull(isn, Bind(isn.Operand), isn.Negated);
            case Call c:
                return BindCall(c);
            default:
                throw new InvalidOperationException("unknown node type " + n.GetType().Name);
        }
    }

    /// <summary>Binds an operand that must be boolean; a possibly-null boolean is coerced (null is false).</summary>
    private BoundExpr BindCondition(Node n)
    {
        BoundExpr b = Bind(n);
        if (b.Kind != ValueKind.Bool)
        {
            throw Err(n, $"expected a boolean condition but found {Kinds.Describe(b)}");
        }

        return b.MaybeNull ? new BoundTruthy(n, b) : b;
    }

    private static string OpSymbol(BinaryOp op) => op switch
    {
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
        BinaryOp.Matches => "matches",
        BinaryOp.And => "and",
        _ => "or",
    };

    // ---------------------------------------------------------------- identifiers

    private static IEnumerable<PropertyInfo> PublicProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0);

    private static bool IsTraversable(Type t) =>
        t.IsClass && t != typeof(string) && !t.IsArray
        && !typeof(Delegate).IsAssignableFrom(t) && !typeof(IEnumerable).IsAssignableFrom(t);

    private static bool TryClassify(Type t, out ValueKind kind, out Type? enumType, out bool nullable)
    {
        Type? under = Nullable.GetUnderlyingType(t);
        nullable = under != null;
        Type u = under ?? t;
        enumType = null;
        kind = default;
        if (u.IsEnum)
        {
            kind = ValueKind.Enum;
            enumType = u;
            return true;
        }

        if (u == typeof(bool))
        {
            kind = ValueKind.Bool;
        }
        else if (u == typeof(sbyte) || u == typeof(byte) || u == typeof(short) || u == typeof(ushort)
                 || u == typeof(int) || u == typeof(uint) || u == typeof(long))
        {
            kind = ValueKind.Integer;
        }
        else if (u == typeof(float) || u == typeof(double))
        {
            kind = ValueKind.Double;
        }
        else if (u == typeof(decimal))
        {
            kind = ValueKind.Decimal;
        }
        else if (u == typeof(string))
        {
            kind = ValueKind.String;
            nullable = true;
        }
        else if (u == typeof(DateOnly))
        {
            kind = ValueKind.Date;
        }
        else if (u == typeof(DateTime))
        {
            kind = ValueKind.DateTime;
        }
        else if (u == typeof(DateTimeOffset))
        {
            kind = ValueKind.DateTimeOffset;
        }
        else
        {
            return false;
        }

        return true;
    }

    private BoundExpr BindIdentifier(Identifier id)
    {
        Type current = rootType;
        var path = new List<PropertyInfo>();
        int offset = id.Span.Start;
        bool maybeNull = false;
        for (int i = 0; i < id.Segments.Count; i++)
        {
            string segment = id.Segments[i];
            PropertyInfo prop = FindProperty(current, segment, id, i, offset);
            path.Add(prop);
            current = prop.PropertyType;
            if (i < id.Segments.Count - 1)
            {
                if (!IsTraversable(current))
                {
                    int nextOffset = offset + segment.Length + 1;
                    throw new RuleException(
                        $"cannot read '{id.Segments[i + 1]}' of '{segment}' (type {current.Name}); only nested object properties can be navigated",
                        nextOffset,
                        id.Segments[i + 1].Length);
                }

                maybeNull = true;
            }

            offset += segment.Length + 1;
        }

        if (!TryClassify(current, out ValueKind kind, out Type? enumType, out bool leafNullable))
        {
            throw Err(id, $"property '{string.Join('.', id.Segments)}' has type {current.Name}, which rules cannot use");
        }

        return new BoundMember(id, path, kind, enumType, maybeNull || leafNullable);
    }

    private static PropertyInfo FindProperty(Type type, string name, Identifier id, int index, int offset)
    {
        List<PropertyInfo> props = PublicProperties(type).ToList();
        PropertyInfo? exact = props.FirstOrDefault(p => p.Name == name);
        if (exact != null)
        {
            return exact;
        }

        List<PropertyInfo> ci = props.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ci.Count == 1)
        {
            return ci[0];
        }

        if (ci.Count > 1)
        {
            throw new RuleException(
                $"property name '{name}' is ambiguous on type {type.Name} ({string.Join(", ", ci.Select(p => p.Name))})",
                offset,
                name.Length);
        }

        string? best = EditDistance.Suggest(name, props.Select(p => p.Name));
        string? suggestion = best == null ? null : string.Join('.', id.Segments.Take(index).Append(best));
        throw new RuleException($"unknown property '{name}' on type {type.Name}", offset, name.Length, suggestion);
    }

    // ---------------------------------------------------------------- operators

    private BoundExpr BindUnary(Unary u)
    {
        if (u.Op == UnaryOp.Not)
        {
            return new BoundNot(u, BindCondition(u.Operand));
        }

        BoundExpr o = Bind(u.Operand);
        if (!Kinds.IsNumeric(o.Kind))
        {
            throw Err(u, $"operator '-' needs a number but found {Kinds.Describe(o)}");
        }

        if (o is BoundConst c)
        {
            object folded = c.Value switch
            {
                long l => -l,
                decimal d => -d,
                _ => -(double)c.Value,
            };
            return new BoundConst(u, folded, o.Kind, null);
        }

        return new BoundNeg(u, o);
    }

    private BoundExpr BindBinary(Binary b)
    {
        switch (b.Op)
        {
            case BinaryOp.Or:
            case BinaryOp.And:
                return new BoundLogical(b, b.Op == BinaryOp.And, BindCondition(b.Left), BindCondition(b.Right));
            case BinaryOp.Add:
            case BinaryOp.Sub:
            case BinaryOp.Mul:
            case BinaryOp.Div:
            case BinaryOp.Mod:
                return BindArithmetic(b);
            case BinaryOp.StartsWith:
            case BinaryOp.EndsWith:
            case BinaryOp.Contains:
            case BinaryOp.Matches:
                return BindStringOp(b);
            default:
                {
                    IReadOnlyList<BoundExpr> unified = Unify([Bind(b.Left), Bind(b.Right)], b, OpSymbol(b.Op));
                    if (b.Op is not (BinaryOp.Eq or BinaryOp.Ne))
                    {
                        RequireOrdered(unified[0], b, OpSymbol(b.Op));
                    }

                    return new BoundCompare(b, b.Op, unified[0], unified[1], unified[0].Kind, unified[0].EnumType);
                }
        }
    }

    private static void RequireOrdered(BoundExpr e, Node at, string op)
    {
        if (e.Kind is ValueKind.Bool or ValueKind.Enum)
        {
            throw Err(at, $"operator '{op}' is not supported for {Kinds.Describe(e)} values (only == and != and 'in')");
        }
    }

    private BoundExpr BindArithmetic(Binary b)
    {
        BoundExpr l = Bind(b.Left);
        BoundExpr r = Bind(b.Right);
        if (b.Op == BinaryOp.Add && l.Kind == ValueKind.String && r.Kind == ValueKind.String)
        {
            return new BoundArith(b, b.Op, l, r, ValueKind.String, l.MaybeNull || r.MaybeNull, false);
        }

        if (!Kinds.IsNumeric(l.Kind) || !Kinds.IsNumeric(r.Kind))
        {
            throw Err(b, $"operator '{OpSymbol(b.Op)}' cannot be applied to {Kinds.Describe(l)} and {Kinds.Describe(r)}");
        }

        IReadOnlyList<BoundExpr> u = Unify([l, r], b, OpSymbol(b.Op));
        bool divisorNonZero = b.Op is BinaryOp.Div or BinaryOp.Mod
                              && u[1] is BoundConst c && !IsZero(c.Value);
        bool isDivision = b.Op is BinaryOp.Div or BinaryOp.Mod;
        bool maybeNull = u[0].MaybeNull || u[1].MaybeNull || (isDivision && !divisorNonZero);
        return new BoundArith(b, b.Op, u[0], u[1], u[0].Kind, maybeNull, divisorNonZero);
    }

    private static bool IsZero(object value) => value switch
    {
        long l => l == 0,
        decimal d => d == 0m,
        double db => db == 0.0,
        _ => false,
    };

    private BoundExpr BindStringOp(Binary b)
    {
        BoundExpr l = Bind(b.Left);
        BoundExpr r = Bind(b.Right);
        if (l.Kind != ValueKind.String || r.Kind != ValueKind.String)
        {
            throw Err(b, $"operator '{OpSymbol(b.Op)}' needs two strings but found {Kinds.Describe(l)} and {Kinds.Describe(r)}");
        }

        if (b.Op != BinaryOp.Matches)
        {
            return new BoundStringOp(b, b.Op, l, r, null, 0);
        }

        if (b.Right is not StringLiteral pattern)
        {
            throw Err(b.Right, "the pattern after 'matches' must be a string literal");
        }

        try
        {
            var regex = new Regex(pattern.Value, RegexOptions.CultureInvariant, options.RegexTimeout);
            return new BoundStringOp(b, b.Op, l, r, regex, pattern.Span.Start);
        }
        catch (ArgumentException ex)
        {
            throw Err(pattern, $"invalid regular expression: {ex.Message}");
        }
    }

    private BoundExpr BindCall(Call c)
    {
        if (!Functions.TryGetValue(c.Name, out string? name))
        {
            throw new RuleException(
                $"unknown function '{c.Name}' (allowed: {string.Join(", ", FunctionNames)})",
                c.Span.Start,
                c.Name.Length,
                EditDistance.Suggest(c.Name, FunctionNames));
        }

        int arity = name == "now" ? 0 : 1;
        if (c.Args.Count != arity)
        {
            throw Err(c, $"function '{name}' takes {arity} argument{(arity == 1 ? string.Empty : "s")} but {c.Args.Count} {(c.Args.Count == 1 ? "was" : "were")} given");
        }

        if (name == "now")
        {
            return new BoundNow(c, ValueKind.DateTimeOffset);
        }

        BoundExpr a = Bind(c.Args[0]);
        switch (name)
        {
            case "len":
                RequireKind(a, c, name, ValueKind.String);
                return new BoundCall(c, name, [a], ValueKind.Integer, a.MaybeNull);
            case "lower":
            case "upper":
                RequireKind(a, c, name, ValueKind.String);
                return new BoundCall(c, name, [a], ValueKind.String, a.MaybeNull);
            case "abs":
                if (!Kinds.IsNumeric(a.Kind))
                {
                    throw Err(c, $"function 'abs' needs a number but found {Kinds.Describe(a)}");
                }

                return new BoundCall(c, name, [a], a.Kind, a.MaybeNull);
            default: // daysSince
                if (!Kinds.IsDate(a.Kind))
                {
                    throw Err(c, $"function 'daysSince' needs a date, datetime or datetimeoffset but found {Kinds.Describe(a)}");
                }

                return new BoundCall(c, name, [a], ValueKind.Integer, a.MaybeNull);
        }
    }

    private static void RequireKind(BoundExpr a, Node at, string function, ValueKind expected)
    {
        if (a.Kind != expected)
        {
            throw Err(at, $"function '{function}' needs a {Kinds.Describe(expected, null)} but found {Kinds.Describe(a)}");
        }
    }

    // ---------------------------------------------------------------- unification

    /// <summary>
    /// Brings operands to one common kind. Rules: integers widen to decimal or double; a decimal LITERAL adapts to
    /// double; decimal and double VALUES never mix; string literals convert to the enum or date kind of the other
    /// operands; now() adapts to the date kind of the other operands; everything else must match exactly.
    /// </summary>
    private static IReadOnlyList<BoundExpr> Unify(IReadOnlyList<BoundExpr> xs, Node src, string op)
    {
        BoundExpr? enumOperand = xs.FirstOrDefault(x => x.Kind == ValueKind.Enum);
        if (enumOperand != null)
        {
            Type et = enumOperand.EnumType!;
            var result = new List<BoundExpr>();
            foreach (BoundExpr x in xs)
            {
                if (x.Kind == ValueKind.Enum && x.EnumType == et)
                {
                    result.Add(x);
                }
                else if (x is BoundConst { Kind: ValueKind.String } c)
                {
                    result.Add(ToEnum(c, et));
                }
                else
                {
                    throw Mismatch(xs, src, op);
                }
            }

            return result;
        }

        if (xs.All(x => Kinds.IsNumeric(x.Kind)))
        {
            bool anyDouble = xs.Any(x => x.Kind == ValueKind.Double);
            bool strictDecimal = xs.Any(x => x.Kind == ValueKind.Decimal && x is not BoundConst);
            if (anyDouble && strictDecimal)
            {
                throw Err(src, $"operator '{op}' cannot mix decimal and double values (decimal literals adapt to double, decimal values do not)");
            }

            ValueKind target = anyDouble ? ValueKind.Double
                : xs.Any(x => x.Kind == ValueKind.Decimal) ? ValueKind.Decimal
                : ValueKind.Integer;
            return xs.Select(x => ConvertTo(x, target)).ToList();
        }

        if (xs.Any(x => Kinds.IsDate(x.Kind)))
        {
            List<ValueKind> strict = xs.Where(x => Kinds.IsDate(x.Kind) && x is not BoundNow).Select(x => x.Kind).Distinct().ToList();
            if (strict.Count > 1)
            {
                throw Mismatch(xs, src, op);
            }

            ValueKind target = strict.Count == 1 ? strict[0] : ValueKind.DateTimeOffset;
            var result = new List<BoundExpr>();
            foreach (BoundExpr x in xs)
            {
                if (x is BoundNow)
                {
                    result.Add(new BoundNow(x.Source, target));
                }
                else if (x.Kind == target)
                {
                    result.Add(x);
                }
                else if (x is BoundConst { Kind: ValueKind.String } c)
                {
                    result.Add(ToDate(c, target));
                }
                else
                {
                    throw Mismatch(xs, src, op);
                }
            }

            return result;
        }

        if (xs.All(x => x.Kind == xs[0].Kind))
        {
            return xs;
        }

        throw Mismatch(xs, src, op);
    }

    private static RuleException Mismatch(IReadOnlyList<BoundExpr> xs, Node src, string op) =>
        Err(src, $"operator '{op}' cannot be applied to {string.Join(" and ", xs.Select(Kinds.Describe).Distinct())}");

    private static BoundExpr ConvertTo(BoundExpr x, ValueKind target)
    {
        if (x.Kind == target)
        {
            return x;
        }

        if (x is BoundConst c)
        {
            object converted = (c.Value, target) switch
            {
                (long l, ValueKind.Decimal) => (decimal)l,
                (long l, ValueKind.Double) => (double)l,
                (decimal d, ValueKind.Double) => (double)d,
                _ => throw new InvalidOperationException("unsupported constant conversion"),
            };
            return new BoundConst(c.Source, converted, target, null);
        }

        return new BoundConvert(x.Source, x, target);
    }

    private static BoundConst ToEnum(BoundConst c, Type enumType)
    {
        string text = (string)c.Value;
        string[] names = Enum.GetNames(enumType);
        string? match = names.FirstOrDefault(n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            throw new RuleException(
                $"'{text}' is not a member of enum {enumType.Name}",
                c.Source.Span.Start,
                Math.Max(1, c.Source.Span.Length),
                EditDistance.Suggest(text, names));
        }

        return new BoundConst(c.Source, Enum.Parse(enumType, match), ValueKind.Enum, enumType);
    }

    private static BoundConst ToDate(BoundConst c, ValueKind target)
    {
        string text = (string)c.Value;
        object? value = null;
        if (target == ValueKind.Date)
        {
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
            {
                value = d;
            }
        }
        else if (DateTimeOffset.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
        {
            value = target == ValueKind.DateTime ? (object)dto.UtcDateTime : dto;
        }

        if (value == null)
        {
            throw Err(c.Source, $"cannot read \"{text}\" as a {Kinds.Describe(target, null)}; use ISO format yyyy-MM-dd or yyyy-MM-ddTHH:mm:ssZ");
        }

        return new BoundConst(c.Source, value, target, null);
    }
}
