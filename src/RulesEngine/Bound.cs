using System.Text.RegularExpressions;

namespace RulesEngine;

/// <summary>The rule language's value kinds. Many CLR types collapse onto one kind (all integrals are Integer = long).</summary>
internal enum ValueKind
{
    Bool,
    Integer,
    Decimal,
    Double,
    String,
    Date,
    DateTime,
    DateTimeOffset,
    Enum,
}

internal static class Kinds
{
    public static bool IsNumeric(ValueKind k) => k is ValueKind.Integer or ValueKind.Decimal or ValueKind.Double;

    public static bool IsDate(ValueKind k) => k is ValueKind.Date or ValueKind.DateTime or ValueKind.DateTimeOffset;

    public static string Describe(ValueKind k, Type? enumType) => k switch
    {
        ValueKind.Bool => "boolean",
        ValueKind.Integer => "integer",
        ValueKind.Decimal => "decimal",
        ValueKind.Double => "double",
        ValueKind.String => "string",
        ValueKind.Date => "date",
        ValueKind.DateTime => "datetime",
        ValueKind.DateTimeOffset => "datetimeoffset",
        _ => $"enum {enumType?.Name}",
    };

    public static string Describe(BoundExpr e) => Describe(e.Kind, e.EnumType);

    public static Type ClrOf(ValueKind k, Type? enumType) => k switch
    {
        ValueKind.Bool => typeof(bool),
        ValueKind.Integer => typeof(long),
        ValueKind.Decimal => typeof(decimal),
        ValueKind.Double => typeof(double),
        ValueKind.String => typeof(string),
        ValueKind.Date => typeof(DateOnly),
        ValueKind.DateTime => typeof(DateTime),
        ValueKind.DateTimeOffset => typeof(DateTimeOffset),
        _ => enumType!,
    };
}

/// <summary>
/// The typed ("bound") tree produced by the binder. Both the expression-tree compiler and the interpreter
/// consume it. <c>MaybeNull</c> says whether the value can be null (nullable property, string property, or a
/// path through a possibly-null object); the CLR type of the compiled expression is Nullable&lt;T&gt; exactly
/// when MaybeNull is true and T is a value type.
/// </summary>
internal abstract class BoundExpr(Node source, ValueKind kind, Type? enumType, bool maybeNull)
{
    public Node Source { get; } = source;

    public ValueKind Kind { get; } = kind;

    public Type? EnumType { get; } = enumType;

    public bool MaybeNull { get; } = maybeNull;
}

internal sealed class BoundConst(Node source, object value, ValueKind kind, Type? enumType)
    : BoundExpr(source, kind, enumType, false)
{
    public object Value { get; } = value;
}

internal sealed class BoundMember(Node source, IReadOnlyList<System.Reflection.PropertyInfo> path, ValueKind kind, Type? enumType, bool maybeNull)
    : BoundExpr(source, kind, enumType, maybeNull)
{
    public IReadOnlyList<System.Reflection.PropertyInfo> Path { get; } = path;
}

/// <summary>Widening Integer -> Decimal / Double (and literal Decimal -> Double).</summary>
internal sealed class BoundConvert(Node source, BoundExpr operand, ValueKind target)
    : BoundExpr(source, target, null, operand.MaybeNull)
{
    public BoundExpr Operand { get; } = operand;
}

internal sealed class BoundNow(Node source, ValueKind kind) : BoundExpr(source, kind, null, false);

internal sealed class BoundCall(Node source, string function, IReadOnlyList<BoundExpr> args, ValueKind kind, bool maybeNull)
    : BoundExpr(source, kind, null, maybeNull)
{
    public string Function { get; } = function;

    public IReadOnlyList<BoundExpr> Args { get; } = args;
}

internal sealed class BoundNot(Node source, BoundExpr operand) : BoundExpr(source, ValueKind.Bool, null, false)
{
    public BoundExpr Operand { get; } = operand;
}

internal sealed class BoundNeg(Node source, BoundExpr operand) : BoundExpr(source, operand.Kind, null, operand.MaybeNull)
{
    public BoundExpr Operand { get; } = operand;
}

/// <summary>Turns a possibly-null boolean into a plain boolean (null becomes false).</summary>
internal sealed class BoundTruthy(Node source, BoundExpr operand) : BoundExpr(source, ValueKind.Bool, null, false)
{
    public BoundExpr Operand { get; } = operand;
}

internal sealed class BoundLogical(Node source, bool isAnd, BoundExpr left, BoundExpr right)
    : BoundExpr(source, ValueKind.Bool, null, false)
{
    public bool IsAnd { get; } = isAnd;

    public BoundExpr Left { get; } = left;

    public BoundExpr Right { get; } = right;
}

internal sealed class BoundArith(Node source, BinaryOp op, BoundExpr left, BoundExpr right, ValueKind kind, bool maybeNull, bool divisorNonZero)
    : BoundExpr(source, kind, null, maybeNull)
{
    public BinaryOp Op { get; } = op;

    public BoundExpr Left { get; } = left;

    public BoundExpr Right { get; } = right;

    /// <summary>True when the divisor is a non-zero constant (no zero check needed).</summary>
    public bool DivisorNonZero { get; } = divisorNonZero;
}

internal sealed class BoundCompare(Node source, BinaryOp op, BoundExpr left, BoundExpr right, ValueKind operandKind, Type? operandEnumType)
    : BoundExpr(source, ValueKind.Bool, null, false)
{
    public BinaryOp Op { get; } = op;

    public BoundExpr Left { get; } = left;

    public BoundExpr Right { get; } = right;

    public ValueKind OperandKind { get; } = operandKind;

    public Type? OperandEnumType { get; } = operandEnumType;
}

internal sealed class BoundIn(Node source, BoundExpr operand, IReadOnlyList<BoundExpr> items, bool negated)
    : BoundExpr(source, ValueKind.Bool, null, false)
{
    public BoundExpr Operand { get; } = operand;

    public IReadOnlyList<BoundExpr> Items { get; } = items;

    public bool Negated { get; } = negated;
}

internal sealed class BoundBetween(Node source, BoundExpr operand, BoundExpr low, BoundExpr high, bool negated)
    : BoundExpr(source, ValueKind.Bool, null, false)
{
    public BoundExpr Operand { get; } = operand;

    public BoundExpr Low { get; } = low;

    public BoundExpr High { get; } = high;

    public bool Negated { get; } = negated;
}

internal sealed class BoundIsNull(Node source, BoundExpr operand, bool negated)
    : BoundExpr(source, ValueKind.Bool, null, false)
{
    public BoundExpr Operand { get; } = operand;

    public bool Negated { get; } = negated;
}

internal sealed class BoundStringOp(Node source, BinaryOp op, BoundExpr left, BoundExpr right, Regex? regex, int patternPosition)
    : BoundExpr(source, ValueKind.Bool, null, false)
{
    public BinaryOp Op { get; } = op;

    public BoundExpr Left { get; } = left;

    public BoundExpr Right { get; } = right;

    public Regex? Regex { get; } = regex;

    public int PatternPosition { get; } = patternPosition;
}
