using System.Globalization;

namespace RulesEngine.Tests.Support;

public enum Color
{
    Red,
    Green,
    Blue,
}

public sealed class Inner
{
    public int Value { get; set; }

    public string? Label { get; set; }

    public Inner? Deeper { get; set; }
}

/// <summary>Test model covering every supported kind, nullable and not.</summary>
public class Rec
{
    public int A { get; set; }

    public int? B { get; set; }

    public long Big { get; set; }

    public decimal Price { get; set; }

    public decimal? Rebate { get; set; }

    public double Score { get; set; }

    public double? Ratio { get; set; }

    public string? Name { get; set; }

    public string Code { get; set; } = string.Empty;

    public bool Flag { get; set; }

    public bool? FlagN { get; set; }

    public Color Color { get; set; }

    public Color? MaybeColor { get; set; }

    public DateOnly Day { get; set; }

    public DateTime Moment { get; set; }

    public DateTimeOffset? Stamp { get; set; }

    public Inner? Child { get; set; }

    // Things rules must NOT be able to reach.
    public Guid Id { get; set; }

    public List<int> Numbers { get; set; } = [];

    public int PublicField;

    public static int Counter => 5;

    public string this[int index] => "indexer" + index;

    public string Describe() => "method";

    private string Secret { get; } = "s3cret";

    internal int HiddenNumber => 42;

    protected bool Protected => true;

    public string RevealSecret() => Secret;
}

public sealed class Ambiguous
{
    public int Name { get; set; }

    public int NAME { get; set; }
}

public readonly record struct Point(int X, int Y);

public static class H
{
    public static readonly DateTimeOffset Now = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public static readonly RuleOptions Options = new() { TimeProvider = new FixedTimeProvider(Now) };

    public static CompiledRule<Rec> C(string text) => RuleCompiler.Compile<Rec>(text, Options);

    /// <summary>Evaluates with BOTH implementations, fails if they disagree, returns the answer.</summary>
    public static bool Eval(string text, Rec r)
    {
        CompiledRule<Rec> rule = C(text);
        bool compiled = rule.Evaluate(r);
        bool interpreted = rule.Interpret(r);
        Assert.True(compiled == interpreted, $"compiled={compiled} interpreted={interpreted} for: {text}");
        return compiled;
    }

    public static RuleException Fail(string text) => Assert.Throws<RuleException>(() => C(text));

    public static Rec Nulls() => new()
    {
        A = 7,
        B = null,
        Rebate = null,
        Ratio = null,
        Name = null,
        FlagN = null,
        MaybeColor = null,
        Stamp = null,
        Child = null,
        Day = new DateOnly(2025, 6, 5),
        Moment = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static Rec Values() => new()
    {
        A = 7,
        B = 5,
        Big = 10,
        Price = 20.50m,
        Rebate = 2.5m,
        Score = 0.25,
        Ratio = 1.5,
        Name = "Alice",
        Code = "X1",
        Flag = true,
        FlagN = true,
        Color = Color.Green,
        MaybeColor = Color.Blue,
        Day = new DateOnly(2025, 6, 5),
        Moment = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        Stamp = new DateTimeOffset(2025, 6, 14, 12, 0, 0, TimeSpan.FromHours(2)),
        Child = new Inner { Value = 11, Label = "kid", Deeper = new Inner { Value = 99 } },
    };

    public static string Sx(Node n) => n switch
    {
        NumberLiteral x => x.Value is long l ? l.ToString(CultureInfo.InvariantCulture) : ((decimal)x.Value).ToString(CultureInfo.InvariantCulture),
        StringLiteral s => "'" + s.Value + "'",
        BoolLiteral b => b.Value ? "true" : "false",
        Identifier i => string.Join('.', i.Segments),
        Unary u => $"({(u.Op == UnaryOp.Not ? "not" : "neg")} {Sx(u.Operand)})",
        Binary b => $"({Sym(b.Op)} {Sx(b.Left)} {Sx(b.Right)})",
        InList il => $"({(il.Negated ? "not-in" : "in")} {Sx(il.Operand)} [{string.Join(' ', il.Items.Select(Sx))}])",
        Between bt => $"({(bt.Negated ? "not-between" : "between")} {Sx(bt.Operand)} {Sx(bt.Low)} {Sx(bt.High)})",
        IsNull isn => $"({(isn.Negated ? "is-not-null" : "is-null")} {Sx(isn.Operand)})",
        Call c => "(call " + c.Name + string.Concat(c.Args.Select(a => " " + Sx(a))) + ")",
        _ => throw new InvalidOperationException(),
    };

    private static string Sym(BinaryOp op) => op switch
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

    public static Rec RandomRec(Random r)
    {
        string?[] names = [null, "Alice", "bob", "ALICE", string.Empty, "a1b2", "Zed", "Eli", "x"];
        string[] codes = ["X1", "x1", string.Empty, "abc", "Q9"];
        string?[] labels = [null, "kid", "Li", string.Empty, "zzz"];
        var origin = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return new Rec
        {
            A = r.Next(-30, 130),
            B = r.Next(4) == 0 ? null : r.Next(-30, 130),
            Big = r.Next(-1000, 1000),
            Price = r.Next(0, 100000) / 100m,
            Rebate = r.Next(3) == 0 ? null : r.Next(0, 5000) / 100m,
            Score = r.NextDouble(),
            Ratio = r.Next(4) == 0 ? null : (r.NextDouble() * 3) - 1,
            Name = names[r.Next(names.Length)],
            Code = codes[r.Next(codes.Length)],
            Flag = r.Next(2) == 0,
            FlagN = r.Next(3) switch { 0 => null, 1 => true, _ => false },
            Color = (Color)r.Next(3),
            MaybeColor = r.Next(3) == 0 ? null : (Color)r.Next(3),
            Day = DateOnly.FromDateTime(origin.AddDays(r.Next(0, 2900))),
            Moment = DateTime.SpecifyKind(origin.AddMinutes(r.Next(0, 4_000_000)), DateTimeKind.Utc),
            Stamp = r.Next(4) == 0 ? null : new DateTimeOffset(origin.AddMinutes(r.Next(0, 4_000_000)), TimeSpan.FromMinutes(30 * r.Next(-12, 13))),
            Child = r.Next(3) == 0
                ? null
                : new Inner
                {
                    Value = r.Next(-20, 60),
                    Label = labels[r.Next(labels.Length)],
                    Deeper = r.Next(2) == 0 ? null : new Inner { Value = r.Next(100) },
                },
        };
    }
}
