using System.Text.RegularExpressions;
using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

/// <summary>
/// Three independent oracles for a compiler:
/// (a) compiled expression tree == tree-walking interpreter on seeded random typed rules over random objects;
/// (b) both == a hand-written C# lambda for a fixed set of rules over thousands of random objects;
/// (c) print/parse round-trip equality (and the reprinted rule means the same thing).
/// </summary>
public class OracleTests(ITestOutputHelper output)
{
    private static readonly Rec[] BigSample = Enumerable.Range(0, 5000).Select(i => H.RandomRec(new Random(1000 + i))).ToArray();

    // ------------------------------------------------------------------ (a)

    [Theory]
    [InlineData(11)]
    [InlineData(22)]
    [InlineData(33)]
    [InlineData(44)]
    [InlineData(55)]
    public void Oracle_a_compiled_equals_interpreted_equals_explain_on_random_rules(int seed)
    {
        var gen = new RuleGenerator(seed);
        Rec[] data = Enumerable.Range(0, 60).Select(i => H.RandomRec(new Random((seed * 1000) + i))).ToArray();
        int rules = 0;
        int mixed = 0;
        int evaluations = 0;
        int trues = 0;
        for (int i = 0; i < 120; i++)
        {
            string text = gen.Bool(3);
            CompiledRule<Rec> rule = Compile(text);
            bool sawTrue = false;
            bool sawFalse = false;
            foreach (Rec rec in data)
            {
                bool compiled = rule.Evaluate(rec);
                bool interpreted = rule.Interpret(rec);
                Assert.True(compiled == interpreted, $"compiled={compiled} interpreted={interpreted} for: {text}");
                Assert.True(compiled == (rule.Explain(rec).Result == true), $"explain disagrees for: {text}");
                sawTrue |= compiled;
                sawFalse |= !compiled;
                evaluations++;
                if (compiled)
                {
                    trues++;
                }
            }

            rules++;
            if (sawTrue && sawFalse)
            {
                mixed++;
            }
        }

        output.WriteLine($"seed {seed}: {rules} rules, {evaluations} evaluations, {trues} true, {mixed} rules with both outcomes");
        Assert.True(mixed >= rules / 4, $"only {mixed}/{rules} rules produced both true and false; the generator is too trivial");
    }

