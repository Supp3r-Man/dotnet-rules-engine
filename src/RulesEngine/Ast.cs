namespace RulesEngine;

/// <summary>Half-open character range [Start, End) in the rule text.</summary>
public readonly record struct Span(int Start, int End)
{
    public int Length => End - Start;

    public static Span Join(Span a, Span b) => new(Math.Min(a.Start, b.Start), Math.Max(a.End, b.End));

    public override string ToString() => $"[{Start}..{End})";
}

public enum UnaryOp
{
    Not,
    Negate,
}

public enum BinaryOp
{
    Or,
    And,
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    StartsWith,
    EndsWith,
    Contains,
    Matches,
}

/// <summary>Base class of the syntax tree. Spans of parenthesised operands do not include the parentheses.</summary>
public abstract class Node(Span span, int depth)
{
    public Span Span { get; } = span;

    /// <summary>Height of the tree rooted here (a leaf has depth 1).</summary>
    public int Depth { get; } = depth;
}

/// <summary>An unsigned numeric literal: <see cref="long"/> or <see cref="decimal"/> (when written with a decimal point).</summary>
public sealed class NumberLiteral(object value, Span span) : Node(span, 1)
{
    public object Value { get; } = value;
}

public sealed class StringLiteral(string value, Span span) : Node(span, 1)
{
    public string Value { get; } = value;
}

public sealed class BoolLiteral(bool value, Span span) : Node(span, 1)
{
    public bool Value { get; } = value;
}

/// <summary>A property reference such as <c>age</c> or <c>address.city</c>.</summary>
public sealed class Identifier(IReadOnlyList<string> segments, Span span) : Node(span, 1)
{
    public IReadOnlyList<string> Segments { get; } = segments;
}

public sealed class Unary(UnaryOp op, Node operand, Span span) : Node(span, operand.Depth + 1)
{
    public UnaryOp Op { get; } = op;

    public Node Operand { get; } = operand;
}

public sealed class Binary(BinaryOp op, Node left, Node right, Span span) : Node(span, Math.Max(left.Depth, right.Depth) + 1)
{
    public BinaryOp Op { get; } = op;

    public Node Left { get; } = left;

    public Node Right { get; } = right;
}

public sealed class InList(Node operand, IReadOnlyList<Node> items, bool negated, Span span)
    : Node(span, Math.Max(operand.Depth, items.Max(i => i.Depth)) + 1)
{
    public Node Operand { get; } = operand;

    public IReadOnlyList<Node> Items { get; } = items;

    public bool Negated { get; } = negated;
}

public sealed class Between(Node operand, Node low, Node high, bool negated, Span span)
    : Node(span, Math.Max(operand.Depth, Math.Max(low.Depth, high.Depth)) + 1)
{
    public Node Operand { get; } = operand;

    public Node Low { get; } = low;

    public Node High { get; } = high;

    public bool Negated { get; } = negated;
}

public sealed class IsNull(Node operand, bool negated, Span span) : Node(span, operand.Depth + 1)
{
    public Node Operand { get; } = operand;

    public bool Negated { get; } = negated;
}

public sealed class Call(string name, IReadOnlyList<Node> args, Span span)
    : Node(span, (args.Count == 0 ? 0 : args.Max(a => a.Depth)) + 1)
{
    public string Name { get; } = name;

    public IReadOnlyList<Node> Args { get; } = args;
}

/// <summary>Structural equality of syntax trees. Spans are ignored.</summary>
public static class AstComparer
{
    public static bool AreEqual(Node? a, Node? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        switch (a)
        {
            case NumberLiteral x when b is NumberLiteral y:
                return x.Value.GetType() == y.Value.GetType() && x.Value.Equals(y.Value);
            case StringLiteral x when b is StringLiteral y:
                return x.Value == y.Value;
            case BoolLiteral x when b is BoolLiteral y:
                return x.Value == y.Value;
            case Identifier x when b is Identifier y:
                return x.Segments.SequenceEqual(y.Segments);
            case Unary x when b is Unary y:
                return x.Op == y.Op && AreEqual(x.Operand, y.Operand);
            case Binary x when b is Binary y:
                return x.Op == y.Op && AreEqual(x.Left, y.Left) && AreEqual(x.Right, y.Right);
            case InList x when b is InList y:
                return x.Negated == y.Negated && AreEqual(x.Operand, y.Operand) && ListEqual(x.Items, y.Items);
            case Between x when b is Between y:
                return x.Negated == y.Negated && AreEqual(x.Operand, y.Operand)
                       && AreEqual(x.Low, y.Low) && AreEqual(x.High, y.High);
            case IsNull x when b is IsNull y:
                return x.Negated == y.Negated && AreEqual(x.Operand, y.Operand);
            case Call x when b is Call y:
                return x.Name == y.Name && ListEqual(x.Args, y.Args);
            default:
                return false;
        }
    }

    private static bool ListEqual(IReadOnlyList<Node> a, IReadOnlyList<Node> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!AreEqual(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }
}