    private static CompiledRule<Rec> Compile(string text)
    {
        try
        {
            return H.C(text);
        }
        catch (RuleException ex)
        {
            throw new Xunit.Sdk.XunitException($"generator produced an invalid rule: {text}\n{ex.Message}");
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Oracle_a_typed_results_compiled_equals_interpreted(int seed)
    {
        var gen = new RuleGenerator(seed);
        Rec[] data = Enumerable.Range(0, 40).Select(i => H.RandomRec(new Random((seed * 500) + i))).ToArray();
        int longs = 0;
        int decimals = 0;
        int doubles = 0;
        for (int i = 0; i < 150; i++)
        {
            int kind = i % 3;
            string text = gen.Num(kind, 3);
            if (TryCheck<long?>(text, data))
            {
                longs++;
            }
            else if (kind == 1 && TryCheck<decimal?>(text, data))
            {
                decimals++;
            }
            else if (kind == 2 && TryCheck<double?>(text, data))
            {
                doubles++;
            }
            else
            {
                Assert.Fail($"expression could not be typed as long?/decimal?/double?: {text}");
            }
        }

        output.WriteLine($"seed {seed}: {longs} long?, {decimals} decimal?, {doubles} double?");
        Assert.True(longs > 0 && decimals > 0 && doubles > 0);
    }

    private static bool TryCheck<TResult>(string text, Rec[] data)
    {
        CompiledExpression<Rec, TResult> expr;
        try
        {
            expr = RuleCompiler.Compile<Rec, TResult>(text, H.Options);
        }
        catch (RuleException ex) when (ex.Message.Contains("cannot be returned"))
        {
            return false;
        }

        foreach (Rec rec in data)
        {
            Assert.True(
                Equals(expr.Evaluate(rec), expr.Interpret(rec)),
                $"compiled={expr.Evaluate(rec)} interpreted={expr.Interpret(rec)} for: {text}");
        }

        return true;
    }

    // ------------------------------------------------------------------ (b)

    private static readonly DateOnly Today = DateOnly.FromDateTime(H.Now.UtcDateTime);

    private static readonly Dictionary<string, Func<Rec, bool>> Hand = new()
    {
        ["a >= 18 and b is null"] = r => r.A >= 18 && r.B == null,
        ["a > 10 and b < 50"] = r => r.A > 10 && r.B is { } b && b < 50,
        ["b != 5"] = r => r.B is { } b && b != 5,
        ["not (b > 5)"] = r => !(r.B is { } b && b > 5),
        ["name startswith \"A\" or flag"] = r => (r.Name != null && r.Name.StartsWith("A", StringComparison.Ordinal)) || r.Flag,
        ["name contains \"li\" and not (code == \"X1\")"] = r => (r.Name != null && r.Name.Contains("li", StringComparison.Ordinal)) && !(r.Code == "X1"),
        ["color in [\"Red\", \"Blue\"]"] = r => r.Color is Color.Red or Color.Blue,
        ["maybeColor not in [\"Red\"]"] = r => r.MaybeColor is { } c && c != Color.Red,
        ["price between 100 and 500.5"] = r => r.Price >= 100m && r.Price <= 500.5m,
        ["rebate is not null and rebate * 2 > price"] = r => r.Rebate is { } rb && rb * 2 > r.Price,
        ["score * 100 >= 75.0"] = r => r.Score * 100.0 >= 75.0,
        ["a % 3 == 0 and a / 2 > 5"] = r => r.A % 3 == 0 && r.A / 2 > 5,
        ["child.value > 10"] = r => r.Child != null && r.Child.Value > 10,
        ["child.label is null"] = r => r.Child == null || r.Child.Label == null,
        ["len(name) > 3"] = r => r.Name != null && r.Name.Length > 3,
        ["lower(code) == \"abc\" or upper(name) == \"BOB\""] = r => r.Code.ToLowerInvariant() == "abc" || (r.Name != null && r.Name.ToUpperInvariant() == "BOB"),
        ["daysSince(day) > 365"] = r => (Today.DayNumber - r.Day.DayNumber) > 365,
        ["stamp is not null and stamp > \"2024-06-01T00:00:00Z\""] = r => r.Stamp is { } s && s > new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        ["flagN"] = r => r.FlagN == true,
        ["flagN == false"] = r => r.FlagN == false,
        ["a + b > 100"] = r => r.B is { } b && r.A + b > 100,
        ["name matches \"^[A-Za-c].*[0-9]$\""] = r => r.Name != null && Regex.IsMatch(r.Name, "^[A-Za-c].*[0-9]$", RegexOptions.CultureInvariant),
        ["day >= \"2023-01-01\" and day < \"2025-01-01\""] = r => r.Day >= new DateOnly(2023, 1, 1) && r.Day < new DateOnly(2025, 1, 1),
        ["abs(a - 60) < 10"] = r => Math.Abs(r.A - 60) < 10,
        ["name in [\"Alice\", \"bob\"] and not flag"] = r => (r.Name == "Alice" || r.Name == "bob") && !r.Flag,
        ["(a > 20 or flag) and (name is null or len(name) < 4)"] = r => (r.A > 20 || r.Flag) && (r.Name == null || r.Name.Length < 4),
        ["moment < now() and moment > \"2022-01-01\""] = r => r.Moment < H.Now.UtcDateTime && r.Moment > new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static IEnumerable<object[]> HandRules() => Hand.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(HandRules))]
    public void Oracle_b_compiled_and_interpreted_equal_the_hand_written_lambda(string rule)
    {
        Func<Rec, bool> hand = Hand[rule];
        CompiledRule<Rec> compiled = H.C(rule);
        int trues = 0;
        foreach (Rec rec in BigSample)
        {
            bool expected = hand(rec);
            Assert.True(compiled.Evaluate(rec) == expected, $"compiled differs from hand-written lambda for: {rule}");
            Assert.True(compiled.Interpret(rec) == expected, $"interpreter differs from hand-written lambda for: {rule}");
            if (expected)
            {
                trues++;
            }
        }

        Assert.True(trues > 0 && trues < BigSample.Length, $"rule is trivially {(trues == 0 ? "false" : "true")} on the sample: {rule}");
    }

    [Fact]
    public void Decimal_and_double_values_do_not_mix_so_the_ratio_price_rule_is_rejected()
    {
        RuleException ex = H.Fail("ratio * 2 + 1 > price / 100");
        Assert.Contains("cannot mix decimal and double", ex.Message);
    }

    [Fact]
    public void Hand_table_has_at_least_twenty_rules()
    {
        Assert.True(Hand.Count >= 20);
    }

    // ------------------------------------------------------------------ (c)

    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    [InlineData(404)]
    [InlineData(505)]
    public void Oracle_c_print_then_parse_round_trips_and_keeps_the_meaning(int seed)
    {
        var gen = new RuleGenerator(seed);
        Rec[] data = Enumerable.Range(0, 25).Select(i => H.RandomRec(new Random((seed * 31) + i))).ToArray();
        for (int i = 0; i < 200; i++)
        {
            string text = gen.Bool(3);
            Node ast = Parser.Parse(text);
            string printed = Printer.Print(ast);
            Node reparsed = Parser.Parse(printed);
            Assert.True(AstComparer.AreEqual(ast, reparsed), $"AST changed through print/parse: {text} -> {printed}");
            Assert.Equal(printed, Printer.Print(reparsed));
            CompiledRule<Rec> original = H.C(text);
            CompiledRule<Rec> again = H.C(printed);
            foreach (Rec rec in data)
            {
                Assert.True(original.Evaluate(rec) == again.Evaluate(rec), $"meaning changed: {text} -> {printed}");
            }
        }
    }

    [Fact]
    public void Generator_covers_the_language()
    {
        var gen = new RuleGenerator(9);
        string all = string.Join("\n", Enumerable.Range(0, 400).Select(_ => gen.Bool(3)));
        foreach (string fragment in new[]
                 {
                     " and ", " or ", "(not ", " between ", " not between ", " in [", " not in [", " matches ", " startswith ",
                     " endswith ", " contains ", " is null", " is not null", "abs(", "len(", "lower(", "upper(", "now()", "daysSince(",
                     " / ", " % ", "child.deeper.value", "\"Red\"",
                 })
        {
            Assert.Contains(fragment, all, StringComparison.OrdinalIgnoreCase);
        }
    }
}
